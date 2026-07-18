"""FastAPI 事件与展示回执路由的服务边界测试。"""

from __future__ import annotations

from datetime import UTC, datetime

import httpx
import pytest
from sqlalchemy import func, select

from stardew_npc_agent.api import create_app
from stardew_npc_agent.config import Settings
from stardew_npc_agent.event_service import EventService
from stardew_npc_agent.schemas import GameEventBatchRequest
from stardew_npc_agent.storage import (
    DialogueDisplayReceiptRecord,
    DialogueGenerationInput,
    DialogueGenerationRecord,
    MemoryRecord,
    SqliteStorage,
)


def _event_payload(request_id: str) -> dict[str, object]:
    """返回一个用于真实 service-backed 路由的合法 wire payload。"""

    return {
        "schema_version": "1.0",
        "request_id": request_id,
        "save_id": "save-api",
        "player_id": "player-api",
        "events": [
            {
                "event_id": "event-api-1",
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


@pytest.mark.asyncio
async def test_event_route_delegates_to_service_and_preserves_item_idempotency(
    storage: SqliteStorage,
    migrated_database_url: str,
) -> None:
    """路由只处理 HTTP/DTO，实际幂等和投影由注入的 EventService 完成。"""

    service = EventService(storage)
    app = create_app(
        Settings(database_url=migrated_database_url),
        event_service=service,
    )
    transport = httpx.ASGITransport(app=app)
    async with httpx.AsyncClient(transport=transport, base_url="http://testserver") as client:
        ready = await client.get("/api/v1/health/ready")
        accepted = await client.post(
            "/api/v1/game-events/batches",
            json=_event_payload("request-api-1"),
        )
        duplicate = await client.post(
            "/api/v1/game-events/batches",
            json=_event_payload("request-api-2"),
        )

    assert ready.status_code == 200
    assert ready.json() == {"status": "ready"}
    assert accepted.status_code == 200
    assert accepted.json() == {
        "schema_version": "1.0",
        "request_id": "request-api-1",
        "memory_revision": 1,
        "committed_through_day_index": 5,
        "items": [
            {
                "event_id": "event-api-1",
                "status": "accepted",
                "reason_code": None,
            }
        ],
    }
    assert duplicate.status_code == 200
    assert duplicate.json()["items"][0]["status"] == "duplicate"


@pytest.mark.asyncio
async def test_display_ack_route_returns_accepted_then_duplicate(
    storage: SqliteStorage,
    migrated_database_url: str,
) -> None:
    """ACK 路由必须保留 service 的幂等状态，且不直接操作 Session。"""

    service = EventService(storage)
    await service.ingest_batch(
        GameEventBatchRequest.model_validate(_event_payload("request-api-ack-event"))
    )
    async with storage.session_factory() as session:
        evidence_id = await session.scalar(select(MemoryRecord.memory_id))
    assert evidence_id is not None
    await storage.save_dialogue_generation(
        DialogueGenerationInput(
            generation_id="generation-api-1",
            generation_key="generation-key-api-1",
            save_id="save-api",
            player_id="player-api",
            game_day_index=6,
            npc_id="Abigail",
            locale="zh-CN",
            source_hash="sha256:api-source",
            relationship_stage="friend",
            friendship_points=750,
            memory_cooldown_days=3,
            status="generated",
            result_text="已通过 Guard 的增强台词。",
            reason_code="TEST_GENERATED",
            evidence_ids=(evidence_id,),
            trace_id="trace-api-1",
            guard_passed=True,
        )
    )
    app = create_app(
        Settings(database_url=migrated_database_url),
        event_service=service,
    )
    payload = {
        "schema_version": "1.0",
        "request_id": "request-api-ack",
        "save_id": "save-api",
        "player_id": "player-api",
        "display_receipt_id": "receipt-api-1",
        "displayed_day_index": 6,
        "npc_id": "Abigail",
        "source_hash": "sha256:api-source",
    }
    transport = httpx.ASGITransport(app=app)
    async with httpx.AsyncClient(transport=transport, base_url="http://testserver") as client:
        accepted = await client.post(
            "/api/v1/dialogue-generations/generation-api-1/displayed",
            json=payload,
        )
        duplicate = await client.post(
            "/api/v1/dialogue-generations/generation-api-1/displayed",
            json={**payload, "request_id": "request-api-ack-replay"},
        )

    assert accepted.status_code == 200
    assert accepted.json()["status"] == "accepted"
    assert duplicate.status_code == 200
    assert duplicate.json()["status"] == "duplicate"


@pytest.mark.asyncio
async def test_display_ack_route_maps_corrupt_evidence_to_stable_422(
    storage: SqliteStorage,
    migrated_database_url: str,
) -> None:
    """list[dict] 腐化 evidence 不得以 TypeError/500 穿透 FastAPI 边界。"""

    service = EventService(storage)
    await service.ingest_batch(
        GameEventBatchRequest.model_validate(_event_payload("request-api-corrupt-event"))
    )
    async with storage.session_factory.begin() as session:
        session.add(
            DialogueGenerationRecord(
                generation_id="generation-api-corrupt",
                generation_key="generation-key-api-corrupt",
                save_id="save-api",
                player_id="player-api",
                game_day_index=6,
                npc_id="Abigail",
                locale="zh-CN",
                source_hash="sha256:api-corrupt",
                relationship_stage="friend",
                friendship_points=750,
                memory_cooldown_days=3,
                status="generated",
                result_text="数据库允许但业务非法的 evidence fixture。",
                reason_code="CORRUPT_TEST_ROW",
                evidence_ids_json=[{"not": "an id"}],
                trace_id="trace-api-corrupt",
                guard_passed=True,
                evidence_authorized=True,
                input_versions_json=None,
                trace_json=None,
                usage_json=None,
                guard_report_json=None,
                created_at_utc=datetime.now(UTC),
                updated_at_utc=datetime.now(UTC),
            )
        )

    app = create_app(
        Settings(database_url=migrated_database_url),
        event_service=service,
    )
    transport = httpx.ASGITransport(app=app, raise_app_exceptions=False)
    async with httpx.AsyncClient(transport=transport, base_url="http://testserver") as client:
        response = await client.post(
            "/api/v1/dialogue-generations/generation-api-corrupt/displayed",
            json={
                "schema_version": "1.0",
                "request_id": "request-api-corrupt-ack",
                "save_id": "save-api",
                "player_id": "player-api",
                "display_receipt_id": "receipt-api-corrupt",
                "displayed_day_index": 6,
                "npc_id": "Abigail",
                "source_hash": "sha256:api-corrupt",
            },
        )

    async with storage.session_factory() as session:
        receipt_count = await session.scalar(
            select(func.count()).select_from(DialogueDisplayReceiptRecord)
        )
        memory = await session.scalar(select(MemoryRecord))

    assert response.status_code == 422
    assert response.json() == {"detail": "DISPLAY_ACK_NOT_ALLOWED"}
    assert receipt_count == 0
    assert memory is not None and memory.use_count == 0
