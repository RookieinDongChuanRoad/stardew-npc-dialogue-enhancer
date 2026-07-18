"""Phase 3 持久化测试的共享数据库 fixture。

每个测试都使用独立的临时 SQLite 文件，并且通过 Alembic 从空库升级到
`head`。这样既能验证 migration 是真正的建表入口，也避免测试偷偷依赖
`metadata.create_all()` 而掩盖 migration 漂移。
"""

from __future__ import annotations

from collections.abc import AsyncIterator
from pathlib import Path

import pytest
import pytest_asyncio
from alembic import command
from alembic.config import Config

from stardew_npc_agent.storage import SqliteStorage

BACKEND_ROOT = Path(__file__).resolve().parents[1]


@pytest.fixture
def migrated_database_url(tmp_path: Path) -> str:
    """创建一个已升级到 head 的临时数据库，返回其异步 SQLAlchemy URL。"""

    database_path = tmp_path / "phase3.sqlite3"
    database_url = f"sqlite+aiosqlite:///{database_path}"
    alembic_config = Config(str(BACKEND_ROOT / "alembic.ini"))
    alembic_config.set_main_option("sqlalchemy.url", database_url)
    command.upgrade(alembic_config, "head")
    return database_url


@pytest_asyncio.fixture
async def storage(migrated_database_url: str) -> AsyncIterator[SqliteStorage]:
    """向测试提供存储门面，并在测试结束后始终释放连接池。"""

    storage_instance = SqliteStorage.from_url(
        migrated_database_url,
        busy_timeout_ms=5_000,
    )
    try:
        yield storage_instance
    finally:
        await storage_instance.dispose()
