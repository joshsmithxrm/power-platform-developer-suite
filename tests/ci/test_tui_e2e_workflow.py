"""The non-live TUI gate must compare owned, deterministic fixtures."""
import json
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]


def test_ci_compares_committed_snapshots():
    workflow = yaml.load(
        (ROOT / ".github/workflows/tui-e2e.yml").read_text(encoding="utf-8"),
        Loader=yaml.BaseLoader,
    )
    steps = workflow["jobs"]["tui-e2e"]["steps"]
    run = next(step for step in steps if step.get("name") == "Run TUI E2E tests")
    assert run["run"] == "npm test"
    assert "continue-on-error" not in run


def test_all_tui_commands_use_the_isolated_runner():
    package = json.loads(
        (ROOT / "tests/PPDS.Tui.E2eTests/package.json").read_text(encoding="utf-8")
    )
    launcher = "node tools/run-isolated-tests.mjs"
    assert package["scripts"]["test"] == launcher
    assert package["scripts"]["test:headed"] == launcher
    assert package["scripts"]["test:update"] == launcher + " --updateSnapshot"
