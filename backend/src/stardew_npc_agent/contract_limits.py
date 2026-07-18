"""跨语言 v1 wire contract 使用的整数端点。

C# 游戏适配层的 DTO 使用 ``int``，而 Python ``int`` 是任意精度整数。公开合同必须
采用两端都能无损表示的最低共同范围，避免 Python 接受后才在 C# 解析或 SQLite bind
阶段失败。本模块只保存无依赖常量，供 DTO 与存储纵深校验共同引用。
"""

from typing import Final

WIRE_INTEGER_MIN: Final[int] = -2_147_483_648
WIRE_INTEGER_MAX: Final[int] = 2_147_483_647
