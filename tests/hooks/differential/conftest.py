"""Make a skipped differential oracle impossible to miss: every skip in this folder is printed, with
its reason, at the end of the pytest run (also under ``-q``)."""
from __future__ import annotations

_SKIPS = []


def pytest_runtest_logreport(report):
    if report.skipped and "hooks/differential" in report.nodeid.replace("\\\\", "/"):
        reason = report.longrepr[2] if isinstance(report.longrepr, tuple) else str(report.longrepr)
        _SKIPS.append((report.nodeid.split("::", 1)[-1], reason))


def pytest_terminal_summary(terminalreporter):
    if not _SKIPS:
        return
    tr = terminalreporter
    tr.write_sep("!", "DIFFERENTIAL HOOK ORACLES SKIPPED (these did not check the hook)", red=True, bold=True)
    for name, reason in _SKIPS:
        tr.write_line(f"SKIPPED {name}: {reason}", red=True)
    tr.write_line("Set PPDS_DIFF_REQUIRE=1 to make a missing tool a failure.", red=True)
