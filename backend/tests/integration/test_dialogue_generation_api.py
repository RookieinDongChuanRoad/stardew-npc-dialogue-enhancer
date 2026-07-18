"""Phase 4 台词批次生成 HTTP 路由的集成测试。"""

from __future__ import annotations

import sqlite3
from pathlib import Path

import httpx
import pytest
from sqlalchemy.engine import make_url

from stardew_npc_agent.api import create_app
from stardew_npc_agent.config import Settings
from stardew_npc_agent.dialogue_service import DialogueGeneratorDecision, DialogueService
from stardew_npc_agent.event_service import EventService
from stardew_npc_agent.schemas import (
    DialogueGenerationBatchRequest,
    DialogueGenerationBatchResponse,
    DialogueGenerationItem,
)
from stardew_npc_agent.storage import SqliteStorage


def _payload(
    *,
    required_memory_revision: int = 0,
    include_second_item: bool = False,
) -> dict[str, object]:
    """返回可直接 POST 的共享 contract payload。"""

    items: list[dict[str, object]] = [
        {
            "task_id": "task-api-abigail",
            "npc_id": "Abigail",
            "source_dialogue": {
                "asset_name": "Characters/Dialogue/Abigail",
                "dialogue_key": "Mon",
                "text": "今天山里的风很舒服。",
                "source_hash": "sha256:api-abigail",
            },
            "relationship_snapshot": {
                "friendship_points": 750,
                "relationship_stage": "friend",
            },
            "style_examples": ["样例一。", "样例二。", "样例三。"],
            "memory_signals": [],
        }
    ]
    if include_second_item:
        items.append(
            {
                "task_id": "task-api-sebastian",
                "npc_id": "Sebastian",
                "source_dialogue": {
                    "asset_name": "Characters/Dialogue/Sebastian",
                    "dialogue_key": "Mon",
                    "text": "我晚些时候再去镇上。",
                    "source_hash": "sha256:api-sebastian",
                },
                "relationship_snapshot": {
                    "friendship_points": 500,
                    "relationship_stage": "friend",
                },
                "style_examples": ["样例甲。", "样例乙。", "样例丙。"],
                "memory_signals": [],
            }
        )
    return {
        "schema_version": "1.0",
        "request_id": "request-dialogue-api",
        "save_id": "save-dialogue-api",
        "player_id": "player-dialogue-api",
        "game_day_index": 6,
        "required_memory_revision": required_memory_revision,
        "stable_day_context": {
            "season": "spring",
            "weather": "sunny",
            "locale": "zh-CN",
            "progression_signals": {},
        },
        "items": items,
    }


@pytest.mark.asyncio
async def test_dialogue_generation_route_returns_contract_valid_partial_response(
    storage: SqliteStorage,
    migrated_database_url: str,
) -> None:
    """单项 generator 异常仍返回 200，并保持另一项的正常 passthrough。"""

    async def partial_generator(
        _request: DialogueGenerationBatchRequest,
        item: DialogueGenerationItem,
    ) -> DialogueGeneratorDecision:
        if item.npc_id == "Abigail":
            raise RuntimeError("不得进入 HTTP 响应的内部异常")
        return DialogueGeneratorDecision(
            status="passthrough",
            text=None,
            reason_code="SCRIPTED_API_PASSTHROUGH",
        )

    app = create_app(
        Settings(database_url=migrated_database_url),
        event_service=EventService(storage),
        dialogue_service=DialogueService(storage, generator=partial_generator),
    )
    transport = httpx.ASGITransport(app=app, raise_app_exceptions=False)
    async with httpx.AsyncClient(transport=transport, base_url="http://testserver") as client:
        response = await client.post(
            "/api/v1/dialogue-generations/batch",
            json=_payload(include_second_item=True),
        )

    assert response.status_code == 200
    parsed = DialogueGenerationBatchResponse.model_validate(response.json())
    assert [item.task_id for item in parsed.items] == [
        "task-api-abigail",
        "task-api-sebastian",
    ]
    assert [item.status for item in parsed.items] == ["failed", "passthrough"]
    assert [item.reason_code for item in parsed.items] == [
        "GENERATOR_FAILED",
        "SCRIPTED_API_PASSTHROUGH",
    ]
    assert "内部异常" not in response.text


@pytest.mark.asyncio
async def test_dialogue_generation_route_maps_revision_not_ready_to_409(
    storage: SqliteStorage,
    migrated_database_url: str,
) -> None:
    """当前空分区 revision=0，required=1 应返回稳定 409。"""

    app = create_app(
        Settings(database_url=migrated_database_url),
        event_service=EventService(storage),
        dialogue_service=DialogueService(storage),
    )
    transport = httpx.ASGITransport(app=app, raise_app_exceptions=False)
    async with httpx.AsyncClient(transport=transport, base_url="http://testserver") as client:
        response = await client.post(
            "/api/v1/dialogue-generations/batch",
            json=_payload(required_memory_revision=1),
        )

    assert response.status_code == 409
    assert response.json() == {"detail": "MEMORY_REVISION_NOT_READY"}


@pytest.mark.asyncio
async def test_dialogue_generation_route_maps_runtime_storage_lock_to_sanitized_503(
    migrated_database_url: str,
) -> None:
    """readiness 之后的 SQLite 写锁超时必须变成无路径、SQL 的稳定 503。"""

    storage = SqliteStorage.from_url(migrated_database_url, busy_timeout_ms=25)
    app = create_app(
        Settings(database_url=migrated_database_url, sqlite_busy_timeout_ms=25),
        event_service=EventService(storage),
        dialogue_service=DialogueService(storage),
    )
    database_name = make_url(migrated_database_url).database
    assert database_name is not None
    database_path = Path(database_name)
    lock_connection = sqlite3.connect(database_path, isolation_level=None)
    lock_connection.execute("BEGIN IMMEDIATE")
    transport = httpx.ASGITransport(app=app, raise_app_exceptions=False)
    try:
        async with httpx.AsyncClient(transport=transport, base_url="http://testserver") as client:
            response = await client.post(
                "/api/v1/dialogue-generations/batch",
                json=_payload(),
            )
    finally:
        lock_connection.rollback()
        lock_connection.close()
        await storage.dispose()

    assert response.status_code == 503
    assert response.json() == {"detail": "SERVICE_NOT_READY"}
    assert str(database_path) not in response.text
    assert "database is locked" not in response.text
    assert "INSERT INTO" not in response.text


@pytest.mark.asyncio
async def test_dialogue_generation_route_keeps_strict_dto_422(
    storage: SqliteStorage,
    migrated_database_url: str,
) -> None:
    """未知字段和错误整数类型必须由共享严格 DTO 在 service 前拒绝。"""

    app = create_app(
        Settings(database_url=migrated_database_url),
        event_service=EventService(storage),
        dialogue_service=DialogueService(storage),
    )
    payload = _payload()
    payload["unexpected_field"] = "must-be-rejected"
    payload["game_day_index"] = "6"
    transport = httpx.ASGITransport(app=app, raise_app_exceptions=False)
    async with httpx.AsyncClient(transport=transport, base_url="http://testserver") as client:
        response = await client.post("/api/v1/dialogue-generations/batch", json=payload)

    assert response.status_code == 422
    assert response.json()["detail"]


@pytest.mark.asyncio
async def test_dialogue_generation_route_rejects_duplicate_envelope_with_stable_422(
    storage: SqliteStorage,
    migrated_database_url: str,
) -> None:
    """共享 DTO 允许的重复业务 ID 仍由 service 整批拒绝，避免响应映射歧义。"""

    app = create_app(
        Settings(database_url=migrated_database_url),
        event_service=EventService(storage),
        dialogue_service=DialogueService(storage),
    )
    payload = _payload(include_second_item=True)
    items = payload["items"]
    assert isinstance(items, list)
    first = items[0]
    second = items[1]
    assert isinstance(first, dict) and isinstance(second, dict)
    second["task_id"] = first["task_id"]
    transport = httpx.ASGITransport(app=app, raise_app_exceptions=False)
    async with httpx.AsyncClient(transport=transport, base_url="http://testserver") as client:
        response = await client.post("/api/v1/dialogue-generations/batch", json=payload)

    assert response.status_code == 422
    assert response.json() == {"detail": "INVALID_BATCH_ENVELOPE"}


def test_api_fixture_itself_matches_the_shared_request_contract() -> None:
    """防止 HTTP 测试因 fixture 漂移而把无关 422 误判为路由行为。"""

    parsed = DialogueGenerationBatchRequest.model_validate(_payload(include_second_item=True))

    assert len(parsed.items) == 2
