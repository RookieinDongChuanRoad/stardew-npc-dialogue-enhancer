"""台词生成终态保存与 displayed ACK 的扁平 SQLAlchemy 操作。

生成结果先在一个短事务中完成 evidence 授权并固化不可变快照；只有游戏确认
``generated`` 文本真正展示后，ACK 才在另一短事务中递增记忆使用计数。这样
模型执行时间不会占用数据库写锁，失败或未展示结果也不会污染冷却状态。
"""

from __future__ import annotations

from typing import Literal, cast

from sqlalchemy import case, select, text, update
from sqlalchemy.dialects.sqlite import insert as sqlite_insert
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker

from stardew_npc_agent.contract_limits import WIRE_INTEGER_MAX
from stardew_npc_agent.storage_models import (
    DialogueDisplayReceiptRecord,
    DialogueGenerationRecord,
    MemoryPartitionStateRecord,
    MemoryRecord,
    utc_now,
)
from stardew_npc_agent.storage_types import (
    DialogueGenerationInput,
    DialogueGenerationSnapshot,
    DisplayNotAllowedStorageError,
    DisplayReceiptConflictStorageError,
    DisplayReceiptInput,
    GenerationNotFoundStorageError,
    GenerationStatus,
    InvalidDialogueGenerationError,
    MemoryPartitionStateInvalidStorageError,
    MemoryRevisionExhaustedStorageError,
    is_non_negative_wire_integer,
    is_wire_integer,
    validate_memory_partition_state_values,
)


class _EvidenceIdsValidationError(ValueError):
    """只在模块内部区分 evidence 格式与重复，不携带腐化原值。"""

    def __init__(self, reason: Literal["format", "duplicate"]) -> None:
        """保存稳定原因枚举，避免异常链或日志意外回显 evidence 内容。"""

        super().__init__(reason)
        self.reason = reason


async def save_dialogue_generation(
    session_factory: async_sessionmaker[AsyncSession],
    value: DialogueGenerationInput,
) -> None:
    """校验终态并在同一短事务中授权 evidence 后保存 generation。

    本函数不调用模型或 Guard，但不会盲信调用方自报“证据已授权”。对于
    ``generated``，它使用 generation day-1、save/player/NPC、关系快照及当时
    冷却配置完整校验所有 memory ID，再与 generation insert 一起提交。
    这里只保存授权快照，绝不提前递增 ``use_count``。
    """

    evidence_ids = _validate_generation_input(value)

    async with session_factory.begin() as session:
        evidence_authorized = False
        if value.status == "generated":
            await _authorize_generation_evidence(session, value, evidence_ids)
            # 空 evidence 仍是合法 generated：它只使用 mandatory context，
            # 因而授权结果应为 true，而不是被误解为授权流程未执行。
            evidence_authorized = True
        session.add(
            DialogueGenerationRecord(
                generation_id=value.generation_id,
                generation_key=value.generation_key,
                save_id=value.save_id,
                player_id=value.player_id,
                game_day_index=value.game_day_index,
                npc_id=value.npc_id,
                locale=value.locale,
                source_hash=value.source_hash,
                relationship_stage=value.relationship_stage,
                friendship_points=value.friendship_points,
                memory_cooldown_days=value.memory_cooldown_days,
                status=value.status,
                result_text=value.result_text,
                reason_code=value.reason_code,
                evidence_ids_json=list(evidence_ids),
                trace_id=value.trace_id,
                guard_passed=value.guard_passed,
                evidence_authorized=evidence_authorized,
                input_versions_json=value.input_versions,
                trace_json=value.trace,
                usage_json=value.usage,
                guard_report_json=value.guard_report,
                created_at_utc=utc_now(),
                updated_at_utc=utc_now(),
            )
        )


async def get_dialogue_generation_by_key(
    session_factory: async_sessionmaker[AsyncSession],
    generation_key: str,
) -> DialogueGenerationSnapshot | None:
    """只读返回 generation key 对应的 session 解耦快照。

    cache miss 是正常的 ``None``。命中时只复制后续服务返回响应所需字段，
    尤其把数据库 JSON list 转为 tuple；调用方不会接触 ORM record，也不能借
    此只读路径修改数据库状态。
    """

    async with session_factory() as session:
        generation = await session.scalar(
            select(DialogueGenerationRecord).where(
                DialogueGenerationRecord.generation_key == generation_key
            )
        )
        if generation is None:
            return None
        # status 受 ORM/migration 的枚举 CHECK 共同约束；cast 只把该物理合同
        # 告诉 mypy，不执行运行时改写或把未知值默认成其他终态。
        status = cast(GenerationStatus, generation.status)
        return DialogueGenerationSnapshot(
            generation_id=generation.generation_id,
            generation_key=generation.generation_key,
            status=status,
            result_text=generation.result_text,
            source_hash=generation.source_hash,
            reason_code=generation.reason_code,
            evidence_ids=tuple(generation.evidence_ids_json),
            trace_id=generation.trace_id,
        )


async def acknowledge_display(
    session_factory: async_sessionmaker[AsyncSession],
    generation_id: str,
    receipt: DisplayReceiptInput,
) -> Literal["accepted", "duplicate"]:
    """原子记录首次展示并消费已持久化授权的 evidence。

    cutoff、audience、过期、关系和保存时冷却已在 generation 保存事务中完整
    校验，并固化为 ``evidence_authorized``。ACK 信任该历史快照，不使用展示时
    已变化的 ``last_used`` 重算；这里只核对 generation/receipt 身份和 evidence
    是否仍可回溯到原 save/player 分区。
    """

    _validate_display_receipt_input(receipt)
    async with session_factory() as session:
        async with session.begin():
            # ACK 的业务原子性不只覆盖“同一个 receipt ID”。两个不同回执也可能
            # 同时消费同一条 evidence；若用 SQLite 默认 deferred transaction，
            # 两个 writer 可以先读到相同 use_count，再依次写回同一个 +1 结果。
            # 在任何 generation/receipt/memory 读取前取得写保留锁，使整段
            # “判重 -> 插入回执 -> 原子消费 evidence”跨连接串行执行。
            await session.execute(text("BEGIN IMMEDIATE"))
            generation = await session.scalar(
                select(DialogueGenerationRecord).where(
                    DialogueGenerationRecord.generation_id == generation_id
                )
            )
            if generation is None:
                raise GenerationNotFoundStorageError("生成记录不存在")

            existing_receipt = await session.scalar(
                select(DialogueDisplayReceiptRecord).where(
                    DialogueDisplayReceiptRecord.display_receipt_id == receipt.display_receipt_id
                )
            )
            if existing_receipt is not None:
                if _receipt_matches(existing_receipt, generation_id, receipt):
                    return "duplicate"
                raise DisplayReceiptConflictStorageError("display_receipt_id 已用于不同展示事实")

            _validate_display_snapshot(generation, receipt)
            try:
                evidence_ids = _validated_evidence_ids(generation.evidence_ids_json)
            except _EvidenceIdsValidationError as error:
                message = (
                    "generation evidence ID 不得重复"
                    if error.reason == "duplicate"
                    else "generation evidence ID 格式非法"
                )
                raise DisplayNotAllowedStorageError(message) from None

            partition_state: MemoryPartitionStateRecord | None = None
            if evidence_ids:
                stored_evidence_ids = set(
                    (
                        await session.scalars(
                            select(MemoryRecord.memory_id).where(
                                MemoryRecord.memory_id.in_(evidence_ids),
                                MemoryRecord.save_id == generation.save_id,
                                MemoryRecord.player_id == generation.player_id,
                            )
                        )
                    ).all()
                )
                if stored_evidence_ids != set(evidence_ids):
                    raise DisplayNotAllowedStorageError(
                        "generation evidence 无法完整回溯到当前分区"
                    )
                partition_state = await session.scalar(
                    select(MemoryPartitionStateRecord).where(
                        MemoryPartitionStateRecord.save_id == generation.save_id,
                        MemoryPartitionStateRecord.player_id == generation.player_id,
                    )
                )
                if partition_state is None:
                    raise MemoryPartitionStateInvalidStorageError()
                validate_memory_partition_state_values(
                    partition_state.memory_revision,
                    partition_state.retrieval_state_revision,
                    partition_state.committed_through_day_index,
                )
                if partition_state.retrieval_state_revision >= WIRE_INTEGER_MAX:
                    raise MemoryRevisionExhaustedStorageError()

            inserted_receipt_id = await session.scalar(
                sqlite_insert(DialogueDisplayReceiptRecord)
                .values(
                    display_receipt_id=receipt.display_receipt_id,
                    generation_id=generation_id,
                    save_id=receipt.save_id,
                    player_id=receipt.player_id,
                    displayed_day_index=receipt.displayed_day_index,
                    npc_id=receipt.npc_id,
                    source_hash=receipt.source_hash,
                    created_at_utc=utc_now(),
                )
                .on_conflict_do_nothing(index_elements=["display_receipt_id"])
                .returning(DialogueDisplayReceiptRecord.id)
            )
            if inserted_receipt_id is None:
                # 仅并发 ACK 会走到这里：唯一约束可能由另一个事务抢先插入。
                # 再读并逐字段比对，才能区分合法重放和 receipt ID 复用。
                concurrent_receipt = await session.scalar(
                    select(DialogueDisplayReceiptRecord).where(
                        DialogueDisplayReceiptRecord.display_receipt_id
                        == receipt.display_receipt_id
                    )
                )
                if concurrent_receipt is not None and _receipt_matches(
                    concurrent_receipt,
                    generation_id,
                    receipt,
                ):
                    return "duplicate"
                raise DisplayReceiptConflictStorageError(
                    "display_receipt_id 并发冲突且 payload 不一致"
                )

            if evidence_ids:
                # 即使未来事务模式或数据库后端发生变化，消费仍由数据库基于当前
                # 行值执行 ``use_count + 1``，不把先前读取的 ORM 快照写回。
                # last_used 同样使用单调 CASE，较早展示日绝不能覆盖较晚水位。
                await session.execute(
                    update(MemoryRecord)
                    .where(
                        MemoryRecord.memory_id.in_(evidence_ids),
                        MemoryRecord.save_id == generation.save_id,
                        MemoryRecord.player_id == generation.player_id,
                    )
                    .values(
                        use_count=MemoryRecord.use_count + 1,
                        last_used_day_index=case(
                            (
                                MemoryRecord.last_used_day_index.is_(None),
                                receipt.displayed_day_index,
                            ),
                            (
                                MemoryRecord.last_used_day_index < receipt.displayed_day_index,
                                receipt.displayed_day_index,
                            ),
                            else_=MemoryRecord.last_used_day_index,
                        ),
                    )
                )
                if partition_state is None:
                    raise RuntimeError("evidence consumption lost partition state")
                partition_state.retrieval_state_revision += 1
                partition_state.updated_at_utc = utc_now()
            await session.flush()

    return "accepted"


def _validate_generation_input(value: DialogueGenerationInput) -> tuple[str, ...]:
    """在开启写事务前校验终态、文本、evidence 与授权快照字段。

    Returns:
        已证明为非空、无边缘空白、无 NUL 且不重复的 evidence ID tuple。
        后续授权与持久化只使用这个返回值，不再次信任原始运行时对象。
    """

    if value.status not in {"generated", "passthrough", "skipped", "failed"}:
        raise InvalidDialogueGenerationError("未知 generation status")
    if not value.relationship_stage or value.relationship_stage != value.relationship_stage.strip():
        raise InvalidDialogueGenerationError("relationship_stage 必须非空且无边缘空白")
    if not is_wire_integer(value.friendship_points):
        raise InvalidDialogueGenerationError("friendship_points 必须是 Int32 范围内的 integer")
    if not is_non_negative_wire_integer(value.memory_cooldown_days):
        raise InvalidDialogueGenerationError("memory_cooldown_days 必须是 0..2147483647 的 integer")
    if not is_non_negative_wire_integer(value.game_day_index):
        raise InvalidDialogueGenerationError("game_day_index 必须是 0..2147483647 的 integer")
    if value.evidence_authorized is not None:
        raise InvalidDialogueGenerationError(
            "evidence_authorized 必须由 storage 计算，调用方不得设置"
        )
    try:
        evidence_ids = _validated_evidence_ids(value.evidence_ids)
    except _EvidenceIdsValidationError as error:
        message = (
            "generation evidence ID 不得重复"
            if error.reason == "duplicate"
            else "generation evidence ID 格式非法"
        )
        raise InvalidDialogueGenerationError(message) from None

    if value.status == "generated":
        if not value.guard_passed:
            raise InvalidDialogueGenerationError("generated 结果必须已通过 Guard")
        if value.result_text is None or not value.result_text.strip():
            raise InvalidDialogueGenerationError("generated 记录必须包含非空白文本")
        if "\x00" in value.result_text:
            # SQLite ``length`` 在首个 NUL 处截断，且游戏台词没有合法 NUL
            # 语义。必须在打开事务前以稳定业务异常拒绝，不能依赖 DBAPI
            # IntegrityError，也不能把原始文本拼入错误消息。
            raise InvalidDialogueGenerationError("generated 文本不得包含 NUL 控制字符")
        if value.result_text != value.result_text.strip():
            raise InvalidDialogueGenerationError(
                "generated 文本首尾不得包含空白，且不会被自动 trim"
            )
    else:
        if value.result_text is not None:
            raise InvalidDialogueGenerationError("非 generated 记录不得包含结果文本")
        if evidence_ids:
            raise InvalidDialogueGenerationError("非 generated 记录不得声明 evidence")

    return evidence_ids


def _validate_display_receipt_input(receipt: DisplayReceiptInput) -> None:
    """在打开 ACK session 前拒绝越界日和 Python ``bool`` 伪整数。

    与 generation 游戏日相等仍需读取已持久化快照后判断；此处只守住不依赖
    数据库的 wire 数值边界，保证内部调用无法把无界 Python 整数带入 SQL bind。
    """

    if not is_non_negative_wire_integer(receipt.displayed_day_index):
        raise DisplayNotAllowedStorageError("displayed_day_index 必须是 0..2147483647 的 integer")


def _validated_evidence_ids(value: object) -> tuple[str, ...]:
    """验证不可信 evidence JSON/Python 对象，再安全执行重复检测。

    容器必须是 list/tuple；元素必须是非空、无边缘空白且不含 NUL 的字符串。
    只有在所有元素都证明为字符串后才调用 ``set``，防止 list[dict] 触发
    ``TypeError`` 并从 ACK 路由泄漏为 500。
    """

    if not isinstance(value, (list, tuple)):
        raise _EvidenceIdsValidationError("format")

    normalized: list[str] = []
    for item in value:
        if not isinstance(item, str) or not item or item != item.strip() or "\x00" in item:
            raise _EvidenceIdsValidationError("format")
        normalized.append(item)

    if len(normalized) != len(set(normalized)):
        raise _EvidenceIdsValidationError("duplicate")
    return tuple(normalized)


async def _authorize_generation_evidence(
    session: AsyncSession,
    value: DialogueGenerationInput,
    evidence_ids: tuple[str, ...],
) -> None:
    """在 generation insert 的同一短事务中完成全部 evidence 授权。

    授权使用保存时快照，因此之后 ACK 不应用已变化的 ``last_used`` 重算
    历史决策。这也保证同日预生成多条台词不会因展示顺序产生不同授权结果。
    """

    if not evidence_ids:
        return

    memories = list(
        (
            await session.scalars(
                select(MemoryRecord).where(MemoryRecord.memory_id.in_(evidence_ids))
            )
        ).all()
    )
    if {memory.memory_id for memory in memories} != set(evidence_ids):
        raise InvalidDialogueGenerationError("generation evidence 存在缺失记忆")

    cutoff_day_index = value.game_day_index - 1
    cooldown_threshold = cutoff_day_index - value.memory_cooldown_days
    for memory in memories:
        partition_matches = memory.save_id == value.save_id and memory.player_id == value.player_id
        audience_matches = (
            memory.audience_scope == "public" and memory.audience_npc_id is None
        ) or (memory.audience_scope == "npc" and memory.audience_npc_id == value.npc_id)
        not_from_future = memory.occurred_day_index <= cutoff_day_index
        not_expired = (
            memory.expires_day_index is None or memory.expires_day_index >= cutoff_day_index
        )
        stage_matches = (
            not memory.relationship_stages_json
            or value.relationship_stage in memory.relationship_stages_json
        )
        min_points_match = (
            memory.min_friendship_points is None
            or memory.min_friendship_points <= value.friendship_points
        )
        max_points_match = (
            memory.max_friendship_points is None
            or memory.max_friendship_points >= value.friendship_points
        )
        cooldown_matches = (
            value.memory_cooldown_days == 0
            or memory.last_used_day_index is None
            or memory.last_used_day_index <= cooldown_threshold
        )
        if not all(
            (
                partition_matches,
                audience_matches,
                not_from_future,
                not_expired,
                stage_matches,
                min_points_match,
                max_points_match,
                cooldown_matches,
            )
        ):
            raise InvalidDialogueGenerationError(
                f"generation evidence 未通过保存时授权: {memory.memory_id}"
            )


def _validate_display_snapshot(
    generation: DialogueGenerationRecord,
    receipt: DisplayReceiptInput,
) -> None:
    """重新核对展示回执与已持久化生成快照的安全不变量。"""

    if generation.status != "generated":
        raise DisplayNotAllowedStorageError("只有 generated 结果可以接受展示 ACK")
    if not generation.guard_passed:
        raise DisplayNotAllowedStorageError("generated 结果未通过 Guard")
    if not generation.evidence_authorized:
        raise DisplayNotAllowedStorageError("generated 结果没有持久化 evidence 授权")
    if generation.save_id != receipt.save_id or generation.player_id != receipt.player_id:
        raise DisplayNotAllowedStorageError("ACK save/player 与 generation 分区不一致")
    if generation.npc_id != receipt.npc_id:
        raise DisplayNotAllowedStorageError("ACK npc_id 与 generation 不一致")
    if generation.source_hash != receipt.source_hash:
        raise DisplayNotAllowedStorageError("ACK source_hash 与 generation 不一致")
    if receipt.displayed_day_index != generation.game_day_index:
        # outbox 可以延迟上传，但 payload 仍应携带实际发生展示的游戏日。
        # 接受未来日会错误延长 cooldown，并允许不同日期的点击冒充本次生成。
        raise DisplayNotAllowedStorageError("ACK 展示日必须等于 generation 游戏日")


def _receipt_matches(
    existing: DialogueDisplayReceiptRecord,
    generation_id: str,
    candidate: DisplayReceiptInput,
) -> bool:
    """判定重放是同一展示事实，而不是仅共用一个 receipt ID。"""

    return (
        existing.generation_id == generation_id
        and existing.save_id == candidate.save_id
        and existing.player_id == candidate.player_id
        and existing.displayed_day_index == candidate.displayed_day_index
        and existing.npc_id == candidate.npc_id
        and existing.source_hash == candidate.source_hash
    )
