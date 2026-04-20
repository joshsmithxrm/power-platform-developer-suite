# PPDS v1.0.0 Pre-release Code Review (W3)

Reviewer: Claude Opus 4.7 (workstream W3)
Scope: 35 PRs merged to `main` 2026-04-19 through 2026-04-20
Reference: PPDS CLAUDE.md rubric (NEVER/ALWAYS rules), Spec Laws SL1–SL5 assumed enforced by CI.

---

## Summary

Verdict counts (35 PRs total; #820 parent + #840 enhancement are tracked as a single logical group in the focus-area findings but listed as separate rows in the per-PR table):

- clean: 28
- minor-issues: 7
- needs-followup: 0
- regression-risk: 0

**Ship-blockers:** none. No NEVER-rule violation in production code. The delta is overwhelmingly workflow/skill tooling (Python scripts, Markdown SKILL files, CI config) with four targeted .NET fixes (#803 auth vendoring, #827 DML coercion, #826 browser-launcher test isolation, #828 CLI reflect sort, #825 libs-reflect inheritdoc).

**Top-5 findings (across the set):**

1. **[#840] Plan schema silently coerces `PR:` and `ready` status to `Session:`/`planned`.** The SKILL.md calls this out, but the round-trip coercion means a Lane B dispatcher writes PR URLs into a `Session:` slot. Parser is forgiving, but log consumers grepping for `PR:` will find nothing. Low severity; suggests a v2 schema adding `PR:` as a first-class field. (`scripts/dispatch_plan.py`)
2. **[#837] Stop-hook watchdog blocks Stop, it does not kill long-running Agents.** The name "circuit breaker" and the meta-retro framing suggest it terminates runaway sessions; it only refuses to let a thrashing hook re-fire. A subagent stuck in a tight loop without a Stop hook firing (or with `stop_hook_active` set) is untouched. Documentation mismatch. (`.claude/hooks/stop-hook-watchdog.py`)
3. **[#846] `_gemini_effectively_done` pattern matching is case-sensitive plain string.** "I have no feedback to provide" would miss "I have **no** feedback to provide" (bold) or any other Gemini phrasing tweak. Tuple of string patterns is tested but brittle — suggest case-insensitive substring check at minimum. (`scripts/pr_monitor.py`)
4. **[#842] `settings.json` `safety` top-level key flagged by Claude Code editor validator.** PR body admits this. Fallback plan (`.claude/safety.json`) not yet implemented — so the file is structurally valid but the editor throws. Low priority but is a known papercut for the next person editing settings.
5. **[#827 / #843] Tests present but shallow for rule/type conversion surfaces.** DML coercion covers happy paths but no adversarial Dataverse type-mismatch tests visible; retro html generator has 28 tests but the rule-drift SKILL.md patch-proposal path is un-tested end-to-end.

---

## Focus-area findings

### #840 dispatch subverb — async Agent spawning

**Files:** `.claude/skills/backlog/SKILL.md` (sole change, +157/-47).

**Concurrency:** Lock acquisition for `.claude/state/dispatch-plan.md` is via `scripts/inflight_common.py::_lock` — OS-level advisory locks (`fcntl.flock` on POSIX, `msvcrt.locking` on Windows) on an open file descriptor. Safe across crashes because the OS releases the lock when the fd closes. The `.claude/state/dispatch-plan.md.lock` file present on disk (zero bytes, ctime 2026-04-19) is a harmless leftover, NOT a held lock.

**Cross-platform asymmetry (minor):** POSIX `_lock` is blocking (`LOCK_EX` with no timeout); Windows uses `LK_NBLCK` with 50× 100 ms retry then raises `OSError`. A long-held POSIX lock would hang the dispatcher silently; a long-held Windows lock would fail with a clear error after 5 s. Not a regression — file pre-dates this PR — but worth a follow-up to normalize (#follow-up-3).

**Dead-lock on crash:** None observed. OS releases on fd close, including on SIGKILL.

**PR-URL-into-Session-field coercion (minor):** SKILL.md §Phase D.3 acknowledges `PR: <url>` would be dropped by `write_plan` round-trip. The workaround is to stuff the PR URL into `session_id`, which the data model renders as `Session: <value>`. This is schema drift; the PR body claims the schema gained a `PR:` field but the code didn't. Documentation-vs-implementation gap.

**Async monitor correctness:** `ScheduleWakeup(1200)` default is sensible (meta-retro context: burns cache otherwise). No infinite-reschedule guard on failure — if `gh pr list` returns errors indefinitely, wake-up loops forever. Low risk in practice; suggest a max-iterations cap (#follow-up-4).

Verdict: **minor-issues** (documentation drift, no production regression).

### #837 Stop-hook circuit breaker watchdog

**Files:** `.claude/hooks/stop-hook-watchdog.py` (+174), `.claude/settings.json` (+9), `.gitignore` (+3), `tests/test_stop_hook_watchdog.py` (+139).

**Mechanism:** Refuses to let a single `(session_id, hook_name)` pair fire >20 Stop events in a 5-minute rolling window. Uses a sidecar `.lock` file with `O_CREAT|O_EXCL` and stale-lock reclamation (30 s). Atomic write via `os.replace`. Fail-open on every bookkeeping error. Clean.

**Edge case — does not kill Agents:** The hook only intervenes when Stop fires. It does NOT kill a subagent running inside a `claude -p` call, nor does it terminate a long `TaskCreate`. The name "circuit breaker" and meta-retro #17 framing (PR #830 ran 509 turns/553 errors) imply stronger semantics than delivered. This is a valid circuit-breaker for loops driven BY Stop-hook mis-configuration; it is not a runaway-agent killer. Documentation mismatch only.

**`stop_hook_active` re-entry guard:** Correct — exits 0 without incrementing the counter. Test coverage for this path is present (`test_respects_stop_hook_active`).

**Threshold tunability:** Hard-coded constants `WINDOW_SECONDS = 300`, `THRESHOLD = 20`. Not user-configurable; PR body says "tune if legit patterns exceed." Minor — for a circuit breaker this is fine.

Verdict: **minor-issues** (doc mismatch; behavior is correct for its actual scope).

### #843 /retro rewrite — plan-with-defaults, HTML synthesis

**Files:** `.claude/skills/retro/SKILL.md` (+255/-213), `scripts/retro_html_generator.py` (+691, new), `tests/test_retro_html_generator.py` (+476, new).

**SKILL.md frontmatter:** Valid YAML, matches PPDS conventions (`description`, `argument-hint` absent intentionally per skill pattern, 9-phase body).

**Hardcoded paths:** `.retros/`, `.workflow/retro-findings.json`, `.claude/interaction-patterns.md` — all are established PPDS locations. No absolute paths.

**Script `retro_html_generator.py`:** 691 lines for an HTML renderer is on the heavy side but is zero-dep (stdlib only). HTML escaping via `html.escape` applied consistently (tested). Mermaid via CDN — fine for a local dashboard, but network-dependent. Low priority, noted.

**Test coverage:** 28 tests cover render/index/escaping/CLI. Good. The `rule-drift` finding path (which generates SKILL.md patch proposals) is NOT covered end-to-end — only the kind taxonomy is tested.

**Self-preference-bias citation:** arXiv link in PR body is consistent with interaction-patterns §7. SKILL.md correctly keeps LLM judgment main-session and delegates only mechanical extraction to subprocess.

Verdict: **minor-issues** (rule-drift e2e untested; Mermaid CDN dependency).

### #846 pr-monitor Gemini-decline / approval-equivalent detection

**Files:** `scripts/pr_monitor.py` (+100/-5), `tests/test_pr_monitor.py` (+218).

**Regex safety:** No user-controlled regex. `_GEMINI_CLEAN_PATTERNS` is a module-level tuple of literal strings; `_gemini_effectively_done` does `pattern in (body or "")` substring matching. No ReDoS possible.

**Approval semantics:** The function returns True when body contains either "I have no feedback to provide" or "Gemini is unable to generate a review". A genuinely declined review where Gemini flagged concerns but couldn't finish (hypothetical) would not contain these strings and would fall through to the "flagged-issues-addressed" path. That path requires ALL inline bot comments to have replies before the gate passes — so misreading a declined review as approved would require a bad pattern AND zero inline comments. Tight.

**Case sensitivity (minor):** `"I have no feedback" in body` is case-sensitive. If Gemini bolds or rephrases (e.g., "**I have no feedback to provide.**" — bold markdown inserts asterisks inside the phrase), the match breaks. Suggest `.lower() in body.lower()` or a small regex with `re.IGNORECASE`. Pattern brittleness, not a correctness bug today.

**Empty-body handling:** `_gemini_effectively_done(None)` and `""` both tested to return False. Correct.

Verdict: **minor-issues** (pattern brittleness, not a semantic bug).

### #842 shakedown consolidation — were old hooks actually removed?

**Files:** `shakedown-readonly.py` (-234, deleted), `shakedown-safety.py` (+249/-111, renamed/merged), old tests deleted (-449, -271), new consolidated test (+722).

**Verified:**
- `.claude/hooks/shakedown-readonly.py` is gone (not in `ls .claude/hooks/`).
- `dev-env-check.py` is gone.
- `.claude/skills/shakedown-workflow/` directory removed; content folded into `/shakedown`'s Workflow Mode.
- Old `tests/hooks/test_dev_env_check.py` and `test_shakedown_readonly.py` removed.

**Not orphaned.** The consolidation is genuine — no dead code on either side.

**Known issues (PR body surfaces both):**
1. `settings.json` top-level `safety` key is rejected by the Claude Code editor schema validator. Written via `json.dump` bypass. Fallback (`.claude/safety.json`) is designed but not implemented. Papercut.
2. `specs/workflow-enforcement.md` / `specs/workflow-verify-tool.md` still reference `/shakedown-workflow` as a separate skill. Spec drift. Should be in a follow-up doc refresh.
3. Legacy `.claude/state/safe-envs.json` fallback still read in `load_allowlist` — intentional one-release bridge.

Verdict: **minor-issues** (spec drift, editor-validator friction).

### #838 CI parallelization — cache-key collision risk

**Files:** `.github/workflows/*.yml` (CI surface).

Sampled the workflow diff via `gh pr diff 838`. Integration tests split by matrix (`Category=Integration` runs in parallel across multiple runners). Shared restore cache uses `hashFiles('**/packages.lock.json')` as the key — stable per branch, no collision between parallel jobs because restore is idempotent (jobs read, never write). TUI E2E path filter uses `paths:` clause, correctly scoped.

**Matrix correctness:** Tests are partitioned by test project, not by test name — so no risk of two jobs running overlapping tests. Projects themselves are disjoint.

**Cache-key leakage:** Restore cache is read-only. Save is gated by `if: always() && github.ref == 'refs/heads/main'`. No cross-PR leakage.

Verdict: **clean**.

### #827 query DML type coercion — .NET production code

**Files:** `src/PPDS.Query/Execution/`, `src/PPDS.Query/Planning/`, `src/PPDS.Dataverse/Query/Planning/Nodes/`, new tests.

**PpdsException wrapping (NEVER rule 3):** Verified — coercion helpers throw `QueryExecutionException` (with `ErrorCode` property, e.g. `QueryErrorCode.TypeMismatch`), NOT raw `InvalidCastException`/`FormatException`. `QueryExecutionException` is a deliberate per-layer typed exception (Dataverse layer cannot reference PPDS.Cli where `PpdsException` lives). `PPDS.Cli/Infrastructure/Errors/ExceptionMapper.cs` wraps it into `PpdsException` at the CLI boundary. Architectural compliance with the NEVER rule; no raw exception escapes Application Services.

**Test coverage:** New tests exist for int/string/Guid/OptionSetValue/EntityReference coercion. No tests for nullable-to-non-nullable Dataverse lookup mismatches (the most common production failure mode). Suggested follow-up.

**IProgressReporter (ALWAYS):** DML coercion is sub-millisecond work, no progress reporter needed. Correct.

Verdict: **minor-issues** (nullable/lookup test coverage gap).

### #803 vendored git-credential-manager

**Files:** `src/PPDS.Auth/Internal/CredentialStore/**/*.cs` (vendored source), `THIRD_PARTY_NOTICES.md` update, `src/PPDS.Auth/PPDS.Auth.csproj` (Devlooped.CredentialManager package reference removed).

**Vendoring genuineness:** Files are C# source, not compiled binary. Namespaces appear adjusted to `PPDS.Auth.Internal.CredentialStore` (internal). License text from the original git-credential-manager project is included in `THIRD_PARTY_NOTICES.md`.

**NOTICES updated:** Devlooped mention removed (also the explicit purpose of PR #824). New GCM attribution added.

**License compatibility:** GCM is MIT; PPDS is MIT — compatible.

Verdict: **clean**.

### #822 shakedown write-block hooks — block or warn?

**Files:** `.claude/hooks/shakedown-safety.py` (unified), `.claude/settings.json`.

Verified via the consolidated `shakedown-safety.py`: when `PPDS_SHAKEDOWN=1` is set and a write tool is invoked on a path matching the write-block rules, the hook returns exit 2 with `{"decision":"block", …}`. This is a hard block, not a warn. Allowlist (`shakedown_safe_envs`) is consulted from `settings.json`.

Verdict: **clean**.

---

## Per-PR table

| PR | Verdict | Finding |
|----|---------|---------|
| #846 | minor-issues | Case-sensitive pattern match for Gemini decline detection; brittle to phrasing tweaks. |
| #845 | clean | pr-gate hook correctly enforces `/pr-skill` entry; tests present. |
| #844 | clean | Draft-on-open + canonical-entry-point rule landed in skill. |
| #843 | minor-issues | HTML generator tested but `rule-drift` e2e path + Mermaid CDN dep. |
| #842 | minor-issues | Spec drift (`/shakedown-workflow` refs), `safety` key editor friction. |
| #841 | clean | Pre-commit gate dedup; no behavior change beyond removing duplicates. |
| #840 | minor-issues | `PR:` schema drift — URL stored in `Session:` slot; async monitor has no max-iteration cap. |
| #838 | clean | Matrix + shared cache correct; no cross-job leakage. |
| #837 | minor-issues | Name/framing ("circuit breaker") overstates scope — gates Stop hook firings, does not kill runaway Agents. |
| #836 | clean | Pre-/pr self-review + post-merge cleanup; workflow-only. |
| #834 | minor-issues | Auto-ready-flip is additive; empty-review guard correct; source-branch rebase logic has no tests for merge-conflict case. |
| #830 | clean | `/start` stale-base fix + `/design` phase-flip fix; hook-driven. |
| #828 | clean | CLI reflect sort by long name — snapshot fixtures updated consistently. |
| #827 | minor-issues | Missing nullable/lookup-mismatch tests for DML coercion. |
| #826 | clean | IBrowserLauncher injection; real browser launches gated behind test flag. |
| #825 | clean | Inheritdoc warning + fallback pointer; snapshot updated. |
| #824 | clean | THIRD_PARTY_NOTICES — Devlooped removed, matches #803. |
| #823 | clean | safe-envs.json populated (pre-#842); now superseded by `settings.json` block. |
| #822 | clean | Write-block hooks genuinely block (exit 2 + `{"decision":"block"}`), not warn-only. |
| #821 | clean | Pre-merge gate (size, secret-refs, major-bump) — CI-only. |
| #820 | clean | `/backlog dispatch` landed as a subverb; see #840 for enhancements. |
| #819 | clean | Docs only — grouped-PR rule reconciliation. |
| #816 | clean | Bundles 6 mechanical fixes (retro #1-3,#5,#16,#18); each item has a dedicated test file. `test_hook_envelope.py` covers the envelope-nesting class of bugs end-to-end. |
| #815 | clean | CLAUDE.md governance — governance doc + markers. |
| #814 | clean | `/dependabot-triage` skill; orthogonal to production code. |
| #813 | clean | Cross-session in-flight state with OS-level locks — same primitives #840 builds on. |
| #812 | clean | Docs only — cross-repo conventions. |
| #806 | clean | npm dep bump. |
| #805 | clean | npm dep bump. |
| #803 | clean | Genuinely vendored C# source; NOTICES updated; MIT/MIT compatible. |
| #784 | clean | Azure.Identity 1.21.0 — standard minor bump. |
| #783 | clean | Microsoft.NET.Test.Sdk 18.4.0 — standard bump. |
| #782 | clean | GitHub Actions bumps. |
| #779 | clean | RadLine 0.10.0 — TUI input library bump. |
| #778 | clean | MSAL bump. |

---

## Recommended follow-up issues

1. **Title:** `_gemini_effectively_done` pattern match should be case-insensitive / whitespace-tolerant
   **Severity:** low
   **Affected files:** `scripts/pr_monitor.py`
   **Rationale:** Gemini phrasing drift (bold, punctuation) will silently regress gate behavior. Switch to `re.search(pattern, body, re.IGNORECASE | re.DOTALL)`.

2. **Title:** Stop-hook watchdog README/docstring — clarify it gates Stop, does not kill Agents
   **Severity:** low (doc-only)
   **Affected files:** `.claude/hooks/stop-hook-watchdog.py` (docstring), meta-retro #17 notes
   **Rationale:** Current framing implies runaway-Agent termination. Explicit scope statement avoids future false-security.

3. **Title:** Normalize `_lock` timeout behavior between POSIX and Windows
   **Severity:** low
   **Affected files:** `scripts/inflight_common.py`
   **Rationale:** POSIX `LOCK_EX` blocks forever; Windows `LK_NBLCK` fails after ~5 s. Operator gets opposite failure modes for the same condition. Make both time-bounded with a consistent diagnostic.

4. **Title:** Dispatch-plan async monitor — cap max re-schedules / escalate on `gh pr list` failure
   **Severity:** low
   **Affected files:** `.claude/skills/backlog/SKILL.md` Phase E
   **Rationale:** Current SKILL.md has no upper bound on `ScheduleWakeup` re-scheduling. A persistent `gh pr list` error or a PR that never reaches terminal state would loop indefinitely.

5. **Title:** Promote `PR:` to a first-class dispatch-plan schema field
   **Severity:** low
   **Affected files:** `scripts/dispatch_plan.py`, `.claude/skills/backlog/SKILL.md`
   **Rationale:** Currently PR URLs are overloaded into `Session:` for Lane B. SKILL.md calls this out as a known limitation. Schema v2 + migration is ~30 LOC.

6. **Title:** DML coercion — add nullable-to-non-nullable Dataverse lookup tests
   **Severity:** medium
   **Affected files:** `tests/PPDS.Query.Tests/Execution/DmlCoercionTests.cs`
   **Rationale:** Most common production Dataverse coercion failure is null in a required lookup. Current tests cover type mismatches but not null handling.

7. **Title:** `settings.json` `safety` block — schema registration or move to `.claude/safety.json`
   **Severity:** low
   **Affected files:** `.claude/settings.json`, `.claude/hooks/shakedown-safety.py`
   **Rationale:** PR #842 admits the editor validator rejects the key. Either register the schema or move the config to a sibling file as designed in the PR body.

8. **Title:** Retro HTML generator — cover `rule-drift` end-to-end patch-proposal path in tests
   **Severity:** low
   **Affected files:** `tests/test_retro_html_generator.py`
   **Rationale:** 28 tests cover rendering; the SKILL.md patch-proposal pipeline (which mutates skill files) is un-tested.

9. **Title:** Spec refresh — remove `/shakedown-workflow` references after #842 consolidation
   **Severity:** low (doc-only)
   **Affected files:** `specs/workflow-enforcement.md`, `specs/workflow-verify-tool.md`
   **Rationale:** Behavioral ACs still valid; skill-name references now stale.

10. **Title:** Retro `Mermaid via CDN` — optional local-first fallback
    **Severity:** low
    **Affected files:** `scripts/retro_html_generator.py`
    **Rationale:** Offline retro dashboards render blank diagrams. Inline the mermaid init script as a vendored alternative (~40 KB) behind a `--offline` flag.
