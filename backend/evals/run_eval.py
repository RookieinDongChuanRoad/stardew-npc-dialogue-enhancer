"""运行 Phase 8 确定性离线评测并用退出码充当 CI/本地门禁。"""

from __future__ import annotations

import argparse
import json
from collections.abc import Sequence
from pathlib import Path

from pydantic import ValidationError

from evals.evaluators.deterministic import evaluate_paths, report_to_json

EVAL_ROOT = Path(__file__).resolve().parent
DEFAULT_DATASET_PATH = EVAL_ROOT / "datasets" / "mvp_dialogue_cases.json"
DEFAULT_OUTPUTS_PATH = EVAL_ROOT / "datasets" / "mvp_reference_outputs.json"


def main(argv: Sequence[str] | None = None) -> int:
    """解析路径、运行 evaluator，并以 0/1/2 区分通过、门禁失败和输入无效。"""

    parser = argparse.ArgumentParser(description="Run deterministic Stardew NPC dialogue evals.")
    parser.add_argument("--dataset", type=Path, default=DEFAULT_DATASET_PATH)
    parser.add_argument("--outputs", type=Path, default=DEFAULT_OUTPUTS_PATH)
    parser.add_argument(
        "--report-json",
        type=Path,
        default=None,
        help="Optional path for the same sanitized JSON printed to stdout.",
    )
    arguments = parser.parse_args(list(argv) if argv is not None else None)

    try:
        report = evaluate_paths(arguments.dataset, arguments.outputs)
    except (OSError, ValueError, ValidationError, json.JSONDecodeError):
        # 不回显路径、自由异常或数据内容；调用者只需要稳定分类与非零退出码。
        print('{"error_code":"EVAL_INPUT_INVALID","passed":false}')
        return 2

    serialized = report_to_json(report)
    print(serialized)
    if arguments.report_json is not None:
        arguments.report_json.write_text(serialized + "\n", encoding="utf-8")
    return 0 if report.passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
