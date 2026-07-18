"""事件入库与记忆检索的扁平 SQLAlchemy 操作。

这里没有 repository protocol、Unit of Work 或额外 service 层：函数直接接收
``async_sessionmaker``，在一个明确短事务或只读会话内完成工作。调用模型、网络
请求与角色决策严禁进入这些函数，以免 SQLite 写锁等待与外部延迟相乘。
"""

from __future__ import annotations

from collections.abc import Sequence

from sqlalchemy import and_, or_, select, text
from sqlalchemy.dialects.sqlite import insert as sqlite_insert
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker
from sqlalchemy.sql.elements import ColumnElement

from stardew_npc_agent.contract_limits import WIRE_INTEGER_MAX
from stardew_npc_agent.memory_capabilities import capability_for_kind
from stardew_npc_agent.memory_retrieval import (
    DomainMemoryCandidate,
    MemoryRetrievalCorruptionError,
    rank_domain_memory_candidates,
)
from stardew_npc_agent.storage_models import (
    GameEventRecord,
    MemoryPartitionStateRecord,
    MemoryRecord,
    utc_now,
)
from stardew_npc_agent.storage_types import (
    MAX_EVENT_BATCH_ITEMS,
    DomainMemoryQuery,
    EventBatchStorageResult,
    EventBatchTooLargeError,
    EventHistoryQuery,
    EventStorageResult,
    EventStorageStatus,
    EvidenceRecord,
    InvalidMemoryQueryError,
    MemoryPartitionSnapshot,
    MemoryPartitionStateInvalidStorageError,
    MemoryRevisionExhaustedStorageError,
    MemorySearchQuery,
    MemorySnapshotMismatchStorageError,
    PreparedEvent,
    ProgressionContextQuery,
    is_non_negative_wire_integer,
    is_wire_integer,
    validate_memory_partition_state_values,
)


async def ingest_events(
    session_factory: async_sessionmaker[AsyncSession],
    save_id: str,
    player_id: str,
    events: Sequence[PreparedEvent],
) -> EventBatchStorageResult:
    """原子写入一批已投影事件并返回分区水位。

    唯一约束而非“先查后写”是并发重复事件的最终防线。冲突后仍比对完整
    事实：逐字等价才是 ``duplicate``；相同 ID 被复用于另一事实则为
    ``conflict``。事实、记忆投影和 revision 在同一短事务中提交。

    Raises:
        EventBatchTooLargeError: 超过 64 条；在打开 session 前拒绝，保证零写入。
        ValueError: 某条事件的 save/player 与批次 envelope 不一致。
    """

    if len(events) > MAX_EVENT_BATCH_ITEMS:
        raise EventBatchTooLargeError(f"事件批次不得超过 {MAX_EVENT_BATCH_ITEMS} 条")

    for item in events:
        if not is_non_negative_wire_integer(item.occurred_day_index):
            raise ValueError("occurred_day_index 必须是 0..2147483647 的 integer")
        if (
            not is_non_negative_wire_integer(item.projection.occurred_day_index)
            or item.projection.occurred_day_index != item.occurred_day_index
        ):
            # 记忆投影日也会写入 SQLite，且它在语义上必须逐字继承来源事件日。
            # 同时校验范围与相等性可阻止内部调用者绕过 DTO 后制造分叉事实。
            raise ValueError("projection.occurred_day_index 必须是合法且与事件一致的 wire integer")
        _validate_projection_integer_fields(item)
        _validate_projection_classification(item)
        if item.save_id != save_id or item.player_id != player_id:
            raise ValueError("批次事件的 save/player 必须与 envelope 一致")

    async with session_factory() as session:
        async with session.begin():
            # SQLite deferred transaction 允许两个并发 writer 在预检时看到同一旧
            # revision。先取得 IMMEDIATE 写保留锁，使“计数新事实 -> 校验上限 ->
            # 提交”成为跨连接串行的一个事务；这里只加锁，不写业务数据。
            await session.execute(text("BEGIN IMMEDIATE"))
            state = await session.scalar(
                select(MemoryPartitionStateRecord).where(
                    MemoryPartitionStateRecord.save_id == save_id,
                    MemoryPartitionStateRecord.player_id == player_id,
                )
            )
            if state is not None:
                _validate_partition_state(state)
            current_revision = 0 if state is None else state.memory_revision
            current_retrieval_revision = 0 if state is None else state.retrieval_state_revision
            prospective_new_count = await _count_prospective_new_events(
                session,
                save_id,
                player_id,
                events,
            )
            if (
                not is_non_negative_wire_integer(current_revision)
                or prospective_new_count > WIRE_INTEGER_MAX - current_revision
                or not is_non_negative_wire_integer(current_retrieval_revision)
                or prospective_new_count > WIRE_INTEGER_MAX - current_retrieval_revision
            ):
                # 必须在任何 INSERT/UPDATE 前失败。否则事务可能成功提交，随后
                # Pydantic response 因 max+1 无法构造而只向调用方暴露 500。
                raise MemoryRevisionExhaustedStorageError()

            if state is None:
                state = MemoryPartitionStateRecord(
                    save_id=save_id,
                    player_id=player_id,
                    memory_revision=0,
                    retrieval_state_revision=0,
                    committed_through_day_index=-1,
                    updated_at_utc=utc_now(),
                )
                session.add(state)

            results: list[EventStorageResult] = []
            accepted_count = 0
            accepted_days: list[int] = []
            for item in events:
                inserted_id = await session.scalar(
                    sqlite_insert(GameEventRecord)
                    .values(
                        save_id=item.save_id,
                        player_id=item.player_id,
                        event_id=item.event_id,
                        event_type=item.event_type,
                        event_version=item.event_version,
                        occurred_day_index=item.occurred_day_index,
                        source=item.source,
                        audience_scope=item.audience_scope,
                        audience_npc_id=item.audience_npc_id,
                        payload_json=item.payload_json,
                        created_at_utc=utc_now(),
                    )
                    .on_conflict_do_nothing(index_elements=["save_id", "player_id", "event_id"])
                    .returning(GameEventRecord.id)
                )
                if inserted_id is None:
                    existing = await session.scalar(
                        select(GameEventRecord).where(
                            GameEventRecord.save_id == save_id,
                            GameEventRecord.player_id == player_id,
                            GameEventRecord.event_id == item.event_id,
                        )
                    )
                    # 唯一冲突后必须能读到胜出行；否则数据库约束或隔离语义
                    # 已偏离经过验证的 SQLite 路径，不能猜测为 duplicate。
                    if existing is None:
                        raise RuntimeError("事件唯一冲突后无法读取已存事实")
                    status: EventStorageStatus = (
                        "duplicate" if _event_matches(existing, item) else "conflict"
                    )
                    results.append(EventStorageResult(item.event_id, status))
                    continue

                projection = item.projection
                session.add(
                    MemoryRecord(
                        memory_id=projection.memory_id,
                        event_id=projection.event_id,
                        save_id=projection.save_id,
                        player_id=projection.player_id,
                        audience_scope=projection.audience_scope,
                        audience_npc_id=projection.audience_npc_id,
                        event_type=projection.event_type,
                        event_version=projection.event_version,
                        source=projection.source,
                        payload_json=projection.payload_json,
                        classification_status=projection.classification_status,
                        memory_domain=projection.memory_domain,
                        memory_kind=projection.memory_kind,
                        subject_namespace=projection.subject_namespace,
                        subject_value=projection.subject_value,
                        summary=projection.summary,
                        tags_json=list(projection.tags),
                        importance=projection.importance,
                        occurred_day_index=projection.occurred_day_index,
                        expires_day_index=projection.expires_day_index,
                        last_used_day_index=None,
                        use_count=0,
                        relationship_stages_json=list(projection.relationship_stages),
                        min_friendship_points=projection.min_friendship_points,
                        max_friendship_points=projection.max_friendship_points,
                        created_at_utc=utc_now(),
                    )
                )
                results.append(EventStorageResult(item.event_id, "accepted"))
                accepted_count += 1
                accepted_days.append(item.occurred_day_index)

            if accepted_count:
                state.memory_revision += accepted_count
                state.retrieval_state_revision += accepted_count
                state.committed_through_day_index = max(
                    state.committed_through_day_index,
                    max(accepted_days),
                )
                state.updated_at_utc = utc_now()

            # flush 使投影唯一约束在返回水位前实际检查，避免离开事务后才
            # 暴露异常并让调用方误以为 revision 已成功提交。
            await session.flush()
            final_revision = state.memory_revision
            final_day = state.committed_through_day_index

    return EventBatchStorageResult(tuple(results), final_revision, final_day)


def _validate_partition_state(state: MemoryPartitionStateRecord) -> None:
    """在任何业务 DML 前验证既有 revision/day 水位的完整状态机不变量。

    合法状态只有两类：尚无事实时 ``revision=0, day=-1``；至少提交一条事实后
    ``revision>0, day>=0``。两列还必须落在公开 Int32 合同内。腐化状态不能被
    本次请求顺手“修复”或误报成 revision exhausted，否则会掩盖磁盘数据问题。
    """

    validate_memory_partition_state_values(
        state.memory_revision,
        state.retrieval_state_revision,
        state.committed_through_day_index,
    )


async def get_memory_partition_snapshot(
    session_factory: async_sessionmaker[AsyncSession],
    save_id: str,
    player_id: str,
) -> MemoryPartitionSnapshot:
    """只读返回一个 save/player 分区的已持久化水位。

    尚未接收事件的新分区没有数据库行，此时返回冻结初值 ``0/-1``，且查询
    本身不创建占位状态。有行时复用写路径的完整状态机校验；磁盘腐化必须抛
    稳定错误，不能被同一个默认值掩盖为“尚无事件”。
    """

    async with session_factory() as session:
        state = await session.scalar(
            select(MemoryPartitionStateRecord).where(
                MemoryPartitionStateRecord.save_id == save_id,
                MemoryPartitionStateRecord.player_id == player_id,
            )
        )
        if state is None:
            return MemoryPartitionSnapshot(
                memory_revision=0,
                committed_through_day_index=-1,
                retrieval_state_revision=0,
            )
        _validate_partition_state(state)
        return MemoryPartitionSnapshot(
            memory_revision=state.memory_revision,
            committed_through_day_index=state.committed_through_day_index,
            retrieval_state_revision=state.retrieval_state_revision,
        )


async def _count_prospective_new_events(
    session: AsyncSession,
    save_id: str,
    player_id: str,
    events: Sequence[PreparedEvent],
) -> int:
    """在任何 DML 前计算本批真正可能接受的唯一新 event ID 数。

    已存在 ID 与批内后续重复/冲突都不会推进 revision，因此不能保守地使用批次
    长度，否则 max 水位上的合法 duplicate 重放会被错误拒绝。调用方已持有
    ``BEGIN IMMEDIATE``，查询结果在后续写入前不会被另一 writer 改变。
    """

    event_ids = {item.event_id for item in events}
    if not event_ids:
        return 0
    existing_ids = set(
        (
            await session.scalars(
                select(GameEventRecord.event_id).where(
                    GameEventRecord.save_id == save_id,
                    GameEventRecord.player_id == player_id,
                    GameEventRecord.event_id.in_(event_ids),
                )
            )
        ).all()
    )
    prospective_ids: set[str] = set()
    for item in events:
        if item.event_id not in existing_ids:
            prospective_ids.add(item.event_id)
    return len(prospective_ids)


async def search_memories(
    session_factory: async_sessionmaker[AsyncSession],
    query: MemorySearchQuery,
) -> list[EvidenceRecord]:
    """执行带硬过滤、稳定排序与事件类型去重的记忆查询。

    过滤顺序覆盖 save/player、public 或目标 NPC、次日截止、过期、展示冷却、
    好感点和关系阶段。任何一项不匹配都不会进入评分。每种 event type 最多
    选一条，总数最多三条，避免高频事件挤占整个 Prompt。
    """

    _validate_memory_query(query)
    cooldown_threshold = query.cutoff_day_index - query.cooldown_days
    conditions = [
        MemoryRecord.save_id == query.save_id,
        MemoryRecord.player_id == query.player_id,
        or_(
            and_(
                MemoryRecord.audience_scope == "public",
                MemoryRecord.audience_npc_id.is_(None),
            ),
            and_(
                MemoryRecord.audience_scope == "npc",
                MemoryRecord.audience_npc_id == query.npc_id,
            ),
        ),
        MemoryRecord.occurred_day_index <= query.cutoff_day_index,
        or_(
            MemoryRecord.expires_day_index.is_(None),
            MemoryRecord.expires_day_index >= query.cutoff_day_index,
        ),
        or_(
            MemoryRecord.min_friendship_points.is_(None),
            MemoryRecord.min_friendship_points <= query.friendship_points,
        ),
        or_(
            MemoryRecord.max_friendship_points.is_(None),
            MemoryRecord.max_friendship_points >= query.friendship_points,
        ),
    ]
    if query.cooldown_days > 0:
        conditions.append(
            or_(
                MemoryRecord.last_used_day_index.is_(None),
                MemoryRecord.last_used_day_index <= cooldown_threshold,
            )
        )

    async with session_factory() as session:
        rows = list((await session.scalars(select(MemoryRecord).where(*conditions))).all())

    # SQLite JSON 数组包含查询绑定具体 dialect 语义。在 repository 内做精确
    # 阶段匹配仍是硬过滤，并保留未来迁移 PostgreSQL 时的清晰查询边界。
    relationship_eligible = [
        row
        for row in rows
        if not row.relationship_stages_json
        or query.relationship_stage in row.relationship_stages_json
    ]
    query_tags = frozenset(query.tags)
    ranked = sorted(
        relationship_eligible,
        key=lambda row: (
            -_memory_score(row, query_tags),
            -row.occurred_day_index,
            row.memory_id,
        ),
    )

    selected: list[MemoryRecord] = []
    seen_event_types: set[str] = set()
    for row in ranked:
        if row.event_type in seen_event_types:
            continue
        selected.append(row)
        seen_event_types.add(row.event_type)
        if len(selected) >= query.limit:
            break

    return [_evidence_from_memory(row) for row in selected]


async def get_domain_memory_candidates(
    session_factory: async_sessionmaker[AsyncSession],
    query: DomainMemoryQuery,
) -> list[EvidenceRecord]:
    """在一个 SQLite 只读快照中复核二元水位并读取领域候选。

    授权与排序职责刻意分开：SQL 先硬过滤分区、日期、资格、分类、领域和
    audience，Python 再执行 JSON 关系阶段过滤与 registry 防御性复核，最后才
    把脱离 ORM session 的不可变候选交给 scope/bucket 排序器。模型查询词、
    memory summary 和相似度均不能扩大候选集合。

    Raises:
        InvalidMemoryQueryError: runtime 字段、日期或资源上限非法。
        MemorySnapshotMismatchStorageError: 任一当前 revision 不等于冻结快照。
        MemoryPartitionStateInvalidStorageError: 分区水位或候选分类已经腐化。
    """

    _validate_domain_memory_query(query)
    conditions = _domain_memory_conditions(query)
    copied_candidates: list[DomainMemoryCandidate] = []

    async with session_factory() as session:
        # 第一次 SELECT 建立当前 read transaction 的 SQLite snapshot。随后 state
        # 复核与候选 SELECT 使用同一个 session/transaction，不能混入并发 ACK
        # 或事件提交后的第二个候选状态。
        async with session.begin():
            state = await session.scalar(
                select(MemoryPartitionStateRecord).where(
                    MemoryPartitionStateRecord.save_id == query.save_id,
                    MemoryPartitionStateRecord.player_id == query.player_id,
                )
            )
            if state is None:
                if (
                    query.resolved_memory_revision != 0
                    or query.resolved_retrieval_state_revision != 0
                ):
                    raise MemorySnapshotMismatchStorageError()
                orphan_memory_exists = (
                    await session.scalar(
                        select(MemoryRecord.id)
                        .where(
                            MemoryRecord.save_id == query.save_id,
                            MemoryRecord.player_id == query.player_id,
                        )
                        .limit(1)
                    )
                    is not None
                )
                if orphan_memory_exists:
                    raise MemoryPartitionStateInvalidStorageError()
                return []

            _validate_partition_state(state)
            if (
                state.memory_revision != query.resolved_memory_revision
                or state.retrieval_state_revision != query.resolved_retrieval_state_revision
            ):
                raise MemorySnapshotMismatchStorageError()

            rows = list((await session.scalars(select(MemoryRecord).where(*conditions))).all())
            relationship_eligible = [
                row for row in rows if _matches_relationship_stage(row, query.relationship_stage)
            ]
            try:
                copied_candidates = [
                    _domain_candidate_from_memory(row, query=query) for row in relationship_eligible
                ]
                # 排序也在 transaction scope 中完成并不依赖 ORM 懒加载；这样
                # 返回前所有候选及其 revision 都明确属于同一 SQLite snapshot。
                ranked = rank_domain_memory_candidates(
                    copied_candidates,
                    source_dialogue_text=query.source_dialogue_text,
                    locale=query.locale,
                    limit=query.limit,
                )
            except MemoryRetrievalCorruptionError:
                raise MemoryPartitionStateInvalidStorageError() from None

    return [_evidence_from_domain_candidate(item) for item in ranked]


async def get_event_history(
    session_factory: async_sessionmaker[AsyncSession],
    query: EventHistoryQuery,
) -> list[EvidenceRecord]:
    """按主题和事件类型读取多条、稳定排序的可见历史证据。

    与 ``search_memories`` 的相关性排名不同，历史查询保留同一 event type 的多条
    记录，并按绝对日期降序、memory ID 升序返回，便于 Agent 查看时间序列。所有
    分区、可见性、截止日、过期、关系和展示冷却仍是硬过滤。
    """

    _validate_event_history_query(query)
    conditions = _agent_evidence_conditions(
        save_id=query.save_id,
        player_id=query.player_id,
        npc_id=query.npc_id,
        cutoff_day_index=query.cutoff_day_index,
        friendship_points=query.friendship_points,
        cooldown_days=query.cooldown_days,
        since_day_index=query.since_day_index,
        public_only=False,
    )
    if query.event_types:
        conditions.append(MemoryRecord.event_type.in_(query.event_types))

    async with session_factory() as session:
        rows = list((await session.scalars(select(MemoryRecord).where(*conditions))).all())

    selected = [
        row
        for row in rows
        if _matches_relationship_stage(row, query.relationship_stage)
        and _matches_any_topic(row, query.topics)
    ]
    selected.sort(key=lambda row: (-row.occurred_day_index, row.memory_id))
    return [_evidence_from_memory(row) for row in selected[: query.limit]]


async def get_progression_context(
    session_factory: async_sessionmaker[AsyncSession],
    query: ProgressionContextQuery,
) -> list[EvidenceRecord]:
    """读取当前分区截至昨日的公共世界进度证据。

    ``world_progression`` 与 public audience 都在 SQL 条件中写死；即使数据库中
    存在同名 NPC 私有投影或其他事件包含相似标签，也不会被工具误报为世界事实。
    """

    _validate_progression_query(query)
    conditions = _agent_evidence_conditions(
        save_id=query.save_id,
        player_id=query.player_id,
        npc_id=query.npc_id,
        cutoff_day_index=query.cutoff_day_index,
        friendship_points=query.friendship_points,
        cooldown_days=query.cooldown_days,
        since_day_index=query.since_day_index,
        public_only=True,
    )
    conditions.append(MemoryRecord.event_type == "world_progression")

    async with session_factory() as session:
        rows = list((await session.scalars(select(MemoryRecord).where(*conditions))).all())

    selected = [
        row
        for row in rows
        if _matches_relationship_stage(row, query.relationship_stage)
        and _matches_any_topic(row, query.topics)
    ]
    selected.sort(key=lambda row: (-row.occurred_day_index, row.memory_id))
    return [_evidence_from_memory(row) for row in selected[: query.limit]]


def _event_matches(existing: GameEventRecord, candidate: PreparedEvent) -> bool:
    """比对幂等键指向的全部事实字段，避免把 ID 复用误报为重放。"""

    return (
        existing.event_type == candidate.event_type
        and existing.event_version == candidate.event_version
        and existing.occurred_day_index == candidate.occurred_day_index
        and existing.source == candidate.source
        and existing.audience_scope == candidate.audience_scope
        and existing.audience_npc_id == candidate.audience_npc_id
        and existing.payload_json == candidate.payload_json
    )


def _validate_projection_integer_fields(item: PreparedEvent) -> None:
    """校验记忆投影中会进入 SQLite INTEGER 的可选派生字段。

    这些值通常来自确定性事件模板，但存储门面也允许进程内调用；因此不能只依赖
    Pydantic 请求 DTO。所有检查都在 session 建立前完成，失败时保证零写入。
    """

    projection = item.projection
    if projection.expires_day_index is not None and not is_non_negative_wire_integer(
        projection.expires_day_index
    ):
        raise ValueError("projection.expires_day_index 必须是 null 或非负 wire integer")
    if projection.min_friendship_points is not None and not is_wire_integer(
        projection.min_friendship_points
    ):
        raise ValueError("projection.min_friendship_points 必须是 null 或 Int32 integer")
    if projection.max_friendship_points is not None and not is_wire_integer(
        projection.max_friendship_points
    ):
        raise ValueError("projection.max_friendship_points 必须是 null 或 Int32 integer")
    if (
        projection.min_friendship_points is not None
        and projection.max_friendship_points is not None
        and projection.min_friendship_points > projection.max_friendship_points
    ):
        raise ValueError("projection.min_friendship_points 不得大于 max_friendship_points")


def _validate_projection_classification(item: PreparedEvent) -> None:
    """防止进程内调用绕过 EventService 后写入越权 active 分类。"""

    projection = item.projection
    capability = capability_for_kind(projection.memory_kind)
    if projection.classification_status != "active" or capability is None:
        raise ValueError("新事件 projection 必须命中 active memory capability")
    if (
        projection.memory_domain != capability.domain
        or projection.subject_namespace != capability.subject_namespace
        or not projection.subject_value
        or projection.subject_value != projection.subject_value.strip()
        or "\x00" in projection.subject_value
        or len(projection.subject_value) > 255
    ):
        raise ValueError("projection classification 与 capability registry 不一致")
    if capability.subject_value_policy == "qualified_item_id":
        closing_parenthesis = projection.subject_value.find(")")
        if not (
            projection.subject_value.startswith("(")
            and 1 < closing_parenthesis < len(projection.subject_value) - 1
        ):
            raise ValueError("projection gift subject 必须是 QualifiedItemId")
    if (
        capability.allowed_subject_values is not None
        and projection.subject_value not in capability.allowed_subject_values
    ):
        raise ValueError("projection subject 不在 capability registry 中")
    is_npc = (
        capability.audience_scope == "npc"
        and projection.audience_scope == "npc"
        and projection.audience_npc_id is not None
    )
    is_public = (
        capability.audience_scope == "public"
        and projection.audience_scope == "public"
        and projection.audience_npc_id is None
    )
    if not is_npc and not is_public:
        raise ValueError("projection classification audience 不一致")


def _validate_memory_query(query: MemorySearchQuery) -> None:
    """在执行 SQL 前验证次日语义和可配置资源上限。"""

    if not is_non_negative_wire_integer(query.game_day_index):
        raise InvalidMemoryQueryError("game_day_index 必须是 0..2147483647 的 integer")
    if not is_non_negative_wire_integer(query.cutoff_day_index):
        raise InvalidMemoryQueryError("cutoff_day_index 必须是 0..2147483647 的 integer")
    if not is_wire_integer(query.friendship_points):
        raise InvalidMemoryQueryError("friendship_points 必须是 Int32 范围内的 integer")
    if query.cutoff_day_index > query.game_day_index - 1:
        raise InvalidMemoryQueryError("cutoff 必须不晚于 game_day_index - 1")
    if not is_non_negative_wire_integer(query.cooldown_days):
        raise InvalidMemoryQueryError("cooldown_days 必须是 0..2147483647 的 integer")
    if not is_wire_integer(query.limit) or not 1 <= query.limit <= 3:
        raise InvalidMemoryQueryError("limit 必须是 1..3 之间的 integer")


def _validate_domain_memory_query(query: DomainMemoryQuery) -> None:
    """复核零参数领域工具绑定的全部可信 runtime 字段。"""

    for field_name, value in (
        ("save_id", query.save_id),
        ("player_id", query.player_id),
        ("npc_id", query.npc_id),
        ("relationship_stage", query.relationship_stage),
        ("locale", query.locale),
    ):
        if not isinstance(value, str) or not value or value != value.strip():
            raise InvalidMemoryQueryError(f"{field_name} 必须是无首尾空白的非空字符串")
    if (
        not isinstance(query.source_dialogue_text, str)
        or not query.source_dialogue_text
        or "\x00" in query.source_dialogue_text
    ):
        raise InvalidMemoryQueryError("source_dialogue_text 必须是无 NUL 的非空字符串")
    if query.memory_domain not in {
        "npc_history",
        "player_progression",
        "world_progression",
    }:
        raise InvalidMemoryQueryError("memory_domain 必须是已注册领域")
    if not is_non_negative_wire_integer(query.game_day_index):
        raise InvalidMemoryQueryError("game_day_index 必须是 0..2147483647 的 integer")
    if not is_non_negative_wire_integer(query.cutoff_day_index):
        raise InvalidMemoryQueryError("cutoff_day_index 必须是 0..2147483647 的 integer")
    if query.cutoff_day_index > query.game_day_index - 1:
        raise InvalidMemoryQueryError("cutoff 必须不晚于 game_day_index - 1")
    if not is_wire_integer(query.friendship_points):
        raise InvalidMemoryQueryError("friendship_points 必须是 Int32 范围内的 integer")
    if not is_non_negative_wire_integer(query.resolved_memory_revision):
        raise InvalidMemoryQueryError("resolved_memory_revision 必须是非负 wire integer")
    if not is_non_negative_wire_integer(query.resolved_retrieval_state_revision):
        raise InvalidMemoryQueryError("resolved_retrieval_state_revision 必须是非负 wire integer")
    if not is_non_negative_wire_integer(query.cooldown_days):
        raise InvalidMemoryQueryError("cooldown_days 必须是 0..2147483647 的 integer")
    if not is_wire_integer(query.limit) or not 1 <= query.limit <= 5:
        raise InvalidMemoryQueryError("limit 必须是 1..5 之间的 integer")


def _domain_memory_conditions(query: DomainMemoryQuery) -> list[ColumnElement[bool]]:
    """构造领域 repository 的 SQL 硬过滤，domain 与 audience 必须同时写死。"""

    if query.memory_domain == "npc_history":
        visibility = and_(
            MemoryRecord.audience_scope == "npc",
            MemoryRecord.audience_npc_id == query.npc_id,
        )
    else:
        visibility = and_(
            MemoryRecord.audience_scope == "public",
            MemoryRecord.audience_npc_id.is_(None),
        )
    conditions: list[ColumnElement[bool]] = [
        MemoryRecord.save_id == query.save_id,
        MemoryRecord.player_id == query.player_id,
        MemoryRecord.classification_status == "active",
        MemoryRecord.memory_domain == query.memory_domain,
        visibility,
        MemoryRecord.occurred_day_index <= query.cutoff_day_index,
        or_(
            MemoryRecord.expires_day_index.is_(None),
            MemoryRecord.expires_day_index >= query.cutoff_day_index,
        ),
        or_(
            MemoryRecord.min_friendship_points.is_(None),
            MemoryRecord.min_friendship_points <= query.friendship_points,
        ),
        or_(
            MemoryRecord.max_friendship_points.is_(None),
            MemoryRecord.max_friendship_points >= query.friendship_points,
        ),
    ]
    if query.cooldown_days > 0:
        cooldown_threshold = query.cutoff_day_index - query.cooldown_days
        conditions.append(
            or_(
                MemoryRecord.last_used_day_index.is_(None),
                MemoryRecord.last_used_day_index <= cooldown_threshold,
            )
        )
    return conditions


def _validate_event_history_query(query: EventHistoryQuery) -> None:
    """验证历史查询的可信上下文、意图字段与资源上限。"""

    _validate_agent_evidence_common(
        save_id=query.save_id,
        player_id=query.player_id,
        npc_id=query.npc_id,
        game_day_index=query.game_day_index,
        cutoff_day_index=query.cutoff_day_index,
        friendship_points=query.friendship_points,
        relationship_stage=query.relationship_stage,
        topics=query.topics,
        since_day_index=query.since_day_index,
        cooldown_days=query.cooldown_days,
        limit=query.limit,
        max_limit=5,
    )
    if not query.topics and not query.event_types:
        raise InvalidMemoryQueryError("event history 必须至少提供 topic 或 event type")
    _validate_bounded_terms(query.event_types, field_name="event_types", max_items=5)


def _validate_progression_query(query: ProgressionContextQuery) -> None:
    """验证公共世界进度查询；空 topics 合法，表示最近的已知进度。"""

    _validate_agent_evidence_common(
        save_id=query.save_id,
        player_id=query.player_id,
        npc_id=query.npc_id,
        game_day_index=query.game_day_index,
        cutoff_day_index=query.cutoff_day_index,
        friendship_points=query.friendship_points,
        relationship_stage=query.relationship_stage,
        topics=query.topics,
        since_day_index=query.since_day_index,
        cooldown_days=query.cooldown_days,
        limit=query.limit,
        max_limit=3,
    )


def _validate_agent_evidence_common(
    *,
    save_id: str,
    player_id: str,
    npc_id: str,
    game_day_index: int,
    cutoff_day_index: int,
    friendship_points: int,
    relationship_stage: str,
    topics: tuple[str, ...],
    since_day_index: int | None,
    cooldown_days: int,
    limit: int,
    max_limit: int,
) -> None:
    """集中验证两个 Agent evidence 查询共享的硬边界。"""

    for field_name, value in (
        ("save_id", save_id),
        ("player_id", player_id),
        ("npc_id", npc_id),
        ("relationship_stage", relationship_stage),
    ):
        if not isinstance(value, str) or not value or value != value.strip():
            raise InvalidMemoryQueryError(f"{field_name} 必须是无首尾空白的非空字符串")
    if not is_non_negative_wire_integer(game_day_index):
        raise InvalidMemoryQueryError("game_day_index 必须是 0..2147483647 的 integer")
    if not is_non_negative_wire_integer(cutoff_day_index):
        raise InvalidMemoryQueryError("cutoff_day_index 必须是 0..2147483647 的 integer")
    if cutoff_day_index > game_day_index - 1:
        raise InvalidMemoryQueryError("cutoff 必须不晚于 game_day_index - 1")
    if not is_wire_integer(friendship_points):
        raise InvalidMemoryQueryError("friendship_points 必须是 Int32 范围内的 integer")
    if not is_non_negative_wire_integer(cooldown_days):
        raise InvalidMemoryQueryError("cooldown_days 必须是 0..2147483647 的 integer")
    if not is_wire_integer(limit) or not 1 <= limit <= max_limit:
        raise InvalidMemoryQueryError(f"limit 必须是 1..{max_limit} 之间的 integer")
    if since_day_index is not None:
        if not is_non_negative_wire_integer(since_day_index):
            raise InvalidMemoryQueryError("since_day_index 必须是非负 wire integer 或 null")
        if since_day_index > cutoff_day_index:
            raise InvalidMemoryQueryError("since_day_index 不得晚于 cutoff")
    _validate_bounded_terms(topics, field_name="topics", max_items=5)


def _validate_bounded_terms(
    values: tuple[str, ...],
    *,
    field_name: str,
    max_items: int,
) -> None:
    """拒绝无界、重复或带首尾空白的模型查询词。"""

    if not isinstance(values, tuple) or len(values) > max_items:
        raise InvalidMemoryQueryError(f"{field_name} 最多包含 {max_items} 项")
    normalized: set[str] = set()
    for value in values:
        if not isinstance(value, str) or not value or value != value.strip() or len(value) > 64:
            raise InvalidMemoryQueryError(f"{field_name} 每项必须是 1..64 字符的无边缘空白文本")
        folded = value.casefold()
        if folded in normalized:
            raise InvalidMemoryQueryError(f"{field_name} 不得包含重复项")
        normalized.add(folded)


def _agent_evidence_conditions(
    *,
    save_id: str,
    player_id: str,
    npc_id: str,
    cutoff_day_index: int,
    friendship_points: int,
    cooldown_days: int,
    since_day_index: int | None,
    public_only: bool,
) -> list[ColumnElement[bool]]:
    """构造两个 Agent 查询共享的 SQL 硬过滤条件。"""

    if public_only:
        visibility = and_(
            MemoryRecord.audience_scope == "public",
            MemoryRecord.audience_npc_id.is_(None),
        )
    else:
        visibility = or_(
            and_(
                MemoryRecord.audience_scope == "public",
                MemoryRecord.audience_npc_id.is_(None),
            ),
            and_(
                MemoryRecord.audience_scope == "npc",
                MemoryRecord.audience_npc_id == npc_id,
            ),
        )

    conditions: list[ColumnElement[bool]] = [
        MemoryRecord.save_id == save_id,
        MemoryRecord.player_id == player_id,
        visibility,
        MemoryRecord.occurred_day_index <= cutoff_day_index,
        or_(
            MemoryRecord.expires_day_index.is_(None),
            MemoryRecord.expires_day_index >= cutoff_day_index,
        ),
        or_(
            MemoryRecord.min_friendship_points.is_(None),
            MemoryRecord.min_friendship_points <= friendship_points,
        ),
        or_(
            MemoryRecord.max_friendship_points.is_(None),
            MemoryRecord.max_friendship_points >= friendship_points,
        ),
    ]
    if since_day_index is not None:
        conditions.append(MemoryRecord.occurred_day_index >= since_day_index)
    if cooldown_days > 0:
        cooldown_threshold = cutoff_day_index - cooldown_days
        conditions.append(
            or_(
                MemoryRecord.last_used_day_index.is_(None),
                MemoryRecord.last_used_day_index <= cooldown_threshold,
            )
        )
    return conditions


def _matches_relationship_stage(row: MemoryRecord, relationship_stage: str) -> bool:
    """执行 JSON 关系阶段硬过滤，保持与 ``search_memories`` 相同语义。"""

    return not row.relationship_stages_json or relationship_stage in row.relationship_stages_json


def _matches_any_topic(row: MemoryRecord, topics: tuple[str, ...]) -> bool:
    """以大小写不敏感方式匹配摘要或标签；空 topics 表示不增加主题过滤。"""

    if not topics:
        return True
    searchable_values = (row.summary, *row.tags_json)
    folded_values = tuple(str(value).casefold() for value in searchable_values)
    return any(
        topic.casefold() in searchable_value
        for topic in topics
        for searchable_value in folded_values
    )


def _evidence_from_memory(row: MemoryRecord) -> EvidenceRecord:
    """把已过硬过滤的 ORM record 复制为脱离 session 的不可变 evidence。"""

    return EvidenceRecord(
        evidence_id=row.memory_id,
        evidence_type=row.event_type,
        source_event_ids=(row.event_id,),
        summary=row.summary,
        occurred_day_index=row.occurred_day_index,
        tags=tuple(row.tags_json),
        visibility_scope=(
            "public" if row.audience_scope == "public" else f"npc:{row.audience_npc_id}"
        ),
    )


def _domain_candidate_from_memory(
    row: MemoryRecord,
    *,
    query: DomainMemoryQuery,
) -> DomainMemoryCandidate:
    """复制并复核一条 SQL 已选中的 active 领域行。

    SQL 条件负责最小数据面，本函数是返回前的第二道边界。它尤其防止被外部
    SQLite 工具绕过 CHECK 后留下的错误 namespace/audience 进入模型 evidence。
    """

    expected_public = query.memory_domain != "npc_history"
    if expected_public:
        visibility_matches = row.audience_scope == "public" and row.audience_npc_id is None
        visibility_scope = "public"
    else:
        visibility_matches = row.audience_scope == "npc" and row.audience_npc_id == query.npc_id
        visibility_scope = f"npc:{query.npc_id}"
    if (
        row.classification_status != "active"
        or row.memory_domain != query.memory_domain
        or row.memory_kind is None
        or row.subject_namespace is None
        or row.subject_value is None
        or not visibility_matches
    ):
        raise MemoryRetrievalCorruptionError("selected memory classification is invalid")
    return DomainMemoryCandidate(
        memory_id=row.memory_id,
        event_id=row.event_id,
        event_type=row.event_type,
        summary=row.summary,
        tags=tuple(row.tags_json),
        occurred_day_index=row.occurred_day_index,
        importance=row.importance,
        use_count=row.use_count,
        memory_domain=query.memory_domain,
        memory_kind=row.memory_kind,
        subject_namespace=row.subject_namespace,
        subject_value=row.subject_value,
        visibility_scope=visibility_scope,
    )


def _evidence_from_domain_candidate(candidate: DomainMemoryCandidate) -> EvidenceRecord:
    """把最终可见候选转换为带完整分类字段的统一 evidence。"""

    return EvidenceRecord(
        evidence_id=candidate.memory_id,
        evidence_type=candidate.event_type,
        source_event_ids=(candidate.event_id,),
        summary=candidate.summary,
        occurred_day_index=candidate.occurred_day_index,
        tags=candidate.tags,
        visibility_scope=candidate.visibility_scope,
        memory_domain=candidate.memory_domain,
        memory_kind=candidate.memory_kind,
        subject_namespace=candidate.subject_namespace,
        subject_value=candidate.subject_value,
    )


def _memory_score(row: MemoryRecord, query_tags: frozenset[str]) -> float:
    """为已过硬过滤的候选计算确定性分数。

    importance 是主项；标签命中提高当前语境相关性；绝对日提供轻量 recency；
    已使用次数作为 novelty penalty。同分继续按日期与 memory_id 稳定排序。
    """

    tag_match_count = len(query_tags.intersection(row.tags_json))
    return (
        row.importance * 100.0
        + tag_match_count * 10.0
        + row.occurred_day_index * 0.01
        - row.use_count * 0.5
    )
