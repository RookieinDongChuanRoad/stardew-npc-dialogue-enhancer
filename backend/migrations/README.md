# 数据库 migration

Schema 只能通过 Alembic 升级，运行时不会自动调用
`metadata.create_all()`。默认本地数据库 URL 位于 `alembic.ini`；测试和部署可以
在 Alembic Config 中显式覆盖 `sqlalchemy.url`。

从空库升级：

```bash
uv run alembic upgrade head
```
