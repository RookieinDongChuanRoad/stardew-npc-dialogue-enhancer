"""Phase 4 台词生成的确定性幂等键。

本模块只负责把已经通过 Pydantic 合同解析的批次和单 NPC 任务转换为稳定
SHA-256 摘要。它不读取 profile、数据库或网络，也不决定 NPC 是否支持某种
locale；这些职责留给调用方的 preflight。保持纯函数边界后，并发去重、缓存
命中和离线复现都能共享完全相同的键语义。
"""

from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass

from stardew_npc_agent.dialogue_source_policy import DIALOGUE_SOURCE_POLICY_VERSION
from stardew_npc_agent.dialogue_template import DISPLAY_TOKEN_POLICY_VERSION
from stardew_npc_agent.memory_capabilities import (
    EVENT_PRODUCER_CAPABILITY_VERSION,
    MEMORY_CLASSIFICATION_VERSION,
    MEMORY_RETRIEVAL_POLICY_VERSION,
)
from stardew_npc_agent.schemas import (
    DialogueGenerationBatchRequest,
    DialogueGenerationItem,
)

# 这些值是 Phase 4 的生成输入版本，而不是展示文案。Phase 5 替换成 Agent、
# 新 Prompt 或另一模型配置时必须主动更新相应常量，防止命中 scripted 旧结果。
GENERATION_KEY_FORMAT_VERSION = "generation-key-v4"
SCRIPTED_PROMPT_VERSION = "scripted-prompt-v1"
SCRIPTED_MODEL_CONFIGURATION = "scripted-passthrough"

# memory_revision 只表示某个存档/玩家分区已经接受多少条真实事件，不能因后端
# 改写派生摘要而伪造增长。投影规则因此使用独立版本轴；摘要/标签语义变化时
# 更新此常量，既有生成缓存就会自然 miss，同时 SMAPI 的事件 ACK 水位保持真实。
MEMORY_PROJECTION_VERSION = "memory-projection-v3"


@dataclass(frozen=True, slots=True)
class GenerationKeyResult:
    """一次 key 构造产生的完整摘要。

    Attributes:
        generation_key: 覆盖身份、水位、上下文摘要和实现版本的最终幂等键。
        context_hash: 只覆盖模型可见的稳定上下文，供审计输入变化使用。
    """

    generation_key: str
    context_hash: str


def build_generation_key(
    request: DialogueGenerationBatchRequest,
    item: DialogueGenerationItem,
    *,
    profile_version: str,
    prompt_version: str = SCRIPTED_PROMPT_VERSION,
    model_configuration: str = SCRIPTED_MODEL_CONFIGURATION,
    memory_projection_version: str,
    resolved_memory_revision: int,
    resolved_retrieval_state_revision: int,
    event_producer_capability_version: str = EVENT_PRODUCER_CAPABILITY_VERSION,
    memory_classification_version: str = MEMORY_CLASSIFICATION_VERSION,
    memory_retrieval_policy_version: str = MEMORY_RETRIEVAL_POLICY_VERSION,
    dialogue_source_policy_version: str = DIALOGUE_SOURCE_POLICY_VERSION,
    display_token_policy_version: str = DISPLAY_TOKEN_POLICY_VERSION,
) -> GenerationKeyResult:
    """为一个已解析的单 NPC 任务构造 Phase 4 generation key。

    ``request_id`` 和 ``task_id`` 只标识一次传输尝试，所以刻意排除；重试可以
    更换这两个 ID 而继续命中同一结果。其余字段分成两层：完整上下文先形成
    ``context_hash``，再与存档身份、日、水位和实现版本共同形成最终 key。

    Args:
        request: 已通过公开 contract 解析的批次 envelope 与稳定日上下文。
        item: 该批次中一个已解析的 NPC 生成任务。
        profile_version: 当前 NPC profile 的显式版本；人格正文不在 Phase 4。
        prompt_version: Prompt 构造语义版本，Phase 4 默认 scripted v1。
        model_configuration: 会影响生成结果的模型配置身份。
        memory_projection_version: 模型可检索 memory 的确定性投影规则版本。
        resolved_memory_revision: 本批实际冻结的事实水位，不是公开 required 下界。
        resolved_retrieval_state_revision: 本批实际冻结的候选可变状态水位。
        event_producer_capability_version: 游戏侧正式 producer 能力清单版本。
        memory_classification_version: event 到领域/kind/subject 的分类规则版本。
        memory_retrieval_policy_version: 领域候选排序、多样化和上限策略版本。
        dialogue_source_policy_version: ordinary/rainy exact source 分类规则版本。
        display_token_policy_version: typed 玩家名槽解析、Guard 与还原规则版本。
    Returns:
        同时包含 ``sha256:`` 前缀 generation key 与 context hash 的不可变结果。
    """

    if (
        isinstance(resolved_memory_revision, bool)
        or not isinstance(resolved_memory_revision, int)
        or resolved_memory_revision < request.required_memory_revision
        or resolved_memory_revision > 2_147_483_647
    ):
        raise ValueError("resolved_memory_revision 必须是满足 required 下界的 wire integer")
    if (
        isinstance(resolved_retrieval_state_revision, bool)
        or not isinstance(resolved_retrieval_state_revision, int)
        or not 0 <= resolved_retrieval_state_revision <= 2_147_483_647
    ):
        raise ValueError("resolved_retrieval_state_revision 必须是非负 wire integer")

    # source hash 虽然在最终键中单列，完整 source dialogue（特别是正文）仍必须
    # 进入 context。这样错误复用 source hash 或只改 asset/key/text 时也会 miss。
    context_payload = {
        "stable_day_context": request.stable_day_context.model_dump(mode="json"),
        "source_dialogue": item.source_dialogue.model_dump(mode="json"),
        "relationship": item.relationship_snapshot.model_dump(mode="json"),
        "style_examples": item.style_examples,
        "memory_signals": item.memory_signals,
    }
    context_hash = _sha256_json(context_payload)

    generation_payload = {
        "generation_key_format": GENERATION_KEY_FORMAT_VERSION,
        "schema_version": request.schema_version,
        "save_id": request.save_id,
        "player_id": request.player_id,
        "game_day_index": request.game_day_index,
        "npc_id": item.npc_id,
        "source_hash": item.source_dialogue.source_hash,
        "locale": request.stable_day_context.locale,
        "context_hash": context_hash,
        "required_memory_revision": request.required_memory_revision,
        "resolved_memory_revision": resolved_memory_revision,
        "resolved_retrieval_state_revision": resolved_retrieval_state_revision,
        "profile_version": profile_version,
        "prompt_version": prompt_version,
        "model_configuration": model_configuration,
        "memory_projection_version": memory_projection_version,
        "event_producer_capability_version": event_producer_capability_version,
        "memory_classification_version": memory_classification_version,
        "memory_retrieval_policy_version": memory_retrieval_policy_version,
        "dialogue_source_policy_version": dialogue_source_policy_version,
        "display_token_policy_version": display_token_policy_version,
    }
    return GenerationKeyResult(
        generation_key=_sha256_json(generation_payload),
        context_hash=context_hash,
    )


def _sha256_json(value: object) -> str:
    """对项目内 JSON-compatible 值执行稳定 UTF-8 canonical 序列化。

    ``sort_keys=True`` 递归消除 object 插入顺序，JSON array 则由标准库原样保留
    顺序。``ensure_ascii=False`` 让中文等 Unicode 直接以 UTF-8 参与摘要；
    ``allow_nan=False`` 防止非标准 NaN/Infinity 产生不可移植键。
    """

    canonical_bytes = json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
        allow_nan=False,
    ).encode("utf-8")
    return f"sha256:{hashlib.sha256(canonical_bytes).hexdigest()}"
