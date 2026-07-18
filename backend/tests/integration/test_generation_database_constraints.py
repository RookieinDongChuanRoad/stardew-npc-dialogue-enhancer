"""initial migration 对 generation 终态不变量的真实 SQLite 约束测试。"""

from __future__ import annotations

import sqlite3

import pytest
from sqlalchemy.engine import make_url

_GENERATION_INSERT_SQL = """
INSERT INTO dialogue_generations (
    generation_id,
    generation_key,
    save_id,
    player_id,
    game_day_index,
    npc_id,
    locale,
    source_hash,
    relationship_stage,
    friendship_points,
    memory_cooldown_days,
    status,
    result_text,
    reason_code,
    evidence_ids_json,
    trace_id,
    guard_passed,
    evidence_authorized,
    created_at_utc,
    updated_at_utc
) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
"""

# 本项目固定 Python 3.11；以下集合来自该运行时 ``str.isspace()``，且不包含
# 无法出现在 SQLite 字符串字面量中的 NUL。数据库 CHECK 必须与 Python
# ``str.strip()`` 使用同一集合，避免正常 API 与 direct/ORM insert 语义分叉。
_PYTHON_311_STRIP_CODEPOINTS = (
    9,
    10,
    11,
    12,
    13,
    28,
    29,
    30,
    31,
    32,
    133,
    160,
    5760,
    *range(8192, 8203),
    8232,
    8233,
    8239,
    8287,
    12288,
)
_INVALID_EDGE_WHITESPACE_CASES = (
    *(
        (f"leading-U+{codepoint:04X}", f"{chr(codepoint)}leading")
        for codepoint in _PYTHON_311_STRIP_CODEPOINTS
    ),
    ("trailing-crlf", "trailing\r\n"),
    ("trailing-nbsp", "trailing\u00a0"),
)


def _database_path(database_url: str) -> str:
    """从测试 aiosqlite URL 取得已由 Alembic 升级的真实 SQLite 路径。"""

    database_name = make_url(database_url).database
    assert database_name is not None
    return database_name


def _generation_values(
    *,
    generation_id: str,
    status: str,
    result_text: str | None,
    evidence_ids_json: str,
    guard_passed: int,
    evidence_authorized: int,
) -> tuple[object, ...]:
    """构造 direct INSERT 所需完整非空列，不经过 ORM 或正常保存 API。"""

    return (
        generation_id,
        f"key:{generation_id}",
        "save-db-check",
        "player-db-check",
        14,
        "Abigail",
        "zh-CN",
        "sha256:db-check",
        "friend",
        750,
        3,
        status,
        result_text,
        "DATABASE_CONSTRAINT_TEST",
        evidence_ids_json,
        f"trace:{generation_id}",
        guard_passed,
        evidence_authorized,
        "2026-07-13 00:00:00",
        "2026-07-13 00:00:00",
    )


@pytest.mark.parametrize(
    ("_case_name", "invalid_text"),
    _INVALID_EDGE_WHITESPACE_CASES,
    ids=[case_name for case_name, _ in _INVALID_EDGE_WHITESPACE_CASES],
)
def test_database_rejects_generated_text_with_python_strip_edge_whitespace(
    migrated_database_url: str,
    _case_name: str,
    invalid_text: str,
) -> None:
    """直接 SQL 也必须拒绝 Python ``str.strip()`` 会删除的边缘空白。"""

    connection = sqlite3.connect(_database_path(migrated_database_url))
    try:
        with pytest.raises(sqlite3.IntegrityError):
            connection.execute(
                _GENERATION_INSERT_SQL,
                _generation_values(
                    generation_id="generation-invalid-whitespace",
                    status="generated",
                    result_text=invalid_text,
                    evidence_ids_json="[]",
                    guard_passed=1,
                    evidence_authorized=1,
                ),
            )
    finally:
        connection.close()


@pytest.mark.parametrize(
    "invalid_text",
    ["\x00", "\x00abc", "abc\x00"],
    ids=["nul-only", "nul-prefix", "nul-suffix"],
)
def test_database_rejects_generated_text_containing_nul_anywhere(
    migrated_database_url: str,
    invalid_text: str,
) -> None:
    """SQLite CHECK 必须用 ``instr`` 拒绝任意位置 NUL，不能依赖 length 语义。"""

    connection = sqlite3.connect(_database_path(migrated_database_url))
    try:
        with pytest.raises(sqlite3.IntegrityError):
            connection.execute(
                _GENERATION_INSERT_SQL,
                _generation_values(
                    generation_id="generation-invalid-nul",
                    status="generated",
                    result_text=invalid_text,
                    evidence_ids_json="[]",
                    guard_passed=1,
                    evidence_authorized=1,
                ),
            )
    finally:
        connection.close()


@pytest.mark.parametrize(
    "evidence_ids_json",
    ['["memory:ghost"]', "{}"],
    ids=["non-empty-array", "not-an-array"],
)
def test_database_rejects_non_generated_row_with_invalid_evidence_shape(
    migrated_database_url: str,
    evidence_ids_json: str,
) -> None:
    """passthrough/skipped/failed 在数据库层也必须拥有真实空 evidence 数组。"""

    connection = sqlite3.connect(_database_path(migrated_database_url))
    try:
        with pytest.raises(sqlite3.IntegrityError):
            connection.execute(
                _GENERATION_INSERT_SQL,
                _generation_values(
                    generation_id="generation-passthrough-with-evidence",
                    status="passthrough",
                    result_text=None,
                    evidence_ids_json=evidence_ids_json,
                    guard_passed=0,
                    evidence_authorized=0,
                ),
            )
    finally:
        connection.close()


def test_database_preserves_legal_internal_whitespace_and_empty_passthrough_evidence(
    migrated_database_url: str,
) -> None:
    """边缘规则不得误删合法内部 CRLF/tab/NBSP，也不得拒绝空 evidence 终态。"""

    all_internal_whitespace = "".join(chr(value) for value in _PYTHON_311_STRIP_CODEPOINTS)
    legal_text = f"开头\r\n{all_internal_whitespace}\r\n结尾"
    connection = sqlite3.connect(_database_path(migrated_database_url))
    try:
        connection.execute(
            _GENERATION_INSERT_SQL,
            _generation_values(
                generation_id="generation-internal-whitespace",
                status="generated",
                result_text=legal_text,
                evidence_ids_json="[]",
                guard_passed=1,
                evidence_authorized=1,
            ),
        )
        connection.execute(
            _GENERATION_INSERT_SQL,
            _generation_values(
                generation_id="generation-empty-passthrough",
                status="passthrough",
                result_text=None,
                evidence_ids_json="[]",
                guard_passed=0,
                evidence_authorized=0,
            ),
        )
        connection.commit()

        persisted_text = connection.execute(
            "SELECT result_text FROM dialogue_generations WHERE generation_id = ?",
            ("generation-internal-whitespace",),
        ).fetchone()
        assert persisted_text == (legal_text,)
    finally:
        connection.close()
