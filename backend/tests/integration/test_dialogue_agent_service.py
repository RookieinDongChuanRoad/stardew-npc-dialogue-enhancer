"""Agent generation identity 与 DialogueService 安全接入集成测试。"""

from __future__ import annotations

from collections.abc import Sequence
from datetime import UTC, datetime
from typing import Any

import pytest
from langchain_core.callbacks import (
    AsyncCallbackManagerForLLMRun,
    CallbackManagerForLLMRun,
)
from langchain_core.language_models import BaseChatModel
from langchain_core.messages import AIMessage, BaseMessage
from langchain_core.outputs import ChatGeneration, ChatResult
from langchain_core.runnables import Runnable
from pydantic import PrivateAttr
from sqlalchemy import select, update
from typing_extensions import override

from stardew_npc_agent.dialogue_agent import (
    DIALOGUE_DOMAIN_TOOL_NAMES,
    DIALOGUE_PROMPT_VERSION,
    AgentBackedDialogueGenerator,
    DialogueAgentFactory,
    DialogueAgentRunner,
)
from stardew_npc_agent.dialogue_service import (
    SCRIPTED_GENERATION_IDENTITY,
    DialogueGenerationIdentity,
    DialogueGeneratorDecision,
    DialogueGeneratorFailure,
    DialogueService,
)
from stardew_npc_agent.generation_key import (
    SCRIPTED_MODEL_CONFIGURATION,
    SCRIPTED_PROMPT_VERSION,
    build_generation_key,
)
from stardew_npc_agent.profiles import NPC_AGENT_PROFILES, NPC_PROFILES, get_npc_profile
from stardew_npc_agent.schemas import (
    DialogueGenerationBatchRequest,
    DialogueGenerationItem,
)
from stardew_npc_agent.storage import (
    DialogueGenerationRecord,
    DomainMemoryQuery,
    MemoryPartitionSnapshot,
    MemoryPartitionStateRecord,
    MemorySnapshotMismatchStorageError,
    SqliteStorage,
)


class _ServiceScriptedModel(BaseChatModel):
    """Task 4 只需 structured final/异常的零网络测试模型。"""

    _steps: list[AIMessage | Exception] = PrivateAttr()
    _physical_calls: int = PrivateAttr(default=0)
    _message_batches: list[tuple[BaseMessage, ...]] = PrivateAttr(default_factory=list)

    def __init__(self, steps: Sequence[AIMessage | Exception]) -> None:
        super().__init__()
        self._steps = list(steps)

    @property
    def physical_calls(self) -> int:
        return self._physical_calls

    @property
    def message_batches(self) -> tuple[tuple[BaseMessage, ...], ...]:
        """返回模型实际收到的消息快照，供 persona 接线测试只读检查。"""

        return tuple(self._message_batches)

    @property
    @override
    def _llm_type(self) -> str:
        return "stardew-service-scripted-model"

    @override
    def bind_tools(
        self,
        tools: Sequence[dict[str, Any] | type | Any],
        *,
        tool_choice: str | None = None,
        **kwargs: Any,
    ) -> Runnable[Any, BaseMessage]:
        """模型不依赖工具实现，但仍让 create_agent 完成真实绑定。"""

        del tools, tool_choice, kwargs
        return self

    @override
    def _generate(
        self,
        messages: list[BaseMessage],
        stop: list[str] | None = None,
        run_manager: CallbackManagerForLLMRun | None = None,
        **kwargs: Any,
    ) -> ChatResult:
        self._message_batches.append(tuple(messages))
        del stop, run_manager, kwargs
        return self._next_result()

    @override
    async def _agenerate(
        self,
        messages: list[BaseMessage],
        stop: list[str] | None = None,
        run_manager: AsyncCallbackManagerForLLMRun | None = None,
        **kwargs: Any,
    ) -> ChatResult:
        self._message_batches.append(tuple(messages))
        del stop, run_manager, kwargs
        return self._next_result()

    def _next_result(self) -> ChatResult:
        self._physical_calls += 1
        if not self._steps:
            raise AssertionError("service scripted model steps exhausted")
        step = self._steps.pop(0)
        if isinstance(step, Exception):
            raise step
        return ChatResult(generations=[ChatGeneration(message=step)])


def _final(
    decision: str,
    *,
    text: str | None,
    evidence_ids: list[str] | None = None,
    reason_code: str,
) -> AIMessage:
    """构造 ToolStrategy 可解析的 DialogueAgentDecision call。"""

    return AIMessage(
        content="",
        tool_calls=[
            {
                "name": "DialogueAgentDecision",
                "id": "decision-call",
                "args": {
                    "decision": decision,
                    "template": (
                        None
                        if text is None
                        else {
                            "prefix": text,
                            "address_slot": "none",
                            "suffix": "",
                        }
                    ),
                    "evidence_ids": evidence_ids or [],
                    "reason_code": reason_code,
                },
                "type": "tool_call",
            }
        ],
    )


def _request(
    *,
    request_id: str = "request-agent-service",
    task_id: str = "task-agent-service",
    npc_id: str = "Abigail",
) -> DialogueGenerationBatchRequest:
    """构造服务接入共用的合法单 NPC 任务，默认保持既有 Abigail 场景。"""

    return DialogueGenerationBatchRequest.model_validate(
        {
            "schema_version": "1.0",
            "request_id": request_id,
            "save_id": "save-agent-service",
            "player_id": "player-agent-service",
            "game_day_index": 10,
            "required_memory_revision": 0,
            "stable_day_context": {
                "season": "fall",
                "weather": "rain",
                "locale": "zh-CN",
                "progression_signals": {"mine_level": 40},
            },
            "items": [
                {
                    "task_id": task_id,
                    "npc_id": npc_id,
                    "source_dialogue": {
                        "asset_name": f"Characters/Dialogue/{npc_id}",
                        "dialogue_key": "fall_Mon",
                        "text": "雨天待在屋里也不算太糟。",
                        "source_hash": "sha256:agent-service-source",
                    },
                    "relationship_snapshot": {
                        "friendship_points": 750,
                        "relationship_stage": "friend",
                    },
                    "style_examples": ["样本一。", "样本二。", "样本三。"],
                    "memory_signals": [],
                }
            ],
        }
    )


def _profile_versions(suffix: str) -> dict[str, str]:
    """为当前所有 Phase 4 支持 NPC 构造完整、可区分的 identity mapping。"""

    return {npc_id: f"{npc_id.lower()}-{suffix}" for npc_id in NPC_PROFILES}


async def _passthrough_generator(
    _request_value: DialogueGenerationBatchRequest,
    _item: DialogueGenerationItem,
) -> DialogueGeneratorDecision:
    """identity 测试使用的确定性本地 generator。"""

    return DialogueGeneratorDecision(
        status="passthrough",
        text=None,
        reason_code="IDENTITY_TEST_PASSTHROUGH",
    )


class _SnapshotRecordingGenerator:
    """记录服务注入的批次级 snapshot，不读取模型或数据库。"""

    def __init__(self) -> None:
        self.snapshots: list[MemoryPartitionSnapshot] = []

    async def generate_with_memory_snapshot(
        self,
        _request_value: DialogueGenerationBatchRequest,
        _item: DialogueGenerationItem,
        snapshot: MemoryPartitionSnapshot,
    ) -> DialogueGeneratorDecision:
        """保存对象引用并返回合法 passthrough。"""

        self.snapshots.append(snapshot)
        return DialogueGeneratorDecision(
            status="passthrough",
            text=None,
            reason_code="SNAPSHOT_RECORDED",
        )


class _SnapshotMutationProbeGenerator:
    """在 resolve 后推进 retrieval revision，证明后续领域读取会 fail closed。"""

    def __init__(self, storage: SqliteStorage) -> None:
        self._storage = storage
        self.mismatch_observed = False

    async def generate_with_memory_snapshot(
        self,
        request: DialogueGenerationBatchRequest,
        item: DialogueGenerationItem,
        snapshot: MemoryPartitionSnapshot,
    ) -> DialogueGeneratorDecision:
        """模拟并发 evidence-consuming ACK，再用旧 snapshot 发起领域读取。"""

        async with self._storage.session_factory.begin() as session:
            await session.execute(
                update(MemoryPartitionStateRecord)
                .where(
                    MemoryPartitionStateRecord.save_id == request.save_id,
                    MemoryPartitionStateRecord.player_id == request.player_id,
                )
                .values(
                    retrieval_state_revision=(
                        MemoryPartitionStateRecord.retrieval_state_revision + 1
                    )
                )
            )
        try:
            await self._storage.get_domain_memory_candidates(
                DomainMemoryQuery(
                    save_id=request.save_id,
                    player_id=request.player_id,
                    npc_id=item.npc_id,
                    game_day_index=request.game_day_index,
                    cutoff_day_index=request.game_day_index - 1,
                    friendship_points=item.relationship_snapshot.friendship_points,
                    relationship_stage=item.relationship_snapshot.relationship_stage,
                    memory_domain="player_progression",
                    source_dialogue_text=item.source_dialogue.text,
                    locale=request.stable_day_context.locale,
                    resolved_memory_revision=snapshot.memory_revision,
                    resolved_retrieval_state_revision=(snapshot.retrieval_state_revision),
                    cooldown_days=3,
                    limit=5,
                )
            )
        except MemorySnapshotMismatchStorageError:
            self.mismatch_observed = True
        else:
            raise AssertionError("stale resolved snapshot unexpectedly read new candidate state")
        return DialogueGeneratorDecision(
            status="failed",
            text=None,
            reason_code="SNAPSHOT_MISMATCH_CONFIRMED",
        )


@pytest.mark.asyncio
async def test_generation_identity_axes_each_change_service_generation_key(
    storage: SqliteStorage,
) -> None:
    """Prompt、模型、profile、memory 投影任一变化都必须 cache miss。"""

    identities = [
        DialogueGenerationIdentity(
            prompt_version="prompt-v1",
            model_configuration="model-v1",
            memory_projection_version="memory-projection-v2",
            profile_versions=_profile_versions("profile-v1"),
        ),
        DialogueGenerationIdentity(
            prompt_version="prompt-v2",
            model_configuration="model-v1",
            memory_projection_version="memory-projection-v2",
            profile_versions=_profile_versions("profile-v1"),
        ),
        DialogueGenerationIdentity(
            prompt_version="prompt-v1",
            model_configuration="model-v2",
            memory_projection_version="memory-projection-v2",
            profile_versions=_profile_versions("profile-v1"),
        ),
        DialogueGenerationIdentity(
            prompt_version="prompt-v1",
            model_configuration="model-v1",
            memory_projection_version="memory-projection-v2",
            profile_versions=_profile_versions("profile-v2"),
        ),
        DialogueGenerationIdentity(
            prompt_version="prompt-v1",
            model_configuration="model-v1",
            memory_projection_version="memory-projection-v3",
            profile_versions=_profile_versions("profile-v1"),
        ),
        DialogueGenerationIdentity(
            prompt_version="prompt-v1",
            model_configuration="model-v1",
            memory_projection_version="memory-projection-v2",
            profile_versions=_profile_versions("profile-v1"),
            event_producer_capability_version="event-producer-capabilities-v2",
        ),
        DialogueGenerationIdentity(
            prompt_version="prompt-v1",
            model_configuration="model-v1",
            memory_projection_version="memory-projection-v2",
            profile_versions=_profile_versions("profile-v1"),
            memory_classification_version="memory-classification-v2",
        ),
        DialogueGenerationIdentity(
            prompt_version="prompt-v1",
            model_configuration="model-v1",
            memory_projection_version="memory-projection-v2",
            profile_versions=_profile_versions("profile-v1"),
            memory_retrieval_policy_version="memory-retrieval-policy-v2",
        ),
        DialogueGenerationIdentity(
            prompt_version="prompt-v1",
            model_configuration="model-v1",
            memory_projection_version="memory-projection-v2",
            profile_versions=_profile_versions("profile-v1"),
            dialogue_source_policy_version="dialogue-source-policy-v2",
        ),
        DialogueGenerationIdentity(
            prompt_version="prompt-v1",
            model_configuration="model-v1",
            memory_projection_version="memory-projection-v2",
            profile_versions=_profile_versions("profile-v1"),
            display_token_policy_version="display-token-policy-v2",
        ),
    ]
    keys: list[str] = []
    for identity in identities:
        response = await DialogueService(
            storage,
            generator=_passthrough_generator,
            generation_identity=identity,
        ).generate_batch(_request())
        keys.append(response.items[0].generation_key)

    assert len(set(keys)) == 10


@pytest.mark.asyncio
async def test_scripted_and_agent_identities_carry_source_and_display_policy_versions(
    storage: SqliteStorage,
) -> None:
    """两种生产 generator identity 都必须显式暴露同一 source policy 版本轴。"""

    model = _ServiceScriptedModel([])
    agent_generator = AgentBackedDialogueGenerator(
        runner=DialogueAgentRunner(
            DialogueAgentFactory(
                model=model,
                model_configuration="identity-only-model",
            )
        ),
        storage=storage,
        allowed_tools=frozenset(),
    )

    assert (
        SCRIPTED_GENERATION_IDENTITY.dialogue_source_policy_version == "dialogue-source-policy-v1"
    )
    assert (
        agent_generator.generation_identity.dialogue_source_policy_version
        == "dialogue-source-policy-v1"
    )
    assert SCRIPTED_GENERATION_IDENTITY.display_token_policy_version == "display-token-policy-v1"
    assert (
        agent_generator.generation_identity.display_token_policy_version
        == "display-token-policy-v1"
    )
    assert model.physical_calls == 0


@pytest.mark.asyncio
async def test_batch_freezes_one_dual_revision_snapshot_for_all_npc_tasks(
    storage: SqliteStorage,
) -> None:
    """同一批次所有 NPC 必须收到同一个 snapshot 对象和值，并写入各自审计。"""

    async with storage.session_factory.begin() as session:
        session.add(
            MemoryPartitionStateRecord(
                save_id="save-agent-service",
                player_id="player-agent-service",
                memory_revision=4,
                retrieval_state_revision=6,
                committed_through_day_index=8,
                updated_at_utc=datetime.now(UTC),
            )
        )
    request = _request().model_copy(update={"required_memory_revision": 2})
    abigail = request.items[0]
    sebastian = abigail.model_copy(
        update={
            "task_id": "task-sebastian-service",
            "npc_id": "Sebastian",
            "source_dialogue": abigail.source_dialogue.model_copy(
                update={
                    "asset_name": "Characters/Dialogue/Sebastian",
                    "source_hash": "sha256:sebastian-service-source",
                }
            ),
        }
    )
    request = request.model_copy(update={"items": [abigail, sebastian]})
    generator = _SnapshotRecordingGenerator()
    identity = DialogueGenerationIdentity(
        prompt_version="snapshot-test-prompt-v1",
        model_configuration="snapshot-test-model-v1",
        memory_projection_version="memory-projection-v3",
        profile_versions=_profile_versions("snapshot-test"),
    )

    response = await DialogueService(
        storage,
        generator=generator,  # type: ignore[arg-type]
        generation_identity=identity,
    ).generate_batch(request)

    assert response.memory_revision == 2
    assert len(generator.snapshots) == 2
    assert generator.snapshots[0] is generator.snapshots[1]
    assert generator.snapshots[0] == MemoryPartitionSnapshot(
        memory_revision=4,
        retrieval_state_revision=6,
        committed_through_day_index=8,
    )
    async with storage.session_factory() as session:
        records = list((await session.scalars(select(DialogueGenerationRecord))).all())
    assert len(records) == 2
    assert all(record.input_versions_json is not None for record in records)
    assert {
        (
            record.input_versions_json["resolved_memory_revision"],  # type: ignore[index]
            record.input_versions_json["resolved_retrieval_state_revision"],  # type: ignore[index]
        )
        for record in records
    } == {(4, 6)}


@pytest.mark.asyncio
async def test_revision_change_after_batch_resolve_makes_domain_read_fail_closed(
    storage: SqliteStorage,
) -> None:
    """resolve 与工具调用之间的 ACK/事件变化不能被同一批生成静默吸收。"""

    async with storage.session_factory.begin() as session:
        session.add(
            MemoryPartitionStateRecord(
                save_id="save-agent-service",
                player_id="player-agent-service",
                memory_revision=1,
                retrieval_state_revision=0,
                committed_through_day_index=8,
                updated_at_utc=datetime.now(UTC),
            )
        )
    request = _request().model_copy(update={"required_memory_revision": 1})
    generator = _SnapshotMutationProbeGenerator(storage)
    identity = DialogueGenerationIdentity(
        prompt_version="snapshot-probe-prompt-v1",
        model_configuration="snapshot-probe-model-v1",
        memory_projection_version="memory-projection-v3",
        profile_versions=_profile_versions("snapshot-probe"),
    )

    response = await DialogueService(
        storage,
        generator=generator,  # type: ignore[arg-type]
        generation_identity=identity,
    ).generate_batch(request)

    assert generator.mismatch_observed is True
    assert response.items[0].status == "failed"
    assert response.items[0].reason_code == "SNAPSHOT_MISMATCH_CONFIRMED"
    async with storage.session_factory() as session:
        record = await session.scalar(select(DialogueGenerationRecord))
    assert record is not None
    assert record.input_versions_json is not None
    assert record.input_versions_json["resolved_retrieval_state_revision"] == 0


@pytest.mark.asyncio
async def test_default_service_keeps_phase4_identity_and_behavior(
    storage: SqliteStorage,
) -> None:
    """未注入 Agent 时仍必须是 scripted passthrough 与原 Phase 4 key。"""

    request = _request()
    response = await DialogueService(storage).generate_batch(request)
    profile = get_npc_profile("Abigail")
    assert profile is not None
    expected = build_generation_key(
        request,
        request.items[0],
        profile_version=profile.profile_version,
        prompt_version=SCRIPTED_PROMPT_VERSION,
        model_configuration=SCRIPTED_MODEL_CONFIGURATION,
        memory_projection_version="memory-projection-v3",
        resolved_memory_revision=0,
        resolved_retrieval_state_revision=0,
    )

    assert response.items[0].status == "passthrough"
    assert response.items[0].reason_code == "SCRIPTED_PASSTHROUGH"
    assert response.items[0].generation_key == expected.generation_key


@pytest.mark.asyncio
async def test_new_vanilla_npc_uses_scripted_profile_identity(
    storage: SqliteStorage,
) -> None:
    """Alex 必须走正常 scripted passthrough，并把独立 metadata 版本写入 key 审计。"""

    request = _request(npc_id="Alex", task_id="task-alex-scripted")

    response = await DialogueService(storage).generate_batch(request)

    result = response.items[0]
    assert result.status == "passthrough"
    assert result.reason_code == "SCRIPTED_PASSTHROUGH"
    profile = get_npc_profile("Alex")
    assert profile is not None
    assert profile.profile_version == "alex-profile-v1"
    expected = build_generation_key(
        request,
        request.items[0],
        profile_version="alex-profile-v1",
        prompt_version=SCRIPTED_PROMPT_VERSION,
        model_configuration=SCRIPTED_MODEL_CONFIGURATION,
        memory_projection_version="memory-projection-v3",
        resolved_memory_revision=0,
        resolved_retrieval_state_revision=0,
    )
    assert result.generation_key == expected.generation_key
    async with storage.session_factory() as session:
        record = await session.scalar(
            select(DialogueGenerationRecord).where(
                DialogueGenerationRecord.generation_key == result.generation_key
            )
        )
    assert record is not None
    assert record.input_versions_json is not None
    assert record.input_versions_json["profile_version"] == "alex-profile-v1"


@pytest.mark.asyncio
async def test_new_vanilla_npc_agent_uses_independent_identity_and_persona(
    storage: SqliteStorage,
) -> None:
    """Alex Agent key 与实际模型 Prompt 必须共同来自同一个独立 Agent profile。"""

    model = _ServiceScriptedModel(
        [
            _final(
                "passthrough",
                text=None,
                reason_code="NO_VALUABLE_ENHANCEMENT",
            )
        ]
    )
    generator = AgentBackedDialogueGenerator(
        runner=DialogueAgentRunner(
            DialogueAgentFactory(
                model=model,
                model_configuration="scripted-alex-model-v1",
            )
        ),
        storage=storage,
        allowed_tools=frozenset(),
    )
    request = _request(npc_id="Alex", task_id="task-alex-agent")

    response = await DialogueService(storage, generator=generator).generate_batch(request)

    result = response.items[0]
    assert result.status == "passthrough"
    assert result.reason_code == "NO_VALUABLE_ENHANCEMENT"
    profile = NPC_AGENT_PROFILES["Alex"]
    assert profile.profile_version == "alex-agent-profile-v1"
    expected = build_generation_key(
        request,
        request.items[0],
        profile_version="alex-agent-profile-v1",
        prompt_version=DIALOGUE_PROMPT_VERSION,
        model_configuration="scripted-alex-model-v1",
        memory_projection_version="memory-projection-v3",
        resolved_memory_revision=0,
        resolved_retrieval_state_revision=0,
    )
    assert result.generation_key == expected.generation_key
    assert model.message_batches
    model_prompt = "\n".join(
        str(message.content) for message_batch in model.message_batches for message in message_batch
    )
    assert profile.persona in model_prompt
    async with storage.session_factory() as session:
        record = await session.scalar(
            select(DialogueGenerationRecord).where(
                DialogueGenerationRecord.generation_key == result.generation_key
            )
        )
    assert record is not None
    assert record.input_versions_json is not None
    assert record.input_versions_json["profile_version"] == "alex-agent-profile-v1"


@pytest.mark.asyncio
async def test_agent_adapter_direct_entry_rejects_invalid_exact_source_before_model(
    storage: SqliteStorage,
) -> None:
    """绕过 DialogueService 的独立 adapter 调用仍必须复用 exact source policy。"""

    model = _ServiceScriptedModel([])
    generator = AgentBackedDialogueGenerator(
        runner=DialogueAgentRunner(
            DialogueAgentFactory(
                model=model,
                model_configuration="invalid-source-must-not-call-model",
            )
        ),
        storage=storage,
        allowed_tools=frozenset(),
    )
    request = _request()
    item = request.items[0]
    invalid_item = item.model_copy(
        update={
            "source_dialogue": item.source_dialogue.model_copy(
                update={"asset_name": "Characters/Dialogue/MarriageDialogueAbigail"}
            )
        }
    )
    invalid_request = request.model_copy(update={"items": [invalid_item]})
    snapshot = await storage.get_memory_partition_snapshot(
        request.save_id,
        request.player_id,
    )

    with pytest.raises(DialogueGeneratorFailure) as error_info:
        await generator.generate_with_memory_snapshot(
            invalid_request,
            invalid_item,
            snapshot,
        )

    assert error_info.value.reason_code == "AGENT_DIALOGUE_SOURCE_UNSUPPORTED"
    assert model.physical_calls == 0


@pytest.mark.asyncio
async def test_agent_passthrough_uses_agent_identity_and_cache_without_second_model_call(
    storage: SqliteStorage,
) -> None:
    """Agent passthrough 是正常成功态，并继续复用既有 generation cache。"""

    model = _ServiceScriptedModel(
        [
            _final(
                "passthrough",
                text=None,
                reason_code="NO_VALUABLE_ENHANCEMENT",
            )
        ]
    )
    factory = DialogueAgentFactory(
        model=model,
        model_configuration="scripted-service-model-v1",
    )
    generator = AgentBackedDialogueGenerator(
        runner=DialogueAgentRunner(factory),
        storage=storage,
        allowed_tools=DIALOGUE_DOMAIN_TOOL_NAMES,
    )
    service = DialogueService(storage, generator=generator)
    first_request = _request()
    retry_request = _request(request_id="transport-retry", task_id="retry-task")

    first = await service.generate_batch(first_request)
    second = await service.generate_batch(retry_request)

    assert model.physical_calls == 1
    assert first.items[0].status == second.items[0].status == "passthrough"
    assert first.items[0].reason_code == "NO_VALUABLE_ENHANCEMENT"
    assert first.items[0].generation_key == second.items[0].generation_key
    assert second.items[0].task_id == "retry-task"
    expected_profile_version = NPC_AGENT_PROFILES["Abigail"].profile_version
    expected_key = build_generation_key(
        first_request,
        first_request.items[0],
        profile_version=expected_profile_version,
        prompt_version=DIALOGUE_PROMPT_VERSION,
        model_configuration="scripted-service-model-v1",
        memory_projection_version="memory-projection-v3",
        resolved_memory_revision=0,
        resolved_retrieval_state_revision=0,
    )
    assert first.items[0].generation_key == expected_key.generation_key
    pre_activation_key = build_generation_key(
        first_request,
        first_request.items[0],
        profile_version=expected_profile_version,
        prompt_version="dialogue-agent-prompt-v4",
        model_configuration="scripted-service-model-v1",
        memory_projection_version="memory-projection-v3",
        resolved_memory_revision=0,
        resolved_retrieval_state_revision=0,
    )
    assert DIALOGUE_PROMPT_VERSION == "dialogue-agent-prompt-v6"
    assert first.items[0].generation_key != pre_activation_key.generation_key

    async with storage.session_factory() as session:
        record = await session.scalar(
            select(DialogueGenerationRecord).where(
                DialogueGenerationRecord.generation_key == first.items[0].generation_key
            )
        )
    assert record is not None
    assert record.input_versions_json is not None
    assert record.input_versions_json["profile_version"] == expected_profile_version
    assert record.input_versions_json["prompt_version"] == DIALOGUE_PROMPT_VERSION
    assert record.input_versions_json["model_configuration"] == "scripted-service-model-v1"
    assert record.input_versions_json["memory_projection_version"] == "memory-projection-v3"
    assert record.input_versions_json["resolved_memory_revision"] == 0
    assert record.input_versions_json["resolved_retrieval_state_revision"] == 0
    assert (
        record.input_versions_json["event_producer_capability_version"]
        == "event-producer-capabilities-v1"
    )
    assert record.input_versions_json["memory_classification_version"] == "memory-classification-v1"
    assert (
        record.input_versions_json["memory_retrieval_policy_version"]
        == "memory-retrieval-policy-v1"
    )
    assert (
        record.input_versions_json["dialogue_source_policy_version"] == "dialogue-source-policy-v1"
    )
    assert record.input_versions_json["display_token_policy_version"] == "display-token-policy-v1"
    assert record.guard_passed is False
    assert record.trace_json is not None
    assert record.trace_json["trace_version"] == "dialogue-generation-trace-v4"
    assert record.trace_json["agent"]["decision"]["decision"] == "passthrough"
    assert record.usage_json is not None
    assert record.guard_report_json is not None
    assert record.guard_report_json["bypass_reason"] == "PASSTHROUGH"


@pytest.mark.asyncio
async def test_agent_rewrite_with_unobserved_evidence_fails_without_repair(
    storage: SqliteStorage,
) -> None:
    """虚构 evidence 属于不可修复 Guard 失败，不能进入 Repair 或 generated。"""

    model = _ServiceScriptedModel(
        [
            _final(
                "rewrite",
                text="这场雨让我想起那块紫水晶。",
                evidence_ids=["memory:model-claim"],
                reason_code="RELEVANT_SHARED_MEMORY",
            )
        ]
    )
    factory = DialogueAgentFactory(
        model=model,
        model_configuration="scripted-service-model-v1",
    )
    generator = AgentBackedDialogueGenerator(
        runner=DialogueAgentRunner(factory),
        storage=storage,
        allowed_tools=frozenset(),
    )

    response = await DialogueService(storage, generator=generator).generate_batch(_request())

    result = response.items[0]
    assert result.status == "failed"
    assert result.text is None
    assert result.evidence_ids == []
    assert result.reason_code == "GUARD_REJECTED"
    assert model.physical_calls == 1
    async with storage.session_factory() as session:
        record = await session.scalar(
            select(DialogueGenerationRecord).where(
                DialogueGenerationRecord.generation_key == result.generation_key
            )
        )
    assert record is not None
    assert record.status == "failed"
    assert record.result_text is None
    assert record.evidence_ids_json == []
    assert record.guard_passed is False
    assert record.evidence_authorized is False
    assert record.trace_json is not None
    assert record.trace_json["agent"]["decision"]["template"] == {
        "address_slot": "none",
        "prefix": "这场雨让我想起那块紫水晶。",
        "suffix": "",
    }
    assert record.guard_report_json is not None
    assert record.guard_report_json["attempts"][0]["violations"] == [
        {"code": "EVIDENCE_NOT_OBSERVED", "repairable": False}
    ]


@pytest.mark.asyncio
async def test_agent_error_becomes_stable_failed_result_without_secret_text(
    storage: SqliteStorage,
) -> None:
    """模型/工具/预算异常只能留下稳定机器码，不能污染台词或普通 trace。"""

    secret = "/secret/provider.key SELECT prompt FROM internal"
    model = _ServiceScriptedModel([RuntimeError(secret)])
    factory = DialogueAgentFactory(
        model=model,
        model_configuration="scripted-service-model-v1",
    )
    generator = AgentBackedDialogueGenerator(
        runner=DialogueAgentRunner(factory),
        storage=storage,
        allowed_tools=frozenset(),
    )

    response = await DialogueService(storage, generator=generator).generate_batch(_request())

    result = response.items[0]
    assert result.status == "failed"
    assert result.text is None
    assert result.reason_code == "AGENT_EXECUTION_FAILED"
    assert secret not in response.model_dump_json()
    async with storage.session_factory() as session:
        record = await session.scalar(
            select(DialogueGenerationRecord).where(
                DialogueGenerationRecord.generation_key == result.generation_key
            )
        )
    assert record is not None
    assert record.trace_json is None
    assert record.result_text is None
