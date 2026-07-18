"""Phase 4 确定性台词生成服务的行为测试。

这些测试使用真实迁移后的 SQLite 存储验证 cache 与并发语义，但生成器始终是
本地 scripted callable，不访问网络、模型或 Provider。测试重点是服务边界：
preflight、单项失败隔离、幂等键去重、水位门禁和稳定错误分类。
"""

from __future__ import annotations

import asyncio
from collections import Counter

import pytest
from sqlalchemy import select

from stardew_npc_agent.dialogue_service import (
    DialogueBatchEnvelopeError,
    DialogueGeneratorDecision,
    DialogueService,
    DialogueServiceUnavailableError,
    MemoryRevisionNotReadyError,
)
from stardew_npc_agent.generation_key import build_generation_key
from stardew_npc_agent.profiles import get_npc_profile
from stardew_npc_agent.schemas import (
    DialogueGenerationBatchRequest,
    DialogueGenerationItem,
)
from stardew_npc_agent.storage import (
    DialogueGenerationRecord,
    SqliteStorage,
    StorageUnavailableError,
)


def _item(
    npc_id: str,
    *,
    task_id: str | None = None,
    text: str | None = None,
    source_hash: str | None = None,
) -> dict[str, object]:
    """构造一个普通、无 Dialogue DSL 的单 NPC wire item。"""

    normalized_id = npc_id.lower()
    return {
        "task_id": task_id or f"task-{normalized_id}",
        "npc_id": npc_id,
        "source_dialogue": {
            "asset_name": f"Characters/Dialogue/{npc_id}",
            "dialogue_key": "Mon",
            "text": text or f"{npc_id} 今天想在镇上散步。",
            "source_hash": source_hash or f"sha256:source-{normalized_id}",
        },
        "relationship_snapshot": {
            "friendship_points": 750,
            "relationship_stage": "friend",
        },
        "style_examples": ["样例一。", "样例二。", "样例三。"],
        "memory_signals": [],
    }


def _request(
    *items: dict[str, object],
    request_id: str = "request-dialogue-service",
    locale: str = "zh-CN",
    required_memory_revision: int = 0,
    game_day_index: int = 6,
) -> DialogueGenerationBatchRequest:
    """构造已通过共享 Pydantic contract 的批次请求。"""

    return DialogueGenerationBatchRequest.model_validate(
        {
            "schema_version": "1.0",
            "request_id": request_id,
            "save_id": "save-dialogue-service",
            "player_id": "player-dialogue-service",
            "game_day_index": game_day_index,
            "required_memory_revision": required_memory_revision,
            "stable_day_context": {
                "season": "spring",
                "weather": "sunny",
                "locale": locale,
                "progression_signals": {"community_center": "open"},
            },
            "items": list(items) or [_item("Abigail")],
        }
    )


def _with_transport_ids(
    request: DialogueGenerationBatchRequest,
    *,
    request_id: str,
    task_id: str,
) -> DialogueGenerationBatchRequest:
    """只替换应从 generation key 排除的传输标识。"""

    item = request.items[0].model_copy(update={"task_id": task_id})
    return request.model_copy(update={"request_id": request_id, "items": [item]})


@pytest.mark.asyncio
async def test_default_generator_returns_passthrough_and_persists_every_item(
    storage: SqliteStorage,
) -> None:
    """默认服务不得调用模型；两个受支持 NPC 都应得到可缓存 passthrough。"""

    service = DialogueService(storage, max_concurrency=2)
    request = _request(_item("Abigail"), _item("Sebastian"))

    response = await service.generate_batch(request)

    assert response.request_id == request.request_id
    assert response.memory_revision == request.required_memory_revision
    assert [item.task_id for item in response.items] == ["task-abigail", "task-sebastian"]
    assert [item.status for item in response.items] == ["passthrough", "passthrough"]
    assert [item.text for item in response.items] == [None, None]
    assert [item.evidence_ids for item in response.items] == [[], []]
    assert [item.reason_code for item in response.items] == [
        "SCRIPTED_PASSTHROUGH",
        "SCRIPTED_PASSTHROUGH",
    ]
    for result in response.items:
        cached = await storage.get_dialogue_generation_by_key(result.generation_key)
        assert cached is not None
        assert cached.generation_id == result.generation_id
        assert cached.status == "passthrough"


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("batch_request", "expected_reason"),
    [
        (_request(_item("UnknownNpc")), "UNSUPPORTED_NPC"),
        (_request(_item("Abigail"), locale="fr"), "UNSUPPORTED_LOCALE"),
        (
            _request(_item("Abigail", text="今天去矿洞吧。$action giveItem 74")),
            "SOURCE_DIALOGUE_UNSAFE",
        ),
    ],
)
async def test_preflight_skips_unsupported_or_unsafe_items_without_calling_generator(
    storage: SqliteStorage,
    batch_request: DialogueGenerationBatchRequest,
    expected_reason: str,
) -> None:
    """未知 NPC、不支持 locale 与危险原文必须在 generator 前确定性跳过。"""

    generator_calls = 0

    async def forbidden_generator(
        _request: DialogueGenerationBatchRequest,
        _item: DialogueGenerationItem,
    ) -> DialogueGeneratorDecision:
        nonlocal generator_calls
        generator_calls += 1
        raise AssertionError("preflight skipped 项不得调用 generator")

    service = DialogueService(storage, generator=forbidden_generator)

    response = await service.generate_batch(batch_request)

    assert generator_calls == 0
    assert response.items[0].status == "skipped"
    assert response.items[0].reason_code == expected_reason
    assert response.items[0].text is None
    cached = await storage.get_dialogue_generation_by_key(response.items[0].generation_key)
    assert cached is not None


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("asset_name", "dialogue_key"),
    [
        ("Characters/Dialogue/MarriageDialogueAbigail", "Mon"),
        ("Characters/Dialogue/MarriageDialogue", "Mon"),
        ("Characters/Dialogue/Rainy", "Abigail"),
        ("Characters/Dialogue/rainy", "Sebastian"),
        ("Characters/Dialogue/Abigail", "divorced"),
    ],
)
async def test_exact_source_preflight_rejects_unsupported_family_before_generator(
    storage: SqliteStorage,
    asset_name: str,
    dialogue_key: str,
) -> None:
    """后端必须按 exact NPC/asset/key 独立分类，不能只信任游戏侧已做检查。"""

    source = _item("Abigail")
    source_dialogue = dict(source["source_dialogue"])  # type: ignore[arg-type]
    source_dialogue.update(asset_name=asset_name, dialogue_key=dialogue_key)
    source["source_dialogue"] = source_dialogue
    generator_calls = 0

    async def forbidden_generator(
        _request_value: DialogueGenerationBatchRequest,
        _item_value: DialogueGenerationItem,
    ) -> DialogueGeneratorDecision:
        nonlocal generator_calls
        generator_calls += 1
        raise AssertionError("invalid exact source must not reach generator")

    response = await DialogueService(storage, generator=forbidden_generator).generate_batch(
        _request(source)
    )

    assert generator_calls == 0
    assert response.items[0].status == "skipped"
    assert response.items[0].reason_code == "UNSUPPORTED_DIALOGUE_SOURCE"


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("asset_name", "dialogue_key"),
    [
        ("Characters/Dialogue/Abigail", "Mon"),
        ("Characters/Dialogue/rainy", "Abigail"),
    ],
)
async def test_source_preflight_accepts_ordinary_and_rainy_without_extending_v1_wire(
    storage: SqliteStorage,
    asset_name: str,
    dialogue_key: str,
) -> None:
    """source family 由两端推导；合法 v1 DTO 不新增 source_family 字段。"""

    item_payload = _item("Abigail")
    source_dialogue = dict(item_payload["source_dialogue"])  # type: ignore[arg-type]
    source_dialogue.update(asset_name=asset_name, dialogue_key=dialogue_key)
    item_payload["source_dialogue"] = source_dialogue
    generator_calls = 0

    async def counting_generator(
        _request_value: DialogueGenerationBatchRequest,
        _item_value: DialogueGenerationItem,
    ) -> DialogueGeneratorDecision:
        nonlocal generator_calls
        generator_calls += 1
        return DialogueGeneratorDecision(
            status="passthrough",
            text=None,
            reason_code="VALID_SOURCE",
        )

    request = _request(item_payload)
    response = await DialogueService(storage, generator=counting_generator).generate_batch(request)

    assert generator_calls == 1
    assert response.items[0].status == "passthrough"
    assert "source_family" not in request.items[0].model_dump(mode="json")
    assert "source_family" not in request.items[0].source_dialogue.model_dump(mode="json")


@pytest.mark.asyncio
async def test_source_preflight_allows_one_player_name_slot_but_rejects_raw_dsl(
    storage: SqliteStorage,
) -> None:
    """source 的唯一 ``@`` 应进入 generator；第二个槽和其他 DSL 仍提前跳过。"""

    generator_calls = 0

    async def counting_generator(
        _request_value: DialogueGenerationBatchRequest,
        _item_value: DialogueGenerationItem,
    ) -> DialogueGeneratorDecision:
        nonlocal generator_calls
        generator_calls += 1
        return DialogueGeneratorDecision(
            status="passthrough",
            text=None,
            reason_code="VALID_TYPED_SOURCE",
        )

    service = DialogueService(storage, generator=counting_generator)
    accepted = await service.generate_batch(
        _request(_item("Abigail", text="@，今天也想去镇上散步。"))
    )
    duplicate_slot = await service.generate_batch(
        _request(
            _item(
                "Abigail",
                text="@@，今天也想去镇上散步。",
                source_hash="sha256:duplicate-slot",
            )
        )
    )
    endearment = await service.generate_batch(
        _request(
            _item(
                "Abigail",
                text="%endearment，今天也想去镇上散步。",
                source_hash="sha256:endearment",
            )
        )
    )

    assert generator_calls == 1
    assert accepted.items[0].status == "passthrough"
    assert duplicate_slot.items[0].reason_code == "SOURCE_DIALOGUE_UNSAFE"
    assert endearment.items[0].reason_code == "SOURCE_DIALOGUE_UNSAFE"


@pytest.mark.asyncio
async def test_invalid_source_preflight_runs_before_any_cached_terminal_return(
    storage: SqliteStorage,
) -> None:
    """旧 policy 的 cached terminal 不能让 invalid exact source 绕过新 preflight。"""

    valid_response = await DialogueService(storage).generate_batch(_request(_item("Abigail")))
    cached = await storage.get_dialogue_generation_by_key(valid_response.items[0].generation_key)
    assert cached is not None

    class PoisonedCacheStorage:
        """无论新 key 是什么都返回旧 terminal，用于证明调用顺序而非模拟真实索引。"""

        async def get_memory_partition_snapshot(
            self,
            save_id: str,
            player_id: str,
        ):  # type: ignore[no-untyped-def]
            return await storage.get_memory_partition_snapshot(save_id, player_id)

        async def get_dialogue_generation_by_key(
            self,
            _generation_key: str,
        ):  # type: ignore[no-untyped-def]
            return cached

        async def save_dialogue_generation(self, value):  # type: ignore[no-untyped-def]
            return await storage.save_dialogue_generation(value)

    invalid_item = _item("Abigail")
    invalid_source = dict(invalid_item["source_dialogue"])  # type: ignore[arg-type]
    invalid_source["asset_name"] = "Characters/Dialogue/MarriageDialogueAbigail"
    invalid_item["source_dialogue"] = invalid_source
    generator_calls = 0

    async def forbidden_generator(
        _request_value: DialogueGenerationBatchRequest,
        _item_value: DialogueGenerationItem,
    ) -> DialogueGeneratorDecision:
        nonlocal generator_calls
        generator_calls += 1
        raise AssertionError("invalid source must never call generator")

    response = await DialogueService(  # type: ignore[arg-type]
        PoisonedCacheStorage(),
        generator=forbidden_generator,
    ).generate_batch(_request(invalid_item))

    assert generator_calls == 0
    assert response.items[0].status == "skipped"
    assert response.items[0].reason_code == "UNSUPPORTED_DIALOGUE_SOURCE"


@pytest.mark.asyncio
async def test_generator_exception_only_fails_its_item_and_never_leaks_message(
    storage: SqliteStorage,
) -> None:
    """一个 NPC 的 scripted 异常不能中止同批次其他 NPC，也不能进入响应。"""

    secret = "/private/database.sqlite3 SELECT prompt-secret"

    async def partial_generator(
        _request: DialogueGenerationBatchRequest,
        item: DialogueGenerationItem,
    ) -> DialogueGeneratorDecision:
        if item.npc_id == "Abigail":
            raise RuntimeError(secret)
        return DialogueGeneratorDecision(
            status="passthrough",
            text=None,
            reason_code="SCRIPTED_SECOND_ITEM",
        )

    service = DialogueService(storage, generator=partial_generator, max_concurrency=2)
    request = _request(_item("Abigail"), _item("Sebastian"))

    response = await service.generate_batch(request)

    assert [item.status for item in response.items] == ["failed", "passthrough"]
    assert [item.reason_code for item in response.items] == [
        "GENERATOR_FAILED",
        "SCRIPTED_SECOND_ITEM",
    ]
    assert secret not in response.model_dump_json()
    for result in response.items:
        cached = await storage.get_dialogue_generation_by_key(result.generation_key)
        assert cached is not None and cached.status == result.status


@pytest.mark.asyncio
async def test_trusted_generated_requires_explicit_guard_passed_and_is_cached(
    storage: SqliteStorage,
) -> None:
    """Phase 4 测试桩只有显式声明 guard_passed 才能保存 generated 文本。"""

    decisions = iter(
        (
            DialogueGeneratorDecision(
                status="generated",
                text="我今天也想去山边看看。",
                reason_code="TRUSTED_SCRIPTED_GENERATED",
                guard_passed=True,
            ),
            DialogueGeneratorDecision(
                status="generated",
                text="这条文本没有 Guard 声明。",
                reason_code="UNTRUSTED_SCRIPTED_GENERATED",
            ),
        )
    )

    async def scripted_generator(
        _request: DialogueGenerationBatchRequest,
        _item: DialogueGenerationItem,
    ) -> DialogueGeneratorDecision:
        return next(decisions)

    service = DialogueService(storage, generator=scripted_generator)
    accepted = await service.generate_batch(
        _request(_item("Abigail", source_hash="sha256:trusted-generated"))
    )
    rejected = await service.generate_batch(
        _request(_item("Abigail", source_hash="sha256:unguarded-generated"))
    )

    assert accepted.items[0].status == "generated"
    assert accepted.items[0].text == "我今天也想去山边看看。"
    assert accepted.items[0].reason_code == "TRUSTED_SCRIPTED_GENERATED"
    assert rejected.items[0].status == "failed"
    assert rejected.items[0].text is None
    assert rejected.items[0].reason_code == "GENERATOR_DECISION_INVALID"
    accepted_cache = await storage.get_dialogue_generation_by_key(accepted.items[0].generation_key)
    rejected_cache = await storage.get_dialogue_generation_by_key(rejected.items[0].generation_key)
    assert accepted_cache is not None and accepted_cache.status == "generated"
    assert rejected_cache is not None and rejected_cache.status == "failed"


@pytest.mark.asyncio
async def test_service_reparses_guarded_public_template_before_cache_and_response(
    storage: SqliteStorage,
) -> None:
    """generated 公共文本只允许 codec 可逆的零/单 ``@`` 模板。"""

    decisions = iter(
        (
            DialogueGeneratorDecision(
                status="generated",
                text="@，今天也想去镇上散步。",
                reason_code="TYPED_PLAYER_NAME",
                guard_passed=True,
            ),
            DialogueGeneratorDecision(
                status="generated",
                text="@@，今天也想去镇上散步。",
                reason_code="DUPLICATE_RAW_SLOT",
                guard_passed=True,
            ),
        )
    )

    async def generator(
        _request_value: DialogueGenerationBatchRequest,
        _item_value: DialogueGenerationItem,
    ) -> DialogueGeneratorDecision:
        return next(decisions)

    service = DialogueService(storage, generator=generator)
    accepted = await service.generate_batch(
        _request(_item("Abigail", source_hash="sha256:typed-player-name"))
    )
    rejected = await service.generate_batch(
        _request(_item("Abigail", source_hash="sha256:duplicate-output-slot"))
    )

    assert accepted.items[0].status == "generated"
    assert accepted.items[0].text == "@，今天也想去镇上散步。"
    assert rejected.items[0].status == "failed"
    assert rejected.items[0].reason_code == "GENERATOR_DECISION_INVALID"


@pytest.mark.asyncio
async def test_storage_evidence_rejection_preserves_generator_audit_on_failed_fallback(
    storage: SqliteStorage,
) -> None:
    """保存时 evidence 授权失败不能丢掉已支付的 Agent/Guard 审计。"""

    trace = {"trace_version": "test-v1", "candidate": "雨天也不错。"}
    usage = {"usage_version": "test-v1", "total_tokens": 17}
    guard_report = {"guard_version": "test-v1", "passed": True}

    async def generator(
        _request_value: DialogueGenerationBatchRequest,
        _item_value: DialogueGenerationItem,
    ) -> DialogueGeneratorDecision:
        return DialogueGeneratorDecision(
            status="generated",
            text="雨天也不错。",
            reason_code="TRUSTED_BUT_MISSING_EVIDENCE",
            evidence_ids=("memory:does-not-exist",),
            guard_passed=True,
            trace=trace,
            usage=usage,
            guard_report=guard_report,
        )

    result = (
        await DialogueService(storage, generator=generator).generate_batch(
            _request(_item("Abigail", source_hash="sha256:missing-evidence-audit"))
        )
    ).items[0]

    assert result.status == "failed"
    assert result.text is None
    assert result.evidence_ids == []
    assert result.reason_code == "GENERATOR_DECISION_INVALID"
    async with storage.session_factory() as session:
        record = await session.scalar(
            select(DialogueGenerationRecord).where(
                DialogueGenerationRecord.generation_key == result.generation_key
            )
        )
    assert record is not None
    assert record.trace_json == trace
    assert record.usage_json == usage
    assert record.guard_report_json == guard_report


@pytest.mark.asyncio
async def test_sequential_retry_with_new_transport_ids_uses_cached_generation(
    storage: SqliteStorage,
) -> None:
    """request/task ID 改变不应再次执行 generator，但响应要回显本次 task ID。"""

    generator_calls = 0

    async def counting_generator(
        _request: DialogueGenerationBatchRequest,
        _item: DialogueGenerationItem,
    ) -> DialogueGeneratorDecision:
        nonlocal generator_calls
        generator_calls += 1
        return DialogueGeneratorDecision(
            status="passthrough",
            text=None,
            reason_code="COUNTED_PASSTHROUGH",
        )

    service = DialogueService(storage, generator=counting_generator)
    original = _request(_item("Abigail"))
    retry = _with_transport_ids(
        original,
        request_id="request-dialogue-retry",
        task_id="task-dialogue-retry",
    )

    first = await service.generate_batch(original)
    second = await service.generate_batch(retry)

    assert generator_calls == 1
    assert first.items[0].task_id == "task-abigail"
    assert second.items[0].task_id == "task-dialogue-retry"
    assert first.items[0].generation_id == second.items[0].generation_id
    assert first.items[0].generation_key == second.items[0].generation_key
    assert first.items[0].reason_code == second.items[0].reason_code


@pytest.mark.asyncio
async def test_same_key_concurrent_requests_call_generator_once_in_ten_rounds(
    storage: SqliteStorage,
) -> None:
    """十轮高重叠请求验证进程内 keyed lock 去重与等待者安全清理。"""

    generator_calls: Counter[str] = Counter()

    async def delayed_generator(
        _request: DialogueGenerationBatchRequest,
        item: DialogueGenerationItem,
    ) -> DialogueGeneratorDecision:
        generator_calls[item.source_dialogue.source_hash] += 1
        await asyncio.sleep(0.01)
        return DialogueGeneratorDecision(
            status="passthrough",
            text=None,
            reason_code="CONCURRENT_PASSTHROUGH",
        )

    service = DialogueService(storage, generator=delayed_generator, max_concurrency=2)
    for round_index in range(10):
        source_hash = f"sha256:concurrent-round-{round_index}"
        base = _request(_item("Abigail", source_hash=source_hash))
        requests = [
            _with_transport_ids(
                base,
                request_id=f"request-concurrent-{round_index}-{attempt}",
                task_id=f"task-concurrent-{round_index}-{attempt}",
            )
            for attempt in range(12)
        ]

        responses = await asyncio.gather(*(service.generate_batch(value) for value in requests))

        assert generator_calls[source_hash] == 1
        assert len({response.items[0].generation_id for response in responses}) == 1
        assert len({response.items[0].generation_key for response in responses}) == 1
        assert {response.items[0].status for response in responses} == {"passthrough"}
        assert service.active_key_lock_count == 0


@pytest.mark.asyncio
async def test_batch_respects_generator_concurrency_and_preserves_request_order(
    storage: SqliteStorage,
) -> None:
    """fan-out 最多使用配置并发数；完成顺序不得改变响应 item 顺序。"""

    active_calls = 0
    maximum_active_calls = 0
    both_started = asyncio.Event()

    async def coordinated_generator(
        _request: DialogueGenerationBatchRequest,
        item: DialogueGenerationItem,
    ) -> DialogueGeneratorDecision:
        nonlocal active_calls, maximum_active_calls
        active_calls += 1
        maximum_active_calls = max(maximum_active_calls, active_calls)
        if active_calls == 2:
            both_started.set()
        await asyncio.wait_for(both_started.wait(), timeout=1)
        # 让第二项先完成，证明 gather 仍按输入位置聚合。
        if item.npc_id == "Abigail":
            await asyncio.sleep(0.01)
        active_calls -= 1
        return DialogueGeneratorDecision(
            status="passthrough",
            text=None,
            reason_code=f"{item.npc_id.upper()}_PASSTHROUGH",
        )

    service = DialogueService(storage, generator=coordinated_generator, max_concurrency=2)
    response = await service.generate_batch(_request(_item("Abigail"), _item("Sebastian")))

    assert maximum_active_calls == 2
    assert [item.task_id for item in response.items] == ["task-abigail", "task-sebastian"]
    assert [item.reason_code for item in response.items] == [
        "ABIGAIL_PASSTHROUGH",
        "SEBASTIAN_PASSTHROUGH",
    ]


@pytest.mark.asyncio
async def test_two_batches_share_the_service_level_generator_semaphore(
    storage: SqliteStorage,
) -> None:
    """两个并发 HTTP 批次必须共享同一服务实例的 max_concurrency=2 边界。

    每个请求各含两项；如果实现错误地为每个 request 新建 semaphore，峰值会达到 4。
    这里直接并发调用同一 production service，既证明跨请求共享，也不启动真实 HTTP 服务。
    """

    active_calls = 0
    maximum_active_calls = 0
    two_calls_started = asyncio.Event()

    async def coordinated_generator(
        _request_value: DialogueGenerationBatchRequest,
        _item_value: DialogueGenerationItem,
    ) -> DialogueGeneratorDecision:
        nonlocal active_calls, maximum_active_calls
        active_calls += 1
        maximum_active_calls = max(maximum_active_calls, active_calls)
        if active_calls == 2:
            two_calls_started.set()
        await asyncio.wait_for(two_calls_started.wait(), timeout=1)
        await asyncio.sleep(0.01)
        active_calls -= 1
        return DialogueGeneratorDecision(
            status="passthrough",
            text=None,
            reason_code="SHARED_SERVICE_SEMAPHORE",
        )

    service = DialogueService(storage, generator=coordinated_generator, max_concurrency=2)
    first_request = _request(
        _item("Abigail", source_hash="sha256:first-abigail"),
        _item("Sebastian", source_hash="sha256:first-sebastian"),
        request_id="request-first-batch",
    )
    second_request = _request(
        _item("Alex", source_hash="sha256:second-alex"),
        _item("Elliott", source_hash="sha256:second-elliott"),
        request_id="request-second-batch",
    )

    first_response, second_response = await asyncio.gather(
        service.generate_batch(first_request),
        service.generate_batch(second_request),
    )

    assert maximum_active_calls == 2
    assert [item.status for item in first_response.items] == ["passthrough", "passthrough"]
    assert [item.status for item in second_response.items] == ["passthrough", "passthrough"]


@pytest.mark.asyncio
async def test_required_memory_revision_is_checked_before_generator(
    storage: SqliteStorage,
) -> None:
    """空分区 revision=0 时，required=1 必须整批 409 语义且零 generator 调用。"""

    generator_calls = 0

    async def forbidden_generator(
        _request: DialogueGenerationBatchRequest,
        _item: DialogueGenerationItem,
    ) -> DialogueGeneratorDecision:
        nonlocal generator_calls
        generator_calls += 1
        raise AssertionError("revision 未就绪时不得调用 generator")

    service = DialogueService(storage, generator=forbidden_generator)

    with pytest.raises(MemoryRevisionNotReadyError) as error_info:
        await service.generate_batch(_request(required_memory_revision=1))

    assert generator_calls == 0
    assert str(error_info.value) == "memory revision not ready"
    assert error_info.value.__cause__ is None


@pytest.mark.asyncio
@pytest.mark.parametrize(
    "batch_request",
    [
        _request(
            _item("Abigail", task_id="duplicate-task"),
            _item("Sebastian", task_id="duplicate-task"),
        ),
        _request(
            _item("Abigail", task_id="task-first"),
            _item("Abigail", task_id="task-second", source_hash="sha256:second-source"),
        ),
    ],
)
async def test_duplicate_task_or_npc_id_is_rejected_as_batch_envelope_error(
    storage: SqliteStorage,
    batch_request: DialogueGenerationBatchRequest,
) -> None:
    """重复身份会让响应映射歧义，必须在任何存储/生成副作用前整批拒绝。"""

    service = DialogueService(storage)

    with pytest.raises(DialogueBatchEnvelopeError) as error_info:
        await service.generate_batch(batch_request)

    assert str(error_info.value) == "invalid dialogue batch envelope"


@pytest.mark.asyncio
async def test_storage_unavailable_maps_to_stable_service_error_without_leak() -> None:
    """底层路径、SQL 和异常链不能穿过 DialogueService 的稳定错误边界。"""

    secret = "/private/dialogue.sqlite3 SELECT * FROM memory_partition_states"

    class UnavailableStorage:
        """只实现本测试会触达的读取端口。"""

        async def get_memory_partition_snapshot(
            self,
            _save_id: str,
            _player_id: str,
        ) -> None:
            raise StorageUnavailableError() from RuntimeError(secret)

    service = DialogueService(UnavailableStorage())  # type: ignore[arg-type]

    with pytest.raises(DialogueServiceUnavailableError) as error_info:
        await service.generate_batch(_request())

    assert str(error_info.value) == "service unavailable"
    assert secret not in str(error_info.value)
    assert error_info.value.__cause__ is None


def test_generation_key_for_supported_profile_remains_the_service_cache_identity() -> None:
    """测试辅助断言：服务验收使用的预期 key 来自冻结的公共纯函数。"""

    request = _request(_item("Abigail"))
    profile = get_npc_profile("Abigail")
    assert profile is not None

    expected = build_generation_key(
        request,
        request.items[0],
        profile_version=profile.profile_version,
        memory_projection_version="memory-projection-v3",
        resolved_memory_revision=request.required_memory_revision,
        resolved_retrieval_state_revision=0,
    )

    assert expected.generation_key.startswith("sha256:")
