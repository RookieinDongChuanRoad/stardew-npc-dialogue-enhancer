"""ordinary/rainy daily source classifier 的精确合同测试。"""

import pytest

from stardew_npc_agent.dialogue_source_policy import classify_dialogue_source


@pytest.mark.parametrize(
    ("npc_id", "asset_name", "dialogue_key", "expected_family"),
    [
        ("Abigail", "Characters/Dialogue/Abigail", "fall_Mon", "ordinary_daily"),
        ("Abigail", "Characters\\Dialogue\\Abigail", "fall_Mon", "ordinary_daily"),
        ("Abigail", "Characters/Dialogue/rainy", "Abigail", "rainy_daily"),
        ("Abigail", "Characters\\Dialogue\\rainy", "Abigail", "rainy_daily"),
        ("Abigail", "Characters/Dialogue/rainy", "Sebastian", None),
        ("Abigail", "Characters/Dialogue/Rainy", "Abigail", None),
        ("Abigail", "Characters/Dialogue/MarriageDialogueAbigail", "Mon", None),
        ("Abigail", "Characters/Dialogue/MarriageDialogue", "Mon", None),
        ("Abigail", "Strings/StringsFromCSFiles", "NPC.cs.123", None),
        ("Abigail", "Characters/Dialogue/Abigail", "divorced", None),
        ("Abigail", "Characters/Dialogue/Abigail", "EngagementDialogue", None),
        ("Abigail", " Characters/Dialogue/Abigail", "fall_Mon", None),
        ("Abigail", "Characters/Dialogue/Abigail", "fall_Mon ", None),
        ("Á", "Characters/Dialogue/Á", "fall_Mon", None),
    ],
)
def test_classify_dialogue_source_accepts_only_exact_supported_daily_sources(
    npc_id: str,
    asset_name: str,
    dialogue_key: str,
    expected_family: str | None,
) -> None:
    """两端应独立推导同一 family，不能信任 wire 中不存在的 source_family。"""

    identity = classify_dialogue_source(
        npc_id=npc_id,
        asset_name=asset_name,
        dialogue_key=dialogue_key,
    )

    if expected_family is None:
        assert identity is None
        return

    assert identity is not None
    assert identity.family.value == expected_family
    assert identity.npc_id == npc_id
    assert identity.asset_name == asset_name.replace("\\", "/")
    assert identity.dialogue_key == dialogue_key
