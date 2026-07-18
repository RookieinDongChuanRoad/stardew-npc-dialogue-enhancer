"""结构化事件、确定性记忆投影和展示 ACK 的业务服务。

``EventService`` 是 FastAPI 路由与 SQLAlchemy 存储之间的编排边界。它只将
经过 Pydantic wire 校验的 DTO 转换为存储输入，不向路由暴露 Session。

记忆摘要全部由本模块的白名单模板派生。模板不调用 LLM，不使用
“昨天/最近”等相对时间，并对事件版本、audience 与 payload 进行二次验证。
无法确定解释的单条事件返回 ``rejected``，不影响同批次的其他合法事件。
"""

from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass
from typing import Any, NoReturn

from stardew_npc_agent.memory_capabilities import MEMORY_CAPABILITIES, MemoryCapability
from stardew_npc_agent.schemas import (
    DisplayAckRequest,
    DisplayAckResponse,
    GameEvent,
    GameEventBatchRequest,
    GameEventBatchResponse,
    GameEventItemResult,
)
from stardew_npc_agent.storage import (
    MAX_EVENT_BATCH_ITEMS,
    DisplayNotAllowedStorageError,
    DisplayReceiptConflictStorageError,
    DisplayReceiptInput,
    EventBatchTooLargeError,
    GenerationNotFoundStorageError,
    MemoryPartitionStateInvalidStorageError,
    MemoryProjection,
    MemoryRevisionExhaustedStorageError,
    PreparedEvent,
    SqliteStorage,
    StorageUnavailableError,
)
from stardew_npc_agent.storage_types import is_wire_integer

# 旧 SMAPI LevelChanged 只通过这一冻结形状进入 legacy alias；其他
# world_progression source 或相似 milestone 一律拒绝，不再投影通用世界文本。
_PLAYER_SKILL_MILESTONE_PATTERN = re.compile(
    r"^skill_(?P<skill_id>[a-z][a-z0-9_]*)_level_(?P<level>[1-9][0-9]*)$"
)
_PLAYER_SKILL_DISPLAY_NAMES: dict[str, str] = {
    "farming": "耕种",
    "fishing": "钓鱼",
    "foraging": "采集",
    "mining": "采矿",
    "combat": "战斗",
}

_RELATIONSHIP_TRANSITION_SUMMARIES: dict[tuple[str, str], str] = {
    ("friendly", "dating"): "开始交往",
    ("dating", "engaged"): "订婚",
    ("engaged", "married"): "结婚",
    ("dating", "friendly"): "结束了交往关系",
    ("engaged", "friendly"): "结束了婚约关系",
    ("married", "divorced"): "离婚",
}

_TOOL_DISPLAY_NAMES: dict[str, str] = {
    "axe": "斧头",
    "pickaxe": "镐子",
    "hoe": "锄头",
    "watering_can": "水壶",
    "pan": "淘金盘",
    "trash_can": "垃圾桶",
}
_TOOL_LEVEL_DISPLAY_NAMES: dict[int, str] = {1: "铜", 2: "钢", 3: "金", 4: "铱"}

_FACILITY_BY_MILESTONE: dict[str, tuple[str, str]] = {
    "public_facility_greenhouse_restored": ("greenhouse", "温室恢复并可以使用了"),
    "public_facility_minecarts_restored": ("minecarts", "矿车交通恢复了"),
    "public_facility_bus_service_restored": ("bus_service", "沙漠巴士恢复运营了"),
    "public_facility_quarry_bridge_restored": ("quarry_bridge", "通往采石场的桥修复了"),
    "public_facility_glittering_boulder_removed": (
        "glittering_boulder",
        "闪光巨石被移除并解锁了淘金",
    ),
}


class MemoryProjectionError(ValueError):
    """单条事件不能由已注册的确定性模板安全投影。"""

    def __init__(self, reason_code: str, message: str) -> None:
        """保存给游戏的稳定机器码与仅供后端诊断的详细信息。"""

        super().__init__(message)
        self.reason_code = reason_code


class DisplayAckNotAllowedError(ValueError):
    """ACK 指向不可展示状态、不合法快照或不可追溯 evidence。"""


class DisplayAckConflictError(ValueError):
    """相同 display receipt ID 被用来表示两个不同的展示事实。"""


class DisplayGenerationNotFoundError(LookupError):
    """ACK URL 中的 generation ID 不存在。"""


class EventServiceUnavailableError(RuntimeError):
    """事件/展示服务的持久化依赖在请求执行期间不可用。"""

    def __init__(self) -> None:
        """向 HTTP 层只暴露稳定类别，不保留可泄露的底层异常消息。"""

        super().__init__("service unavailable")


class MemoryRevisionExhaustedError(ValueError):
    """分区 revision 已无法在 v1 Int32 wire contract 内继续推进。"""

    def __init__(self) -> None:
        """只暴露稳定业务分类，不包含分区身份或底层存储细节。"""

        super().__init__("memory revision exhausted")


class MemoryPartitionStateInvalidError(RuntimeError):
    """既有分区水位已腐化，当前请求不能安全继续。"""

    def __init__(self) -> None:
        """只向 HTTP 层传播稳定分类，不暴露分区身份或持久化实现。"""

        super().__init__("memory partition state invalid")


@dataclass(frozen=True, slots=True)
class _ProjectionTemplateResult:
    """事件类型模板返回的内容字段，不包含分区与身份信息。"""

    summary: str
    tags: tuple[str, ...]
    importance: float
    subject_value: str
    expires_day_index: int | None = None
    relationship_stages: tuple[str, ...] = ()
    min_friendship_points: int | None = None
    max_friendship_points: int | None = None


@dataclass(frozen=True, slots=True)
class _ResolvedProducer:
    """registry 中唯一命中的 producer，以及它是否来自只读 legacy alias。"""

    capability: MemoryCapability
    is_legacy_alias: bool


class EventService:
    """处理事件批次和展示回执，但不把 SQLAlchemy Session 暴露给 HTTP 层。"""

    def __init__(self, storage: SqliteStorage) -> None:
        """注入唯一存储门面，便于测试使用独立临时数据库。"""

        self._storage = storage

    async def is_ready(self) -> bool:
        """返回持久化边界是否已迁移且可安全处理业务请求。"""

        return await self._storage.is_ready()

    async def ingest_batch(self, request: GameEventBatchRequest) -> GameEventBatchResponse:
        """逐项投影并幂等提交一批游戏事件。

        Args:
            request: 已通过 wire contract 校验的单存档/单玩家事件批次。

        Returns:
            保持输入顺序的逐项 ``accepted/duplicate/rejected`` 结果，以及
            本次事务提交后稳定的 revision 和 day watermark。

        边界：投影失败只拒绝对应事件。数据库整体不可用则向上抛出，由 API
        层统一映射为不泄露内部路径的服务错误。
        """

        if len(request.events) > MAX_EVENT_BATCH_ITEMS:
            raise EventBatchTooLargeError(f"事件批次不得超过 {MAX_EVENT_BATCH_ITEMS} 条")

        prepared_events: list[PreparedEvent] = []
        projection_errors: dict[int, MemoryProjectionError] = {}
        prepared_indexes: list[int] = []
        for index, event in enumerate(request.events):
            try:
                projection = project_event_to_memory(
                    request.save_id,
                    request.player_id,
                    event,
                )
            except MemoryProjectionError as error:
                projection_errors[index] = error
                continue
            prepared_events.append(
                PreparedEvent(
                    save_id=request.save_id,
                    player_id=request.player_id,
                    event_id=event.event_id,
                    event_type=event.event_type,
                    event_version=event.event_version,
                    occurred_day_index=event.occurred_day_index,
                    source=event.source,
                    audience_scope=event.audience_scope,
                    audience_npc_id=event.audience_npc_id,
                    payload_json=dict(event.payload),
                    projection=projection,
                )
            )
            prepared_indexes.append(index)

        try:
            stored = await self._storage.ingest_events(
                request.save_id,
                request.player_id,
                prepared_events,
            )
        except MemoryPartitionStateInvalidStorageError:
            raise MemoryPartitionStateInvalidError() from None
        except MemoryRevisionExhaustedStorageError:
            raise MemoryRevisionExhaustedError() from None
        except StorageUnavailableError:
            raise EventServiceUnavailableError() from None
        stored_by_original_index = dict(zip(prepared_indexes, stored.items, strict=True))

        item_results: list[GameEventItemResult] = []
        for index, event in enumerate(request.events):
            projection_error = projection_errors.get(index)
            if projection_error is not None:
                item_results.append(
                    GameEventItemResult(
                        event_id=event.event_id,
                        status="rejected",
                        reason_code=projection_error.reason_code,
                    )
                )
                continue

            storage_result = stored_by_original_index[index]
            if storage_result.status == "accepted":
                item_results.append(
                    GameEventItemResult(
                        event_id=event.event_id,
                        status="accepted",
                        reason_code=None,
                    )
                )
            elif storage_result.status == "duplicate":
                item_results.append(
                    GameEventItemResult(
                        event_id=event.event_id,
                        status="duplicate",
                        reason_code="EVENT_ALREADY_COMMITTED",
                    )
                )
            else:
                item_results.append(
                    GameEventItemResult(
                        event_id=event.event_id,
                        status="rejected",
                        reason_code="EVENT_ID_CONFLICT",
                    )
                )

        return GameEventBatchResponse(
            schema_version=request.schema_version,
            request_id=request.request_id,
            memory_revision=stored.memory_revision,
            committed_through_day_index=stored.committed_through_day_index,
            items=item_results,
        )

    async def acknowledge_display(
        self,
        generation_id: str,
        request: DisplayAckRequest,
    ) -> DisplayAckResponse:
        """幂等确认一条 generated 台词已实际展示。

        业务服务只负责 DTO 转换和稳定异常类型；receipt 插入、evidence
        证明与 memory 消费必须留在存储层的同一事务中。
        """

        storage_input = DisplayReceiptInput(
            display_receipt_id=request.display_receipt_id,
            save_id=request.save_id,
            player_id=request.player_id,
            displayed_day_index=request.displayed_day_index,
            npc_id=request.npc_id,
            source_hash=request.source_hash,
        )
        try:
            status = await self._storage.acknowledge_display(generation_id, storage_input)
        except MemoryPartitionStateInvalidStorageError:
            raise MemoryPartitionStateInvalidError() from None
        except MemoryRevisionExhaustedStorageError:
            raise MemoryRevisionExhaustedError() from None
        except StorageUnavailableError:
            raise EventServiceUnavailableError() from None
        except GenerationNotFoundStorageError as error:
            raise DisplayGenerationNotFoundError(str(error)) from None
        except DisplayReceiptConflictStorageError as error:
            raise DisplayAckConflictError(str(error)) from None
        except DisplayNotAllowedStorageError as error:
            raise DisplayAckNotAllowedError(str(error)) from None

        return DisplayAckResponse(
            schema_version=request.schema_version,
            request_id=request.request_id,
            display_receipt_id=request.display_receipt_id,
            status=status,
        )


def project_event_to_memory(
    save_id: str,
    player_id: str,
    event: GameEvent,
) -> MemoryProjection:
    """将精确命中 capability registry 的结构化事件转换为确定性记忆。

    Args:
        save_id: 事件所属存档，来自批次 envelope 而非 payload。
        player_id: 事件所属玩家，来自批次 envelope 而非 payload。
        event: 经 Pydantic 验证的事件 DTO；开放 payload 仍会在具体模板中严格校验。

    Returns:
        完整、可直接原子写入的 ``MemoryProjection``。

    Raises:
        MemoryProjectionError: 事件类型/版本未注册，audience 错误或 payload
            不足以确定事实。
    """

    resolved = _resolve_registered_producer(event)
    capability = resolved.capability
    _validate_registered_audience(event, capability)
    template = _project_registered_payload(event, resolved)
    _validate_subject_value(capability, template.subject_value)

    return MemoryProjection(
        memory_id=_stable_memory_id(save_id, player_id, event.event_id),
        event_id=event.event_id,
        save_id=save_id,
        player_id=player_id,
        audience_scope=event.audience_scope,
        audience_npc_id=event.audience_npc_id,
        event_type=event.event_type,
        event_version=event.event_version,
        source=event.source,
        payload_json=dict(event.payload),
        classification_status="active",
        memory_domain=capability.domain,
        memory_kind=capability.kind,
        subject_namespace=capability.subject_namespace,
        subject_value=template.subject_value,
        summary=template.summary,
        tags=template.tags,
        importance=template.importance,
        occurred_day_index=event.occurred_day_index,
        expires_day_index=template.expires_day_index,
        relationship_stages=template.relationship_stages,
        min_friendship_points=template.min_friendship_points,
        max_friendship_points=template.max_friendship_points,
    )


def _resolve_registered_producer(event: GameEvent) -> _ResolvedProducer:
    """按 ``event_type + version + source`` 唯一解析正式 producer 或 legacy alias。

    错误码按 type、version、source 分层，便于 SMAPI 把确定性的合同漂移送入
    dead letter。这里不按 payload、摘要或字符串前缀猜测 producer。
    """

    type_known = False
    version_known = False
    matches: list[_ResolvedProducer] = []
    for capability in MEMORY_CAPABILITIES:
        contracts = (
            (
                capability.wire_event_type,
                capability.wire_event_version,
                capability.producer_source,
                False,
            ),
            *(
                (
                    alias.event_type,
                    alias.event_version,
                    alias.producer_source,
                    True,
                )
                for alias in capability.legacy_ingest_aliases
            ),
        )
        for event_type, event_version, producer_source, is_legacy_alias in contracts:
            if event.event_type != event_type:
                continue
            type_known = True
            if event.event_version != event_version:
                continue
            version_known = True
            if event.source == producer_source:
                matches.append(
                    _ResolvedProducer(
                        capability=capability,
                        is_legacy_alias=is_legacy_alias,
                    )
                )

    if len(matches) == 1:
        return matches[0]
    if len(matches) > 1:
        # 这是静态 registry 自身的编程错误，不能把任意一个候选当作业务事实。
        raise RuntimeError("memory producer registry contains an ambiguous contract")
    if not type_known:
        raise MemoryProjectionError(
            "UNSUPPORTED_EVENT_TYPE",
            f"事件类型 {event.event_type} 没有已审核的 producer 合同",
        )
    if not version_known:
        raise MemoryProjectionError(
            "UNSUPPORTED_EVENT_VERSION",
            f"事件 {event.event_type} 不支持版本 {event.event_version}",
        )
    raise MemoryProjectionError(
        "UNSUPPORTED_EVENT_SOURCE",
        f"事件 {event.event_type}/{event.event_version} 的 source 未注册",
    )


def _validate_registered_audience(event: GameEvent, capability: MemoryCapability) -> None:
    """强制 registry 的领域可见性与 wire audience 同时成立。"""

    is_valid_npc = (
        capability.audience_scope == "npc"
        and event.audience_scope == "npc"
        and event.audience_npc_id is not None
    )
    is_valid_public = (
        capability.audience_scope == "public"
        and event.audience_scope == "public"
        and event.audience_npc_id is None
    )
    if not is_valid_npc and not is_valid_public:
        raise MemoryProjectionError(
            "INVALID_EVENT_AUDIENCE",
            f"{capability.kind} 的 audience 不符合 {capability.audience_scope} 领域合同",
        )


def _project_registered_payload(
    event: GameEvent,
    resolved: _ResolvedProducer,
) -> _ProjectionTemplateResult:
    """按已解析 kind 调用唯一严格模板；禁止通用 world/gift 文本兜底。"""

    kind = resolved.capability.kind
    if kind == "gift_given":
        return _project_gift_given(event)
    if kind == "relationship_status_changed":
        return _project_relationship_status_changed(event)
    if kind == "friendship_milestone_reached":
        return _project_friendship_milestone_reached(event)
    if kind == "skill_level_reached":
        return _project_skill_level_reached(event, is_legacy_alias=resolved.is_legacy_alias)
    if kind == "mine_depth_milestone_reached":
        return _project_mine_depth_milestone_reached(event)
    if kind == "tool_upgrade_received":
        return _project_tool_upgrade_received(event)
    if kind == "mastery_claimed":
        return _project_mastery_claimed(event)
    if kind == "public_facility_restored":
        return _project_public_facility_restored(event)
    raise RuntimeError(f"registered memory kind has no projection: {kind}")


def _validate_subject_value(capability: MemoryCapability, subject_value: str) -> None:
    """用同一 registry 复核 projector 产生的 subject，不复制固定枚举。"""

    if not subject_value or subject_value != subject_value.strip():
        _invalid_payload("subject value 必须是非空且无边缘空白的 string")
    allowed = capability.allowed_subject_values
    if allowed is not None and subject_value not in allowed:
        _invalid_payload(f"{capability.kind} subject 不在 capability registry 中")


def _project_gift_given(event: GameEvent) -> _ProjectionTemplateResult:
    """校验并投影唯一受审计 Harmony producer 的 NPC 私有送礼事实。"""

    _validate_exact_payload_keys(
        event.payload,
        required=frozenset({"item_id", "taste"}),
        optional=frozenset(),
    )
    item_id = _required_payload_string(event.payload, "item_id")
    if not _is_qualified_item_id(item_id):
        _invalid_payload("item_id 必须是 public QualifiedItemId")
    taste = _required_payload_string(event.payload, "taste")
    allowed_tastes = frozenset({"love", "like", "neutral", "dislike", "hate", "stardrop_tea"})
    if taste not in allowed_tastes:
        _invalid_payload("taste 不在六值 accepted-gift 枚举中")
    importance_by_taste = {
        "love": 0.9,
        "like": 0.7,
        "neutral": 0.5,
        "dislike": 0.6,
        "hate": 0.8,
        "stardrop_tea": 0.95,
    }
    npc_id = event.audience_npc_id
    if npc_id is None:  # generic audience validation 已保证；保留类型收窄与防御边界。
        raise RuntimeError("validated npc audience unexpectedly has no NPC ID")
    return _ProjectionTemplateResult(
        summary=(
            f"第 {event.occurred_day_index} 天，玩家向 {npc_id} "
            f"赠送了 {item_id}，礼物反应为 {taste}。"
        ),
        tags=("gift", f"item:{item_id}", f"taste:{taste}"),
        importance=importance_by_taste[taste],
        subject_value=item_id,
    )


def _project_relationship_status_changed(event: GameEvent) -> _ProjectionTemplateResult:
    """只陈述六种已验收关系迁移，不推断礼物或剧情原因。"""

    _validate_exact_payload_keys(
        event.payload,
        required=frozenset({"old_status", "new_status"}),
        optional=frozenset(),
    )
    old_status = _required_payload_string(event.payload, "old_status")
    new_status = _required_payload_string(event.payload, "new_status")
    transition_summary = _RELATIONSHIP_TRANSITION_SUMMARIES.get((old_status, new_status))
    if transition_summary is None:
        _invalid_payload("relationship status transition 未注册")
    npc_id = event.audience_npc_id
    if npc_id is None:
        raise RuntimeError("validated npc audience unexpectedly has no NPC ID")
    return _ProjectionTemplateResult(
        summary=f"第 {event.occurred_day_index} 天，玩家与 {npc_id} {transition_summary}。",
        tags=(
            "relationship_history",
            f"relationship:{new_status}",
            f"from:{old_status}",
            f"to:{new_status}",
        ),
        importance=0.95,
        subject_value=new_status,
    )


def _project_friendship_milestone_reached(event: GameEvent) -> _ProjectionTemplateResult:
    """投影 durable checkpoint 证明的首次四心朋友里程碑。"""

    _validate_exact_payload_keys(
        event.payload,
        required=frozenset({"milestone_id", "threshold_points"}),
        optional=frozenset(),
    )
    milestone_id = _required_payload_string(event.payload, "milestone_id")
    threshold_points = _required_payload_integer(event.payload, "threshold_points")
    if milestone_id != "friend" or threshold_points != 1000:
        _invalid_payload("friendship milestone 必须是 friend/1000")
    npc_id = event.audience_npc_id
    if npc_id is None:
        raise RuntimeError("validated npc audience unexpectedly has no NPC ID")
    return _ProjectionTemplateResult(
        summary=f"第 {event.occurred_day_index} 天，玩家与 {npc_id} 首次达到四心朋友里程碑。",
        tags=("friendship", "milestone:friend", "hearts:4"),
        importance=0.8,
        subject_value=milestone_id,
    )


def _project_skill_level_reached(
    event: GameEvent,
    *,
    is_legacy_alias: bool,
) -> _ProjectionTemplateResult:
    """把新技能事件或唯一 legacy milestone 还原为相同 memory kind。

    legacy 没有 old level，因此投影不伪造该字段；新事件则完整校验
    ``0 <= old < new <= 10``。两条路径共享摘要、标签和分类。
    """

    if is_legacy_alias:
        _validate_exact_payload_keys(
            event.payload,
            required=frozenset({"milestone"}),
            optional=frozenset(),
        )
        milestone = _required_payload_string(event.payload, "milestone")
        match = _PLAYER_SKILL_MILESTONE_PATTERN.fullmatch(milestone)
        if match is None:
            _invalid_payload("legacy LevelChanged milestone 形状无效")
        skill_id = match.group("skill_id")
        new_level = int(match.group("level"))
        if new_level > 10:
            _invalid_payload("legacy LevelChanged level 必须在 1..10")
    else:
        _validate_exact_payload_keys(
            event.payload,
            required=frozenset({"skill_id", "old_level", "new_level"}),
            optional=frozenset(),
        )
        skill_id = _required_payload_string(event.payload, "skill_id")
        old_level = _required_payload_integer(event.payload, "old_level")
        new_level = _required_payload_integer(event.payload, "new_level")
        if old_level < 0 or new_level > 10 or new_level <= old_level:
            _invalid_payload("skill levels 必须满足 0 <= old < new <= 10")

    display_name = _PLAYER_SKILL_DISPLAY_NAMES.get(skill_id)
    if display_name is None:
        _invalid_payload("LevelChanged skill 不在五种原版技能中")

    return _ProjectionTemplateResult(
        summary=(
            f"第 {event.occurred_day_index} 天，玩家的{display_name}技能提升到 {new_level} 级。"
        ),
        tags=(
            "progression",
            "actor:player",
            f"skill:{skill_id}",
            f"level:{new_level}",
            f"milestone:skill_{skill_id}_level_{new_level}",
        ),
        importance=0.85,
        subject_value=skill_id,
    )


def _project_mine_depth_milestone_reached(event: GameEvent) -> _ProjectionTemplateResult:
    """严格校验普通矿井/骷髅洞穴展示深度，不把 observed depth 写入摘要。"""

    _validate_exact_payload_keys(
        event.payload,
        required=frozenset({"mine_id", "milestone_depth", "observed_depth"}),
        optional=frozenset(),
    )
    mine_id = _required_payload_string(event.payload, "mine_id")
    milestone_depth = _required_payload_integer(event.payload, "milestone_depth")
    observed_depth = _required_payload_integer(event.payload, "observed_depth")
    if mine_id == "the_mines":
        valid_milestone = 5 <= milestone_depth <= 120 and milestone_depth % 5 == 0
        valid_observation = milestone_depth <= observed_depth <= 120
        mine_display_name = "矿井"
    elif mine_id == "skull_cavern":
        valid_milestone = milestone_depth in {25, 50} or (
            milestone_depth >= 100 and milestone_depth % 100 == 0
        )
        valid_observation = observed_depth >= milestone_depth
        mine_display_name = "骷髅洞穴"
    else:
        _invalid_payload("mine_id 未注册")
    if not valid_milestone or not valid_observation:
        _invalid_payload("mine milestone/observed depth 组合无效")

    return _ProjectionTemplateResult(
        summary=(
            f"第 {event.occurred_day_index} 天，玩家在{mine_display_name}"
            f"到达了第 {milestone_depth} 层里程碑。"
        ),
        tags=(
            "progression",
            "actor:player",
            f"mine:{mine_id}",
            f"milestone_depth:{milestone_depth}",
        ),
        importance=0.85,
        subject_value=mine_id,
    )


def _project_tool_upgrade_received(event: GameEvent) -> _ProjectionTemplateResult:
    """只投影已领取的六类 1..4 级工具升级。"""

    _validate_exact_payload_keys(
        event.payload,
        required=frozenset({"tool_id", "upgrade_level"}),
        optional=frozenset(),
    )
    tool_id = _required_payload_string(event.payload, "tool_id")
    upgrade_level = _required_payload_integer(event.payload, "upgrade_level")
    tool_name = _TOOL_DISPLAY_NAMES.get(tool_id)
    level_name = _TOOL_LEVEL_DISPLAY_NAMES.get(upgrade_level)
    if tool_name is None or level_name is None:
        _invalid_payload("tool ID 或 upgrade level 未注册")
    return _ProjectionTemplateResult(
        summary=f"第 {event.occurred_day_index} 天，玩家取回并拥有了{level_name}{tool_name}。",
        tags=(
            "progression",
            "actor:player",
            f"tool:{tool_id}",
            f"upgrade_level:{upgrade_level}",
        ),
        importance=0.8,
        subject_value=tool_id,
    )


def _project_mastery_claimed(event: GameEvent) -> _ProjectionTemplateResult:
    """投影五种原版技能之一已领取的精通奖励。"""

    _validate_exact_payload_keys(
        event.payload,
        required=frozenset({"skill_id"}),
        optional=frozenset(),
    )
    skill_id = _required_payload_string(event.payload, "skill_id")
    display_name = _PLAYER_SKILL_DISPLAY_NAMES.get(skill_id)
    if display_name is None:
        _invalid_payload("mastery skill 不在五种原版技能中")
    return _ProjectionTemplateResult(
        summary=f"第 {event.occurred_day_index} 天，玩家领取了{display_name}技能的精通奖励。",
        tags=("progression", "actor:player", "mastery", f"skill:{skill_id}"),
        importance=0.9,
        subject_value=skill_id,
    )


def _project_public_facility_restored(event: GameEvent) -> _ProjectionTemplateResult:
    """只接受五个路线中立 public facility milestone。"""

    _validate_exact_payload_keys(
        event.payload,
        required=frozenset({"milestone"}),
        optional=frozenset(),
    )
    milestone = _required_payload_string(event.payload, "milestone")
    facility = _FACILITY_BY_MILESTONE.get(milestone)
    if facility is None:
        _invalid_payload("public facility milestone 未注册")
    facility_id, fact_summary = facility
    return _ProjectionTemplateResult(
        summary=f"第 {event.occurred_day_index} 天，{fact_summary}。",
        tags=("progression", f"facility:{facility_id}", f"milestone:{milestone}"),
        importance=0.9,
        subject_value=facility_id,
    )


def _required_payload_string(payload: dict[str, Any], field_name: str) -> str:
    """读取一个非空、无边缘空白的 payload 字符串，不做静默修改。"""

    value = payload.get(field_name)
    if not isinstance(value, str) or not value or value != value.strip():
        _invalid_payload(f"{field_name} 必须是非空且无边缘空白的 string")
    return value


def _required_payload_integer(payload: dict[str, Any], field_name: str) -> int:
    """读取严格 Int32；显式拒绝 ``bool``、float、string 与任意超界整数。"""

    value = payload.get(field_name)
    if not is_wire_integer(value):
        _invalid_payload(f"{field_name} 必须是 Int32 integer")
    return value


def _is_qualified_item_id(value: str) -> bool:
    """验证 ``(<type>)<item-id>`` 边界，同时允许未来自定义 type identifier。"""

    closing_parenthesis = value.find(")")
    return (
        value.startswith("(") and closing_parenthesis > 1 and closing_parenthesis < len(value) - 1
    )


def _validate_exact_payload_keys(
    payload: dict[str, Any],
    *,
    required: frozenset[str],
    optional: frozenset[str],
) -> None:
    """强制一个事件版本的 payload 字段集合精确匹配已审核模板。

    JSON Schema 为了支持多个 event type，只能把外层 ``payload`` 定义为开放
    object。因此具体 ``event_type + event_version`` 必须在投影时拒绝未知字段；
    否则 producer 忘记升级版本时，后端会静默忽略新语义并产生不完整事实。
    """

    actual = frozenset(payload)
    missing = required - actual
    unknown = actual - required - optional
    if missing:
        _invalid_payload(f"payload 缺少必填字段: {','.join(sorted(missing))}")
    if unknown:
        _invalid_payload(f"payload 包含未知字段: {','.join(sorted(unknown))}")


def _invalid_payload(message: str) -> NoReturn:
    """用统一机器码抛出 payload 错误，不向游戏返回具体内部字段。"""

    raise MemoryProjectionError("INVALID_EVENT_PAYLOAD", message)


def _stable_memory_id(save_id: str, player_id: str, event_id: str) -> str:
    """用无歧义 JSON array 编码分区三元组，再生成稳定 SHA-256 ID。

    不能用任何一个可能出现在业务 ID 内部的分隔符直接 join，否则不同
    ``(save_id, player_id, event_id)`` 可能编码为同一字节串。JSON array 保留
    元素边界，紧凑 separators 与 UTF-8 则保证跨进程稳定。
    """

    canonical_identity = json.dumps(
        [save_id, player_id, event_id],
        ensure_ascii=False,
        separators=(",", ":"),
    ).encode("utf-8")
    return f"memory:{hashlib.sha256(canonical_identity).hexdigest()}"
