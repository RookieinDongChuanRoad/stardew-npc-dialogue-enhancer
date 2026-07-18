"""SQLite engine、readiness 与持久化操作的薄兼容门面。
公开调用方继续从 ``stardew_npc_agent.storage`` 导入原有 DTO、ORM record、异常
和 ``SqliteStorage``，因此本次拆分不破坏外部导入路径。实际事件/记忆与
generation/ACK SQL 分别位于两个扁平模块；这里仅管理连接、Schema 探针、稳定
异常翻译和调用转发，不引入 protocol、Unit of Work 或多层 repository。
"""

from __future__ import annotations

import sqlite3
from collections.abc import Awaitable, Sequence
from pathlib import Path
from typing import Literal, TypeVar
from urllib.parse import quote

import aiosqlite
from sqlalchemy import event
from sqlalchemy.engine import make_url
from sqlalchemy.engine.interfaces import DBAPIConnection
from sqlalchemy.exc import (
    DBAPIError,
    DisconnectionError,
    InterfaceError,
    OperationalError,
)
from sqlalchemy.exc import (
    TimeoutError as SQLAlchemyTimeoutError,
)
from sqlalchemy.ext.asyncio import (
    AsyncEngine,
    AsyncSession,
    async_sessionmaker,
    create_async_engine,
)

from stardew_npc_agent.dialogue_storage import (
    acknowledge_display as _acknowledge_display,
)
from stardew_npc_agent.dialogue_storage import (
    get_dialogue_generation_by_key as _get_dialogue_generation_by_key,
)
from stardew_npc_agent.dialogue_storage import (
    save_dialogue_generation as _save_dialogue_generation,
)
from stardew_npc_agent.event_memory_storage import (
    get_domain_memory_candidates as _get_domain_memory_candidates,
)
from stardew_npc_agent.event_memory_storage import (
    get_event_history as _get_event_history,
)
from stardew_npc_agent.event_memory_storage import (
    get_memory_partition_snapshot as _get_memory_partition_snapshot,
)
from stardew_npc_agent.event_memory_storage import (
    get_progression_context as _get_progression_context,
)
from stardew_npc_agent.event_memory_storage import ingest_events as _ingest_events
from stardew_npc_agent.event_memory_storage import search_memories as _search_memories
from stardew_npc_agent.storage_models import (
    Base,
    DialogueDisplayReceiptRecord,
    DialogueGenerationRecord,
    GameEventRecord,
    MemoryPartitionStateRecord,
    MemoryRecord,
)
from stardew_npc_agent.storage_types import (
    MAX_EVENT_BATCH_ITEMS,
    REQUIRED_COLUMN_SIGNATURES,
    REQUIRED_CORE_TABLES,
    REQUIRED_DATABASE_REVISION,
    REQUIRED_UNIQUE_SIGNATURES,
    AudienceScope,
    DialogueGenerationInput,
    DialogueGenerationSnapshot,
    DisplayNotAllowedStorageError,
    DisplayReceiptConflictStorageError,
    DisplayReceiptInput,
    DomainMemoryQuery,
    EventBatchStorageResult,
    EventBatchTooLargeError,
    EventHistoryQuery,
    EventStorageResult,
    EventStorageStatus,
    EvidenceRecord,
    GenerationNotFoundStorageError,
    GenerationStatus,
    InvalidDialogueGenerationError,
    InvalidMemoryQueryError,
    MemoryPartitionSnapshot,
    MemoryPartitionStateInvalidStorageError,
    MemoryProjection,
    MemoryRevisionExhaustedStorageError,
    MemorySearchQuery,
    MemorySnapshotMismatchStorageError,
    PreparedEvent,
    ProgressionContextQuery,
    StorageUnavailableError,
)

# 明确列出兼容导出，既记录稳定公共边界，也避免调用方因物理拆分而修改 import。
__all__ = [
    "AudienceScope",
    "Base",
    "DialogueDisplayReceiptRecord",
    "DialogueGenerationInput",
    "DialogueGenerationRecord",
    "DialogueGenerationSnapshot",
    "DomainMemoryQuery",
    "DisplayNotAllowedStorageError",
    "DisplayReceiptConflictStorageError",
    "DisplayReceiptInput",
    "EventBatchStorageResult",
    "EventBatchTooLargeError",
    "EventHistoryQuery",
    "EventStorageResult",
    "EventStorageStatus",
    "EvidenceRecord",
    "GameEventRecord",
    "GenerationNotFoundStorageError",
    "GenerationStatus",
    "InvalidDialogueGenerationError",
    "InvalidMemoryQueryError",
    "MAX_EVENT_BATCH_ITEMS",
    "MemoryPartitionStateInvalidStorageError",
    "MemoryPartitionSnapshot",
    "MemoryPartitionStateRecord",
    "MemoryProjection",
    "MemoryRevisionExhaustedStorageError",
    "MemoryRecord",
    "MemorySearchQuery",
    "MemorySnapshotMismatchStorageError",
    "PreparedEvent",
    "ProgressionContextQuery",
    "SqliteStorage",
    "StorageUnavailableError",
]

StorageResultT = TypeVar("StorageResultT")

# SQLite 把锁竞争、磁盘 I/O、权限问题以及“表不存在/SQL 错误”都归入
# ``OperationalError``。只按异常类映射会把后两类编程错误也伪装成 503。
# 这里采用 SQLite 官方 result code 的基础码，显式列出“存储当前不可服务”的
# 连接/资源类失败；SQLITE_ERROR、SCHEMA、MISUSE、RANGE 等保留原异常。
_UNAVAILABLE_SQLITE_ERROR_CODES = frozenset(
    {
        sqlite3.SQLITE_PERM,
        sqlite3.SQLITE_BUSY,
        sqlite3.SQLITE_LOCKED,
        sqlite3.SQLITE_NOMEM,
        sqlite3.SQLITE_READONLY,
        sqlite3.SQLITE_INTERRUPT,
        sqlite3.SQLITE_IOERR,
        sqlite3.SQLITE_CORRUPT,
        sqlite3.SQLITE_FULL,
        sqlite3.SQLITE_CANTOPEN,
        sqlite3.SQLITE_PROTOCOL,
        sqlite3.SQLITE_AUTH,
        sqlite3.SQLITE_NOTADB,
    }
)


class SqliteStorage:
    """集中管理异步 engine、session factory 与扁平持久化函数。

    类不会自动建表；生产和测试都必须先运行 Alembic migration。这是故意的
    fail-closed 设计，避免 ORM metadata 与 migration 两套 Schema 悄然分叉。
    """

    def __init__(
        self,
        engine: AsyncEngine,
        session_factory: async_sessionmaker[AsyncSession],
        database_path: Path | None,
    ) -> None:
        """保存 engine、session factory 与供无副作用探针使用的本地路径。"""

        self.engine = engine
        self.session_factory = session_factory
        self._database_path = database_path

    @classmethod
    def from_url(cls, database_url: str, *, busy_timeout_ms: int = 5_000) -> SqliteStorage:
        """根据 aiosqlite URL 创建延迟连接的存储门面。

        Args:
            database_url: 必须是普通文件或 ``:memory:`` 的
                ``sqlite+aiosqlite`` URL；同步 driver 与 ``file:`` URI 均拒绝。
            busy_timeout_ms: SQLite 写锁竞争的最长等待毫秒数，必须为正数。
        Returns:
            尚未打开物理连接、也不会自动建表的 ``SqliteStorage``。
        Raises:
            ValueError: driver、file URI 或 busy timeout 超出受支持边界。
        """

        parsed_url = make_url(database_url)
        if parsed_url.drivername != "sqlite+aiosqlite":
            raise ValueError("database_url 必须使用 sqlite+aiosqlite driver")
        if busy_timeout_ms <= 0:
            raise ValueError("busy_timeout_ms 必须大于 0")

        database_name = parsed_url.database
        if database_name is not None and database_name.startswith("file:"):
            # SQLAlchemy/SQLite 的 file: URI 还携带 uri、mode、cache 等额外
            # 语义。MVP readiness 无法可靠复刻所有组合，必须显式拒绝而不是
            # 让 engine 与旁路探针各自猜测。
            raise ValueError("database_url 暂不支持 SQLite file: URI")

        if database_name is None or database_name == ":memory:":
            database_path = None
            canonical_url = parsed_url
        else:
            # create_async_engine 是 lazy 的；若保留相对 URL，创建 storage 后
            # cwd 改变可能让业务连接与已保存 readiness 路径分叉。先解析符号
            # 链接并转成唯一绝对路径，再把同一路径放回 SQLAlchemy URL 对象。
            database_path = Path(database_name).expanduser().resolve()
            canonical_url = parsed_url.set(database=str(database_path))

        engine = create_async_engine(
            canonical_url,
            connect_args={"timeout": busy_timeout_ms / 1_000},
            pool_pre_ping=True,
        )

        @event.listens_for(engine.sync_engine, "connect")
        def configure_sqlite_connection(
            dbapi_connection: DBAPIConnection,
            _connection_record: object,
        ) -> None:
            """在每条物理连接上重申并发和引用完整性约束。"""

            cursor = dbapi_connection.cursor()
            try:
                # WAL 允许读者与单写者并行；busy_timeout 让短暂锁竞争等待，
                # 而不是立即把可重试的事件上传误判为永久丢失。
                cursor.execute("PRAGMA journal_mode=WAL")
                cursor.execute(f"PRAGMA busy_timeout={busy_timeout_ms:d}")
                cursor.execute("PRAGMA foreign_keys=ON")
            finally:
                cursor.close()

        return cls(
            engine,
            async_sessionmaker(engine, expire_on_commit=False),
            database_path,
        )

    async def dispose(self) -> None:
        """关闭本实例连接池；不删除数据文件，也不影响其他进程。"""

        await self.engine.dispose()

    async def is_ready(self) -> bool:
        """只读确认数据库 revision、核心表、关键列与幂等唯一约束。

        普通文件 URL 使用 SQLite ``mode=ro``；缺失文件返回 ``False``，不会因
        health check 创建空库。探针不运行 migration、不修改 PRAGMA，任何路径、
        SQL 或 Schema 错误都被折叠为 ``False``，避免通过 HTTP 泄露内部信息。
        """

        if self._database_path is None or not self._database_path.is_file():
            return False

        connection: aiosqlite.Connection | None = None
        try:
            # aiosqlite 需要 URI 才能使用 mode=ro。文件系统中的字面 ``%``、
            # 空格、问号或井号必须先百分号转义，否则 SQLite 会二次解码并探测
            # 另一文件；``/:`` 保留路径分隔与平台盘符语义。
            quoted_path = quote(self._database_path.as_posix(), safe="/:")
            read_only_uri = f"file:{quoted_path}?mode=ro"
            connection = await aiosqlite.connect(read_only_uri, uri=True)
            version_cursor = await connection.execute("SELECT version_num FROM alembic_version")
            try:
                version_rows = await version_cursor.fetchall()
            finally:
                await version_cursor.close()
            # Alembic 可在分叉时保存多个 head。只取第一行会把“期望 head +
            # bogus head”误判为健康，因此集合必须精确等于唯一受支持 revision。
            available_revisions = {str(row[0]) for row in version_rows}
            if available_revisions != {REQUIRED_DATABASE_REVISION}:
                return False

            table_cursor = await connection.execute(
                "SELECT name FROM sqlite_master WHERE type = 'table'"
            )
            try:
                table_rows = await table_cursor.fetchall()
            finally:
                await table_cursor.close()
            if not REQUIRED_CORE_TABLES.issubset({str(row[0]) for row in table_rows}):
                return False
            if not await _has_required_column_signatures(connection):
                return False
            return await _has_required_unique_signatures(connection)
        except (aiosqlite.Error, OSError, ValueError):
            return False
        finally:
            if connection is not None:
                await connection.close()

    async def ingest_events(
        self,
        save_id: str,
        player_id: str,
        events: Sequence[PreparedEvent],
    ) -> EventBatchStorageResult:
        """写入事件批次，并只翻译明确的运行时连接类失败。"""

        return await _translate_storage_errors(
            _ingest_events(self.session_factory, save_id, player_id, events)
        )

    async def search_memories(self, query: MemorySearchQuery) -> list[EvidenceRecord]:
        """查询硬过滤后的 evidence；参数与编程错误保持原类型。"""

        return await _translate_storage_errors(_search_memories(self.session_factory, query))

    async def get_domain_memory_candidates(
        self,
        query: DomainMemoryQuery,
    ) -> list[EvidenceRecord]:
        """按冻结二元快照读取一个固定领域的候选 evidence。"""

        return await _translate_storage_errors(
            _get_domain_memory_candidates(self.session_factory, query)
        )

    async def get_event_history(self, query: EventHistoryQuery) -> list[EvidenceRecord]:
        """读取 Agent 可见事件历史，并只翻译明确的运行时存储失败。"""

        return await _translate_storage_errors(_get_event_history(self.session_factory, query))

    async def get_progression_context(
        self,
        query: ProgressionContextQuery,
    ) -> list[EvidenceRecord]:
        """读取公共世界进度，并只翻译明确的运行时存储失败。"""

        return await _translate_storage_errors(
            _get_progression_context(self.session_factory, query)
        )

    async def get_memory_partition_snapshot(
        self,
        save_id: str,
        player_id: str,
    ) -> MemoryPartitionSnapshot:
        """读取生成所需分区水位，并沿用稳定的运行时存储错误翻译。"""

        return await _translate_storage_errors(
            _get_memory_partition_snapshot(self.session_factory, save_id, player_id)
        )

    async def get_dialogue_generation_by_key(
        self,
        generation_key: str,
    ) -> DialogueGenerationSnapshot | None:
        """读取 generation cache；Schema/数据错误保持原类型而不伪装 503。"""

        return await _translate_storage_errors(
            _get_dialogue_generation_by_key(self.session_factory, generation_key)
        )

    async def save_dialogue_generation(self, value: DialogueGenerationInput) -> None:
        """保存并授权生成终态；IntegrityError 不得伪装为运行时 503。"""

        await _translate_storage_errors(_save_dialogue_generation(self.session_factory, value))

    async def acknowledge_display(
        self,
        generation_id: str,
        receipt: DisplayReceiptInput,
    ) -> Literal["accepted", "duplicate"]:
        """记录展示 ACK，并翻译可重试的运行时存储失败。"""

        return await _translate_storage_errors(
            _acknowledge_display(self.session_factory, generation_id, receipt)
        )


async def _translate_storage_errors(operation: Awaitable[StorageResultT]) -> StorageResultT:
    """只翻译 Operational/connection/pool-timeout，不掩盖代码或数据错误。
    ``IntegrityError``、``ProgrammingError`` 和普通 ``DBAPIError`` 原样上抛，
    否则不变量破坏会被伪装成可重试 503。连接失败使用 ``from None``，阻止 DB
    path、SQL 与 driver message 通过异常链进入上层响应。
    """

    try:
        return await operation
    except (DisconnectionError, SQLAlchemyTimeoutError):
        raise StorageUnavailableError() from None
    except (OperationalError, InterfaceError) as error:
        if _is_explicit_storage_unavailable_error(error):
            raise StorageUnavailableError() from None
        raise
    except DBAPIError as error:
        if error.connection_invalidated:
            raise StorageUnavailableError() from None
        raise


def _is_explicit_storage_unavailable_error(error: DBAPIError) -> bool:
    """判断 DBAPIError 是否明确属于连接/资源不可用，而非 SQL 编程错误。"""

    if error.connection_invalidated:
        return True
    sqlite_error_code = getattr(error.orig, "sqlite_errorcode", None)
    if not isinstance(sqlite_error_code, int):
        return False
    # extended result code 的低 8 位是基础码，例如 SQLITE_BUSY_SNAPSHOT 仍应
    # 归入 SQLITE_BUSY；这避免依赖 driver 的具体扩展码版本。
    return sqlite_error_code & 0xFF in _UNAVAILABLE_SQLITE_ERROR_CODES


async def _has_required_column_signatures(connection: aiosqlite.Connection) -> bool:
    """使用只读 PRAGMA 核对五张核心表全部业务列的类型、可空性和主键位。"""

    for table_name, required_columns in REQUIRED_COLUMN_SIGNATURES.items():
        cursor = await connection.execute(f'PRAGMA table_info("{table_name}")')
        try:
            rows = await cursor.fetchall()
        finally:
            await cursor.close()
        actual_columns = {
            str(row[1]): (str(row[2]).upper(), bool(row[3]), bool(row[5])) for row in rows
        }
        if any(
            actual_columns.get(column_name) != required_signature
            for column_name, required_signature in required_columns.items()
        ):
            return False
    return True


async def _has_required_unique_signatures(connection: aiosqlite.Connection) -> bool:
    """核对全部核心幂等唯一语义，不依赖索引名或建表语法。
    同列名的 partial index 可以允许条件外重复，NOCASE collation 也会改变业务
    ID 的逐字符身份语义。因此只接受完整非 partial、全部真实列、顺序完全一致且
    使用 BINARY collation 的 unique index；显式 ``CREATE UNIQUE INDEX`` 与表级
    ``UNIQUE`` 都可接受。
    """

    for table_name, required_signatures in REQUIRED_UNIQUE_SIGNATURES.items():
        index_cursor = await connection.execute(f'PRAGMA index_list("{table_name}")')
        try:
            index_rows = await index_cursor.fetchall()
        finally:
            await index_cursor.close()

        actual_signatures: set[tuple[str, ...]] = set()
        for index_row in index_rows:
            # PRAGMA index_list: seq, name, unique, origin, partial。只看 unique
            # 位不足以阻止 ``WHERE event_id <> 'bypass'`` 这类条件绕过。
            if not bool(index_row[2]) or len(index_row) < 5 or bool(index_row[4]):
                continue
            index_name = str(index_row[1]).replace('"', '""')
            column_cursor = await connection.execute(f'PRAGMA index_xinfo("{index_name}")')
            try:
                column_rows = await column_cursor.fetchall()
            finally:
                await column_cursor.close()

            # index_xinfo: seqno, cid, name, desc, coll, key。辅助 rowid 行的
            # key=0 不属于唯一键；cid<0/name=NULL 表示表达式或 rowid，不能冒充
            # 业务列。身份字符串必须按 BINARY 逐字符比较，NOCASE 不等价。
            key_rows = [row for row in column_rows if bool(row[5])]
            if not key_rows or any(
                int(row[1]) < 0 or row[2] is None or str(row[4]).upper() != "BINARY"
                for row in key_rows
            ):
                continue
            actual_signatures.add(tuple(str(row[2]) for row in key_rows))

        if not required_signatures.issubset(actual_signatures):
            return False
    return True
