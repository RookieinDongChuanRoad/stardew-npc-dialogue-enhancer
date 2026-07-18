"""ASGI 入口。

模块只暴露预构造的 `app`，不调用 uvicorn，也不在 import 时启动线程、网络连接或
长驻进程。实际服务器启动必须由未来明确的 CLI/部署命令负责。
"""

from stardew_npc_agent.api import create_app

app = create_app()
