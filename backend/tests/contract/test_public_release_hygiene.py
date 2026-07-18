"""公开支持文件的发布卫生门禁。

这些入口会直接出现在公开仓库、release 包或用户复制的配置中。因此这里刻意不依赖
运行时配置：只做静态扫描，避免维护者的本机路径、历史 Provider 选择或内部开发文档
意外重新进入公开指导材料。
"""

from __future__ import annotations

from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
# 维护者 home 路径也需要被门禁拦截，但源码不能原样包含该值，否则独立快照的
# 全树静态扫描会把门禁规则自身误判为泄漏。分片只影响源码表示，运行时仍精确匹配完整路径。
MAINTAINER_HOME_FRAGMENT = "/Users/" + "liurongfu"
PUBLIC_SUPPORT_ENTRYPOINTS = (
    REPOSITORY_ROOT / "backend" / ".env.example",
    REPOSITORY_ROOT / "contracts" / "README.md",
    REPOSITORY_ROOT / "tools" / "VanillaDialogueManifestExtractor" / "README.md",
    REPOSITORY_ROOT / "backend" / "tests" / "unit" / "test_provider_launcher.py",
)


def test_public_support_entrypoints_do_not_embed_maintainer_specific_guidance() -> None:
    """公开入口只能给出可移植、非秘密的操作说明。

    回放命令以片段拼接，防止测试源码自身成为公开可复制的付费 Provider 诱导；扫描的
    目标仍是完整命令名称，确保它不会从任一公开入口重新出现。
    """

    forbidden_fragments = (
        MAINTAINER_HOME_FRAGMENT,
        "api.pinaic.com",
        "gpt-5.6-sol",
        "docs/development.md",
        "run_domain_" + "provider_replay",
    )

    leaked_by_entrypoint: dict[str, list[str]] = {}
    for entrypoint in PUBLIC_SUPPORT_ENTRYPOINTS:
        contents = entrypoint.read_text(encoding="utf-8")
        leaked_fragments = [fragment for fragment in forbidden_fragments if fragment in contents]
        if leaked_fragments:
            leaked_by_entrypoint[str(entrypoint.relative_to(REPOSITORY_ROOT))] = leaked_fragments

    assert not leaked_by_entrypoint, f"公开支持入口包含不可公开的指导信息: {leaked_by_entrypoint}"
