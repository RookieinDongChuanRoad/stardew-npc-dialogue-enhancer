"""公开 synthetic 对话来源 manifest 的跨语言合同测试。

该 fixture 只承载可公开的合成文本，不能混入真实游戏安装、版本、XNB 路径或
散列等 provenance。测试同时复用生产 source-family classifier 与 typed-template
parser，保证 fixture 的安全边界不会另起一套近似规则。
"""

from __future__ import annotations

import hashlib
import json
from collections import Counter
from pathlib import Path
from typing import Any

from stardew_npc_agent.dialogue_source_policy import classify_dialogue_source
from stardew_npc_agent.dialogue_template import DialogueTemplateError, parse_game_template

BACKEND_ROOT = Path(__file__).resolve().parents[2]
REPOSITORY_ROOT = BACKEND_ROOT.parent
MANIFEST_PATH = (
    REPOSITORY_ROOT / "contracts" / "fixtures" / "synthetic_dialogue_source_manifest.json"
)
NPC_REGISTRY_PATH = REPOSITORY_ROOT / "contracts" / "fixtures" / "vanilla_marriageable_npcs.json"

EXPECTED_LOCALES = ("en", "zh-CN")
EXPECTED_FAMILIES = ("ordinary_daily", "rainy_daily")
EXPECTED_ROOT_KEYS = frozenset({"manifest_version", "fixture_provenance", "entries"})
EXPECTED_ENTRY_KEYS = frozenset(
    {
        "npc_id",
        "locale",
        "source_family",
        "asset_name",
        "dialogue_key",
        "source_text",
        "source_hash",
        "source_provenance",
        "style_asset_name",
        "style_context_season",
        "style_context_heart_level",
        "style_keys",
        "style_texts",
        "style_provenance",
    }
)


def _read_json_object(path: Path) -> dict[str, Any]:
    """读取 JSON object，不捕获缺失文件以保留 fixture 缺失的明确失败信号。"""

    value = json.loads(path.read_text(encoding="utf-8"))
    assert isinstance(value, dict)
    return value


def _sha256_text(text: str) -> str:
    """按生产合同的 UTF-8 逐字节语义计算 source hash，绝不 trim 或 normalize。"""

    return "sha256:" + hashlib.sha256(text.encode("utf-8")).hexdigest()


def _assert_nonempty_trimmed_string(value: Any, *, field_name: str) -> None:
    """拒绝非字符串、空字符串和边缘空白，避免畸形值被长度或散列检查误导。"""

    assert isinstance(value, str), f"{field_name} 必须是字符串"
    assert value, f"{field_name} 不得为空"
    assert value == value.strip(), f"{field_name} 不得含边缘空白"


def _assert_synthetic_text_markers(entry: dict[str, Any]) -> None:
    """验证公开样本文本的 locale marker，仅防意外混入，原创性仍须人工审查。"""

    locale = entry["locale"]
    if locale == "en":
        source_prefix = "Synthetic test:"
        style_prefix = "Synthetic style sample"
    else:
        assert locale == "zh-CN"
        source_prefix = "合成测试："
        style_prefix = "合成测试："

    assert entry["source_text"].startswith(source_prefix)
    assert all(text.startswith(style_prefix) for text in entry["style_texts"])


def test_synthetic_manifest_has_only_public_synthetic_provenance_and_complete_matrix() -> None:
    """验证根字段、禁止的真实来源元数据及 12×2×2 无重复覆盖矩阵。"""

    manifest = _read_json_object(MANIFEST_PATH)
    registry = _read_json_object(NPC_REGISTRY_PATH)

    assert frozenset(manifest) == EXPECTED_ROOT_KEYS
    assert manifest["manifest_version"] == "synthetic-dialogue-source-manifest-v1"
    assert manifest["fixture_provenance"] == "synthetic"
    assert isinstance(manifest["entries"], list)

    npc_ids = registry["npc_ids"]
    expected_combinations = [
        (npc_id, locale, family)
        for npc_id in npc_ids
        for locale in EXPECTED_LOCALES
        for family in EXPECTED_FAMILIES
    ]
    actual_combinations = [
        (entry["npc_id"], entry["locale"], entry["source_family"]) for entry in manifest["entries"]
    ]
    assert len(manifest["entries"]) == 48
    assert Counter(actual_combinations) == Counter(expected_combinations)
    assert all(frozenset(entry) == EXPECTED_ENTRY_KEYS for entry in manifest["entries"])


def test_synthetic_manifest_entries_are_hashable_and_production_classifiable() -> None:
    """验证每条合成正文与风格样本可重算，并由生产分类器推导 family。"""

    manifest = _read_json_object(MANIFEST_PATH)

    for entry in manifest["entries"]:
        _assert_nonempty_trimmed_string(entry["source_text"], field_name="source_text")
        assert isinstance(entry["style_keys"], list)
        assert isinstance(entry["style_texts"], list)
        for style_key in entry["style_keys"]:
            _assert_nonempty_trimmed_string(style_key, field_name="style_keys 元素")
        for style_text in entry["style_texts"]:
            _assert_nonempty_trimmed_string(style_text, field_name="style_texts 元素")
        _assert_synthetic_text_markers(entry)
        assert entry["source_provenance"] == "synthetic"
        assert entry["style_provenance"] == "synthetic"
        assert entry["source_hash"] == _sha256_text(entry["source_text"])
        assert entry["style_asset_name"] == f"Characters/Dialogue/{entry['npc_id']}"
        assert len(entry["style_keys"]) == len(entry["style_texts"])
        assert 2 <= len(entry["style_keys"]) <= 5
        assert len(entry["style_keys"]) == len(set(entry["style_keys"]))
        assert len(entry["style_texts"]) == len(set(entry["style_texts"]))
        assert entry["source_text"] not in entry["style_texts"]
        assert entry["style_context_season"] in {"spring", "summer", "fall", "winter"}
        assert entry["style_context_heart_level"] in {0, 2, 4, 6, 8, 10}

        identity = classify_dialogue_source(
            npc_id=entry["npc_id"],
            asset_name=entry["asset_name"],
            dialogue_key=entry["dialogue_key"],
        )
        assert identity is not None
        assert identity.family.value == entry["source_family"]


def test_synthetic_manifest_template_coverage_has_one_intentional_rejection() -> None:
    """验证可解析正文覆盖无称呼与玩家名槽，且仅 Shane 雨天样本故意被拒绝。"""

    manifest = _read_json_object(MANIFEST_PATH)
    accepted_templates = []
    rejected_entries = []

    for entry in manifest["entries"]:
        try:
            accepted_templates.append(parse_game_template(entry["source_text"]))
        except DialogueTemplateError:
            rejected_entries.append(entry)

    assert any(template.address_slot.value == "none" for template in accepted_templates)
    assert any(template.address_slot.value == "player_name" for template in accepted_templates)
    assert len(rejected_entries) == 1
    assert rejected_entries[0]["npc_id"] == "Shane"
    assert rejected_entries[0]["locale"] == "en"
    assert rejected_entries[0]["source_family"] == "rainy_daily"
    assert rejected_entries[0]["source_text"] == "Synthetic test: Shane notices the rain.$h"
