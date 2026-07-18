"""Alembic 异步 migration 环境。

运行时后端和 migration 共用 ``storage.Base.metadata``，但建表仍只由
Alembic revision 完成。这个文件不读取模型密钥，也不启动 FastAPI 服务。
"""

from __future__ import annotations

import asyncio
from logging.config import fileConfig

from alembic import context
from sqlalchemy import Connection, pool
from sqlalchemy.ext.asyncio import async_engine_from_config

from stardew_npc_agent.storage import Base

config = context.config
if config.config_file_name is not None:
    fileConfig(config.config_file_name)

target_metadata = Base.metadata


def run_migrations_offline() -> None:
    """在不建立数据库连接的情况下输出 migration SQL。"""

    context.configure(
        url=config.get_main_option("sqlalchemy.url"),
        target_metadata=target_metadata,
        literal_binds=True,
        dialect_opts={"paramstyle": "named"},
        compare_type=True,
    )
    with context.begin_transaction():
        context.run_migrations()


def _run_migrations(connection: Connection) -> None:
    """在 Alembic 适配的同步 connection facade 中执行 revision。"""

    context.configure(
        connection=connection,
        target_metadata=target_metadata,
        compare_type=True,
        render_as_batch=connection.dialect.name == "sqlite",
    )
    with context.begin_transaction():
        context.run_migrations()


async def _run_async_migrations() -> None:
    """使用 aiosqlite engine 建立短连接并交给 Alembic 同步执行器。"""

    connectable = async_engine_from_config(
        config.get_section(config.config_ini_section, {}),
        prefix="sqlalchemy.",
        poolclass=pool.NullPool,
    )
    async with connectable.connect() as connection:
        await connection.run_sync(_run_migrations)
    await connectable.dispose()


def run_migrations_online() -> None:
    """从同步 Alembic CLI 入口驱动一次有边界的异步 migration。"""

    asyncio.run(_run_async_migrations())


if context.is_offline_mode():
    run_migrations_offline()
else:
    run_migrations_online()
