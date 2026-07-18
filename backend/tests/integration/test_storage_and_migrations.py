"""Alembic 空库升级与 SQLite 运行 PRAGMA 测试。"""

from __future__ import annotations

import asyncio
import json
import re
import sqlite3
from pathlib import Path

import httpx
import pytest
from alembic import command
from alembic.config import Config
from sqlalchemy import UniqueConstraint, inspect
from sqlalchemy.engine import make_url
from sqlalchemy.ext.asyncio import create_async_engine
from sqlalchemy.pool import NullPool

from stardew_npc_agent.api import create_app
from stardew_npc_agent.config import Settings
from stardew_npc_agent.contract_limits import WIRE_INTEGER_MAX
from stardew_npc_agent.event_service import EventService
from stardew_npc_agent.storage import SqliteStorage
from stardew_npc_agent.storage_models import Base
from stardew_npc_agent.storage_types import (
    REQUIRED_COLUMN_SIGNATURES,
    REQUIRED_DATABASE_REVISION,
    REQUIRED_UNIQUE_SIGNATURES,
)

BACKEND_ROOT = Path(__file__).resolve().parents[2]


def _rebuild_table_without_named_unique(
    connection: sqlite3.Connection,
    *,
    table_name: str,
    constraint_name: str,
) -> None:
    """保持原列签名重建空表，仅删除一个表级 UNIQUE constraint。

    SQLite 不允许直接删除 UNIQUE 自动索引。测试库来自空 migration，因而可安全
    重建目标表；调用者随后可以创建缺失、partial 或不同 collation 的替代索引，
    精确验证 readiness 对“唯一语义”而不只是列名的判断。
    """

    before_columns = connection.execute(f'PRAGMA table_info("{table_name}")').fetchall()
    create_row = connection.execute(
        "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = ?",
        (table_name,),
    ).fetchone()
    assert create_row is not None and isinstance(create_row[0], str)
    damaged_sql, replacement_count = re.subn(
        rf",\s*CONSTRAINT {re.escape(constraint_name)} UNIQUE \([^)]+\)",
        "",
        create_row[0],
        count=1,
    )
    assert replacement_count == 1

    replacement_table = f"{table_name}__without_required_unique"
    # 初始 migration 生成未加引号的表名；SQLite batch migration 重建后会把
    # 表名写成双引号形式。测试辅助函数必须同时支持两种等价 DDL，避免把
    # migration 的序列化风格误判成 readiness 行为失败。
    damaged_sql, table_name_replacement_count = re.subn(
        rf'^CREATE TABLE "?{re.escape(table_name)}"?',
        f'CREATE TABLE "{replacement_table}"',
        damaged_sql,
        count=1,
    )
    assert table_name_replacement_count == 1
    connection.execute("PRAGMA foreign_keys=OFF")
    connection.execute(damaged_sql)
    connection.execute(f'DROP TABLE "{table_name}"')
    connection.execute(f'ALTER TABLE "{replacement_table}" RENAME TO "{table_name}"')

    after_columns = connection.execute(f'PRAGMA table_info("{table_name}")').fetchall()
    assert after_columns == before_columns


def _upgrade_database(database_path: Path) -> None:
    """用真实 Alembic head 升级指定普通文件路径，供路径身份测试复用。"""

    alembic_config = Config(str(BACKEND_ROOT / "alembic.ini"))
    alembic_config.set_main_option(
        "sqlalchemy.url",
        f"sqlite+aiosqlite:///{database_path}",
    )
    command.upgrade(alembic_config, "head")


def _alembic_config(database_path: Path) -> Config:
    """为迁移边界测试创建指向独立临时数据库的 Alembic 配置。"""

    alembic_config = Config(str(BACKEND_ROOT / "alembic.ini"))
    alembic_config.set_main_option(
        "sqlalchemy.url",
        f"sqlite+aiosqlite:///{database_path}",
    )
    return alembic_config


def _insert_partition_state(
    connection: sqlite3.Connection,
    *,
    revision: int | float,
    committed_day: int | float,
    save_id: str = "save-migration",
) -> None:
    """直接写入分区水位，以验证数据库自身而非 ORM 的硬约束。"""

    connection.execute(
        "INSERT INTO memory_partition_states "
        "(save_id, player_id, memory_revision, committed_through_day_index, updated_at_utc) "
        "VALUES (?, ?, ?, ?, ?)",
        (
            save_id,
            "player-migration",
            revision,
            committed_day,
            "2026-07-13 00:00:00",
        ),
    )


def _insert_legacy_progression_memory(
    connection: sqlite3.Connection,
    *,
    memory_id: str,
    event_id: str,
    milestone: str,
    source: str,
    occurred_day_index: int,
    use_count: int = 0,
    last_used_day_index: int | None = None,
) -> None:
    """向 0002 数据库写入旧通用投影，绕过当前 ORM 的新投影逻辑。

    data migration 的测试输入必须真实代表历史落盘形状，因此这里显式填写
    ``memories`` 的全部非空列。若改用当前 ``EventService``，测试会在 migration
    运行前就得到新摘要，无法证明旧数据确实被 backfill。
    """

    _insert_legacy_memory(
        connection,
        memory_id=memory_id,
        event_id=event_id,
        audience_scope="public",
        audience_npc_id=None,
        event_type="world_progression",
        event_version="1",
        source=source,
        payload={"milestone": milestone},
        summary=f"第 {occurred_day_index} 天，世界进度达成：{milestone}。",
        tags=["progression", f"milestone:{milestone}"],
        occurred_day_index=occurred_day_index,
        use_count=use_count,
        last_used_day_index=last_used_day_index,
    )


def _insert_legacy_memory(
    connection: sqlite3.Connection,
    *,
    memory_id: str,
    event_id: str,
    audience_scope: str,
    audience_npc_id: str | None,
    event_type: str,
    event_version: str,
    source: str,
    payload: dict[str, object],
    summary: str,
    tags: list[str],
    occurred_day_index: int,
    use_count: int = 0,
    last_used_day_index: int | None = None,
) -> None:
    """向 0003 memories 写入任意历史 raw 事实，供 0004 分类迁移使用。"""

    connection.execute(
        "INSERT INTO memories ("
        "memory_id,event_id,save_id,player_id,audience_scope,audience_npc_id,"
        "event_type,event_version,source,payload_json,summary,tags_json,importance,"
        "occurred_day_index,expires_day_index,last_used_day_index,use_count,"
        "relationship_stages_json,min_friendship_points,max_friendship_points,created_at_utc"
        ") VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
        (
            memory_id,
            event_id,
            "save-migration",
            "player-migration",
            audience_scope,
            audience_npc_id,
            event_type,
            event_version,
            source,
            json.dumps(payload),
            summary,
            json.dumps(tags),
            0.85,
            occurred_day_index,
            None,
            last_used_day_index,
            use_count,
            json.dumps([]),
            None,
            None,
            "2026-07-15 00:00:00",
        ),
    )


def _insert_legacy_passthrough_generation(connection: sqlite3.Connection) -> None:
    """写入一条满足 0002 CHECK 的历史终态，验证迁移不触碰生成审计。"""

    connection.execute(
        "INSERT INTO dialogue_generations ("
        "generation_id,generation_key,save_id,player_id,game_day_index,npc_id,locale,"
        "source_hash,relationship_stage,friendship_points,memory_cooldown_days,status,"
        "result_text,reason_code,evidence_ids_json,trace_id,guard_passed,evidence_authorized,"
        "input_versions_json,trace_json,usage_json,guard_report_json,created_at_utc,updated_at_utc"
        ") VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
        (
            "generation:legacy",
            "sha256:legacy",
            "save-migration",
            "player-migration",
            14,
            "Abigail",
            "zh-CN",
            "sha256:legacy-source",
            "acquaintance",
            10,
            3,
            "passthrough",
            None,
            "NO_CLEAR_ENHANCEMENT_VALUE",
            json.dumps([]),
            "trace:legacy",
            0,
            0,
            None,
            None,
            None,
            None,
            "2026-07-15 00:00:00",
            "2026-07-15 00:00:00",
        ),
    )


def test_alembic_upgrades_empty_database_with_required_tables_and_unique_constraints(
    tmp_path: Path,
) -> None:
    """从空文件升级必须建立四类核心表及所有数据幂等防线。"""

    database_path = tmp_path / "migration.sqlite3"
    async_database_url = f"sqlite+aiosqlite:///{database_path}"
    alembic_config = Config(str(BACKEND_ROOT / "alembic.ini"))
    alembic_config.set_main_option("sqlalchemy.url", async_database_url)

    command.upgrade(alembic_config, "head")

    # Schema inspection 只在 Alembic 完成后使用短时同步连接；生产 I/O 仍使用
    # SQLAlchemy async + aiosqlite。
    from sqlalchemy import create_engine

    engine = create_engine(f"sqlite:///{database_path}")
    try:
        inspector = inspect(engine)
        assert {
            "game_events",
            "memories",
            "dialogue_generations",
            "dialogue_display_receipts",
        }.issubset(inspector.get_table_names())
        event_uniques = {
            tuple(constraint["column_names"])
            for constraint in inspector.get_unique_constraints("game_events")
        }
        generation_uniques = {
            tuple(constraint["column_names"])
            for constraint in inspector.get_unique_constraints("dialogue_generations")
        }
        receipt_uniques = {
            tuple(constraint["column_names"])
            for constraint in inspector.get_unique_constraints("dialogue_display_receipts")
        }
        assert ("save_id", "player_id", "event_id") in event_uniques
        assert ("generation_key",) in generation_uniques
        assert ("display_receipt_id",) in receipt_uniques
    finally:
        engine.dispose()


def test_alembic_metadata_has_no_unmigrated_schema_drift(tmp_path: Path) -> None:
    """ORM metadata 与 Alembic head 必须完全一致，不得依赖 create_all 隐藏漂移。"""

    database_path = tmp_path / "alembic-check.sqlite3"
    alembic_config = Config(str(BACKEND_ROOT / "alembic.ini"))
    alembic_config.set_main_option(
        "sqlalchemy.url",
        f"sqlite+aiosqlite:///{database_path}",
    )
    command.upgrade(alembic_config, "head")

    # ``command.check`` 会在 autogenerate 发现任何未迁移 metadata 变更时抛错。
    command.check(alembic_config)


def test_required_database_revision_includes_memory_domain_migration() -> None:
    """readiness 必须等待语义分类与 retrieval state migration 完成。"""

    assert REQUIRED_DATABASE_REVISION == "20260715_0004"


def test_memory_domain_migration_classifies_only_exact_legacy_contracts(
    tmp_path: Path,
) -> None:
    """0004 只 active 可由 raw producer 逐字证明的行，其余原样 quarantine。"""

    database_path = tmp_path / "memory-domain-backfill.sqlite3"
    alembic_config = _alembic_config(database_path)
    command.upgrade(alembic_config, "20260715_0003")
    connection = sqlite3.connect(database_path)
    try:
        _insert_partition_state(connection, revision=4, committed_day=14)
        _insert_legacy_progression_memory(
            connection,
            memory_id="memory:skill",
            event_id="event:skill",
            milestone="skill_farming_level_5",
            source="smapi.player.level_changed",
            occurred_day_index=14,
            use_count=2,
            last_used_day_index=20,
        )
        _insert_legacy_progression_memory(
            connection,
            memory_id="memory:unknown-world",
            event_id="event:unknown-world",
            milestone="community_center_pantry_completed",
            source="smapi",
            occurred_day_index=10,
        )
        _insert_legacy_memory(
            connection,
            memory_id="memory:gift-v1",
            event_id="event:gift-v1",
            audience_scope="npc",
            audience_npc_id="Abigail",
            event_type="gift_given",
            event_version="1",
            source="smapi",
            payload={"item_id": "Amethyst", "taste": "love"},
            summary="第 8 天，玩家向 Abigail 赠送了 Amethyst，礼物反应为 love。",
            tags=["gift", "item:Amethyst", "taste:love"],
            occurred_day_index=8,
        )
        _insert_legacy_memory(
            connection,
            memory_id="memory:gift-v2",
            event_id="event:gift-v2",
            audience_scope="npc",
            audience_npc_id="Abigail",
            event_type="gift_given",
            event_version="2",
            source="harmony.farmer.on_gift_given",
            payload={"item_id": "(O)66", "taste": "love"},
            summary="第 9 天，玩家向 Abigail 赠送了 (O)66，礼物反应为 love。",
            tags=["gift", "item:(O)66", "taste:love"],
            occurred_day_index=9,
        )
        connection.commit()
    finally:
        connection.close()

    command.upgrade(alembic_config, "head")

    connection = sqlite3.connect(database_path)
    try:
        rows = connection.execute(
            "SELECT memory_id,classification_status,memory_domain,memory_kind,"
            "subject_namespace,subject_value,use_count,last_used_day_index "
            "FROM memories ORDER BY memory_id"
        ).fetchall()
        partition = connection.execute(
            "SELECT memory_revision,committed_through_day_index,retrieval_state_revision "
            "FROM memory_partition_states"
        ).fetchone()
    finally:
        connection.close()

    assert rows == [
        ("memory:gift-v1", "quarantined", None, None, None, None, 0, None),
        (
            "memory:gift-v2",
            "active",
            "npc_history",
            "gift_given",
            "item_id",
            "(O)66",
            0,
            None,
        ),
        (
            "memory:skill",
            "active",
            "player_progression",
            "skill_level_reached",
            "skill_id",
            "farming",
            2,
            20,
        ),
        ("memory:unknown-world", "quarantined", None, None, None, None, 0, None),
    ]
    assert partition == (4, 14, 0)


def test_memory_domain_head_has_classification_constraints_and_retrieval_index(
    tmp_path: Path,
) -> None:
    """head 必须在数据库层保存分类、audience 组合和检索水位硬约束。"""

    database_path = tmp_path / "memory-domain-schema.sqlite3"
    _upgrade_database(database_path)
    connection = sqlite3.connect(database_path)
    try:
        memory_columns = {
            row[1]: (row[2], bool(row[3]))
            for row in connection.execute("PRAGMA table_info('memories')").fetchall()
        }
        partition_columns = {
            row[1]: (row[2], bool(row[3]))
            for row in connection.execute("PRAGMA table_info('memory_partition_states')").fetchall()
        }
        memory_sql = connection.execute(
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='memories'"
        ).fetchone()
        partition_sql = connection.execute(
            "SELECT sql FROM sqlite_master WHERE type='table' AND name='memory_partition_states'"
        ).fetchone()
        index_names = {
            row[1] for row in connection.execute("PRAGMA index_list('memories')").fetchall()
        }
    finally:
        connection.close()

    assert memory_columns["classification_status"] == ("VARCHAR(16)", True)
    assert memory_columns["memory_domain"] == ("VARCHAR(32)", False)
    assert memory_columns["memory_kind"] == ("VARCHAR(64)", False)
    assert memory_columns["subject_namespace"] == ("VARCHAR(64)", False)
    assert memory_columns["subject_value"] == ("VARCHAR(255)", False)
    assert partition_columns["retrieval_state_revision"] == ("INTEGER", True)
    assert memory_sql is not None
    assert "ck_memories_classification_status" in memory_sql[0]
    assert "ck_memories_subject_pair" in memory_sql[0]
    assert "ck_memories_active_classification_contract" in memory_sql[0]
    assert partition_sql is not None
    assert "ck_memory_partition_retrieval_revision_wire_bounds" in partition_sql[0]
    assert "ix_memories_partition_classification_visibility_day" in index_names


@pytest.mark.parametrize(
    "classification_update",
    [
        "classification_status='unknown'",
        "subject_namespace='skill_id',subject_value=NULL",
        (
            "classification_status='active',memory_domain='npc_history',"
            "memory_kind='gift_given',subject_namespace='item_id',subject_value='(O)66'"
        ),
        (
            "classification_status='active',memory_domain='player_progression',"
            "memory_kind='skill_level_reached',subject_namespace='skill_id',"
            "subject_value='luck'"
        ),
        (
            "audience_scope='npc',audience_npc_id='Abigail',"
            "classification_status='active',memory_domain='npc_history',"
            "memory_kind='gift_given',subject_namespace='item_id',"
            "subject_value='Amethyst'"
        ),
    ],
    ids=[
        "status",
        "subject-pair",
        "npc-domain-public-audience",
        "unsupported-subject",
        "unqualified-gift-subject",
    ],
)
def test_memory_domain_head_rejects_invalid_classification_rows(
    tmp_path: Path,
    classification_update: str,
) -> None:
    """进程外 direct SQL 也不能制造越权 active 行或部分 subject。"""

    database_path = tmp_path / "memory-domain-invalid.sqlite3"
    _upgrade_database(database_path)
    connection = sqlite3.connect(database_path)
    try:
        _insert_legacy_memory(
            connection,
            memory_id="memory:quarantined-probe",
            event_id="event:quarantined-probe",
            audience_scope="public",
            audience_npc_id=None,
            event_type="unknown",
            event_version="1",
            source="unknown",
            payload={"unknown": True},
            summary="保留的未知历史行。",
            tags=[],
            occurred_day_index=1,
        )
        connection.commit()
        with pytest.raises(sqlite3.IntegrityError):
            connection.execute(f"UPDATE memories SET {classification_update}")
    finally:
        connection.close()


@pytest.mark.parametrize("invalid_revision", [-1, 2_147_483_648, 1.5])
def test_memory_domain_head_rejects_invalid_retrieval_state_revision(
    tmp_path: Path,
    invalid_revision: int | float,
) -> None:
    """候选状态水位与 memory revision 使用相同的严格 Int32 物理边界。"""

    database_path = tmp_path / "retrieval-revision-invalid.sqlite3"
    _upgrade_database(database_path)
    connection = sqlite3.connect(database_path)
    try:
        with pytest.raises(sqlite3.IntegrityError):
            connection.execute(
                "INSERT INTO memory_partition_states ("
                "save_id,player_id,memory_revision,retrieval_state_revision,"
                "committed_through_day_index,updated_at_utc) VALUES (?,?,?,?,?,?)",
                (
                    "save-invalid-retrieval",
                    "player-migration",
                    0,
                    invalid_revision,
                    -1,
                    "2026-07-15 00:00:00",
                ),
            )
    finally:
        connection.close()


def test_player_skill_projection_migration_backfills_without_revision_drift(
    tmp_path: Path,
) -> None:
    """0003 只修正目标 memory 文本和标签，不伪造事件 revision 或历史使用状态。"""

    database_path = tmp_path / "player-skill-projection.sqlite3"
    alembic_config = _alembic_config(database_path)
    command.upgrade(alembic_config, "20260713_0002")
    connection = sqlite3.connect(database_path)
    try:
        _insert_partition_state(connection, revision=2, committed_day=14)
        _insert_legacy_progression_memory(
            connection,
            memory_id="memory:combat",
            event_id="event:combat",
            milestone="skill_combat_level_1",
            source="smapi.player.level_changed",
            occurred_day_index=14,
            use_count=2,
            last_used_day_index=20,
        )
        _insert_legacy_progression_memory(
            connection,
            memory_id="memory:fishing",
            event_id="event:fishing",
            milestone="skill_fishing_level_1",
            source="smapi.player.level_changed",
            occurred_day_index=7,
        )
        _insert_legacy_progression_memory(
            connection,
            memory_id="memory:community",
            event_id="event:community",
            milestone="community_center_pantry_completed",
            source="smapi",
            occurred_day_index=10,
        )
        _insert_legacy_passthrough_generation(connection)
        connection.commit()
    finally:
        connection.close()

    command.upgrade(alembic_config, "head")

    connection = sqlite3.connect(database_path)
    try:
        memories = connection.execute(
            "SELECT memory_id,summary,tags_json,occurred_day_index,use_count,"
            "last_used_day_index FROM memories ORDER BY memory_id"
        ).fetchall()
        watermark = connection.execute(
            "SELECT memory_revision,committed_through_day_index FROM memory_partition_states"
        ).fetchone()
        database_revision = connection.execute("SELECT version_num FROM alembic_version").fetchone()
        legacy_generation = connection.execute(
            "SELECT status,reason_code,result_text,evidence_ids_json "
            "FROM dialogue_generations WHERE generation_id='generation:legacy'"
        ).fetchone()
    finally:
        connection.close()

    assert memories[0] == (
        "memory:combat",
        "第 14 天，玩家的战斗技能提升到 1 级。",
        '["progression","actor:player","skill:combat","level:1","milestone:skill_combat_level_1"]',
        14,
        2,
        20,
    )
    assert memories[1] == (
        "memory:community",
        "第 10 天，世界进度达成：community_center_pantry_completed。",
        '["progression", "milestone:community_center_pantry_completed"]',
        10,
        0,
        None,
    )
    assert memories[2] == (
        "memory:fishing",
        "第 7 天，玩家的钓鱼技能提升到 1 级。",
        '["progression","actor:player","skill:fishing","level:1",'
        '"milestone:skill_fishing_level_1"]',
        7,
        0,
        None,
    )
    assert watermark == (2, 14)
    assert database_revision == ("20260715_0004",)
    assert legacy_generation == (
        "passthrough",
        "NO_CLEAR_ENHANCEMENT_VALUE",
        None,
        "[]",
    )


def test_player_skill_projection_migration_round_trip_is_idempotent(
    tmp_path: Path,
) -> None:
    """0003→0002→0003 不降级已改进语义，也不重复改变业务状态。"""

    database_path = tmp_path / "player-skill-projection-round-trip.sqlite3"
    alembic_config = _alembic_config(database_path)
    command.upgrade(alembic_config, "20260713_0002")
    connection = sqlite3.connect(database_path)
    try:
        _insert_partition_state(connection, revision=2, committed_day=14)
        _insert_legacy_progression_memory(
            connection,
            memory_id="memory:combat",
            event_id="event:combat",
            milestone="skill_combat_level_1",
            source="smapi.player.level_changed",
            occurred_day_index=14,
            use_count=2,
            last_used_day_index=20,
        )
        connection.commit()
    finally:
        connection.close()

    command.upgrade(alembic_config, "head")

    def read_business_snapshot() -> tuple[tuple[object, ...], tuple[object, ...]]:
        """读取 migration 获准修改和必须保留的字段，排除 Alembic revision。"""

        snapshot_connection = sqlite3.connect(database_path)
        try:
            memory = snapshot_connection.execute(
                "SELECT summary,tags_json,occurred_day_index,use_count,last_used_day_index,"
                "payload_json,created_at_utc FROM memories WHERE memory_id='memory:combat'"
            ).fetchone()
            watermark = snapshot_connection.execute(
                "SELECT memory_revision,committed_through_day_index,updated_at_utc "
                "FROM memory_partition_states"
            ).fetchone()
        finally:
            snapshot_connection.close()
        assert memory is not None
        assert watermark is not None
        return memory, watermark

    first_upgrade_snapshot = read_business_snapshot()
    command.downgrade(alembic_config, "20260713_0002")
    downgraded_snapshot = read_business_snapshot()
    command.upgrade(alembic_config, "head")
    second_upgrade_snapshot = read_business_snapshot()

    connection = sqlite3.connect(database_path)
    try:
        final_revision = connection.execute("SELECT version_num FROM alembic_version").fetchone()
    finally:
        connection.close()

    assert downgraded_snapshot == first_upgrade_snapshot
    assert second_upgrade_snapshot == first_upgrade_snapshot
    assert final_revision == ("20260715_0004",)


@pytest.mark.parametrize(
    ("revision", "committed_day"),
    [
        (1, WIRE_INTEGER_MAX + 1),
        (1, -2),
        (-1, -1),
        (WIRE_INTEGER_MAX + 1, 0),
        (0, 0),
        (1, -1),
        (1.5, 0),
        (1, 0.5),
    ],
    ids=[
        "day-overflow",
        "day-below-minus-one",
        "negative-revision",
        "revision-overflow",
        "zero-revision-with-day",
        "positive-revision-with-no-day",
        "fractional-revision",
        "fractional-committed-day",
    ],
)
def test_head_rejects_invalid_memory_partition_state_rows(
    tmp_path: Path,
    revision: int | float,
    committed_day: int | float,
) -> None:
    """即使调用者绕过服务层，SQLite head 也必须拒绝八类腐化水位。"""

    database_path = tmp_path / "partition-state-invalid.sqlite3"
    _upgrade_database(database_path)
    connection = sqlite3.connect(database_path)
    try:
        with pytest.raises(sqlite3.IntegrityError):
            _insert_partition_state(
                connection,
                revision=revision,
                committed_day=committed_day,
            )
    finally:
        connection.close()


def test_head_accepts_memory_partition_state_boundary_rows(tmp_path: Path) -> None:
    """未初始化、最小已提交与 Int32 最大水位三组合法边界都必须可持久化。"""

    database_path = tmp_path / "partition-state-valid.sqlite3"
    _upgrade_database(database_path)
    connection = sqlite3.connect(database_path)
    try:
        valid_states = [
            (0, -1),
            (1, 0),
            (WIRE_INTEGER_MAX, WIRE_INTEGER_MAX),
        ]
        for index, (revision, committed_day) in enumerate(valid_states):
            _insert_partition_state(
                connection,
                revision=revision,
                committed_day=committed_day,
                save_id=f"save-valid-{index}",
            )
        connection.commit()
        rows = connection.execute(
            "SELECT memory_revision, committed_through_day_index "
            "FROM memory_partition_states ORDER BY id"
        ).fetchall()
    finally:
        connection.close()

    assert rows == valid_states


def test_partition_state_bounds_upgrade_preserves_valid_existing_row(tmp_path: Path) -> None:
    """从 0001 升级 0002 必须保留符合新不变量的既有分区水位。"""

    database_path = tmp_path / "partition-state-upgrade-valid.sqlite3"
    alembic_config = _alembic_config(database_path)
    command.upgrade(alembic_config, "20260713_0001")
    connection = sqlite3.connect(database_path)
    try:
        _insert_partition_state(connection, revision=1, committed_day=0)
        connection.commit()
    finally:
        connection.close()

    command.upgrade(alembic_config, "20260713_0002")

    connection = sqlite3.connect(database_path)
    try:
        row = connection.execute(
            "SELECT memory_revision, committed_through_day_index FROM memory_partition_states"
        ).fetchone()
        database_revision = connection.execute("SELECT version_num FROM alembic_version").fetchone()
    finally:
        connection.close()

    assert row == (1, 0)
    assert database_revision == ("20260713_0002",)


def test_partition_state_bounds_upgrade_fails_closed_on_old_invalid_row(
    tmp_path: Path,
) -> None:
    """0001 曾允许的不一致水位不能在升级时被静默修复或丢弃。"""

    database_path = tmp_path / "partition-state-upgrade-invalid.sqlite3"
    alembic_config = _alembic_config(database_path)
    command.upgrade(alembic_config, "20260713_0001")
    connection = sqlite3.connect(database_path)
    try:
        _insert_partition_state(connection, revision=1, committed_day=-1)
        connection.commit()
    finally:
        connection.close()

    with pytest.raises(RuntimeError, match="memory partition state invalid"):
        command.upgrade(alembic_config, "20260713_0002")

    # 0002 必须在 SQLite batch 建临时表前发现腐化；原业务表与 revision 保持
    # 旧状态，便于运维人员修复后重试，而不是得到不可恢复的半迁移数据库。
    connection = sqlite3.connect(database_path)
    try:
        original_row = connection.execute(
            "SELECT memory_revision, committed_through_day_index FROM memory_partition_states"
        ).fetchone()
        database_revision = connection.execute("SELECT version_num FROM alembic_version").fetchone()
        temporary_table = connection.execute(
            "SELECT name FROM sqlite_master "
            "WHERE type = 'table' AND name = '_alembic_tmp_memory_partition_states'"
        ).fetchone()
    finally:
        connection.close()

    assert original_row == (1, -1)
    assert database_revision == ("20260713_0001",)
    assert temporary_table is None

    # 运维只修复明确识别出的业务水位；migration 本身不得静默改写。清理失败
    # 现场后，同一条 Alembic 命令必须可安全重试，不能被残留 batch 临时表永久
    # 卡住。
    connection = sqlite3.connect(database_path)
    try:
        connection.execute(
            "UPDATE memory_partition_states "
            "SET committed_through_day_index = 0 "
            "WHERE save_id = 'save-migration'"
        )
        connection.commit()
    finally:
        connection.close()

    command.upgrade(alembic_config, "20260713_0002")

    connection = sqlite3.connect(database_path)
    try:
        repaired_row = connection.execute(
            "SELECT memory_revision, committed_through_day_index FROM memory_partition_states"
        ).fetchone()
        final_revision = connection.execute("SELECT version_num FROM alembic_version").fetchone()
    finally:
        connection.close()

    assert repaired_row == (1, 0)
    assert final_revision == ("20260713_0002",)


@pytest.mark.parametrize(
    ("revision", "committed_day", "repair_column"),
    [
        (1.5, 0, "memory_revision"),
        (1, 0.5, "committed_through_day_index"),
    ],
    ids=["fractional-revision", "fractional-committed-day"],
)
def test_partition_state_bounds_upgrade_rejects_fractional_old_row_and_can_retry(
    tmp_path: Path,
    revision: int | float,
    committed_day: int | float,
    repair_column: str,
) -> None:
    """0001 的 REAL 水位必须在 batch 前失败，人工改回 integer 后可重试。

    SQLite 的 ``INTEGER`` 只是 affinity，不能阻止 1.5 以 REAL 存储。测试同时
    检查失败现场没有 Alembic 临时表，避免一次数据问题永久破坏迁移可恢复性。
    """

    database_path = tmp_path / f"partition-state-upgrade-{repair_column}.sqlite3"
    alembic_config = _alembic_config(database_path)
    command.upgrade(alembic_config, "20260713_0001")
    connection = sqlite3.connect(database_path)
    try:
        _insert_partition_state(
            connection,
            revision=revision,
            committed_day=committed_day,
        )
        connection.commit()
    finally:
        connection.close()

    with pytest.raises(RuntimeError, match="memory partition state invalid"):
        command.upgrade(alembic_config, "20260713_0002")

    connection = sqlite3.connect(database_path)
    try:
        stored_state = connection.execute(
            "SELECT memory_revision, typeof(memory_revision), "
            "committed_through_day_index, typeof(committed_through_day_index) "
            "FROM memory_partition_states"
        ).fetchone()
        database_revision = connection.execute("SELECT version_num FROM alembic_version").fetchone()
        temporary_table = connection.execute(
            "SELECT name FROM sqlite_master "
            "WHERE type = 'table' AND name = '_alembic_tmp_memory_partition_states'"
        ).fetchone()
    finally:
        connection.close()

    expected_state = (
        revision,
        "real" if repair_column == "memory_revision" else "integer",
        committed_day,
        "real" if repair_column == "committed_through_day_index" else "integer",
    )
    assert stored_state == expected_state
    assert database_revision == ("20260713_0001",)
    assert temporary_table is None

    connection = sqlite3.connect(database_path)
    try:
        connection.execute(
            f"UPDATE memory_partition_states SET {repair_column} = ?",
            (1 if repair_column == "memory_revision" else 0,),
        )
        connection.commit()
    finally:
        connection.close()

    command.upgrade(alembic_config, "20260713_0002")

    connection = sqlite3.connect(database_path)
    try:
        repaired_state = connection.execute(
            "SELECT memory_revision, typeof(memory_revision), "
            "committed_through_day_index, typeof(committed_through_day_index) "
            "FROM memory_partition_states"
        ).fetchone()
        final_revision = connection.execute("SELECT version_num FROM alembic_version").fetchone()
    finally:
        connection.close()

    assert repaired_state == (1, "integer", 0, "integer")
    assert final_revision == ("20260713_0002",)


def test_partition_state_bounds_migration_round_trip_preserves_data_and_constraints(
    tmp_path: Path,
) -> None:
    """0002→0001→0002 必须保留合法数据，并准确移除、恢复新 CHECK。"""

    database_path = tmp_path / "partition-state-round-trip.sqlite3"
    alembic_config = _alembic_config(database_path)
    command.upgrade(alembic_config, "20260713_0002")
    connection = sqlite3.connect(database_path)
    try:
        _insert_partition_state(connection, revision=1, committed_day=0)
        connection.commit()
    finally:
        connection.close()

    command.downgrade(alembic_config, "20260713_0001")

    connection = sqlite3.connect(database_path)
    try:
        downgraded_row = connection.execute(
            "SELECT memory_revision, committed_through_day_index FROM memory_partition_states"
        ).fetchone()
        downgraded_revision = connection.execute(
            "SELECT version_num FROM alembic_version"
        ).fetchone()
        downgraded_table_sql = connection.execute(
            "SELECT sql FROM sqlite_master "
            "WHERE type = 'table' AND name = 'memory_partition_states'"
        ).fetchone()
        # 0001 确实允许 REAL；立即删除探针行，避免人为污染后续重新升级。
        _insert_partition_state(
            connection,
            revision=1.5,
            committed_day=0,
            save_id="save-0001-fractional-probe",
        )
        connection.execute(
            "DELETE FROM memory_partition_states WHERE save_id = 'save-0001-fractional-probe'"
        )
        connection.commit()
    finally:
        connection.close()

    assert downgraded_row == (1, 0)
    assert downgraded_revision == ("20260713_0001",)
    assert downgraded_table_sql is not None
    assert "ck_memory_partition_revision_wire_bounds" not in downgraded_table_sql[0]
    assert "ck_memory_partition_committed_day_wire_bounds" not in downgraded_table_sql[0]
    assert "ck_memory_partition_state_consistency" not in downgraded_table_sql[0]

    command.upgrade(alembic_config, "20260713_0002")

    connection = sqlite3.connect(database_path)
    try:
        upgraded_row = connection.execute(
            "SELECT memory_revision, committed_through_day_index FROM memory_partition_states"
        ).fetchone()
        upgraded_revision = connection.execute("SELECT version_num FROM alembic_version").fetchone()
        upgraded_table_sql = connection.execute(
            "SELECT sql FROM sqlite_master "
            "WHERE type = 'table' AND name = 'memory_partition_states'"
        ).fetchone()
        with pytest.raises(sqlite3.IntegrityError):
            _insert_partition_state(
                connection,
                revision=1.5,
                committed_day=0,
                save_id="save-0002-fractional-probe",
            )
    finally:
        connection.close()

    assert upgraded_row == (1, 0)
    assert upgraded_revision == ("20260713_0002",)
    assert upgraded_table_sql is not None
    assert "ck_memory_partition_revision_wire_bounds" in upgraded_table_sql[0]
    assert "ck_memory_partition_committed_day_wire_bounds" in upgraded_table_sql[0]
    assert "ck_memory_partition_state_consistency" in upgraded_table_sql[0]


def test_readiness_column_contract_covers_every_core_orm_column() -> None:
    """新增 ORM 业务列时必须同步扩展 readiness，不能再次退化为抽样探针。"""

    for table_name, required_columns in REQUIRED_COLUMN_SIGNATURES.items():
        orm_columns = {column.name for column in Base.metadata.tables[table_name].columns}
        assert set(required_columns) == orm_columns


def test_readiness_unique_contract_covers_every_core_orm_unique() -> None:
    """新增 ORM UNIQUE 时必须同步 readiness，不能只保护部分幂等键。"""

    orm_unique_signatures: dict[str, frozenset[tuple[str, ...]]] = {}
    for table_name in REQUIRED_COLUMN_SIGNATURES:
        table = Base.metadata.tables[table_name]
        orm_unique_signatures[table_name] = frozenset(
            tuple(column.name for column in constraint.columns)
            for constraint in table.constraints
            if isinstance(constraint, UniqueConstraint)
        )

    assert REQUIRED_UNIQUE_SIGNATURES == orm_unique_signatures


async def _read_sqlite_pragmas(storage: SqliteStorage) -> tuple[str, int, int]:
    """通过已配置的异步 engine 读取连接级安全 PRAGMA。"""

    async with storage.engine.connect() as connection:
        journal_mode = (await connection.exec_driver_sql("PRAGMA journal_mode")).scalar_one()
        busy_timeout = (await connection.exec_driver_sql("PRAGMA busy_timeout")).scalar_one()
        foreign_keys = (await connection.exec_driver_sql("PRAGMA foreign_keys")).scalar_one()
    return str(journal_mode), int(busy_timeout), int(foreign_keys)


async def _read_revision_through_engine(storage: SqliteStorage) -> str:
    """通过业务 engine 而不是 readiness 旁路读取实际连接的 revision。"""

    async with storage.engine.connect() as connection:
        revision = (
            await connection.exec_driver_sql("SELECT version_num FROM alembic_version")
        ).scalar_one()
    return str(revision)


def test_storage_configures_wal_busy_timeout_and_foreign_keys(
    migrated_database_url: str,
) -> None:
    """连接池中每条 SQLite 连接都必须强制并发与引用完整性 PRAGMA。"""

    import asyncio

    storage = SqliteStorage.from_url(migrated_database_url, busy_timeout_ms=7_500)
    try:
        journal_mode, busy_timeout, foreign_keys = asyncio.run(_read_sqlite_pragmas(storage))
    finally:
        asyncio.run(storage.dispose())

    assert journal_mode.lower() == "wal"
    assert busy_timeout == 7_500
    assert foreign_keys == 1


@pytest.mark.asyncio
async def test_percent_encoded_literal_filename_cannot_redirect_readiness_to_decoy(
    tmp_path: Path,
) -> None:
    """字面 ``%20`` 文件不得被 SQLite URI 二次解码为空格后探测另一数据库。"""

    decoy_database_path = tmp_path / "db name.sqlite3"
    configured_database_path = tmp_path / "db%20name.sqlite3"
    await asyncio.to_thread(_upgrade_database, decoy_database_path)
    sqlite3.connect(configured_database_path).close()

    storage = SqliteStorage.from_url(f"sqlite+aiosqlite:///{configured_database_path}")
    try:
        assert await storage.is_ready() is False
    finally:
        await storage.dispose()


@pytest.mark.asyncio
async def test_space_filename_uses_same_canonical_path_for_engine_and_readiness(
    tmp_path: Path,
) -> None:
    """普通空格文件名经统一规范化后，readiness 与业务 engine 必须命中同一库。"""

    database_path = tmp_path / "canonical database.sqlite3"
    await asyncio.to_thread(_upgrade_database, database_path)
    storage = SqliteStorage.from_url(f"sqlite+aiosqlite:///{database_path}")
    try:
        assert await storage.is_ready() is True
        assert storage.engine.url.database == str(database_path.resolve())
        assert await _read_revision_through_engine(storage) == REQUIRED_DATABASE_REVISION
    finally:
        await storage.dispose()


@pytest.mark.asyncio
async def test_relative_database_is_canonical_before_lazy_engine_connection(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """创建 storage 后 cwd 改变，也不能让 lazy engine 与 readiness 指向不同文件。"""

    database_path = tmp_path / "relative.sqlite3"
    await asyncio.to_thread(_upgrade_database, database_path)
    monkeypatch.chdir(tmp_path)
    storage = SqliteStorage.from_url("sqlite+aiosqlite:///relative.sqlite3")

    later_working_directory = tmp_path / "later-cwd"
    later_working_directory.mkdir()
    monkeypatch.chdir(later_working_directory)
    try:
        assert await storage.is_ready() is True
        assert storage.engine.url.database == str(database_path.resolve())
        assert await _read_revision_through_engine(storage) == REQUIRED_DATABASE_REVISION
        assert not (later_working_directory / "relative.sqlite3").exists()
    finally:
        await storage.dispose()


@pytest.mark.asyncio
async def test_symlink_database_identity_is_shared_by_engine_and_readiness(
    tmp_path: Path,
) -> None:
    """符号链接只在规范化时解析一次，engine 与探针都使用同一真实目标。"""

    target_path = tmp_path / "target.sqlite3"
    symlink_path = tmp_path / "database-link.sqlite3"
    await asyncio.to_thread(_upgrade_database, target_path)
    symlink_path.symlink_to(target_path)

    storage = SqliteStorage.from_url(f"sqlite+aiosqlite:///{symlink_path}")
    try:
        assert storage.engine.url.database == str(target_path.resolve())
        assert await storage.is_ready() is True
        assert await _read_revision_through_engine(storage) == REQUIRED_DATABASE_REVISION
    finally:
        await storage.dispose()


def test_sqlite_file_uri_is_rejected_instead_of_guessed() -> None:
    """file: URI 的共享缓存/URI 参数语义未受支持时必须显式拒绝。"""

    with pytest.raises(ValueError, match="file: URI"):
        SqliteStorage.from_url("sqlite+aiosqlite:///file:memory-db?mode=memory&cache=shared")


def test_readiness_probe_requires_alembic_head_and_core_tables(
    migrated_database_url: str,
    tmp_path: Path,
) -> None:
    """探针必须区分已迁移库与仅存在的空 SQLite 文件，且不建表。"""

    import asyncio
    import sqlite3

    ready_storage = SqliteStorage.from_url(migrated_database_url)
    empty_database_path = tmp_path / "empty.sqlite3"
    sqlite3.connect(empty_database_path).close()
    empty_storage = SqliteStorage.from_url(f"sqlite+aiosqlite:///{empty_database_path}")
    try:
        assert asyncio.run(ready_storage.is_ready()) is True
        assert asyncio.run(empty_storage.is_ready()) is False
    finally:
        asyncio.run(ready_storage.dispose())
        asyncio.run(empty_storage.dispose())

    connection = sqlite3.connect(empty_database_path)
    try:
        table_count = connection.execute(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'"
        ).fetchone()
    finally:
        connection.close()
    assert table_count == (0,)


@pytest.mark.asyncio
async def test_ready_returns_503_when_core_column_signature_is_damaged(
    migrated_database_url: str,
) -> None:
    """伪造正确 revision 和表名不足以 ready，核心 source 列缺失必须 fail closed。"""

    import sqlite3

    database_name = make_url(migrated_database_url).database
    assert database_name is not None
    connection = sqlite3.connect(database_name)
    try:
        connection.execute("ALTER TABLE game_events DROP COLUMN source")
        connection.commit()
    finally:
        connection.close()

    storage = SqliteStorage.from_url(migrated_database_url)
    app = create_app(
        Settings(database_url=migrated_database_url),
        event_service=EventService(storage),
    )
    transport = httpx.ASGITransport(app=app)
    try:
        async with httpx.AsyncClient(transport=transport, base_url="http://testserver") as client:
            response = await client.get("/api/v1/health/ready")
    finally:
        await storage.dispose()

    assert response.status_code == 503
    assert response.json() == {"detail": "SERVICE_NOT_READY"}
    assert database_name not in response.text


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("table_name", "column_name"),
    [
        ("game_events", "event_version"),
        ("memories", "expires_day_index"),
        ("memory_partition_states", "updated_at_utc"),
        ("dialogue_generations", "locale"),
        ("dialogue_display_receipts", "npc_id"),
    ],
)
async def test_readiness_rejects_missing_business_column_from_each_core_table(
    migrated_database_url: str,
    table_name: str,
    column_name: str,
) -> None:
    """每张核心表的实际读写列都属于 readiness 合同，不能只抽查少数字段。"""

    database_name = make_url(migrated_database_url).database
    assert database_name is not None
    connection = sqlite3.connect(database_name)
    try:
        connection.execute(f'ALTER TABLE "{table_name}" DROP COLUMN "{column_name}"')
        connection.commit()
    finally:
        connection.close()

    storage = SqliteStorage.from_url(migrated_database_url)
    try:
        assert await storage.is_ready() is False
    finally:
        await storage.dispose()


@pytest.mark.asyncio
@pytest.mark.parametrize(
    ("table_name", "constraint_name"),
    [
        ("game_events", "uq_game_events_partition_event"),
        ("memories", "uq_memories_memory_id"),
        ("memories", "uq_memories_partition_event"),
        (
            "memory_partition_states",
            "uq_memory_partition_states_partition",
        ),
        ("dialogue_generations", "uq_dialogue_generations_generation_id"),
        ("dialogue_generations", "uq_dialogue_generations_generation_key"),
        (
            "dialogue_display_receipts",
            "uq_dialogue_display_receipts_receipt_id",
        ),
    ],
)
async def test_readiness_rejects_each_missing_core_unique_constraint(
    migrated_database_url: str,
    table_name: str,
    constraint_name: str,
) -> None:
    """revision、表和列都正确时，缺任一核心幂等唯一约束仍必须 not ready。

    SQLite 不允许直接删除由表级 UNIQUE constraint 创建的自动索引，因此测试在
    空迁移库中用原始 CREATE SQL 重建目标表，仅删除指定约束。重建前后列签名
    完全相同，保证探针失败确实来自唯一约束，而不是附带的列损坏。
    """

    database_name = make_url(migrated_database_url).database
    assert database_name is not None
    connection = sqlite3.connect(database_name)
    try:
        _rebuild_table_without_named_unique(
            connection,
            table_name=table_name,
            constraint_name=constraint_name,
        )
        connection.commit()
        assert connection.execute("SELECT version_num FROM alembic_version").fetchone() == (
            REQUIRED_DATABASE_REVISION,
        )
    finally:
        connection.close()

    storage = SqliteStorage.from_url(migrated_database_url)
    try:
        assert await storage.is_ready() is False
    finally:
        await storage.dispose()


@pytest.mark.asyncio
async def test_readiness_requires_exactly_one_expected_alembic_head(
    migrated_database_url: str,
) -> None:
    """额外 revision row 代表分叉/腐化，不能因 fetchone 命中期望 head 而 ready。"""

    database_name = make_url(migrated_database_url).database
    assert database_name is not None
    connection = sqlite3.connect(database_name)
    try:
        connection.execute(
            "INSERT INTO alembic_version(version_num) VALUES (?)",
            ("bogus_parallel_head",),
        )
        connection.commit()
    finally:
        connection.close()

    storage = SqliteStorage.from_url(migrated_database_url)
    try:
        assert await storage.is_ready() is False
    finally:
        await storage.dispose()


@pytest.mark.asyncio
@pytest.mark.parametrize("weakened_index", ["partial", "nocase", "expression"])
async def test_readiness_rejects_semantically_weakened_unique_index(
    migrated_database_url: str,
    weakened_index: str,
) -> None:
    """partial、NOCASE 或表达式索引都不能冒充完整 BINARY 身份约束。"""

    database_name = make_url(migrated_database_url).database
    assert database_name is not None
    connection = sqlite3.connect(database_name)
    try:
        _rebuild_table_without_named_unique(
            connection,
            table_name="game_events",
            constraint_name="uq_game_events_partition_event",
        )
        if weakened_index == "partial":
            connection.execute(
                "CREATE UNIQUE INDEX ux_test_game_events_partial "
                "ON game_events(save_id, player_id, event_id) "
                "WHERE event_id <> 'bypass'"
            )
            insert_sql = (
                "INSERT INTO game_events "
                "(save_id, player_id, event_id, event_type, event_version, "
                "occurred_day_index, source, audience_scope, audience_npc_id, "
                "payload_json, created_at_utc) "
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)"
            )
            bypass_values = (
                "save-partial",
                "player-partial",
                "bypass",
                "world_progression",
                "1",
                1,
                "test",
                "public",
                None,
                "{}",
                "2026-07-13 00:00:00",
            )
            connection.execute(insert_sql, bypass_values)
            connection.execute(insert_sql, bypass_values)
            duplicate_count = connection.execute(
                "SELECT COUNT(*) FROM game_events WHERE event_id = 'bypass'"
            ).fetchone()
            assert duplicate_count == (2,), "partial unique 必须真实展示可绕过的重复身份"
        elif weakened_index == "nocase":
            connection.execute(
                "CREATE UNIQUE INDEX ux_test_game_events_nocase "
                "ON game_events(save_id COLLATE NOCASE, player_id, event_id)"
            )
        else:
            connection.execute(
                "CREATE UNIQUE INDEX ux_test_game_events_expression "
                "ON game_events(save_id, player_id, lower(event_id))"
            )
        connection.commit()
    finally:
        connection.close()

    storage = SqliteStorage.from_url(migrated_database_url)
    try:
        assert await storage.is_ready() is False
    finally:
        await storage.dispose()


@pytest.mark.asyncio
async def test_readiness_accepts_equivalent_explicit_full_unique_index(
    migrated_database_url: str,
) -> None:
    """显式、完整、BINARY 的等价 UNIQUE INDEX 可替代表级 constraint。"""

    database_name = make_url(migrated_database_url).database
    assert database_name is not None
    connection = sqlite3.connect(database_name)
    try:
        _rebuild_table_without_named_unique(
            connection,
            table_name="game_events",
            constraint_name="uq_game_events_partition_event",
        )
        connection.execute(
            "CREATE UNIQUE INDEX ux_test_game_events_full "
            "ON game_events(save_id, player_id, event_id)"
        )
        connection.commit()
    finally:
        connection.close()

    storage = SqliteStorage.from_url(migrated_database_url)
    try:
        assert await storage.is_ready() is True
    finally:
        await storage.dispose()


def test_plain_unconfigured_engine_is_not_used_as_false_positive(tmp_path: Path) -> None:
    """测试本身必须能区分我们的配置和 SQLAlchemy 默认值。"""

    import asyncio

    engine = create_async_engine(
        f"sqlite+aiosqlite:///{tmp_path / 'plain.sqlite3'}",
        poolclass=NullPool,
    )

    async def read_plain_values() -> tuple[int, int]:
        async with engine.connect() as connection:
            timeout = (await connection.exec_driver_sql("PRAGMA busy_timeout")).scalar_one()
            foreign_keys = (await connection.exec_driver_sql("PRAGMA foreign_keys")).scalar_one()
        await engine.dispose()
        return int(timeout), int(foreign_keys)

    timeout, foreign_keys = asyncio.run(read_plain_values())
    assert (timeout, foreign_keys) != (7_500, 1)
