# Verify - REFERENCE

Rationale and worked examples for `.claude/skills/verify/SKILL.md`. The
procedure stays in SKILL.md; everything that doesn't change between
sessions lives here.

---

## TUI Mode - worked example

The TUI verifier wraps `@microsoft/tui-test` + `node-pty`. Full command
reference: `@tui-verify` skill.

```bash
# Phase A: Build and launch
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs launch --build
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "PPDS" 15000
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 0

# Phase B (MANDATORY when src/PPDS.Cli/Tui/ changes): interactive nav
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs key "tab"
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 2
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs text 28
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs wait "SQL Query" 5000
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs screenshot $TEMP/tui-verify.json

# Phase C: Cleanup
node tests/PPDS.Tui.E2eTests/tools/tui-verify.mjs close
```

---

## Extension Mode - worked example

Wraps `@playwright/test` + `@vscode/test-electron`. Full reference:
`@ext-verify` skill.

```bash
# Phase A: Launch and screenshot
node src/PPDS.Extension/tools/webview-cdp.mjs launch --build
node src/PPDS.Extension/tools/webview-cdp.mjs command "PPDS: Data Explorer"
node src/PPDS.Extension/tools/webview-cdp.mjs wait --ext "power-platform-developer-suite"
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot $TEMP/data-explorer.png
node src/PPDS.Extension/tools/webview-cdp.mjs logs --channel "PPDS"

# Phase B (MANDATORY when query/data/panel code changes): exercise a query
node src/PPDS.Extension/tools/webview-cdp.mjs eval 'monaco.editor.getEditors()[0].setValue("SELECT TOP 5 name FROM account")'
node src/PPDS.Extension/tools/webview-cdp.mjs click "#execute-btn" --ext "power-platform-developer-suite"
node src/PPDS.Extension/tools/webview-cdp.mjs screenshot $TEMP/after-query.png

# Phase C: Cleanup
node src/PPDS.Extension/tools/webview-cdp.mjs close
```

Phase B trigger files: `SqlQueryService`, `RpcMethodHandler`, `QueryPanel`,
`query-panel.ts`, anything under data rendering or panel interactions.

---

## Empirical shakedown gate - rationale

PR #1051 Phase B shipped clean from `/verify` then took seven fix commits
because `tests/conftest.py` auto-stubs `claude_dispatch.spawn`. Unit tests
cannot regress what they cannot exercise. The shakedown gate spawns one
real `claude --bg` session against a throwaway prompt and asserts exit 0,
deliberately bypassing the stub.

**Allowlist source of truth:** `scripts/_shakedown_allowlist.py`. Both the
gate (`scripts/verify_shakedown.py`) and the post-`/verify` drift detector
(`scripts/retro_helpers.py:detect_allowlist_drift`) read it. Adding a new
subprocess-spawning wrapper is a one-line append.

**Cost bound:** the helper defaults to a 5-minute timeout. The throwaway
prompt is `"Reply with the word OK and stop."` so a healthy session
finishes in seconds. The gate only runs when the diff touches the allowlist
- typical PRs skip it entirely with one log line.

**Pool discipline:** uses `claude_dispatch.spawn(mode="interactive", ...)`
- subscription pool, never `-p`. The dispatcher emits the SDK-spend
warning when callers pick `-p`, so accidentally regressing this surfaces
in stderr.

---

## Report template

```
## Verification Results -- [component]

| Check | Status | Details |
|-------|--------|---------|
| Unit tests | PASS | 12/12 passing |
| Daemon connection | PASS | PID 12345, uptime 30s |
| Tree view state | PASS | 2 profiles, 3 environments |
| Data Explorer open | PASS | Panel created |
| SQL query execution | PASS | 5 rows returned |
| Webview rendering | PASS | Query panel layout correct |

### Verdict: PASS -- all checks green
```

---

## Retro store schema (Check 7)

If `.retros/summary.json` exists:
- Parse as JSON
- Required keys: `schema_version`, `last_updated`, `total_retros`,
  `findings_by_category`, `metrics`
- `schema_version == 1`

Missing file passes (the store is optional until first retro).
