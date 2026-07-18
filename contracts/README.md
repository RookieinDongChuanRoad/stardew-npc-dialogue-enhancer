# 共享 wire contract 与测试 fixtures

本目录包含 C# 与 Python 共享的 wire schemas（`contracts/v1/*.schema.json`）和
fixtures（`contracts/fixtures/*.json`）。完整验证由两部分组成：

1. Draft 2020-12 JSON Schema 负责 object 形状、required/unknown 字段、枚举、数组大小、
   字符串和整数数值范围；
2. 项目扩展 `x-stardew-json-integer-token: true` 负责整数的 raw JSON 词法规则。

标准 JSON Schema 把数学值 `13.0` 与 `1.3e1` 视为 integer，无法表达“token 中不得出现
小数点或指数”。本项目有意采用更严格的 v1 规则：所有标记该扩展的字段必须使用
`13`、`-2` 这类 JSON integer token；`13.0`、`1.3e1`、字符串和布尔值均非法。

执行位置：

- Python/FastAPI：Pydantic strict DTO 从 raw JSON 解析后拒绝 float；
- C#/SMAPI：`WireJsonShapeValidator` 在 DTO 实例化前检查 `JsonElement.GetRawText()`；
- 合同测试：同时验证 Schema 扩展存在、两端 raw JSON 入口行为一致，以及 Int32 范围。

该扩展只作用于显式 typed contract 字段，不递归限制 `payload`、
`progression_signals` 或 `memory_signals` 中由具体事件版本另行解释的开放 JSON object。

## Synthetic manifest fixtures

synthetic manifest 是为合同和分类器测试编写的原创测试数据。它们出现的 canonical asset
名称或 key 只用于 classifier 测试，不能证明任何真实 XNB 的内容、存在性或对应关系。

真实 manifest 只能由本机工具从用户自己的游戏安装生成，默认不提交到仓库；它是本地
验证输入，而不是公开 fixture 或资产事实的来源。

## 对话风格样本数量

`dialogue_generation_batch.schema.json` 的 `style_examples=2..5` 仅是 wire contract 的
数组数量范围。它不声明、推断或保证任何 NPC、locale、资产文件或游戏数据中存在相应数量
的可用样本；实际运行时是否接受样本仍由各端的业务规则决定。
