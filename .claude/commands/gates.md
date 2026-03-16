# Gates

Mechanical pass/fail checks. No judgment, no opinions — compiler, linter, and tests either pass or they don't. Run this before code reviews, after fix batches, and before PRs.

## Input

$ARGUMENTS = optional scope hint (e.g., `extension` for TypeScript-only, `dotnet` for C#-only). Default: run all applicable gates.

## Process

### Step 1: Detect What Changed

```bash
git diff --name-only main...HEAD
```

Categorize changed files:
- `.cs` files → run .NET gates
- `.ts`/`.js` files → run TypeScript gates
- Both → run all gates

If $ARGUMENTS specifies a scope, use that instead of auto-detection.

### Step 2: Run Gates

**Gate 1: .NET Build** (if C# files changed)

```bash
dotnet build PPDS.sln -v q
```

Pass: 0 errors, 0 warnings treated as errors
Fail: any error — report exact error messages with file:line

**Gate 2: .NET Tests** (if C# files changed)

```bash
dotnet test PPDS.sln --filter "Category!=Integration" -v q --no-build
```

Pass: 0 failures
Fail: report failing test names and assertion messages

**Gate 3: TypeScript Build** (if TS/JS files changed)

```bash
npm run compile --prefix src/PPDS.Extension
```

Pass: 0 errors
Fail: report exact error messages with file:line

**Gate 3.5: TypeScript Type Check** (if TS/JS files changed)

```bash
npm run typecheck:all --prefix src/PPDS.Extension
```

Pass: 0 errors across both host and webview tsconfigs
Fail: report exact error messages with file:line

Note: `compile` (Gate 3) only runs esbuild which does NOT type-check. This gate runs `tsc --noEmit` against both `tsconfig.json` (host) and `tsconfig.webview.json` (browser) to catch type errors.

**Gate 4: TypeScript Lint** (if TS/JS files changed)

```bash
npm run lint --prefix src/PPDS.Extension
```

Pass: 0 errors
Fail: report lint violations

**Gate 4.5: CSS Lint** (if CSS files changed)

```bash
npm run lint:css --prefix src/PPDS.Extension
```

Pass: 0 errors
Fail: report CSS lint violations with file:line

**Gate 4.6: Dead Code Analysis** (if TS/JS files changed)

```bash
npm run dead-code --prefix src/PPDS.Extension
```

Pass: 0 unused exports
Fail: report unused exports/files

**Gate 5: TypeScript Tests** (if TS/JS files changed)

```bash
npm test --prefix src/PPDS.Extension
```

Pass: 0 failures
Fail: report failing test names and messages

**Gate 6: AC Verification** (if specs with ACs are relevant)

Read `specs/README.md` to map changed files to specs. For each relevant spec with ACs:
- Extract test method names from the AC table
- For .NET tests: `dotnet test --filter "FullyQualifiedName~{method}" -v q --no-build`
- For TypeScript tests: `npx vitest run -t "{method}" --prefix src/PPDS.Extension`
- Report which ACs pass and which fail

### Step 3: Report

```
## Quality Gate Results

| Gate | Status | Details |
|------|--------|---------|
| .NET Build | PASS | 0 errors |
| .NET Tests | FAIL | 2 failures (see below) |
| TS Build | PASS | 0 errors |
| TS Lint | SKIP | No TS files changed |
| TS Tests | SKIP | No TS files changed |
| AC Verify | PARTIAL | 3/5 ACs pass |

### Failures
- `ConnectionPoolTests.GetClient_Throttled_SkipsSource`: Expected source-b, got source-a
- AC-03 (connection-pooling): Test method not found

### Verdict: FAIL — 2 issues must be resolved before proceeding
```

## Post-Gate Reminder

Gates are necessary but NOT sufficient. After gates pass:

- **If changed files include webview TS/CSS/HTML** (`src/PPDS.Extension/src/panels/`): You MUST also run `/verify extension` and/or `/qa extension` for visual verification. Gates prove code compiles and tests pass — they do NOT prove it renders correctly or works as the user would experience.
- **If changed files include CLI commands** (`src/PPDS.Cli/Commands/`, not `Serve/`): Run the command and verify the output.
- **If changed files include MCP tools** (`src/PPDS.Mcp/`): Call the tool and verify the response.
- **If changed files include TUI** (`src/PPDS.Cli/Tui/`): Run `npm run tui:test` for snapshot verification.

**Do not commit UI/CLI/MCP changes with only gates passing.** That is how bugs ship undetected.

## Rules

1. **Mechanical only** — no subjective judgments, no code review, no style opinions
2. **All gates run** — don't skip gates because "they probably pass"
3. **Exact output** — report exact error messages, not summaries
4. **Binary verdict** — PASS (all green) or FAIL (any red). No "PASS with warnings."
5. **No fixes** — report problems, don't fix them. That's the implementer's job.
