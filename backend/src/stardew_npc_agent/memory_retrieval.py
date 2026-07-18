"""领域 memory 候选的确定性 scope 排序与 bucket 多样化。

本模块是纯函数层：不访问数据库、不读取存档、不调用模型，也不执行授权。
repository 必须先完成分区、日期、关系、冷却、分类和 audience 硬过滤，再把
候选复制成 :class:`DomainMemoryCandidate`。这里仅使用可信原台词、locale 和
版本化静态 capability phrases 排序，绝不从 memory summary 猜测主题。
"""

from __future__ import annotations

import unicodedata
from collections import defaultdict
from collections.abc import Sequence
from dataclasses import dataclass

from stardew_npc_agent.memory_capabilities import (
    MEMORY_CAPABILITIES,
    MemoryCapability,
    MemoryDomain,
    retrieval_bucket_key,
)

ScopeScore = tuple[int, int, int]


class MemoryRetrievalCorruptionError(RuntimeError):
    """候选分类与静态 registry 不一致，Retriever 必须 fail closed。"""


@dataclass(frozen=True, slots=True)
class DomainMemoryCandidate:
    """已通过 repository 硬过滤、等待纯排序的不可变候选。

    ``summary`` 只会在最终 evidence 中原样展示，排序逻辑不得读取它。
    ``visibility_scope`` 是 repository 防御性复核后的规范值：公共事实为
    ``public``，NPC 私有事实为 ``npc:<current NPC id>``。
    """

    memory_id: str
    event_id: str
    event_type: str
    summary: str
    tags: tuple[str, ...]
    occurred_day_index: int
    importance: float
    use_count: int
    memory_domain: MemoryDomain
    memory_kind: str
    subject_namespace: str
    subject_value: str
    visibility_scope: str


@dataclass(frozen=True, slots=True)
class _RankedCandidate:
    """候选与其静态派生排序元数据；不会离开本模块。"""

    candidate: DomainMemoryCandidate
    capability: MemoryCapability
    bucket_key: str
    scope_score: ScopeScore


def rank_domain_memory_candidates(
    candidates: Sequence[DomainMemoryCandidate],
    *,
    source_dialogue_text: str,
    locale: str,
    limit: int = 5,
    registry: tuple[MemoryCapability, ...] = MEMORY_CAPABILITIES,
) -> list[DomainMemoryCandidate]:
    """按规格的 scope、bucket representative 与受控回填返回 Top-K。

    Args:
        candidates: 已通过全部授权硬过滤的候选快照。
        source_dialogue_text: runtime 冻结的原版台词；模型不能覆盖。
        locale: runtime 冻结的原台词 locale，精确选择 registry phrases。
        limit: 返回上限，领域工具合同固定不超过 5。
        registry: 构建时静态 registry；测试可传入 target active 副本或 cap fixture。
    Returns:
        最多 ``limit`` 条、顺序逐字可复现的原候选对象。
    Raises:
        ValueError: 可信 runtime 字段或资源上限非法。
        MemoryRetrievalCorruptionError: 候选分类、可见性或 registry 本身漂移。
    """

    if not isinstance(source_dialogue_text, str) or not source_dialogue_text:
        raise ValueError("source_dialogue_text 必须是非空字符串")
    if not isinstance(locale, str) or not locale or locale != locale.strip():
        raise ValueError("locale 必须是无首尾空白的非空字符串")
    if isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= 5:
        raise ValueError("limit 必须是 1..5 的 integer")

    capabilities_by_kind = _index_registry(registry)
    normalized_source = _normalize_for_scope(source_dialogue_text)
    seen_memory_ids: set[str] = set()
    ranked: list[_RankedCandidate] = []
    for candidate in candidates:
        if candidate.memory_id in seen_memory_ids:
            raise MemoryRetrievalCorruptionError("duplicate memory id in retrieval candidates")
        seen_memory_ids.add(candidate.memory_id)

        capability = capabilities_by_kind.get(candidate.memory_kind)
        _validate_candidate(candidate, capability)
        assert capability is not None  # narrowed by the fail-closed validator above
        try:
            bucket_key = retrieval_bucket_key(capability, candidate.subject_value)
        except ValueError as exc:
            raise MemoryRetrievalCorruptionError("invalid retrieval bucket contract") from exc
        ranked.append(
            _RankedCandidate(
                candidate=candidate,
                capability=capability,
                bucket_key=bucket_key,
                scope_score=_scope_score(
                    normalized_source,
                    capability.scope_phrases(
                        locale=locale,
                        subject_value=candidate.subject_value,
                    ),
                ),
            )
        )

    buckets: dict[str, list[_RankedCandidate]] = defaultdict(list)
    for item in ranked:
        buckets[item.bucket_key].append(item)
    for bucket_items in buckets.values():
        bucket_items.sort(key=_within_bucket_sort_key)

    representatives = [items[0] for items in buckets.values()]
    representatives.sort(key=_bucket_sort_key)
    selected = representatives[:limit]
    if len(selected) < limit:
        selected.extend(_refill_candidates(buckets, selected, limit=limit))
    return [item.candidate for item in selected]


def _index_registry(
    registry: tuple[MemoryCapability, ...],
) -> dict[str, MemoryCapability]:
    """构建 kind 索引，并拒绝测试或调用方传入重复 registry。"""

    result: dict[str, MemoryCapability] = {}
    for capability in registry:
        if capability.kind in result:
            raise MemoryRetrievalCorruptionError("duplicate kind in memory capability registry")
        result[capability.kind] = capability
    return result


def _normalize_for_scope(value: str) -> str:
    """使用 Unicode NFKC 与语言无关 casefold 构造可审计匹配文本。"""

    return unicodedata.normalize("NFKC", value).casefold()


def _scope_score(normalized_source: str, phrases: tuple[str, ...]) -> ScopeScore:
    """只从静态 phrases 与可信原台词计算三元 scope score。

    phrase 先做与原文相同的规范化，再按规范化后的 code point 长度计分。
    去重发生在规范化之后，避免大小写或全半角别名被重复计数。
    """

    normalized_phrases = {
        normalized
        for phrase in phrases
        if (normalized := _normalize_for_scope(phrase)) and normalized in normalized_source
    }
    if not normalized_phrases:
        return (0, 0, 0)
    return (1, max(len(phrase) for phrase in normalized_phrases), len(normalized_phrases))


def _validate_candidate(
    candidate: DomainMemoryCandidate,
    capability: MemoryCapability | None,
) -> None:
    """防御性复核分类与 audience；任何漂移都不能退化为宽松结果。"""

    if capability is None:
        raise MemoryRetrievalCorruptionError("candidate memory kind is not registered")
    if (
        candidate.memory_domain != capability.domain
        or candidate.subject_namespace != capability.subject_namespace
        or not candidate.subject_value
        or candidate.subject_value != candidate.subject_value.strip()
        or "\x00" in candidate.subject_value
    ):
        raise MemoryRetrievalCorruptionError("candidate classification mismatches registry")
    if (
        capability.allowed_subject_values is not None
        and candidate.subject_value not in capability.allowed_subject_values
    ):
        raise MemoryRetrievalCorruptionError("candidate subject is not registered")
    if capability.subject_value_policy == "qualified_item_id":
        closing_parenthesis = candidate.subject_value.find(")")
        if not (
            candidate.subject_value.startswith("(")
            and 1 < closing_parenthesis < len(candidate.subject_value) - 1
        ):
            raise MemoryRetrievalCorruptionError("candidate gift subject is not qualified")

    if capability.audience_scope == "public":
        audience_matches = candidate.visibility_scope == "public"
    else:
        audience_matches = candidate.visibility_scope.startswith("npc:") and len(
            candidate.visibility_scope
        ) > len("npc:")
    if not audience_matches:
        raise MemoryRetrievalCorruptionError("candidate audience mismatches registry")


def _descending_scope_key(score: ScopeScore) -> tuple[int, int, int]:
    """把三元降序 score 转换为 Python 升序 ``sort`` key。"""

    return (-score[0], -score[1], -score[2])


def _within_bucket_sort_key(item: _RankedCandidate) -> tuple[object, ...]:
    """实现 scope→日期→importance→低使用次数→ID 的 bucket 内顺序。"""

    return (
        *_descending_scope_key(item.scope_score),
        -item.candidate.occurred_day_index,
        -item.candidate.importance,
        item.candidate.use_count,
        item.candidate.memory_id,
    )


def _bucket_sort_key(item: _RankedCandidate) -> tuple[object, ...]:
    """按 representative 排序 bucket，scope 之后优先 importance 和日期。"""

    return (
        *_descending_scope_key(item.scope_score),
        -item.candidate.importance,
        -item.candidate.occurred_day_index,
        item.bucket_key,
    )


def _remaining_sort_key(item: _RankedCandidate) -> tuple[object, ...]:
    """实现 representative 之后的稳定全局回填顺序。"""

    return (
        *_descending_scope_key(item.scope_score),
        -item.candidate.importance,
        -item.candidate.occurred_day_index,
        item.candidate.use_count,
        item.candidate.memory_id,
    )


def _refill_candidates(
    buckets: dict[str, list[_RankedCandidate]],
    selected: list[_RankedCandidate],
    *,
    limit: int,
) -> list[_RankedCandidate]:
    """在每条 capability 的显式 bucket cap 内回填剩余候选。

    当前 registry cap 均为 1，因此通常不会回填。保留这一阶段是为了将来只有在
    独立评测支持时提高某类上限，同时不修改算法或模型 Schema。
    """

    selected_ids = {item.candidate.memory_id for item in selected}
    selected_count_by_bucket: dict[str, int] = defaultdict(int)
    for item in selected:
        selected_count_by_bucket[item.bucket_key] += 1

    remaining = [
        item
        for bucket_items in buckets.values()
        for item in bucket_items
        if item.candidate.memory_id not in selected_ids
        and selected_count_by_bucket[item.bucket_key] < item.capability.max_candidates_per_bucket
    ]
    remaining.sort(key=_remaining_sort_key)

    refill: list[_RankedCandidate] = []
    for item in remaining:
        if len(selected) + len(refill) >= limit:
            break
        current_count = selected_count_by_bucket[item.bucket_key]
        if current_count >= item.capability.max_candidates_per_bucket:
            continue
        refill.append(item)
        selected_count_by_bucket[item.bucket_key] = current_count + 1
    return refill
