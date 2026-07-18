"""Phase 8 离线数据集覆盖与确定性 evaluator 回归测试。"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest

from evals.evaluators.deterministic import (
    EvaluationReport,
    evaluate_paths,
    load_evaluation_bundle,
)
from evals.run_eval import main as run_eval_main
from stardew_npc_agent.dialogue_template import parse_game_template

BACKEND_ROOT = Path(__file__).resolve().parents[2]
DATASET_PATH = BACKEND_ROOT / "evals" / "datasets" / "mvp_dialogue_cases.json"
OUTPUTS_PATH = BACKEND_ROOT / "evals" / "datasets" / "mvp_reference_outputs.json"
SYNTHETIC_MANIFEST_PATH = (
    BACKEND_ROOT.parent / "contracts" / "fixtures" / "synthetic_dialogue_source_manifest.json"
)


def test_mvp_dataset_covers_frozen_product_matrix_and_reference_passes() -> None:
    """十二 NPC、四关系、四天气、三类记忆和特殊台词必须同时存在且基线全绿。"""

    bundle = load_evaluation_bundle(DATASET_PATH, OUTPUTS_PATH)
    report = evaluate_paths(DATASET_PATH, OUTPUTS_PATH)

    assert len(bundle.dataset.cases) >= 24
    assert bundle.coverage.npc_ids == frozenset(
        {
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
        }
    )
    assert bundle.coverage.relationship_stages == frozenset(
        {"acquaintance", "friend", "dating", "spouse"}
    )
    assert bundle.coverage.weather == frozenset({"clear", "rain", "snow", "green_rain"})
    assert bundle.coverage.memory_scenarios == frozenset({"none", "relevant", "conflicting"})
    assert bundle.coverage.dialogue_kinds == frozenset({"ordinary", "special"})
    assert bundle.coverage.source_families == frozenset(
        {"ordinary_daily", "rainy_daily", "unsupported"}
    )
    assert bundle.coverage.address_slots == frozenset({"none", "player_name", "invalid"})
    assert bundle.coverage.memory_domains == frozenset(
        {"npc_history", "player_progression", "world_progression"}
    )
    _assert_reference_report_passes(report)


def test_phase9_boundary_cases_cover_exact_negative_sources_without_fake_provenance() -> None:
    """第二 @、endearment、婚后与绿雨边界必须显式标成 synthetic mutation。"""

    raw = json.loads(DATASET_PATH.read_text(encoding="utf-8"))
    cases = {item["case_id"]: item for item in raw["cases"]}
    expected = {
        "abigail_friend_special_source": "ordinary_daily",
        "alex_second_player_name_slot": "ordinary_daily",
        "abigail_endearment_token": "ordinary_daily",
        "abigail_marriage_dialogue_source": "unsupported",
        "abigail_green_rain_source": "unsupported",
    }
    for case_id, source_family in expected.items():
        case = cases[case_id]
        assert case["dialogue_kind"] == "special"
        assert case["source_family"] == source_family
        assert case["source_provenance"] == "synthetic_boundary_mutation"
        assert case["expected"]["terminal_status"] == "skipped"

    second_slot_text = cases["alex_second_player_name_slot"]["request"]["items"][0][
        "source_dialogue"
    ]["text"]
    assert second_slot_text.count("@") == 2
    assert (
        "%endearment"
        in cases["abigail_endearment_token"]["request"]["items"][0]["source_dialogue"]["text"]
    )


@pytest.mark.parametrize(
    ("case_id", "mutation", "expected_reason"),
    [
        (
            "abigail_friend_unknown_evidence",
            "escape_failed_candidate",
            "GENERATED_EVIDENCE_UNSUPPORTED",
        ),
        (
            "abigail_dating_conflicting_memory",
            "escape_failed_candidate",
            "UNSUPPORTED_FACT_ESCAPED",
        ),
        (
            "sebastian_friend_illegal_dsl",
            "escape_failed_candidate",
            "ILLEGAL_COMMAND_ESCAPED",
        ),
        (
            "abigail_acquaintance_relationship_violation",
            "escape_failed_candidate",
            "RELATIONSHIP_VIOLATION_ESCAPED",
        ),
        (
            "abigail_acquaintance_no_memory",
            "fallback_text",
            "FALLBACK_TEXT_OR_EVIDENCE_PRESENT",
        ),
    ],
)
def test_evaluator_rejects_each_hard_boundary_escape(
    tmp_path: Path,
    case_id: str,
    mutation: str,
    expected_reason: str,
) -> None:
    """负向 candidate 可被 Guard 观察，但任何非法内容进入最终输出都必须令评测失败。"""

    outputs = json.loads(OUTPUTS_PATH.read_text(encoding="utf-8"))
    output_case = next(item for item in outputs["cases"] if item["case_id"] == case_id)
    if mutation == "escape_failed_candidate":
        candidate = output_case["candidate"]
        assert candidate is not None and candidate["text"] is not None
        output_case["terminal"] = {
            "status": "generated",
            "text": candidate["text"],
            "evidence_ids": candidate["evidence_ids"],
            "reason_code": "MUTATED_UNSAFE_ESCAPE",
        }
    elif mutation == "fallback_text":
        output_case["terminal"]["text"] = "不应出现在 fallback 的残留文本。"
    else:  # pragma: no cover - parametrization is intentionally closed above.
        raise AssertionError(f"unknown mutation: {mutation}")

    mutated_outputs = tmp_path / "mutated_outputs.json"
    mutated_outputs.write_text(
        json.dumps(outputs, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )

    report = evaluate_paths(DATASET_PATH, mutated_outputs)

    assert report.passed is False
    case_report = next(item for item in report.cases if item.case_id == case_id)
    assert expected_reason in case_report.reason_codes


def test_loader_rejects_missing_or_extra_case_identity(tmp_path: Path) -> None:
    """dataset/predictions 必须一一对应，不能静默漏评或把另一 case 的结果错配过来。"""

    outputs = json.loads(OUTPUTS_PATH.read_text(encoding="utf-8"))
    outputs["cases"].pop()
    outputs["cases"].append(
        {
            "case_id": "unexpected-case",
            "candidate": None,
            "terminal": {
                "status": "skipped",
                "text": None,
                "evidence_ids": [],
                "reason_code": "UNEXPECTED",
            },
        }
    )
    bad_outputs = tmp_path / "bad_case_ids.json"
    bad_outputs.write_text(json.dumps(outputs, ensure_ascii=False), encoding="utf-8")

    with pytest.raises(ValueError, match="case_id"):
        load_evaluation_bundle(DATASET_PATH, bad_outputs)


def test_synthetic_fixture_cases_join_exact_public_manifest_entries() -> None:
    """每个基线 case 必须唯一复用公开 synthetic source/style，并现场重算原始 UTF-8 hash。"""

    dataset = json.loads(DATASET_PATH.read_text(encoding="utf-8"))
    manifest = json.loads(SYNTHETIC_MANIFEST_PATH.read_text(encoding="utf-8"))

    assert dataset["dataset_version"] == "mvp-dialogue-eval-synthetic-v1"
    assert {case["source_provenance"] for case in dataset["cases"]} == {
        "synthetic_fixture",
        "synthetic_boundary_mutation",
    }

    synthetic_cases = [
        case for case in dataset["cases"] if case["source_provenance"] == "synthetic_fixture"
    ]
    assert synthetic_cases
    for case in synthetic_cases:
        request = case["request"]
        item = request["items"][0]
        source = item["source_dialogue"]
        matches = [
            entry
            for entry in manifest["entries"]
            if entry["source_provenance"] == "synthetic"
            and entry["style_provenance"] == "synthetic"
            and entry["npc_id"] == case["npc_id"]
            and entry["locale"] == request["stable_day_context"]["locale"]
            and entry["source_family"] == case["source_family"]
            and entry["asset_name"] == source["asset_name"]
            and entry["dialogue_key"] == source["dialogue_key"]
            and entry["source_text"] == source["text"]
            and entry["source_hash"] == source["source_hash"]
            and entry["style_texts"] == item["style_examples"]
        ]
        assert len(matches) == 1, case["case_id"]
        expected_hash = "sha256:" + hashlib.sha256(source["text"].encode("utf-8")).hexdigest()
        assert source["source_hash"] == expected_hash


def test_alex_english_fixture_preserves_player_name_address_slot_coverage() -> None:
    """Alex 英文基线是唯一单 ``@`` synthetic 正例，必须保留 player_name typed coverage。"""

    dataset = json.loads(DATASET_PATH.read_text(encoding="utf-8"))
    alex_case = next(
        case for case in dataset["cases"] if case["case_id"] == "alex_manifest_baseline"
    )
    source_text = alex_case["request"]["items"][0]["source_dialogue"]["text"]

    assert alex_case["request"]["stable_day_context"]["locale"] == "en"
    assert parse_game_template(source_text).address_slot.value == "player_name"


def test_eval_cli_returns_zero_for_reference_and_nonzero_for_failed_gate(
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """CLI exit code 是自动化门禁，不得只打印失败报告后仍返回成功。"""

    assert run_eval_main(["--dataset", str(DATASET_PATH), "--outputs", str(OUTPUTS_PATH)]) == 0
    success_stdout = capsys.readouterr().out
    assert '"passed": true' in success_stdout
    assert '"llm_judge_status": "optional_not_run"' in success_stdout

    outputs = json.loads(OUTPUTS_PATH.read_text(encoding="utf-8"))
    first_case = outputs["cases"][0]
    first_case["terminal"]["text"] = "fallback 残留。"
    failed_outputs = tmp_path / "failed_outputs.json"
    failed_outputs.write_text(json.dumps(outputs, ensure_ascii=False), encoding="utf-8")

    assert run_eval_main(["--dataset", str(DATASET_PATH), "--outputs", str(failed_outputs)]) == 1
    failure_stdout = capsys.readouterr().out
    assert '"passed": false' in failure_stdout


def _assert_reference_report_passes(report: EvaluationReport) -> None:
    """集中冻结 MVP 硬指标，避免各测试对阈值产生不同解释。"""

    assert report.passed is True
    assert report.metrics.case_pass_rate == 1.0
    assert report.metrics.evidence_precision == 1.0
    assert report.metrics.unsupported_fact_escape_rate == 0.0
    assert report.metrics.illegal_command_escape_rate == 0.0
    assert report.metrics.relationship_violation_escape_rate == 0.0
    assert report.metrics.fallback_accuracy == 1.0
    assert report.llm_judge_status == "optional_not_run"
    assert all(case.passed for case in report.cases)
