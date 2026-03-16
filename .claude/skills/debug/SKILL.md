---
name: debug
description: Interactive debugging with systematic root-cause analysis. Use when encountering any bug, test failure, or unexpected behavior — before proposing fixes.
---

# Debug

Systematic debugging for CLI, TUI, Extension, and MCP development. Combines interactive feedback loops with disciplined root-cause analysis.

## The Iron Law

**NO FIXES WITHOUT ROOT CAUSE INVESTIGATION FIRST.**

If you have not completed Phase 1 (Root Cause Investigation), you cannot propose fixes. Period. Skipping this is the single most common source of thrashing, wasted time, and user frustration.

## Four Phases (Required Sequence)

### Phase 1: Root Cause Investigation

Before touching any code:

1. **Read error messages carefully** — the full message, not just the first line
2. **Reproduce consistently** — if you can't reproduce it, you can't fix it
3. **Check recent changes** — `git diff` and `git log` for what changed since it last worked
4. **Gather evidence at component boundaries** — logs, network calls, RPC responses, console output
5. **Trace data flow backward** — start from the symptom, trace back through the call chain to find the original trigger

Do NOT skip this phase. Do NOT propose "quick fixes" during this phase.

### Phase 2: Pattern Analysis

1. **Find working examples** in the codebase — how do similar features handle this?
2. **Compare against references** — what does the working version do differently?
3. **Identify differences** — the bug is in the delta between working and broken

### Phase 3: Hypothesis Testing

1. **Form a single hypothesis** — "The bug is caused by X because of evidence Y"
2. **Test with the smallest possible change** — one variable at a time
3. **If it doesn't work, form a NEW hypothesis** — do NOT stack fixes on top of failed attempts
4. **Revert failed attempts** before trying the next hypothesis

### Phase 4: Implementation

1. **Create a failing test case** that reproduces the bug
2. **Implement a single fix** at the root cause (not at the symptom)
3. **Verify the fix** — the test passes, the original error is gone
4. **If 3+ fixes have failed, STOP** — see the escalation rule below

## 3-Fix Escalation Rule

If three or more fix attempts have failed, **STOP**. This is not a failed hypothesis — this is a wrong architecture. The pattern or approach is fundamentally unsound.

Before attempting more fixes:
1. Question whether the architecture/pattern is correct
2. Discuss with the user — present what you've tried and why each failed
3. Consider whether a different approach is needed entirely

## Red Flags

| Thought | Reality |
|---------|---------|
| "Quick fix for now" | Return to Phase 1 |
| "Just try changing X" | Return to Phase 1 |
| "I don't fully understand but this might work" | Return to Phase 1 |
| "One more fix attempt" (after 2+ failures) | Question architecture |

## User Frustration Signals

When the user says these things, they are telling you your approach is wrong:

- **"Stop guessing"** — You are proposing fixes without understanding the root cause
- **"Is that not happening?"** — You assumed something without verifying it
- **"We're stuck?"** — Your approach is not working and you need to change strategy

When you see these signals: **STOP. Return to Phase 1.** Re-read the error messages. Re-trace the data flow. Start fresh.

## Surface Detection

Based on recent changes or explicit argument (`/debug cli`, `/debug tui`, `/debug extension`, `/debug mcp`):

- `src/PPDS.Cli/Commands/` → CLI mode
- `src/PPDS.Cli/Tui/` → TUI mode
- `src/PPDS.Extension/` → Extension mode
- `src/PPDS.Mcp/` → MCP mode
- No clear match → Ask user

## CLI Mode

### Build

```bash
dotnet build src/PPDS.Cli/PPDS.Cli.csproj -f net10.0
```

### Run and verify

```bash
.\src\PPDS.Cli\bin\Debug\net10.0\ppds.exe <command> <args>
```

**Verify:** command executes without error, output format is correct, exit code is 0.

## TUI Mode

### Build and test

```bash
# Run TUI unit tests
dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --no-build

# Run TUI visual snapshots (if Node.js available)
npm test --prefix tests/PPDS.Tui.E2eTests
```

If visual issues, update snapshots:
```bash
npm test --prefix tests/PPDS.Tui.E2eTests -- --update-snapshots
```

## Extension Mode

### Build and install for manual testing

```bash
cd <root>/src/PPDS.Extension && npm run lint && npm run compile && npm run test && npm run local
```

Then reload VS Code (Ctrl+Shift+P -> Reload Window) and test manually.

### Useful npm scripts

Run from repo root with `ext:` prefix, or from `src/PPDS.Extension/` directly:

| Request | Command |
|---------|---------|
| Build + install | `npm run ext:local` |
| Just install existing build | `npm run ext:local:install` |
| Revert to marketplace version | `npm run ext:local:revert` |
| Uninstall local | `npm run ext:local:uninstall` |
| Run unit tests only | `npm run ext:test` |
| Run E2E tests | `npm run ext:test:e2e` |
| Watch mode (hot reload) | `npm run ext:watch` |
| Full release test | `npm run ext:release:test` |

### F5 Launch Configurations

From VS Code debug panel:

| Configuration | When to use |
|---------------|-------------|
| Run Extension | Default — full build, then launch debug host |
| Run Extension (Watch Mode) | Iterating — hot reloads on file changes |
| Run Extension (No Build) | Quick — skip build, use existing compiled code |
| Run Extension (Open Folder) | Testing with a specific project folder |
| Run Extension Tests | Run VS Code extension integration tests |

**For AI self-verification:** Use `/verify extension` to exercise commands, inspect state, and verify webview rendering. See @ext-verify skill for the CDP tool reference.

## MCP Mode

Test MCP tools via MCP Inspector:

```bash
npx @modelcontextprotocol/inspector --cli --server "ppds-mcp-server"
```

Or use `/verify mcp` for structured verification.

## Iterative Fix Loop

After completing root-cause investigation (Phases 1-2), apply fixes iteratively:

1. Parse error/failure — read the full message
2. Locate source code at the root cause (not the symptom)
3. Apply minimal fix — one change at a time
4. Re-run build + tests to verify
5. If fix doesn't work, **revert** and form a new hypothesis (Phase 3)
6. If 3+ fixes fail, **STOP** and escalate (3-fix rule)

## Root-Cause Tracing

When investigating a bug, trace backward through the call chain:

1. Start at the **symptom** — the error message, wrong output, or crash
2. Find the **immediate caller** — what function produced this result?
3. Check the **input to that function** — is the input correct?
4. If the input is wrong, trace back to **where the input was produced**
5. Repeat until you find the **original trigger** — the first place where data or control flow diverges from expected behavior

The root cause is often 3-5 steps removed from the symptom. Fixing the symptom without finding the root cause guarantees the bug will return in a different form.

## Defense-in-Depth

After finding the root cause, add validation at every layer — not just at the fix point:

- **Entry point:** Validate inputs at the API/CLI/RPC boundary
- **Business logic:** Assert preconditions in the service layer
- **Environment guards:** Check for missing config, disconnected services, stale caches
- **Debug instrumentation:** Add logging at component boundaries so the NEXT bug in this area is easier to find

One fix at the root cause plus validation at every layer is more robust than a single fix at the symptom.

## Condition-Based Waiting

When tests fail due to timing (async operations, UI rendering, daemon startup):

- **Never use arbitrary `sleep`/`delay`** — they are flaky by definition
- **Poll for the expected condition** with a timeout:
  ```
  Wait until: condition is true
  Check every: 100-500ms
  Timeout after: reasonable upper bound (5s for UI, 30s for daemon)
  On timeout: fail with descriptive message including what was expected
  ```
- **In test code,** use the framework's built-in waiters (`waitFor`, `retry`, `Eventually`) rather than raw sleep

Condition-based waiting makes tests both faster (they proceed as soon as the condition is met) and more reliable (they don't fail on slow machines).
