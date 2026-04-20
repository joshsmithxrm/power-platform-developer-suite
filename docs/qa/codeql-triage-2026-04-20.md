# CodeQL Triage — 2026-04-20 (v1.0.0 pre-release)

Workstream W1 of 3-agent parallel review. Triage of 217 open CodeQL alerts.

> **Status:** Draft / proposal-only. No alerts have been dismissed, no code changed, no issues filed. Awaiting user approval.

Snapshot source: `.claude/state/codeql-alerts-raw.ndjson` (217 open alerts, 2026-04-20).

---

## Summary (counts per verdict)

| Verdict | Count | Remediation path |
|---|---:|---|
| FP-SUPPRESS via config patch | 183 | Add `cs/call-to-unmanaged-code` + `cs/unmanaged-code` to `query-filters.exclude.id` in `.github/codeql/codeql-config.yml`. |
| TP-FIX (propose patch) | 14 | `cs/dispose-not-called-on-throw` warnings in TUI screens + `UpdateCheckService`. Wrap `Application.Run(dialog)` + `dialog.Dispose()` in `try/finally`. |
| FP-SUPPRESS (Terminal.Gui ownership transfer) | 5 | `cs/local-not-disposed` in `PluginTraceDetailDialog` (4) + `MetadataExplorerScreen` (1) — views/buttons are added to parent via `Add()` which transfers disposal. |
| FP-SUPPRESS (test code, config gap) | 8 | Style / null-deref / upcast / empty-catch / HttpResponseMessage under `tests/**`. Current `paths-ignore: tests/**` rule should cover these; investigate why they surface (see §11). |
| ACCEPT-RISK / FP (double-checked locking, readonly init pattern, cert lifetime handed to caller) | 7 | 3× `cs/missed-using-statement` in auth providers (intentional — caller owns `ServiceClient`); 1× `cs/constant-condition` (double-checked locking, CodeQL can't reason about semaphore); 2× `cs/missed-readonly-modifier` (fields assigned in init method, not ctor); 1× `cs/missed-using-statement` on a field-scoped `CancellationTokenSource`. |

**Grand total:** 217 alerts with verdicts (no open queue items).

---

## Recommended config patch — `.github/codeql/codeql-config.yml`

Append two rule IDs to the existing `query-filters.exclude.id` list:

```yaml
# .github/codeql/codeql-config.yml — addition to existing exclude list
query-filters:
  - exclude:
      id:
        # ... existing entries preserved ...
        # Vendored git-credential-manager P/Invoke wrappers for platform-native
        # secret stores (Windows Credential Manager / advapi32, macOS Keychain /
        # CoreFoundation / SecurityFramework, Linux libsecret / Glib / Gobject).
        # Every file under src/PPDS.Auth/Internal/CredentialStore/ carries a
        # vendoring header from git-ecosystem/git-credential-manager v2.7.3.
        # Both rules merely inventory unmanaged-code use — they do not detect
        # bugs — and are not actionable for an OS secret-store binding.
        - cs/call-to-unmanaged-code
        - cs/unmanaged-code
```

**Why rule-exclude, not path-ignore:** a `paths-ignore` entry would silence *all* rules on these vendored files. A global rule-exclude keeps every other CodeQL check running on them. This is safe today because no PPDS first-party code outside `src/PPDS.Auth/Internal/CredentialStore/**` does P/Invoke (verified by the alert distribution — all 183 findings sit in that tree).

Note: CodeQL's YAML schema for `query-filters` does not support scoping excludes to specific paths, so a global two-rule exclude is the cleanest expression of intent. If PPDS later adds first-party P/Invoke elsewhere, revisit — either move to `paths-ignore` on the vendored subtree, or accept the same-rule suppression everywhere.

### Additional: investigate `tests/**` exclusion gap

The current config contains `paths-ignore: tests/**`, yet 7 test-file alerts surfaced this cycle. Evidence from the NDJSON snapshot:
- `tests/PPDS.Cli.Tests/Services/ConnectionServiceTests.cs` (#979, `cs/local-not-disposed`)
- `tests/PPDS.Cli.Tests/Services/UpdateCheck/SelfUpdateTests.cs` (#926, #927, `cs/empty-catch-block`)
- `tests/PPDS.Cli.Tests/Services/DataProvider/DataProviderServiceTests.cs` (#973, #974, `cs/useless-upcast`)
- `tests/PPDS.Cli.Tests/Services/CustomApi/CustomApiServiceTests.cs` (#980, `cs/useless-upcast`)
- `tests/PPDS.Dataverse.Tests/Metadata/Authoring/MetadataAuthoringServiceTests.cs` (#1011, `cs/dereferenced-value-may-be-null`)
- `tests/PPDS.Dataverse.Tests/Services/SolutionServiceTests.cs` (#931, `cs/dereferenced-value-may-be-null`)

Hypotheses: (a) the workflow isn't wiring `config-file: .github/codeql/codeql-config.yml` into the `init` step, or (b) the existing alerts predate the `paths-ignore` entry and CodeQL does not retroactively close them. Action item: inspect `.github/workflows/*codeql*.yml` for the `config-file` parameter. If config is wired correctly and alerts are merely stale, bulk-dismiss them with reason `used-in-tests` (see "Bulk dismissals" below).

---

## Bulk dismissals (alternative — do not execute)

### Option A — dismiss the 183 P/Invoke note-alerts (if you prefer not to change config)

```bash
#!/usr/bin/env bash
# PROPOSAL ONLY — do not execute.
# Dismisses 183 note-severity alerts (cs/call-to-unmanaged-code + cs/unmanaged-code)
# in vendored GCM P/Invoke wrappers.
set -euo pipefail
REPO="joshsmithxrm/power-platform-developer-suite"
COMMENT="Vendored from git-credential-manager v2.7.3; P/Invoke is inherent to the cross-platform credential-store design. See docs/qa/codeql-triage-2026-04-20.md."

NUMS=$(python -c "
import json
with open('.claude/state/codeql-alerts-raw.ndjson') as f:
    for line in f:
        a=json.loads(line)
        if a['rule'] in ('cs/call-to-unmanaged-code','cs/unmanaged-code'):
            print(a['num'])
")

for n in $NUMS; do
  MSYS_NO_PATHCONV=1 gh api \
    --method PATCH \
    -H "Accept: application/vnd.github+json" \
    "/repos/${REPO}/code-scanning/alerts/${n}" \
    -f state=dismissed \
    -f dismissed_reason="won't fix" \
    -f dismissed_comment="${COMMENT}"
done
```

### Option B — dismiss test-file alerts (config-gap cleanup)

```bash
#!/usr/bin/env bash
# PROPOSAL ONLY — do not execute.
# Dismisses 7 test-file alerts with reason "used-in-tests" (existing paths-ignore
# rule covers these; they are almost certainly stale/pre-config-change).
set -euo pipefail
REPO="joshsmithxrm/power-platform-developer-suite"
COMMENT="Test file — covered by paths-ignore: tests/** in codeql-config.yml. Likely stale (alert predates config). See docs/qa/codeql-triage-2026-04-20.md."

for n in 926 927 973 974 979 980 1011 931; do
  MSYS_NO_PATHCONV=1 gh api \
    --method PATCH \
    -H "Accept: application/vnd.github+json" \
    "/repos/${REPO}/code-scanning/alerts/${n}" \
    -f state=dismissed \
    -f dismissed_reason="used in tests" \
    -f dismissed_comment="${COMMENT}"
done
```

### Option C — dismiss ACCEPT-RISK alerts (intentional patterns)

```bash
#!/usr/bin/env bash
# PROPOSAL ONLY — do not execute.
# Dismisses 7 alerts on intentional patterns (caller-owned ServiceClient,
# double-checked locking, readonly-via-init, field-scoped CTS).
set -euo pipefail
REPO="joshsmithxrm/power-platform-developer-suite"

# 1021, 1022, 1023 — caller owns ServiceClient
for n in 1021 1022 1023; do
  MSYS_NO_PATHCONV=1 gh api --method PATCH -H "Accept: application/vnd.github+json" \
    "/repos/${REPO}/code-scanning/alerts/${n}" \
    -f state=dismissed -f dismissed_reason="won't fix" \
    -f dismissed_comment="ServiceClient is returned to caller — caller (connection pool) owns lifecycle, using-statement would dispose prematurely. See docs/qa/codeql-triage-2026-04-20.md."
done

# 933 — double-checked locking
MSYS_NO_PATHCONV=1 gh api --method PATCH -H "Accept: application/vnd.github+json" \
  "/repos/joshsmithxrm/power-platform-developer-suite/code-scanning/alerts/933" \
  -f state=dismissed -f dismissed_reason="false positive" \
  -f dismissed_comment="Double-checked locking: inner check fires after SemaphoreSlim.WaitAsync so another thread may have set _lockedProfile. CodeQL doesn't model semaphore-as-happens-before. See docs/qa/codeql-triage-2026-04-20.md."

# 1003, 1004 — readonly fields assigned in init, not ctor
for n in 1003 1004; do
  MSYS_NO_PATHCONV=1 gh api --method PATCH -H "Accept: application/vnd.github+json" \
    "/repos/${REPO}/code-scanning/alerts/${n}" \
    -f state=dismissed -f dismissed_reason="false positive" \
    -f dismissed_comment="Field assigned in BuildUi() init method (called from ctor), not directly in ctor — readonly C# semantics require direct ctor assignment. See docs/qa/codeql-triage-2026-04-20.md."
done

# 962 — field-scoped CancellationTokenSource reused across loads
MSYS_NO_PATHCONV=1 gh api --method PATCH -H "Accept: application/vnd.github+json" \
  "/repos/joshsmithxrm/power-platform-developer-suite/code-scanning/alerts/962" \
  -f state=dismissed -f dismissed_reason="false positive" \
  -f dismissed_comment="Field _loadCts has screen lifetime (reused across LoadDataAsync calls to cancel in-flight loads). Using-statement would force single-scope ownership and defeat the cancellation-across-operations design. See docs/qa/codeql-triage-2026-04-20.md."
```

---

## Per-cluster breakdown

### 1. `cs/call-to-unmanaged-code` (107 findings, note) — FP-SUPPRESS via config patch

**Files (all vendored from GCM v2.7.3, commit 5fa7116):**
- `src/PPDS.Auth/Internal/CredentialStore/MacOS/MacOSKeychain.cs` (55)
- `src/PPDS.Auth/Internal/CredentialStore/Linux/SecretServiceCollection.cs` (32)
- `src/PPDS.Auth/Internal/CredentialStore/MacOS/Native/CoreFoundation.cs` (12)
- `src/PPDS.Auth/Internal/CredentialStore/Windows/WindowsCredentialManager.cs` (6)
- `src/PPDS.Auth/Internal/CredentialStore/MacOS/Native/SecurityFramework.cs` (1)
- `src/PPDS.Auth/Internal/CredentialStore/MacOS/Native/LibSystem.cs` (1)

**Evidence:** Each file opens with a header of the form:
```
// Vendored from https://github.com/git-ecosystem/git-credential-manager
// (tag v2.7.3, commit 5fa7116896c82164996a609accd1c5ad90fe730a).
// ... Modifications: namespace renamed ...; visibility lowered to `internal`;
// no behavioral change to OS storage semantics.
```
(See e.g. `src/PPDS.Auth/Internal/CredentialStore/MacOS/MacOSKeychain.cs:1-8`.)

**Verdict:** FP-SUPPRESS — the rule flags every call-site of a `[DllImport]` method. That is inherent to a native-store binding and not a bug. Apply the config patch above.

### 2. `cs/unmanaged-code` (76 findings, note) — FP-SUPPRESS via config patch

Fires on the `[DllImport]` declarations themselves.

- `src/PPDS.Auth/Internal/CredentialStore/MacOS/Native/CoreFoundation.cs` (26)
- `src/PPDS.Auth/Internal/CredentialStore/Linux/Native/Libsecret.cs` (16)
- `src/PPDS.Auth/Internal/CredentialStore/MacOS/Native/SecurityFramework.cs` (13)
- `src/PPDS.Auth/Internal/CredentialStore/Linux/Native/Glib.cs` (11)
- `src/PPDS.Auth/Internal/CredentialStore/Windows/Native/Advapi32.cs` (6)
- `src/PPDS.Auth/Internal/CredentialStore/MacOS/Native/LibSystem.cs` (2)
- `src/PPDS.Auth/Internal/CredentialStore/Linux/Native/Gobject.cs` (2)

**Verdict:** FP-SUPPRESS — identical rationale. Same config patch covers both rules.

### 3. `cs/dispose-not-called-on-throw` (14 findings, warning) — TP-FIX (all 14)

All 14 alerts share the same pattern — a Terminal.Gui dialog is created locally, populated, run, and then explicitly disposed. If `Application.Run(dialog)` or any `dialog.Add(...)` call between construction and `Dispose()` throws, the dialog is leaked (along with any `Dim.Percent(n)` result objects created inline as width/height values).

**Pattern (example from `ConnectionReferencesScreen.cs:511-527`):**
```csharp
private void OpenInMaker()
{
    ...
    var dialog = new Dialog("Open in Maker", new Button("OK", is_default: true))
    { Width = 60, Height = 7 };
    dialog.Add(new Label { ... });   // could throw
    dialog.Add(new Label { ... });   // could throw
    Application.Run(dialog);          // could throw
    dialog.Dispose();                 // missed on any throw above
}
```

**All 14 findings (all verdict: TP-FIX):**

| # | File | Line |
|---|---|---|
| 928 | `src/PPDS.Cli/Services/UpdateCheck/UpdateCheckService.cs` | 484 |
| 951 | `src/PPDS.Cli/Tui/Screens/ConnectionReferencesScreen.cs` | 526 |
| 952 | `src/PPDS.Cli/Tui/Screens/EnvironmentVariablesScreen.cs` | 602 |
| 953 | `src/PPDS.Cli/Tui/Screens/ConnectionReferencesScreen.cs` | 301 |
| 954 | `src/PPDS.Cli/Tui/Screens/ConnectionReferencesScreen.cs` | 417 |
| 955 | `src/PPDS.Cli/Tui/Screens/EnvironmentVariablesScreen.cs` | 360 |
| 956 | `src/PPDS.Cli/Tui/Screens/ConnectionReferencesScreen.cs` | 487 |
| 957 | `src/PPDS.Cli/Tui/Screens/EnvironmentVariablesScreen.cs` | 479 |
| 958 | `src/PPDS.Cli/Tui/Screens/EnvironmentVariablesScreen.cs` | 560 |
| 959 | `src/PPDS.Cli/Tui/Screens/WebResourcesScreen.cs` | 428 |
| 960 | `src/PPDS.Cli/Tui/Screens/WebResourcesScreen.cs` | 222 |
| 964 | `src/PPDS.Cli/Tui/Screens/PluginTracesScreen.cs` | 218 |
| 965 | `src/PPDS.Cli/Tui/Screens/PluginTracesScreen.cs` | 225 |
| 972 | `src/PPDS.Cli/Tui/Screens/WebResourcesScreen.cs` | 375 |

**Proposed patch sketch (applied uniformly at each site):** wrap in `try/finally`:
```csharp
var dialog = new Dialog(...);
try
{
    dialog.Add(...);
    Application.Run(dialog);
}
finally
{
    dialog.Dispose();
}
```
Where the dialog is assigned to a field (e.g. `_detailDialog`), set the field to null inside the finally *after* the Dispose, matching the current happy-path behaviour.

**#928 special case (UpdateCheckService):** pattern differs — `Process.Start()` returning a `Process` that is disposed after `File.WriteAllText(_lockPath, process.Id.ToString())` on the next line. If `WriteAllText` or `ToString` throws, the `Process` handle leaks. Same `try/finally` fix, but scoped to the `Process` variable on lines 480-484. See also `process.Dispose()` comment "R1: don't hold the process handle".

### 4. `cs/local-not-disposed` (6 findings, warning) — mixed

| # | File | Line | Verdict | Rationale |
|---|---|---|---|---|
| 941 | `src/PPDS.Cli/Tui/Dialogs/PluginTraceDetailDialog.cs` | 32 | FP-SUPPRESS | `View` is hung into `tabView` via `tabView.AddTab(...)` at line 40; tabView is hung into the dialog via `Add(tabView, closeButton)` at line 87. Terminal.Gui `View.Add()` transfers disposal to the parent. Parent disposal chain reaches this view. |
| 942 | `src/PPDS.Cli/Tui/Dialogs/PluginTraceDetailDialog.cs` | 43 | FP-SUPPRESS | Same pattern (TextView added via `tabView.AddTab` at line 53). |
| 943 | `src/PPDS.Cli/Tui/Dialogs/PluginTraceDetailDialog.cs` | 56 | FP-SUPPRESS | Same pattern (TextView added via `tabView.AddTab` at line 66). |
| 944 | `src/PPDS.Cli/Tui/Dialogs/PluginTraceDetailDialog.cs` | 69 | FP-SUPPRESS | Same pattern (View added via `tabView.AddTab` at line 77). |
| 963 | `src/PPDS.Cli/Tui/Screens/MetadataExplorerScreen.cs` | 104 | FP-SUPPRESS | Verified: each `_tabButtons[i]` is added to `_detailsFrame` at line 138 (`foreach (var btn in _tabButtons) _detailsFrame.Add(btn);`). Terminal.Gui `View.Add()` transfers disposal responsibility to parent. `OnDispose` at line 1256 correctly unsubscribes click handlers without re-disposing children. Same ownership-transfer pattern as `PluginTraceDetailDialog`. |
| 979 | `tests/PPDS.Cli.Tests/Services/ConnectionServiceTests.cs` | 39 | FP-SUPPRESS (test) | `HttpResponseMessage` is returned via Moq `.ReturnsAsync(...)` — ownership transfers to the mock infrastructure and ultimately to the SUT. Also this is under `tests/**` — see §11 config gap. |

### 5. `cs/missed-using-statement` (4 findings, warning) — ACCEPT-RISK (4 of 4)

| # | File | Line | Verdict | Rationale |
|---|---|---|---|---|
| 962 | `src/PPDS.Cli/Tui/Screens/PluginTracesScreen.cs` | 20 | ACCEPT-RISK | `_loadCts` is a field-scoped `CancellationTokenSource` whose lifetime spans multiple calls to `LoadDataAsync` (line 99-101: cancel previous, dispose, create new). A using-statement would force single-scope ownership and defeat the cross-operation cancellation design. Current disposal in `OnDispose` (line 565-566) is correct. |
| 1021 | `src/PPDS.Auth/Credentials/CertificateFileCredentialProvider.cs` | 151 | ACCEPT-RISK | `ServiceClient client` is returned to caller (`return Task.FromResult(client)` at line 181). The connection pool owns the lifecycle. A using-statement would dispose before return. Defensive dispose-on-failure at lines 167-174 is correct. |
| 1022 | `src/PPDS.Auth/Credentials/CertificateStoreCredentialProvider.cs` | 150 | ACCEPT-RISK | Same caller-owned-ServiceClient pattern as #1021 (return at line 180). |
| 1023 | `src/PPDS.Auth/Credentials/ManagedIdentityCredentialProvider.cs` | 99 | ACCEPT-RISK | Same caller-owned-ServiceClient pattern (return at line 137). Note this file even carries a comment explicitly referencing prior CodeQL alert 1020 for the sibling pattern — team is aware. |

### 6. `cs/useless-upcast` (3 findings) — FP-SUPPRESS (test-code config gap)

| # | File | Line | Verdict |
|---|---|---|---|
| 973 | `tests/PPDS.Cli.Tests/Services/DataProvider/DataProviderServiceTests.cs` | 133 | FP-SUPPRESS (tests/**) — also style-only; `(DateTime?)null` explicit cast arguably aids readability. |
| 974 | same file | 134 | same |
| 980 | `tests/PPDS.Cli.Tests/Services/CustomApi/CustomApiServiceTests.cs` | 932 | FP-SUPPRESS (tests/**) — `(PluginTypeInfo?)null` in a Moq `ReturnsAsync` call where the nullable annotation is explicit for Moq overload resolution. |

### 7. `cs/missed-readonly-modifier` (2 findings) — ACCEPT-RISK / FP

| # | File | Line | Verdict |
|---|---|---|---|
| 1003 | `src/PPDS.Cli/Tui/Screens/ExecutionPlanPreviewDialog.cs` | 19 (`_okButton`) | FP — field initialised to `null!` at declaration, then assigned in `BuildUi()` (line 132) called from ctor. C# `readonly` requires direct ctor assignment; init-method assignment is not permitted. |
| 1004 | same file | 20 (`_cancelButton`) | FP — same pattern (assigned at line 139). |

### 8. `cs/empty-catch-block` (2 findings) — FP-SUPPRESS (test-code + intentional)

| # | File | Line | Verdict |
|---|---|---|---|
| 926 | `tests/PPDS.Cli.Tests/Services/UpdateCheck/SelfUpdateTests.cs` | 64 | FP-SUPPRESS — `try { Directory.Delete(tempDir, true); } catch { }` in a test `finally` block. Swallowing the cleanup exception is the correct behaviour: the test has already passed or failed on its assertions; failed cleanup should not mask the real result. |
| 927 | same file | 89 | FP-SUPPRESS — identical pattern. |

### 9. `cs/dereferenced-value-may-be-null` (2 findings) — FP-SUPPRESS (test-code)

| # | File | Line | Verdict |
|---|---|---|---|
| 1011 | `tests/PPDS.Dataverse.Tests/Metadata/Authoring/MetadataAuthoringServiceTests.cs` | 320 | FP-SUPPRESS (tests/**) — preceded by `updatedRel.Should().NotBeNull(...)` at line 320; the `updatedRel!.CascadeConfiguration.Should().NotBeNull()` at line 321 only runs if the prior assertion passes. FluentAssertions narrowing is not modelled by CodeQL. |
| 931 | `tests/PPDS.Dataverse.Tests/Services/SolutionServiceTests.cs` | 182 | FP-SUPPRESS (tests/**) — identical FluentAssertions pattern (`dict.Should().NotBeNull()` at 182, `dict!.Should()...` at 183). |

### 10. `cs/constant-condition` (1 finding) — ACCEPT-RISK / FP

| # | File | Line | Verdict |
|---|---|---|---|
| 933 | `src/PPDS.Mcp/Infrastructure/McpToolContext.cs` | 92 | FP — classic double-checked locking. Outer check at line 86 (`if (_lockedProfile != null) return _lockedProfile;`), then `await _lockGate.WaitAsync(...)`, then the inner check at line 92 (`if (_lockedProfile != null) return _lockedProfile;`). CodeQL flags the inner check as "always false because of the outer check" — but another thread can take the semaphore first and set `_lockedProfile` between the outer check and the `WaitAsync` return. This is the canonical DCL idiom. |

### 11. Note: test-file alerts & existing `paths-ignore` gap

The config already has:
```yaml
paths-ignore:
  - tests/**
  - '**/*Tests/**'
  - '**/*.Tests/**'
```

Yet 7 test-file alerts surfaced this cycle (IDs 926, 927, 931, 973, 974, 979, 980, 1011). Likely causes:
1. **Stale alerts** — created before `paths-ignore` was added; CodeQL does not retroactively close alerts when config tightens.
2. **Workflow config wiring** — the CodeQL workflow may not be passing `config-file: ./.github/codeql/codeql-config.yml` to the `init` step. Needs verification in `.github/workflows/`.

**Proposed remediation:** run Option B above (bulk-dismiss with reason `used-in-tests`) after verifying the workflow wires the config file. If the workflow is correct, new test alerts will simply never appear.

---

## Actionable follow-ups (numbered — these become GitHub issues after approval)

### F1 — Wrap 14 dialog/process lifecycle patterns in try/finally (cs/dispose-not-called-on-throw)

**Scope:** 13 call sites across 4 TUI screens, 1 call site in `UpdateCheckService` (Process handle).

**Affected files:**
- `src/PPDS.Cli/Tui/Screens/ConnectionReferencesScreen.cs` — lines 301, 417, 487, 526 (4 sites)
- `src/PPDS.Cli/Tui/Screens/EnvironmentVariablesScreen.cs` — lines 360, 479, 560, 602 (4 sites)
- `src/PPDS.Cli/Tui/Screens/WebResourcesScreen.cs` — lines 222, 375, 428 (3 sites)
- `src/PPDS.Cli/Tui/Screens/PluginTracesScreen.cs` — lines 218, 225 (2 sites)
- `src/PPDS.Cli/Services/UpdateCheck/UpdateCheckService.cs` — line 484 (1 site, Process handle)

**Patch sketch (per site):**
```csharp
// Before
var dialog = new Dialog(...) { ... };
dialog.Add(...);
Application.Run(dialog);
dialog.Dispose();

// After
var dialog = new Dialog(...) { ... };
try
{
    dialog.Add(...);
    Application.Run(dialog);
}
finally
{
    dialog.Dispose();
}
```

For sites that assign to a field (e.g. `_detailDialog = new Dialog(...); ...; _detailDialog.Dispose(); _detailDialog = null;`):
```csharp
var dialog = new Dialog(...);
_detailDialog = dialog;
try
{
    dialog.Add(...);
    Application.Run(dialog);
}
finally
{
    dialog.Dispose();
    _detailDialog = null;
}
```

For #928 (Process handle):
```csharp
// Before
var process = Process.Start(psi);
if (process is not null)
{
    File.WriteAllText(_lockPath, process.Id.ToString());
    process.Dispose();
}

// After
var process = Process.Start(psi);
if (process is not null)
{
    try
    {
        File.WriteAllText(_lockPath, process.Id.ToString());
    }
    finally
    {
        process.Dispose();
    }
}
```

**Risk:** low. Pure resource-hygiene change; no behavioural impact on happy path. All changes are mechanical and testable via existing TUI tests.

**Priority:** P2 — note-severity-adjacent hygiene. Not blocking v1.0.0.

### F2 — Apply `config-file` to CodeQL workflow + dismiss 8 stale test alerts

Covers: `cs/useless-upcast` (3), `cs/empty-catch-block` (2), `cs/dereferenced-value-may-be-null` (2), `cs/local-not-disposed` #979 (1).

**Action:** verify `.github/workflows/*codeql*.yml` passes `config-file: ./.github/codeql/codeql-config.yml` to `github/codeql-action/init`. Then run Option B dismiss script for alerts 926, 927, 931, 973, 974, 979, 980, 1011.

**Risk:** zero (dismissals only).

### F3 — Apply CodeQL config patch for unmanaged-code rules (183 alerts)

**Action:** apply YAML patch in §"Recommended config patch". After next CodeQL scan completes successfully, the 183 alerts will auto-close.

**Risk:** zero (config only).

### F4 — Dismiss ACCEPT-RISK alerts with documented reasons (7 alerts)

**Action:** run Option C dismiss script. Each dismissal carries a specific reason referencing this document.

Alerts dismissed: 933, 962, 1003, 1004, 1021, 1022, 1023.

**Risk:** zero.

### F5 — Dismiss FP-SUPPRESS alerts for Terminal.Gui parent-owns-child pattern (5 alerts)

**Action:** dismiss alerts 941, 942, 943, 944 (`PluginTraceDetailDialog` views hung via `tabView.AddTab` and `Add(tabView, closeButton)`) and 963 (`MetadataExplorerScreen._tabButtons` added to `_detailsFrame`).

```bash
#!/usr/bin/env bash
set -euo pipefail
REPO="joshsmithxrm/power-platform-developer-suite"
COMMENT="Terminal.Gui View.Add() transfers disposal ownership to parent. Child views/buttons are reachable via parent disposal chain. See docs/qa/codeql-triage-2026-04-20.md §4."
for n in 941 942 943 944 963; do
  MSYS_NO_PATHCONV=1 gh api --method PATCH -H "Accept: application/vnd.github+json" \
    "/repos/${REPO}/code-scanning/alerts/${n}" \
    -f state=dismissed -f dismissed_reason="false positive" \
    -f dismissed_comment="${COMMENT}"
done
```

**Risk:** zero.

---

## Appendix — Evidence snapshot (for audit)

Rule × path distribution pulled from NDJSON (2026-04-20):

```
cs/call-to-unmanaged-code   (107)
  55 src/PPDS.Auth/Internal/CredentialStore/MacOS/MacOSKeychain.cs
  32 src/PPDS.Auth/Internal/CredentialStore/Linux/SecretServiceCollection.cs
  12 src/PPDS.Auth/Internal/CredentialStore/MacOS/Native/CoreFoundation.cs
   6 src/PPDS.Auth/Internal/CredentialStore/Windows/WindowsCredentialManager.cs
   1 src/PPDS.Auth/Internal/CredentialStore/MacOS/Native/SecurityFramework.cs
   1 src/PPDS.Auth/Internal/CredentialStore/MacOS/Native/LibSystem.cs
cs/unmanaged-code           (76)
  26 src/PPDS.Auth/Internal/CredentialStore/MacOS/Native/CoreFoundation.cs
  16 src/PPDS.Auth/Internal/CredentialStore/Linux/Native/Libsecret.cs
  13 src/PPDS.Auth/Internal/CredentialStore/MacOS/Native/SecurityFramework.cs
  11 src/PPDS.Auth/Internal/CredentialStore/Linux/Native/Glib.cs
   6 src/PPDS.Auth/Internal/CredentialStore/Windows/Native/Advapi32.cs
   2 src/PPDS.Auth/Internal/CredentialStore/MacOS/Native/LibSystem.cs
   2 src/PPDS.Auth/Internal/CredentialStore/Linux/Native/Gobject.cs
cs/dispose-not-called-on-throw (14) — all TUI screens + UpdateCheckService
cs/local-not-disposed       (6)  — 4 in PluginTraceDetailDialog (FP), 1 MetadataExplorer (TP), 1 test (FP)
cs/missed-using-statement   (4)  — 3 auth providers (ACCEPT), 1 field-scoped CTS (ACCEPT)
cs/useless-upcast           (3)  — all in tests/
cs/missed-readonly-modifier (2)  — init-method assignment FP
cs/empty-catch-block        (2)  — both in tests/
cs/dereferenced-value-may-be-null (2) — both in tests/
cs/constant-condition       (1)  — double-checked locking FP
```

Severity: 191 note, 26 warning, 0 error. All 26 warnings are addressed by F1 / F2 above.
