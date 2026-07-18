"""每日台词生成的确定性编排、幂等与持久化服务。

``DialogueService`` 是共享 HTTP DTO 与 SQLite 存储之间的扁平编排边界。它不
创建 Agent、Prompt 或 Provider，也不在模型等待期间持有数据库事务；默认
generator 永远返回 ``passthrough``。Agent-backed generator 可在服务边界之外
完成 Agent → Guard → 一次 Repair 并返回带审计的终态，但任何 ``generated``
仍必须显式声明 ``guard_passed=True``，随后由 storage 再做 evidence 授权。

服务遵守三个重要运行边界：

1. 批次先核对记忆水位，再开始任何 generator 调用；
2. 同一 ``generation_key`` 在单进程内通过引用计数锁池去重，锁内二次读 cache；
3. generator 在数据库事务之外等待，终态只通过存储门面的短事务落盘；
4. 整批 deadline 包含 semaphore 排队，超时项独立保存 failed，不把合法批次变成 500。
"""

from __future__ import annotations

import asyncio
import json
import math
import re
from collections.abc import AsyncIterator, Awaitable, Callable, Mapping
from contextlib import asynccontextmanager
from dataclasses import dataclass, field
from types import MappingProxyType
from typing import Any, Literal, Protocol, TypeAlias, cast, runtime_checkable

from stardew_npc_agent.dialogue_source_policy import (
    DIALOGUE_SOURCE_POLICY_VERSION,
    classify_dialogue_source,
)
from stardew_npc_agent.dialogue_template import (
    DISPLAY_TOKEN_POLICY_VERSION,
    DialogueTemplateError,
    parse_game_template,
    render_game_template,
)
from stardew_npc_agent.generation_key import (
    GENERATION_KEY_FORMAT_VERSION,
    MEMORY_PROJECTION_VERSION,
    SCRIPTED_MODEL_CONFIGURATION,
    SCRIPTED_PROMPT_VERSION,
    GenerationKeyResult,
    build_generation_key,
)
from stardew_npc_agent.memory_capabilities import (
    EVENT_PRODUCER_CAPABILITY_VERSION,
    MEMORY_CLASSIFICATION_VERSION,
    MEMORY_RETRIEVAL_POLICY_VERSION,
)
from stardew_npc_agent.profiles import NPC_PROFILES, NpcProfileMetadata, get_npc_profile
from stardew_npc_agent.schemas import (
    DialogueGenerationBatchRequest,
    DialogueGenerationBatchResponse,
    DialogueGenerationItem,
    DialogueGenerationItemResult,
)
from stardew_npc_agent.storage import (
    DialogueGenerationInput,
    DialogueGenerationSnapshot,
    InvalidDialogueGenerationError,
    MemoryPartitionSnapshot,
    MemoryPartitionStateInvalidStorageError,
    SqliteStorage,
    StorageUnavailableError,
)

# 未支持 NPC 也需要确定性保存 ``skipped`` 终态，因此必须有一个明确、版本化且
# 不冒充真实角色 profile 的 key 输入。未来正式支持该 NPC 后，真实 profile
# version 会自然产生新 key，不会命中这里的跳过记录。
UNSUPPORTED_PROFILE_VERSION = "unsupported-profile-v1"

# generator 的 reason code 是内部机器码，不是自由文本。限制字符集可以防止测试
# 桩或未来实现无意把异常消息、路径、SQL 或 Prompt 作为 reason 返回给游戏。
_REASON_CODE_PATTERN = re.compile(r"[A-Z][A-Z0-9_]{0,99}\Z")


class DialogueBatchEnvelopeError(ValueError):
    """批次 task/NPC 身份重复，无法建立无歧义的逐项响应映射。"""

    def __init__(self) -> None:
        """只保留稳定类别，不回显重复的外部标识。"""

        super().__init__("invalid dialogue batch envelope")


class MemoryRevisionNotReadyError(RuntimeError):
    """后端分区水位尚未达到请求冻结的 required revision。"""

    def __init__(self) -> None:
        """不包含 save/player 或当前水位，避免把分区信息带到 HTTP。"""

        super().__init__("memory revision not ready")


class DialogueServiceUnavailableError(RuntimeError):
    """生成服务的持久化依赖在请求执行期间不可用。"""

    def __init__(self) -> None:
        """向路由只暴露稳定分类，并阻断底层异常链。"""

        super().__init__("service unavailable")


class DialogueGeneratorFailure(RuntimeError):
    """generator 主动返回稳定失败机器码，而不是可展示台词。"""

    def __init__(self, reason_code: str) -> None:
        """验证机器码并隐藏 Agent/Provider 原始错误正文。"""

        if _REASON_CODE_PATTERN.fullmatch(reason_code) is None:
            raise ValueError("generator failure reason_code 非法")
        self.reason_code = reason_code
        super().__init__("dialogue generator rejected")


@dataclass(frozen=True, slots=True)
class DialogueGenerationIdentity:
    """会影响 key 的 Prompt、模型、投影、来源与显示 token 策略版本。

    Mapping 在构造时复制并冻结，防止同一服务实例运行中被外部原地修改，造成
    “相同 version/key 语义变化”。当前所有 Phase 4 可生成 NPC 必须有明确版本；
    未支持 NPC 使用独立 fallback，使未来新增正式 profile 时自然 cache miss。
    """

    prompt_version: str
    model_configuration: str
    memory_projection_version: str
    profile_versions: Mapping[str, str]
    event_producer_capability_version: str = EVENT_PRODUCER_CAPABILITY_VERSION
    memory_classification_version: str = MEMORY_CLASSIFICATION_VERSION
    memory_retrieval_policy_version: str = MEMORY_RETRIEVAL_POLICY_VERSION
    dialogue_source_policy_version: str = DIALOGUE_SOURCE_POLICY_VERSION
    display_token_policy_version: str = DISPLAY_TOKEN_POLICY_VERSION
    unsupported_profile_version: str = UNSUPPORTED_PROFILE_VERSION

    def __post_init__(self) -> None:
        """验证所有版本值并把 mapping 转为只读快照。"""

        for field_name, value in (
            ("prompt_version", self.prompt_version),
            ("model_configuration", self.model_configuration),
            ("memory_projection_version", self.memory_projection_version),
            (
                "event_producer_capability_version",
                self.event_producer_capability_version,
            ),
            ("memory_classification_version", self.memory_classification_version),
            ("memory_retrieval_policy_version", self.memory_retrieval_policy_version),
            ("dialogue_source_policy_version", self.dialogue_source_policy_version),
            ("display_token_policy_version", self.display_token_policy_version),
            ("unsupported_profile_version", self.unsupported_profile_version),
        ):
            _validate_generation_identity_value(field_name, value)

        copied_versions = dict(self.profile_versions)
        if not set(NPC_PROFILES).issubset(copied_versions):
            raise ValueError("profile_versions 必须覆盖全部受支持 NPC")
        for npc_id, profile_version in copied_versions.items():
            _validate_generation_identity_value("profile NPC ID", npc_id)
            _validate_generation_identity_value("profile_version", profile_version)
        object.__setattr__(self, "profile_versions", MappingProxyType(copied_versions))

    def profile_version_for(self, npc_id: str, *, supported: bool) -> str:
        """返回当前任务使用的明确 profile version。"""

        if not supported:
            return self.unsupported_profile_version
        try:
            return self.profile_versions[npc_id]
        except KeyError:
            # 构造时已验证当前 registry；若未来 registry 被替换，此处仍 fail closed。
            raise ValueError("supported NPC 缺少 generation profile version") from None


def _validate_generation_identity_value(field_name: str, value: object) -> None:
    """验证进入 generation key/审计 JSON 的短版本字符串。"""

    if (
        not isinstance(value, str)
        or not value
        or value != value.strip()
        or len(value) > 255
        or "\x00" in value
    ):
        raise ValueError(f"{field_name} 必须是 1..255 字符的安全版本字符串")


SCRIPTED_GENERATION_IDENTITY = DialogueGenerationIdentity(
    prompt_version=SCRIPTED_PROMPT_VERSION,
    model_configuration=SCRIPTED_MODEL_CONFIGURATION,
    memory_projection_version=MEMORY_PROJECTION_VERSION,
    profile_versions={npc_id: profile.profile_version for npc_id, profile in NPC_PROFILES.items()},
)


@dataclass(frozen=True, slots=True)
class DialogueGeneratorDecision:
    """generator 返回给服务的规范化候选终态与可选审计数据。

    Attributes:
        status: 允许 ``generated``、``passthrough`` 或显式 ``failed``；``skipped``
            仍只由 deterministic preflight 决定。
        text: 只有 generated 可以携带的候选文本。
        reason_code: 稳定大写机器码，不能携带异常或 Prompt 自由文本。
        evidence_ids: generated 使用的记忆证据；非 generated 必须为空。
        guard_passed: 受信任 generator 的显式 Guard 授权位。默认 false，避免仅凭
            ``status="generated"`` 绕过后续存储不变量。
        trace/usage/guard_report: Agent/Guard pipeline 产生的固定 JSON 审计形状。
            DialogueService 会在保存前复制、验证 JSON 类型和大小；普通 scripted
            generator 可以继续省略这些字段。
    """

    status: Literal["generated", "passthrough", "failed"]
    text: str | None
    reason_code: str
    evidence_ids: tuple[str, ...] = ()
    guard_passed: bool = False
    trace: dict[str, Any] | None = None
    usage: dict[str, Any] | None = None
    guard_report: dict[str, Any] | None = None


DialogueGenerator: TypeAlias = Callable[
    [DialogueGenerationBatchRequest, DialogueGenerationItem],
    Awaitable[DialogueGeneratorDecision],
]


@runtime_checkable
class DialogueGeneratorWithIdentity(Protocol):
    """可调用 generator 可选提供的类型化 generation identity 合同。"""

    @property
    def generation_identity(self) -> DialogueGenerationIdentity:
        """返回稳定 identity 快照。"""

        ...


@runtime_checkable
class DialogueGeneratorWithStorage(Protocol):
    """需要 storage 的 generator 必须暴露其唯一组合根实例。"""

    @property
    def storage(self) -> SqliteStorage:
        """返回工具查询和 evidence 授权必须共同使用的 storage。"""

        ...

    async def __call__(
        self,
        request: DialogueGenerationBatchRequest,
        item: DialogueGenerationItem,
    ) -> DialogueGeneratorDecision:
        """生成一条服务层决策。"""

        ...


@runtime_checkable
class DialogueGeneratorWithMemorySnapshot(Protocol):
    """需要候选检索的 generator 接收服务已冻结的批次级二元快照。"""

    async def generate_with_memory_snapshot(
        self,
        request: DialogueGenerationBatchRequest,
        item: DialogueGenerationItem,
        snapshot: MemoryPartitionSnapshot,
    ) -> DialogueGeneratorDecision:
        """使用同一批次共享的 snapshot 生成一条服务层决策。"""

        ...


@dataclass(frozen=True, slots=True)
class _TerminalDecision:
    """服务内部已经规范化、可直接保存的四态终态。"""

    status: Literal["generated", "passthrough", "skipped", "failed"]
    text: str | None
    reason_code: str
    evidence_ids: tuple[str, ...] = ()
    guard_passed: bool = False
    trace: dict[str, Any] | None = None
    usage: dict[str, Any] | None = None
    guard_report: dict[str, Any] | None = None


@dataclass(frozen=True, slots=True)
class _PreparedGeneration:
    """正常与 timeout 路径共同复用的 profile、版本和 generation key。"""

    profile: NpcProfileMetadata | None
    profile_version: str
    key_result: GenerationKeyResult


@dataclass(slots=True)
class _KeyLockEntry:
    """一个 generation key 的进程内锁与当前持有/等待引用数。"""

    lock: asyncio.Lock = field(default_factory=asyncio.Lock)
    reference_count: int = 0


class _KeyedAsyncLockPool:
    """按 generation key 提供会自动清理的单进程异步锁。

    引用数在等待实际 key lock 前递增，因此锁持有者释放时不会删除仍有等待者
    的 entry；取消等待也会在 ``finally`` 中递减。最后一个引用离开后删除 entry，
    避免运行天数增加时锁字典无限增长。
    """

    def __init__(self) -> None:
        """创建空锁池；构造不会启动任务或绑定网络资源。"""

        self._entries: dict[str, _KeyLockEntry] = {}
        self._entries_guard = asyncio.Lock()

    @property
    def active_key_count(self) -> int:
        """返回当前持有或等待的 key 数，仅用于进程内诊断与回归测试。"""

        return len(self._entries)

    @asynccontextmanager
    async def acquire(self, key: str) -> AsyncIterator[None]:
        """获取指定 key 的锁，并在最后一个引用离开时安全清理 entry。"""

        async with self._entries_guard:
            entry = self._entries.get(key)
            if entry is None:
                entry = _KeyLockEntry()
                self._entries[key] = entry
            entry.reference_count += 1

        try:
            async with entry.lock:
                yield
        finally:
            async with self._entries_guard:
                entry.reference_count -= 1
                if entry.reference_count == 0 and self._entries.get(key) is entry:
                    del self._entries[key]


async def scripted_passthrough_generator(
    _request: DialogueGenerationBatchRequest,
    _item: DialogueGenerationItem,
) -> DialogueGeneratorDecision:
    """默认零费用 generator：不读取 Provider，始终返回正常 passthrough。"""

    return DialogueGeneratorDecision(
        status="passthrough",
        text=None,
        reason_code="SCRIPTED_PASSTHROUGH",
    )


class DialogueService:
    """逐 NPC 生成并缓存每日台词，保留批次顺序和单项失败隔离。"""

    def __init__(
        self,
        storage: SqliteStorage,
        *,
        generator: DialogueGenerator | None = None,
        generation_identity: DialogueGenerationIdentity | None = None,
        max_concurrency: int = 2,
        fallback_memory_cooldown_days: int = 3,
        batch_deadline_seconds: float = 30.0,
    ) -> None:
        """注入存储、scripted generator 与单进程并发边界。

        Args:
            storage: 已由应用或测试创建的 SQLite 门面。服务不拥有、不释放它。
            generator: 可选 async callable；省略时使用零模型 passthrough。
            generation_identity: 显式 Prompt/model/profile 版本。实现了
                ``DialogueGeneratorWithIdentity`` 的 generator 可自行提供；两者同时
                提供但不一致时拒绝，避免 key 与实际实现身份分叉。
            max_concurrency: 同一服务实例同时等待 generator 的最大任务数，范围 1..8。
            fallback_memory_cooldown_days: 未支持 NPC 保存 skipped 记录时使用的非负
                审计值；受支持 NPC 始终使用 profile 自己的冻结值。
            batch_deadline_seconds: 包含 semaphore 排队与 generator 等待的整批预算。
                超时 item 会在 deadline 后用独立短事务保存稳定 failed。
        Raises:
            ValueError: 并发或 fallback cooldown 超出本阶段合同边界。
        """

        if not 1 <= max_concurrency <= 8:
            raise ValueError("max_concurrency 必须位于 1..8")
        if fallback_memory_cooldown_days < 0:
            raise ValueError("fallback_memory_cooldown_days 必须非负")
        if (
            not isinstance(batch_deadline_seconds, (int, float))
            or isinstance(batch_deadline_seconds, bool)
            or not math.isfinite(float(batch_deadline_seconds))
            or not 0.01 <= float(batch_deadline_seconds) <= 120.0
        ):
            raise ValueError("batch_deadline_seconds 必须位于 0.01..120")
        self._storage = storage
        resolved_generator = generator or scripted_passthrough_generator
        if (
            isinstance(resolved_generator, DialogueGeneratorWithStorage)
            and resolved_generator.storage is not storage
        ):
            # 工具 evidence 与保存时授权必须落在同一个组合根。即使两个实例
            # 指向同一 SQLite 文件，也不能依赖隐含路径相等或不同连接生命周期。
            raise ValueError("Agent generator 与 DialogueService 必须使用同一个 storage 实例")
        provided_identity = (
            resolved_generator.generation_identity
            if isinstance(resolved_generator, DialogueGeneratorWithIdentity)
            else None
        )
        if (
            generation_identity is not None
            and provided_identity is not None
            and generation_identity != provided_identity
        ):
            raise ValueError("显式 generation_identity 与 generator identity 不一致")
        self._generator = resolved_generator
        self._generation_identity = (
            generation_identity or provided_identity or SCRIPTED_GENERATION_IDENTITY
        )
        self._generator_semaphore = asyncio.Semaphore(max_concurrency)
        self._fallback_memory_cooldown_days = fallback_memory_cooldown_days
        self._batch_deadline_seconds = float(batch_deadline_seconds)
        self._key_lock_pool = _KeyedAsyncLockPool()

    @property
    def active_key_lock_count(self) -> int:
        """返回当前 keyed lock entry 数，供内存清理回归与本地诊断使用。"""

        return self._key_lock_pool.active_key_count

    async def generate_batch(
        self,
        request: DialogueGenerationBatchRequest,
    ) -> DialogueGenerationBatchResponse:
        """生成一批 NPC 台词，并按请求 item 原顺序返回全部终态。

        水位和 envelope 错误属于整批错误；generator 普通异常与无效决策属于单项
        ``failed``。任何四态终态都先持久化再返回，因此协议重试可以直接命中
        generation cache，而不会再次调用 generator。
        """

        _validate_batch_envelope(request)
        resolved_snapshot = await self._require_memory_revision(request)

        # 所有 item 共享一个绝对 deadline，因而 semaphore 排队时间也计入预算。
        # asyncio.gather 保留 awaitable 输入顺序；单项完成顺序不会改变 response mapping。
        batch_deadline = asyncio.get_running_loop().time() + self._batch_deadline_seconds
        items = await asyncio.gather(
            *(
                self._generate_item_with_batch_deadline(
                    request,
                    item,
                    batch_deadline,
                    resolved_snapshot,
                )
                for item in request.items
            )
        )
        return DialogueGenerationBatchResponse(
            schema_version=request.schema_version,
            request_id=request.request_id,
            # 这里返回请求冻结水位，而不是请求结束时可能继续增长的实时水位。
            memory_revision=request.required_memory_revision,
            items=list(items),
        )

    async def _require_memory_revision(
        self,
        request: DialogueGenerationBatchRequest,
    ) -> MemoryPartitionSnapshot:
        """确认 required 下界，并冻结本批共享的事实/候选二元水位。"""

        try:
            snapshot = await self._storage.get_memory_partition_snapshot(
                request.save_id,
                request.player_id,
            )
        except (StorageUnavailableError, MemoryPartitionStateInvalidStorageError):
            raise DialogueServiceUnavailableError() from None
        if snapshot.memory_revision < request.required_memory_revision:
            raise MemoryRevisionNotReadyError() from None
        return snapshot

    async def _generate_item(
        self,
        request: DialogueGenerationBatchRequest,
        item: DialogueGenerationItem,
        resolved_snapshot: MemoryPartitionSnapshot,
    ) -> DialogueGenerationItemResult:
        """先执行 source preflight，再在 keyed lock 内做 cache/generator/短保存。"""

        prepared = self._prepare_generation(request, item, resolved_snapshot)
        profile = prepared.profile
        profile_version = prepared.profile_version
        key_result = prepared.key_result
        preflight_reason = _preflight_reason(profile, request, item)

        async with self._key_lock_pool.acquire(key_result.generation_key):
            if preflight_reason is not None:
                terminal = _TerminalDecision(
                    status="skipped",
                    text=None,
                    reason_code=preflight_reason,
                )
            else:
                cached = await self._get_cached(key_result.generation_key)
                if cached is not None:
                    return _result_from_cache(item, cached)
                terminal = await self._run_generator(request, item, resolved_snapshot)

            try:
                await self._save_terminal(
                    request,
                    item,
                    profile,
                    profile_version,
                    key_result,
                    terminal,
                    resolved_snapshot,
                )
            except InvalidDialogueGenerationError:
                # generated 的 evidence 授权属于服务可预期的单项业务失败。原保存
                # 事务已经回滚，随后用同一 key 固化不含文本/evidence 的 failed。
                # 非 generated 若仍不满足存储不变量则表示服务自身 bug，不能掩盖。
                if terminal.status != "generated":
                    raise
                terminal = _TerminalDecision(
                    status="failed",
                    text=None,
                    reason_code="GENERATOR_DECISION_INVALID",
                    # Agent/Guard 已在事务外执行并可能产生费用。保存时 evidence
                    # 授权失败只改变最终可展示状态，不能抹掉候选、真实轨迹、
                    # Token 和 Guard 报告，否则最需要排障的失败反而不可审计。
                    trace=terminal.trace,
                    usage=terminal.usage,
                    guard_report=terminal.guard_report,
                )
                await self._save_terminal(
                    request,
                    item,
                    profile,
                    profile_version,
                    key_result,
                    terminal,
                    resolved_snapshot,
                )

            return _result_from_terminal(item, key_result.generation_key, terminal)

    async def _generate_item_with_batch_deadline(
        self,
        request: DialogueGenerationBatchRequest,
        item: DialogueGenerationItem,
        batch_deadline: float,
        resolved_snapshot: MemoryPartitionSnapshot,
    ) -> DialogueGenerationItemResult:
        """在共享绝对 deadline 内运行 item，超时后独立固化安全 failed。

        timeout context 会取消仍在排队或等待模型的协程；keyed lock、semaphore 与数据库
        context manager 随取消正常释放或回滚。deadline 之后只执行一次短 cache 检查与
        保存，避免返回未持久化的虚假终态。
        """

        try:
            async with asyncio.timeout_at(batch_deadline):
                return await self._generate_item(request, item, resolved_snapshot)
        except TimeoutError:
            return await self._save_batch_deadline_failure(
                request,
                item,
                resolved_snapshot,
            )

    async def _save_batch_deadline_failure(
        self,
        request: DialogueGenerationBatchRequest,
        item: DialogueGenerationItem,
        resolved_snapshot: MemoryPartitionSnapshot,
    ) -> DialogueGenerationItemResult:
        """幂等保存批次超时；若竞态中已有终态，则优先返回已保存事实。"""

        prepared = self._prepare_generation(request, item, resolved_snapshot)
        preflight_reason = _preflight_reason(prepared.profile, request, item)
        async with self._key_lock_pool.acquire(prepared.key_result.generation_key):
            if preflight_reason is not None:
                terminal = _TerminalDecision(
                    status="skipped",
                    text=None,
                    reason_code=preflight_reason,
                )
            else:
                cached = await self._get_cached(prepared.key_result.generation_key)
                if cached is not None:
                    return _result_from_cache(item, cached)
                terminal = _TerminalDecision(
                    status="failed",
                    text=None,
                    reason_code="BATCH_DEADLINE_EXCEEDED",
                )
            await self._save_terminal(
                request,
                item,
                prepared.profile,
                prepared.profile_version,
                prepared.key_result,
                terminal,
                resolved_snapshot,
            )
            return _result_from_terminal(
                item,
                prepared.key_result.generation_key,
                terminal,
            )

    def _prepare_generation(
        self,
        request: DialogueGenerationBatchRequest,
        item: DialogueGenerationItem,
        resolved_snapshot: MemoryPartitionSnapshot,
    ) -> _PreparedGeneration:
        """集中计算 profile 与 generation key，防止正常/timeout 路径身份漂移。"""

        profile = get_npc_profile(item.npc_id)
        profile_version = self._generation_identity.profile_version_for(
            item.npc_id,
            supported=profile is not None,
        )
        key_result = build_generation_key(
            request,
            item,
            profile_version=profile_version,
            prompt_version=self._generation_identity.prompt_version,
            model_configuration=self._generation_identity.model_configuration,
            memory_projection_version=self._generation_identity.memory_projection_version,
            resolved_memory_revision=resolved_snapshot.memory_revision,
            resolved_retrieval_state_revision=(resolved_snapshot.retrieval_state_revision),
            event_producer_capability_version=(
                self._generation_identity.event_producer_capability_version
            ),
            memory_classification_version=(self._generation_identity.memory_classification_version),
            memory_retrieval_policy_version=(
                self._generation_identity.memory_retrieval_policy_version
            ),
            dialogue_source_policy_version=(
                self._generation_identity.dialogue_source_policy_version
            ),
            display_token_policy_version=(self._generation_identity.display_token_policy_version),
        )
        return _PreparedGeneration(profile, profile_version, key_result)

    async def _get_cached(self, generation_key: str) -> DialogueGenerationSnapshot | None:
        """读取 immutable cache，并把运行时存储失败折叠为稳定服务错误。"""

        try:
            return await self._storage.get_dialogue_generation_by_key(generation_key)
        except StorageUnavailableError:
            raise DialogueServiceUnavailableError() from None

    async def _run_generator(
        self,
        request: DialogueGenerationBatchRequest,
        item: DialogueGenerationItem,
        resolved_snapshot: MemoryPartitionSnapshot,
    ) -> _TerminalDecision:
        """在全局 semaphore 内运行 generator，并隔离单项普通异常。"""

        try:
            async with self._generator_semaphore:
                if isinstance(self._generator, DialogueGeneratorWithMemorySnapshot):
                    decision = await self._generator.generate_with_memory_snapshot(
                        request,
                        item,
                        resolved_snapshot,
                    )
                else:
                    decision = await self._generator(request, item)
        except DialogueGeneratorFailure as error:
            return _TerminalDecision(
                status="failed",
                text=None,
                reason_code=error.reason_code,
            )
        except Exception:
            # 不捕获 BaseException，因此取消和进程退出语义仍由 asyncio/宿主管理。
            # 业务响应只包含固定机器码，不保留异常对象、消息或 traceback。
            return _TerminalDecision(
                status="failed",
                text=None,
                reason_code="GENERATOR_FAILED",
            )
        return _normalize_generator_decision(decision)

    async def _save_terminal(
        self,
        request: DialogueGenerationBatchRequest,
        item: DialogueGenerationItem,
        profile: NpcProfileMetadata | None,
        profile_version: str,
        key_result: GenerationKeyResult,
        terminal: _TerminalDecision,
        resolved_snapshot: MemoryPartitionSnapshot,
    ) -> None:
        """把一个已规范化终态交给 storage 的独立短事务。"""

        generation_id, trace_id = _stable_generation_identifiers(key_result.generation_key)
        try:
            await self._storage.save_dialogue_generation(
                DialogueGenerationInput(
                    generation_id=generation_id,
                    generation_key=key_result.generation_key,
                    save_id=request.save_id,
                    player_id=request.player_id,
                    game_day_index=request.game_day_index,
                    npc_id=item.npc_id,
                    locale=request.stable_day_context.locale,
                    source_hash=item.source_dialogue.source_hash,
                    relationship_stage=item.relationship_snapshot.relationship_stage,
                    friendship_points=item.relationship_snapshot.friendship_points,
                    memory_cooldown_days=(
                        profile.memory_cooldown_days
                        if profile is not None
                        else self._fallback_memory_cooldown_days
                    ),
                    status=terminal.status,
                    result_text=terminal.text,
                    reason_code=terminal.reason_code,
                    evidence_ids=terminal.evidence_ids,
                    trace_id=trace_id,
                    guard_passed=terminal.guard_passed,
                    input_versions={
                        "generation_key_format": GENERATION_KEY_FORMAT_VERSION,
                        "context_hash": key_result.context_hash,
                        "profile_version": profile_version,
                        "prompt_version": self._generation_identity.prompt_version,
                        "model_configuration": self._generation_identity.model_configuration,
                        "memory_projection_version": (
                            self._generation_identity.memory_projection_version
                        ),
                        "event_producer_capability_version": (
                            self._generation_identity.event_producer_capability_version
                        ),
                        "memory_classification_version": (
                            self._generation_identity.memory_classification_version
                        ),
                        "memory_retrieval_policy_version": (
                            self._generation_identity.memory_retrieval_policy_version
                        ),
                        "dialogue_source_policy_version": (
                            self._generation_identity.dialogue_source_policy_version
                        ),
                        "display_token_policy_version": (
                            self._generation_identity.display_token_policy_version
                        ),
                        "required_memory_revision": request.required_memory_revision,
                        "resolved_memory_revision": resolved_snapshot.memory_revision,
                        "resolved_retrieval_state_revision": (
                            resolved_snapshot.retrieval_state_revision
                        ),
                    },
                    trace=terminal.trace,
                    usage=terminal.usage,
                    guard_report=terminal.guard_report,
                )
            )
        except StorageUnavailableError:
            raise DialogueServiceUnavailableError() from None


def _validate_batch_envelope(request: DialogueGenerationBatchRequest) -> None:
    """在任何存储/生成副作用前拒绝重复 task_id 或 npc_id。"""

    task_ids = [item.task_id for item in request.items]
    npc_ids = [item.npc_id for item in request.items]
    if len(task_ids) != len(set(task_ids)) or len(npc_ids) != len(set(npc_ids)):
        raise DialogueBatchEnvelopeError() from None


def _preflight_reason(
    profile: NpcProfileMetadata | None,
    request: DialogueGenerationBatchRequest,
    item: DialogueGenerationItem,
) -> str | None:
    """返回确定性 skip reason；``None`` 表示可以进入 scripted generator。"""

    if (
        classify_dialogue_source(
            npc_id=item.npc_id,
            asset_name=item.source_dialogue.asset_name,
            dialogue_key=item.source_dialogue.dialogue_key,
        )
        is None
    ):
        return "UNSUPPORTED_DIALOGUE_SOURCE"
    if profile is None:
        return "UNSUPPORTED_NPC"
    if request.stable_day_context.locale not in profile.supported_locales:
        return "UNSUPPORTED_LOCALE"
    try:
        parse_game_template(item.source_dialogue.text)
    except DialogueTemplateError:
        return "SOURCE_DIALOGUE_UNSAFE"
    return None


def _normalize_generator_decision(decision: object) -> _TerminalDecision:
    """验证 scripted decision；任何不满足约束的值都变成稳定 failed。"""

    if not isinstance(decision, DialogueGeneratorDecision):
        return _invalid_generator_decision()
    if _REASON_CODE_PATTERN.fullmatch(decision.reason_code) is None:
        return _invalid_generator_decision()
    if not isinstance(decision.evidence_ids, tuple):
        return _invalid_generator_decision()
    if any(
        not isinstance(evidence_id, str)
        or not evidence_id
        or evidence_id != evidence_id.strip()
        or "\x00" in evidence_id
        for evidence_id in decision.evidence_ids
    ):
        return _invalid_generator_decision()
    if len(decision.evidence_ids) != len(set(decision.evidence_ids)):
        return _invalid_generator_decision()

    try:
        trace = _copy_audit_mapping(decision.trace, field_name="trace", max_characters=65_536)
        usage = _copy_audit_mapping(decision.usage, field_name="usage", max_characters=8_192)
        guard_report = _copy_audit_mapping(
            decision.guard_report,
            field_name="guard_report",
            max_characters=32_768,
        )
    except (TypeError, ValueError, OverflowError):
        return _invalid_generator_decision()

    if decision.status == "generated":
        if (
            not decision.guard_passed
            or decision.text is None
            or not decision.text.strip()
            or decision.text != decision.text.strip()
            or "\x00" in decision.text
        ):
            return _invalid_generator_decision()
        try:
            # Agent/Guard 已处理 typed template；服务在真正保存和返回公共 v1
            # text 前仍做一次独立 round-trip，防止任意 scripted generator 绕过
            # codec 塞入第二个槽或其他 Stardew DSL。
            reparsed_template = parse_game_template(decision.text)
            if render_game_template(reparsed_template) != decision.text:
                return _invalid_generator_decision()
        except DialogueTemplateError:
            return _invalid_generator_decision()
        return _TerminalDecision(
            status="generated",
            text=decision.text,
            reason_code=decision.reason_code,
            evidence_ids=decision.evidence_ids,
            guard_passed=True,
            trace=trace,
            usage=usage,
            guard_report=guard_report,
        )

    if decision.status == "passthrough":
        if decision.text is not None or decision.evidence_ids or decision.guard_passed:
            return _invalid_generator_decision()
        return _TerminalDecision(
            status="passthrough",
            text=None,
            reason_code=decision.reason_code,
            trace=trace,
            usage=usage,
            guard_report=guard_report,
        )
    if decision.status == "failed":
        if decision.text is not None or decision.evidence_ids or decision.guard_passed:
            return _invalid_generator_decision()
        return _TerminalDecision(
            status="failed",
            text=None,
            reason_code=decision.reason_code,
            trace=trace,
            usage=usage,
            guard_report=guard_report,
        )
    return _invalid_generator_decision()


def _copy_audit_mapping(
    value: object,
    *,
    field_name: str,
    max_characters: int,
) -> dict[str, Any] | None:
    """验证并深复制内部审计 JSON，阻止 mutable/non-JSON/NaN 值进入 storage。

    序列化使用紧凑、稳定 key 顺序；长度限制针对本地 SQLite 审计而不是公共 SDK。
    候选或 trace 超界时整个 generator decision 会 fail closed，不会部分持久化。
    """

    if value is None:
        return None
    if not isinstance(value, dict):
        raise TypeError(f"{field_name} 必须是 JSON object")
    encoded = json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
        allow_nan=False,
    )
    if len(encoded) > max_characters:
        raise ValueError(f"{field_name} 超出审计长度边界")
    decoded = json.loads(encoded)
    if not isinstance(decoded, dict):
        raise TypeError(f"{field_name} 必须是 JSON object")
    return cast(dict[str, Any], decoded)


def _invalid_generator_decision() -> _TerminalDecision:
    """构造不包含原始非法值的稳定失败终态。"""

    return _TerminalDecision(
        status="failed",
        text=None,
        reason_code="GENERATOR_DECISION_INVALID",
    )


def _stable_generation_identifiers(generation_key: str) -> tuple[str, str]:
    """从 key digest 派生跨重试稳定、无时间与随机数的 generation/trace ID。"""

    digest = generation_key.removeprefix("sha256:")
    return f"generation:{digest}", f"trace:{digest}"


def _result_from_terminal(
    item: DialogueGenerationItem,
    generation_key: str,
    terminal: _TerminalDecision,
) -> DialogueGenerationItemResult:
    """把刚保存的终态映射为共享 wire response item。"""

    generation_id, trace_id = _stable_generation_identifiers(generation_key)
    return DialogueGenerationItemResult(
        task_id=item.task_id,
        generation_id=generation_id,
        generation_key=generation_key,
        status=terminal.status,
        text=terminal.text,
        source_hash=item.source_dialogue.source_hash,
        reason_code=terminal.reason_code,
        evidence_ids=list(terminal.evidence_ids),
        trace_id=trace_id,
    )


def _result_from_cache(
    item: DialogueGenerationItem,
    cached: DialogueGenerationSnapshot,
) -> DialogueGenerationItemResult:
    """用本次 task ID 包装不可变 cache；generation 身份和终态保持原样。"""

    return DialogueGenerationItemResult(
        task_id=item.task_id,
        generation_id=cached.generation_id,
        generation_key=cached.generation_key,
        status=cached.status,
        text=cached.result_text,
        source_hash=cached.source_hash,
        reason_code=cached.reason_code,
        evidence_ids=list(cached.evidence_ids),
        trace_id=cached.trace_id,
    )
