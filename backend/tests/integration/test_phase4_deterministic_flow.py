"""Phase 4 后端确定性链路的跨组件验收测试。

测试使用真实 Alembic 临时 SQLite、FastAPI ASGI 应用、事件服务、生成服务与
generation cache，但 generator 仍是本地 scripted callable。它不监听端口、不访问
网络或模型，目的是证明事件 revision、批量生成和协议重试共享同一持久化事实。
"""

from __future__ import annotations

import asyncio
import copy
from collections import Counter

import httpx
import pytest
from sqlalchemy import func, select

from stardew_npc_agent.api import create_app
from stardew_npc_agent.config import Settings
from stardew_npc_agent.dialogue_service import (
    DialogueGeneratorDecision,
    DialogueService,
)
from stardew_npc_agent.event_service import EventService
from stardew_npc_agent.schemas import (
    DialogueGenerationBatchRequest,
    DialogueGenerationItem,
)
from stardew_npc_agent.storage import DialogueGenerationRecord, SqliteStorage


def _event_payload() -> dict[str, object]:
    """构造一条截至昨日的合法世界进度事件。"""

    return {
        "schema_version": "1.0",
        "request_id": "request-phase4-event",
        "save_id": "save-phase4-flow",
        "player_id": "player-phase4-flow",
        "events": [
            {
                "event_id": "event-phase4-mine-level-40",
                "event_type": "world_progression",
                "event_version": "1",
                "occurred_day_index": 5,
                "source": "smapi.world.public_facility_restored",
                "audience_scope": "public",
                "audience_npc_id": None,
                "payload": {"milestone": "public_facility_minecarts_restored"},
            }
        ],
    }


def _dialogue_payload(required_memory_revision: int) -> dict[str, object]:
    """构造两个目标 NPC 的同日稳定生成输入。"""

    return {
        "schema_version": "1.0",
        "request_id": "request-phase4-dialogue-0",
        "save_id": "save-phase4-flow",
        "player_id": "player-phase4-flow",
        "game_day_index": 6,
        "required_memory_revision": required_memory_revision,
        "stable_day_context": {
            "season": "spring",
            "weather": "rain",
            "locale": "zh-CN",
            "progression_signals": {"mine_level": 40},
        },
        "items": [
            {
                "task_id": "task-phase4-abigail-0",
                "npc_id": "Abigail",
                "source_dialogue": {
                    "asset_name": "Characters/Dialogue/Abigail",
                    "dialogue_key": "spring_Mon",
                    "text": "下雨的时候，山谷看起来更神秘了。",
                    "source_hash": "sha256:phase4-abigail-source",
                },
                "relationship_snapshot": {
                    "friendship_points": 750,
                    "relationship_stage": "friend",
                },
                "style_examples": ["样例一。", "样例二。", "样例三。"],
                "memory_signals": [
                    {
                        "event_type": "world_progression",
                        "occurred_day_index": 5,
                        "tags": ["mine", "progression"],
                    }
                ],
            },
            {
                "task_id": "task-phase4-sebastian-0",
                "npc_id": "Sebastian",
                "source_dialogue": {
                    "asset_name": "Characters/Dialogue/Sebastian",
                    "dialogue_key": "spring_Mon",
                    "text": "这种天气正适合待在地下室里。",
                    "source_hash": "sha256:phase4-sebastian-source",
                },
                "relationship_snapshot": {
                    "friendship_points": 500,
                    "relationship_stage": "acquaintance",
                },
                "style_examples": ["样例甲。", "样例乙。", "样例丙。"],
                "memory_signals": [],
            },
        ],
    }


def _with_transport_ids(
    payload: dict[str, object],
    attempt_index: int,
) -> dict[str, object]:
    """只改变明确排除在 generation key 外的 request/task 传输身份。"""

    retry = copy.deepcopy(payload)
    retry["request_id"] = f"request-phase4-dialogue-{attempt_index}"
    items = retry["items"]
    assert isinstance(items, list)
    for item_index, item in enumerate(items):
        assert isinstance(item, dict)
        item["task_id"] = f"task-phase4-{item_index}-{attempt_index}"
    return retry


@pytest.mark.asyncio
async def test_phase4_event_revision_to_concurrent_two_npc_passthrough_cache(
    storage: SqliteStorage,
    migrated_database_url: str,
) -> None:
    """事件先推进 revision；并发协议重试对每个 NPC 只执行一次 scripted generator。"""

    generator_calls: Counter[str] = Counter()

    async def counting_passthrough_generator(
        _request: DialogueGenerationBatchRequest,
        item: DialogueGenerationItem,
    ) -> DialogueGeneratorDecision:
        """制造短暂重叠，同时保持 Phase 4 零模型 passthrough 行为。"""

        generator_calls[item.npc_id] += 1
        await asyncio.sleep(0.01)
        return DialogueGeneratorDecision(
            status="passthrough",
            text=None,
            reason_code="PHASE4_E2E_PASSTHROUGH",
        )

    app = create_app(
        Settings(database_url=migrated_database_url),
        event_service=EventService(storage),
        dialogue_service=DialogueService(
            storage,
            generator=counting_passthrough_generator,
            max_concurrency=2,
        ),
    )
    transport = httpx.ASGITransport(app=app, raise_app_exceptions=False)
    async with httpx.AsyncClient(transport=transport, base_url="http://testserver") as client:
        event_response = await client.post(
            "/api/v1/game-events/batches",
            json=_event_payload(),
        )
        assert event_response.status_code == 200
        memory_revision = event_response.json()["memory_revision"]
        assert memory_revision == 1

        base_payload = _dialogue_payload(memory_revision)
        generation_responses = await asyncio.gather(
            *(
                client.post(
                    "/api/v1/dialogue-generations/batch",
                    json=_with_transport_ids(base_payload, attempt_index),
                )
                for attempt_index in range(8)
            )
        )
        capabilities_response = await client.get("/api/v1/capabilities")

    assert all(response.status_code == 200 for response in generation_responses)
    parsed_responses = [response.json() for response in generation_responses]
    assert all(response["memory_revision"] == 1 for response in parsed_responses)
    assert all(
        [item["status"] for item in response["items"]] == ["passthrough", "passthrough"]
        for response in parsed_responses
    )
    assert generator_calls == Counter({"Abigail": 1, "Sebastian": 1})
    for item_index in range(2):
        assert (
            len({response["items"][item_index]["generation_id"] for response in parsed_responses})
            == 1
        )
        assert (
            len({response["items"][item_index]["generation_key"] for response in parsed_responses})
            == 1
        )

    async with storage.session_factory() as session:
        generation_count = await session.scalar(
            select(func.count()).select_from(DialogueGenerationRecord)
        )
    assert generation_count == 2
    assert capabilities_response.status_code == 200
    assert capabilities_response.json()["provider_configured"] is False
    assert capabilities_response.json()["tool_calling"] is False
    assert capabilities_response.json()["structured_output"] is False
