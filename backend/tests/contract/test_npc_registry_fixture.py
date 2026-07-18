"""Python NPC registries 与跨语言共享 fixture 的顺序合同。"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest

from stardew_npc_agent import profiles

PROJECT_ROOT = Path(__file__).resolve().parents[3]
NPC_REGISTRY_FIXTURE = PROJECT_ROOT / "contracts" / "fixtures" / "vanilla_marriageable_npcs.json"
EXPECTED_NPC_IDS = [
    "Abigail",
    "Alex",
    "Elliott",
    "Emily",
    "Haley",
    "Harvey",
    "Leah",
    "Maru",
    "Penny",
    "Sam",
    "Sebastian",
    "Shane",
]


def _load_registry_fixture() -> dict[str, Any]:
    """读取只供跨语言测试使用的 fixture，并拒绝非 object 顶层。"""

    with NPC_REGISTRY_FIXTURE.open(encoding="utf-8") as file_handle:
        payload = json.load(file_handle)
    assert isinstance(payload, dict)
    return payload


def test_shared_npc_fixture_freezes_exact_vanilla_order() -> None:
    """fixture 自身必须精确冻结批准的十二人顺序，不能只比较集合。"""

    payload = _load_registry_fixture()

    assert payload == {
        "schema_version": "vanilla-marriageable-npcs-v1",
        "npc_ids": EXPECTED_NPC_IDS,
    }


@pytest.mark.parametrize(
    "registry_name",
    ("NPC_DEFINITIONS", "NPC_PROFILES", "NPC_AGENT_PROFILES"),
)
def test_python_registry_keys_match_shared_fixture_in_order(registry_name: str) -> None:
    """三个 Python registry 必须保持 fixture 的相同 key 和插入顺序。

    ``getattr`` 的空 mapping 回退让首轮 RED 能被 pytest 正常收集，并把失败精确
    表达为缺少 ``NPC_DEFINITIONS``，而不是在 collection 阶段因 ImportError 中止。
    """

    fixture_npc_ids = _load_registry_fixture()["npc_ids"]
    registry = getattr(profiles, registry_name, {})

    assert list(registry) == fixture_npc_ids
