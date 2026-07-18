"""SQLite 运行时不可用与 HTTP 503 稳定映射测试。"""

from __future__ import annotations

import sqlite3
from pathlib import Path

import httpx
import pytest
from sqlalchemy import func, select
from sqlalchemy.engine import make_url
from sqlalchemy.exc import IntegrityError, OperationalError

from stardew_npc_agent.api import create_app
from stardew_npc_agent.config import Settings
from stardew_npc_agent.event_service import EventService
from stardew_npc_agent.schemas import GameEventBatchRequest
from stardew_npc_agent.storage import (
    DialogueDisplayReceiptRecord,
    DialogueGenerationInput,
    MemoryRecord,
    SqliteStorage,
    StorageUnavailableError,
)


def _event_payload() -> dict[str, object]:
    """返回一条能在未锁库时正常投影的公共事件。"""

    return {
        "schema_version": "1.0",
        "request_id": "request-storage-lock",
        "save_id": "save-lock",
        "player_id": "player-lock",
        "events": [
            {
                "event_id": "event-storage-lock",
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


def _database_path(database_url: str) -> Path:
    """从测试 aiosqlite URL 中取出现有 SQLite 文件路径。"""

    database_name = make_url(database_url).database
    assert database_name is not None
    return Path(database_name)


def _hold_write_lock(database_url: str) -> sqlite3.Connection:
    """使用独立同步连接持有 ``BEGIN IMMEDIATE`` 写锁，由测试显式释放。"""

    connection = sqlite3.connect(_database_path(database_url), isolation_level=None)
    connection.execute("BEGIN IMMEDIATE")
    return connection


@pytest.mark.asyncio
async def test_event_post_returns_sanitized_503_when_sqlite_write_lock_times_out(
    migrated_database_url: str,
) -> None:
    """真实 SQLite 写锁超时必须映射为无路径、SQL 或底层消息的 503。"""

    storage = SqliteStorage.from_url(migrated_database_url, busy_timeout_ms=25)
    app = create_app(
        Settings(database_url=migrated_database_url),
        event_service=EventService(storage),
    )
    lock_connection = _hold_write_lock(migrated_database_url)
    transport = httpx.ASGITransport(app=app, raise_app_exceptions=False)
    try:
        async with httpx.AsyncClient(transport=transport, base_url="http://testserver") as client:
            response = await client.post("/api/v1/game-events/batches", json=_event_payload())
    finally:
        lock_connection.rollback()
        lock_connection.close()
        await storage.dispose()

    assert response.status_code == 503
    assert response.json() == {"detail": "SERVICE_NOT_READY"}
    assert str(_database_path(migrated_database_url)) not in response.text
    assert "database is locked" not in response.text
    assert "INSERT INTO" not in response.text


@pytest.mark.asyncio
async def test_display_ack_returns_sanitized_503_when_sqlite_write_lock_times_out(
    migrated_database_url: str,
) -> None:
    """displayed ACK 在 readiness 后遇到写锁也必须返回同一稳定 503。"""

    storage = SqliteStorage.from_url(migrated_database_url, busy_timeout_ms=25)
    service = EventService(storage)
    await service.ingest_batch(GameEventBatchRequest.model_validate(_event_payload()))
    async with storage.session_factory() as session:
        evidence_id = await session.scalar(select(MemoryRecord.memory_id))
    assert evidence_id is not None
    await storage.save_dialogue_generation(
        DialogueGenerationInput(
            generation_id="generation-lock",
            generation_key="generation-key-lock",
            save_id="save-lock",
            player_id="player-lock",
            game_day_index=6,
            npc_id="Abigail",
            locale="zh-CN",
            source_hash="sha256:lock-source",
            relationship_stage="friend",
            friendship_points=750,
            memory_cooldown_days=3,
            status="generated",
            result_text="已通过 Guard 的台词。",
            reason_code="TEST_GENERATED",
            evidence_ids=(evidence_id,),
            trace_id="trace-lock",
            guard_passed=True,
        )
    )
    app = create_app(
        Settings(database_url=migrated_database_url),
        event_service=service,
    )
    lock_connection = _hold_write_lock(migrated_database_url)
    transport = httpx.ASGITransport(app=app, raise_app_exceptions=False)
    payload = {
        "schema_version": "1.0",
        "request_id": "request-ack-lock",
        "save_id": "save-lock",
        "player_id": "player-lock",
        "display_receipt_id": "receipt-lock",
        "displayed_day_index": 6,
        "npc_id": "Abigail",
        "source_hash": "sha256:lock-source",
    }
    try:
        async with httpx.AsyncClient(transport=transport, base_url="http://testserver") as client:
            response = await client.post(
                "/api/v1/dialogue-generations/generation-lock/displayed",
                json=payload,
            )
    finally:
        lock_connection.rollback()
        lock_connection.close()

    async with storage.session_factory() as session:
        receipt_count = await session.scalar(
            select(func.count()).select_from(DialogueDisplayReceiptRecord)
        )
        memory = await session.scalar(select(MemoryRecord))
    await storage.dispose()

    assert response.status_code == 503
    assert response.json() == {"detail": "SERVICE_NOT_READY"}
    assert str(_database_path(migrated_database_url)) not in response.text
    assert "database is locked" not in response.text
    assert "INSERT INTO" not in response.text
    assert receipt_count == 0
    assert memory is not None and memory.use_count == 0


@pytest.mark.asyncio
async def test_integrity_error_is_not_disguised_as_storage_unavailable(
    storage: SqliteStorage,
) -> None:
    """编程或数据不变量错误必须保留为 IntegrityError，不能误报运行时 503。"""

    generation = DialogueGenerationInput(
        generation_id="generation-integrity",
        generation_key="generation-key-integrity",
        save_id="save-integrity",
        player_id="player-integrity",
        game_day_index=1,
        npc_id="Abigail",
        locale="zh-CN",
        source_hash="sha256:integrity",
        relationship_stage="friend",
        friendship_points=750,
        memory_cooldown_days=3,
        status="generated",
        result_text="合法台词。",
        reason_code="TEST_GENERATED",
        evidence_ids=(),
        trace_id="trace-integrity",
        guard_passed=True,
    )
    await storage.save_dialogue_generation(generation)

    with pytest.raises(IntegrityError) as error_info:
        await storage.save_dialogue_generation(generation)

    assert not isinstance(error_info.value, StorageUnavailableError)


@pytest.mark.asyncio
async def test_sqlite_schema_programming_error_is_not_disguised_as_unavailable(
    migrated_database_url: str,
) -> None:
    """SQLite 的 SQLITE_ERROR 虽名为 OperationalError，也不能伪装成可重试 503。

    SQLite driver 会把“表不存在”等 Schema/编程错误归入 ``OperationalError``。
    存储边界必须继续暴露这类错误，让监控和测试看到真实缺陷，而不是让调用方
    永久重试一个不会自行恢复的 503。
    """

    database_path = _database_path(migrated_database_url)
    connection = sqlite3.connect(database_path)
    try:
        connection.execute("DROP TABLE game_events")
        connection.commit()
    finally:
        connection.close()

    storage = SqliteStorage.from_url(migrated_database_url)
    service = EventService(storage)
    try:
        with pytest.raises(OperationalError) as error_info:
            await service.ingest_batch(GameEventBatchRequest.model_validate(_event_payload()))
    finally:
        await storage.dispose()

    assert not isinstance(error_info.value, StorageUnavailableError)
