# Logging Transparency Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make daemon-to-extension logging transparent — preserve log levels, add query timing diagnostics, and surface query execution mode in results.

**Architecture:** Three independent changes: (1) Parse daemon stderr log levels in the extension and route to correct LogOutputChannel methods instead of flattening to WARN. (2) Add Debug-level Stopwatch timing for SQL parse and transpile stages in the RPC handler. (3) Append "via TDS" / "via Dataverse" to existing results summary lines in both Query Panel and notebook renderer.

**Tech Stack:** TypeScript (VS Code extension), C# (.NET), Vitest

---

## File Structure

| Action | File | Responsibility |
|--------|------|---------------|
| Modify | `src/PPDS.Extension/src/daemonClient.ts:154-156` | Parse `[LEVEL]` from stderr, route to correct log method |
| Create | `src/PPDS.Extension/src/__tests__/stderrLogParser.test.ts` | Unit tests for stderr log level parsing |
| Modify | `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs:937-1060` | Add Stopwatch timing for parse + transpile stages |
| Modify | `src/PPDS.Extension/src/panels/webview/query-panel.ts:827-831` | Append queryMode to status display |
| Modify | `src/PPDS.Extension/src/notebooks/notebookResultRenderer.ts:44` | Append queryMode to results summary |

---

## Chunk 1: Stderr Log Level Parsing

### Task 1: Parse daemon stderr log levels in extension

The daemon writes to stderr in this format:
```
[14:23:45.123] [INF] [PPDS.Cli.Serve.RpcMethodHandler] Query returned 42 records in 156ms
```

Currently `daemonClient.ts:154-156` dumps everything as `log.warn()`. We need to parse the `[LEVEL]` tag and route to the correct LogOutputChannel method.

VS Code's LogOutputChannel already handles filtering based on the user's "Developer: Set Log Level" setting — no custom filtering needed.

**Files:**
- Modify: `src/PPDS.Extension/src/daemonClient.ts:154-156`
- Create: `src/PPDS.Extension/src/__tests__/stderrLogParser.test.ts`

- [ ] **Step 1: Write failing tests for stderr log level parsing**

Create `src/PPDS.Extension/src/__tests__/stderrLogParser.test.ts`:

```typescript
import { describe, it, expect } from 'vitest';
import { parseDaemonLogLevel } from '../daemonClient.js';

describe('parseDaemonLogLevel', () => {
    it('parses INF as info', () => {
        expect(parseDaemonLogLevel('[14:23:45.123] [INF] [Category] message')).toBe('info');
    });

    it('parses DBG as debug', () => {
        expect(parseDaemonLogLevel('[14:23:45.123] [DBG] [Category] message')).toBe('debug');
    });

    it('parses TRC as trace', () => {
        expect(parseDaemonLogLevel('[14:23:45.123] [TRC] [Category] message')).toBe('trace');
    });

    it('parses WRN as warn', () => {
        expect(parseDaemonLogLevel('[14:23:45.123] [WRN] [Category] message')).toBe('warn');
    });

    it('parses ERR as error', () => {
        expect(parseDaemonLogLevel('[14:23:45.123] [ERR] [Category] error message')).toBe('error');
    });

    it('parses CRT as error', () => {
        expect(parseDaemonLogLevel('[14:23:45.123] [CRT] [Category] critical')).toBe('error');
    });

    it('defaults to warn for unrecognized format', () => {
        expect(parseDaemonLogLevel('some unstructured stderr output')).toBe('warn');
    });

    it('defaults to warn for empty string', () => {
        expect(parseDaemonLogLevel('')).toBe('warn');
    });
});
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `npm run test --prefix src/PPDS.Extension -- --run src/__tests__/stderrLogParser.test.ts`
Expected: FAIL — `parseDaemonLogLevel` is not exported from `daemonClient.ts`

- [ ] **Step 3: Implement parseDaemonLogLevel and update stderr handler**

In `src/PPDS.Extension/src/daemonClient.ts`, add this exported function (place it after the imports, before the class):

```typescript
/** Maps daemon stderr log-level tags to LogOutputChannel method names. */
const DAEMON_LOG_LEVELS: Record<string, 'trace' | 'debug' | 'info' | 'warn' | 'error'> = {
    TRC: 'trace',
    DBG: 'debug',
    INF: 'info',
    WRN: 'warn',
    ERR: 'error',
    CRT: 'error',
};

const DAEMON_LOG_LEVEL_RE = /\]\s+\[(TRC|DBG|INF|WRN|ERR|CRT)\]/;

/** Parses the log level from a daemon stderr line. Returns 'warn' for unrecognized formats. */
export function parseDaemonLogLevel(line: string): 'trace' | 'debug' | 'info' | 'warn' | 'error' {
    const match = DAEMON_LOG_LEVEL_RE.exec(line);
    return match ? DAEMON_LOG_LEVELS[match[1]] : 'warn';
}
```

Then replace the stderr handler at line 154-156:

```typescript
// Old:
this.process.stderr?.on('data', (data: Buffer) => {
    this.log.warn(`[daemon stderr] ${data.toString()}`);
});

// New:
this.process.stderr?.on('data', (data: Buffer) => {
    const text = data.toString().trimEnd();
    for (const line of text.split('\n')) {
        const trimmed = line.trimEnd();
        if (!trimmed) continue;
        const level = parseDaemonLogLevel(trimmed);
        this.log[level](`[daemon] ${trimmed}`);
    }
});
```

Note: split on `\n` handles multi-line buffer chunks. Prefix changes from `[daemon stderr]` to `[daemon]` since the level tag now carries the severity.

- [ ] **Step 4: Run tests to verify they pass**

Run: `npm run test --prefix src/PPDS.Extension -- --run src/__tests__/stderrLogParser.test.ts`
Expected: PASS — all 8 tests green

- [ ] **Step 5: Run full extension test suite**

Run: `npm run ext:test`
Expected: All tests pass (existing tests don't exercise the stderr handler directly)

- [ ] **Step 6: Commit**

```bash
git add src/PPDS.Extension/src/daemonClient.ts src/PPDS.Extension/src/__tests__/stderrLogParser.test.ts
git commit -m "feat(extension): parse daemon stderr log levels instead of flattening to WARN

Route [TRC]→trace, [DBG]→debug, [INF]→info, [WRN]→warn, [ERR/CRT]→error.
VS Code's LogOutputChannel filters based on the user's Developer: Set Log Level
setting — no custom ppds.logLevel config needed."
```

---

## Chunk 2: Query Timing Diagnostics in RPC Handler

### Task 2: Add Debug-level parse/transpile timing to QuerySqlAsync

Currently `RpcMethodHandler.QuerySqlAsync` (line 937) has zero timing logs for the parse and transpile stages. When a query takes longer than expected and `executionTimeMs` doesn't explain it, there's no way to tell if the time was spent in parsing, transpiling, or token acquisition.

Add `Stopwatch`-based `LogDebug` calls for:
1. DML safety parse (when DmlSafety is non-null)
2. SQL → FetchXML transpile (Dataverse path only)

These are Debug-level — invisible by default, available when a user sets log level to Debug.

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

- [ ] **Step 1: Add `System.Diagnostics` using directive**

At the top of `RpcMethodHandler.cs`, add after `using System.Text.Json.Serialization;` (line 1):

```csharp
using System.Diagnostics;
```

- [ ] **Step 2: Add timing around DML safety check**

In `QuerySqlAsync`, wrap the DML safety block (lines 954-997) with a Stopwatch:

```csharp
// Before the DML safety check (line 953):
var methodStopwatch = Stopwatch.StartNew();

// DML safety check: parse SQL and validate BEFORE any execution path (TDS or FetchXML)
if (request.DmlSafety != null)
{
    // ... existing DML safety code unchanged ...
}

_logger.LogDebug("query/sql: DML safety check completed in {ElapsedMs}ms", methodStopwatch.ElapsedMilliseconds);
```

Note: The `LogDebug` after the DML block runs unconditionally (even if DmlSafety is null, in which case it just logs 0ms). This is fine — it's Debug level and the overhead of a Stopwatch read is negligible.

- [ ] **Step 3: Add timing around transpile (Dataverse path)**

In the Dataverse code path, wrap the `TranspileSqlToFetchXml` call (current line ~1035) with timing:

```csharp
// Before transpile:
var transpileStopwatch = Stopwatch.StartNew();
var fetchXml = TranspileSqlToFetchXml(request.Sql, request.Top);
transpileStopwatch.Stop();
_logger.LogDebug("query/sql: transpiled SQL to FetchXML in {ElapsedMs}ms", transpileStopwatch.ElapsedMilliseconds);
```

- [ ] **Step 4: Run .NET tests**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`
Expected: All tests pass

- [ ] **Step 5: Commit**

```bash
git add src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs
git commit -m "feat(daemon): add Debug-level timing for DML check and SQL transpile

Adds Stopwatch-based LogDebug in QuerySqlAsync for DML safety parse and
SQL-to-FetchXML transpilation. Invisible at default Information level,
visible when user sets VS Code log level to Debug."
```

---

## Chunk 3: Display queryMode in Results

### Task 3: Show query execution mode in Query Panel status bar

The `updateStatus` function in `query-panel.ts:827-831` currently shows `"X rows (more available)"` and `"in Yms"`. Append the query mode after the timing.

**Files:**
- Modify: `src/PPDS.Extension/src/panels/webview/query-panel.ts:827-831`

- [ ] **Step 1: Update updateStatus to show queryMode**

In `query-panel.ts`, modify the `updateStatus` function:

```typescript
// Old (line 830):
executionTimeEl.textContent = data.executionTimeMs ? 'in ' + data.executionTimeMs + 'ms' : '';

// New:
const timeText = data.executionTimeMs ? 'in ' + data.executionTimeMs + 'ms' : '';
const modeText = data.queryMode === 'tds' ? ' via TDS' : data.queryMode === 'dataverse' ? ' via Dataverse' : '';
executionTimeEl.textContent = timeText + modeText;
```

- [ ] **Step 2: Run extension tests**

Run: `npm run ext:test`
Expected: All tests pass (no existing tests for updateStatus)

- [ ] **Step 3: Commit (combined with Task 4 below)**

Hold — commit together with Task 4.

### Task 4: Show query execution mode in notebook results summary

The `renderResultsHtml` function in `notebookResultRenderer.ts:44` shows `"X rows returned in Yms"`. Append the query mode.

**Files:**
- Modify: `src/PPDS.Extension/src/notebooks/notebookResultRenderer.ts:44`
- Create: `src/PPDS.Extension/src/notebooks/__tests__/notebookResultRenderer.test.ts`

- [ ] **Step 1: Write failing test for queryMode in renderResultsHtml**

Create `src/PPDS.Extension/src/notebooks/__tests__/notebookResultRenderer.test.ts`:

```typescript
import { describe, it, expect } from 'vitest';
import { renderResultsHtml } from '../notebookResultRenderer.js';
import type { QueryResultResponse } from '../../types.js';

function makeResult(overrides: Partial<QueryResultResponse> = {}): QueryResultResponse {
    return {
        success: true,
        entityName: 'account',
        columns: [{ logicalName: 'name', alias: null, displayName: 'Name', dataType: 'string', linkedEntityAlias: null }],
        records: [{ name: 'Test' }],
        count: 1,
        totalCount: null,
        moreRecords: false,
        pagingCookie: null,
        pageNumber: 1,
        isAggregate: false,
        executedFetchXml: null,
        executionTimeMs: 42,
        queryMode: null,
        ...overrides,
    };
}

describe('renderResultsHtml queryMode display', () => {
    it('shows "via TDS" when queryMode is tds', () => {
        const html = renderResultsHtml(makeResult({ queryMode: 'tds' }), undefined, 'test-id');
        expect(html).toContain('42ms via TDS');
    });

    it('shows "via Dataverse" when queryMode is dataverse', () => {
        const html = renderResultsHtml(makeResult({ queryMode: 'dataverse' }), undefined, 'test-id');
        expect(html).toContain('42ms via Dataverse');
    });

    it('shows no mode label when queryMode is null', () => {
        const html = renderResultsHtml(makeResult({ queryMode: null }), undefined, 'test-id');
        expect(html).toContain('42ms');
        expect(html).not.toContain('via TDS');
        expect(html).not.toContain('via Dataverse');
    });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npm run test --prefix src/PPDS.Extension -- --run src/notebooks/__tests__/notebookResultRenderer.test.ts`
Expected: FAIL — queryMode not used in summary yet

- [ ] **Step 3: Update summary line to show queryMode**

In `notebookResultRenderer.ts`, modify line 44:

```typescript
// Old:
const summary = `<div class="results-summary">${result.count} row${result.count !== 1 ? 's' : ''} returned in ${result.executionTimeMs}ms${result.moreRecords ? ' (more available)' : ''}</div>`;

// New:
const modeLabel = result.queryMode === 'tds' ? ' via TDS' : result.queryMode === 'dataverse' ? ' via Dataverse' : '';
const summary = `<div class="results-summary">${result.count} row${result.count !== 1 ? 's' : ''} returned in ${result.executionTimeMs}ms${modeLabel}${result.moreRecords ? ' (more available)' : ''}</div>`;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `npm run test --prefix src/PPDS.Extension -- --run src/notebooks/__tests__/notebookResultRenderer.test.ts`
Expected: PASS — all 3 tests green

- [ ] **Step 5: Run full extension test suite**

Run: `npm run ext:test`
Expected: All tests pass

- [ ] **Step 6: Commit both queryMode display changes**

```bash
git add src/PPDS.Extension/src/panels/webview/query-panel.ts src/PPDS.Extension/src/notebooks/notebookResultRenderer.ts src/PPDS.Extension/src/notebooks/__tests__/notebookResultRenderer.test.ts
git commit -m "feat(extension): display query execution mode in results summary

Shows 'via TDS' or 'via Dataverse' after execution timing in both the
Query Panel status bar and notebook cell output. Uses the queryMode field
on QueryResultResponse added in commit 6cf4a58."
```

---

## Verification

After all tasks are complete:

- [ ] **Final: Run full test suite**

```bash
dotnet test PPDS.sln --filter "Category!=Integration" -v q
npm run ext:test
```

Expected: All .NET and extension tests pass.
