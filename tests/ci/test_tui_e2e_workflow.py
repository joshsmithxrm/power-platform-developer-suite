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


def test_ci_runs_the_tool_tests_before_the_snapshot_suite():
    workflow = yaml.load(
        (ROOT / ".github/workflows/tui-e2e.yml").read_text(encoding="utf-8"),
        Loader=yaml.BaseLoader,
    )
    steps = workflow["jobs"]["tui-e2e"]["steps"]
    names = [step.get("name") for step in steps]
    tools = steps[names.index("Test the TUI test tools")]
    assert tools["run"] == "npm run test:tools"
    assert tools["working-directory"] == "tests/PPDS.Tui.E2eTests"
    assert "continue-on-error" not in tools
    assert "if" not in tools
    # The end-to-end launch check needs the built CLI and the installed runner.
    assert names.index("Build PPDS CLI") < names.index("Test the TUI test tools")
    assert names.index("Install tui-e2e dependencies") < names.index("Test the TUI test tools")
    assert names.index("Test the TUI test tools") < names.index("Run TUI E2E tests")


def test_tool_tests_cover_every_tool_test_file():
    package = json.loads(
        (ROOT / "tests/PPDS.Tui.E2eTests/package.json").read_text(encoding="utf-8")
    )
    command = package["scripts"]["test:tools"].split()
    assert command[:2] == ["node", "--test"]
    # A per-test timeout, so a test that hangs (for example an unbounded
    # retry) fails instead of running to the job's limit. The end-to-end
    # launch tests set their own, longer timeout.
    assert "--test-timeout=120000" in command
    files = [arg for arg in command[2:] if not arg.startswith("--")]
    tools = ROOT / "tests/PPDS.Tui.E2eTests/tools"
    on_disk = {f"tools/{path.name}" for path in tools.glob("*.test.mjs")}
    assert set(files) == on_disk


def test_tui_test_runs_only_the_screen_specs_and_never_retries_a_test():
    config = (ROOT / "tests/PPDS.Tui.E2eTests/tui-test.config.ts").read_text(encoding="utf-8")
    # The default pattern also imported tools/*.test.mjs (node:test files) into
    # the tui-test run, where their results were ignored.
    assert 'testMatch: "tests/**/*.spec.ts",' in config
    assert "retries: 0," in config


def test_snapshot_upload_names_the_snapshot_folder():
    workflow = yaml.load(
        (ROOT / ".github/workflows/tui-e2e.yml").read_text(encoding="utf-8"),
        Loader=yaml.BaseLoader,
    )
    steps = workflow["jobs"]["tui-e2e"]["steps"]
    upload = next(step for step in steps if step.get("name") == "Upload snapshots on failure")
    path = upload["with"]["path"].strip()
    assert path == "tests/PPDS.Tui.E2eTests/tests/__snapshots__/"
    assert (ROOT / path).is_dir()


def test_every_committed_snapshot_file_has_its_spec():
    # tui-test stores the snapshots of <dir>/<name>.spec.ts in
    # <dir>/__snapshots__/<name>.spec.ts.snap and never reports a file whose
    # spec is gone; the runner fails on one too (tools/run-isolated-tests.mjs).
    tests = ROOT / "tests/PPDS.Tui.E2eTests/tests"
    snapshots = sorted(tests.rglob("__snapshots__/*.snap"))
    assert snapshots
    orphans = [
        str(snap.relative_to(ROOT))
        for snap in snapshots
        if not (snap.parent.parent / snap.name[: -len(".snap")]).is_file()
    ]
    assert orphans == []
