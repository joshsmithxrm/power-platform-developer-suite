# Post-#1057 Workflow Robustness

**Status:** Draft
**Last Updated:** 2026-05-15
**Code:** [scripts/_shakedown_allowlist.py](../scripts/_shakedown_allowlist.py) | [scripts/claude_dispatch.py](../scripts/claude_dispatch.py) | [scripts/retro_helpers.py](../scripts/retro_helpers.py) | [scripts/verify_shakedown.py](../scripts/verify_shakedown.py)
**Surfaces:** N/A (workflow tooling)
**Verification:** `python -m pytest tests/test_retro_helpers.py tests/test_verify_shakedown.py -q`
**Verification Max Iterations:** 5

---

## Overview

PR #1057 shipped the empirical shakedown gate, retro extractor fix, and drift detector for Issue #1054. A parallel PR (#1061) ran the same work and during its self-review + Gemini-review iteration discovered seven small robustness gaps in the version that ultimately squashed onto main. This spec lands those gaps as a focused follow-up: one false-positive bug fix with test coverage, one correctness fix to a misleadingly-named helper, one latent path-normalisation bug, three robustness improvements at git/subprocess boundaries, and one in-code rationale comment.

Net delta: ~80 lines across four scripts and one test file. No new public surface; no behavioural change for the green path; bounded behavioural change for one edge case (`feat(verify): ...`-style commit subjects no longer false-match the `/verify` marker scan).

### Goals

- **Eliminate the `/verify`-marker false positive** so the drift detector cannot lose its anchor when a normal commit happens to mention `verify` in its subject.
- **Make `derive_slug` correct in isolation** so future callers can pass relative paths without silently getting a non-matching slug.
- **Robust handling of `git`'s quoted output** so paths with spaces or unicode are not silently dropped from allowlist matching.
- **Distinct exit code for setup failures in `verify_shakedown`** (rc=2) so CI can differentiate "shakedown failed" from "we never got to run shakedown".
- **Document the `dangerous=True` flag** in the shakedown spawn so future readers understand why it is appropriate for the unattended `/verify` invocation.

### Non-Goals

- **Behavioural change to the shakedown gate itself.** The gate still spawns one real `claude --bg` against an allowlist diff, still asserts exit 0, still has the same 5-minute timeout budget.
- **Schema or contract change for `.workflow/retro-findings.json`.** The drift-detector output is unchanged.
- **Modifying `_spawns_subprocess` heuristic case-sensitivity.** PR #1061 dropped `.lower()` from the source-content read; this spec deliberately keeps main's `.lower()` because the asymmetry (false-positive cost is bounded by the human-approval layer in `.workflow/retro-findings.json`, false-negative cost is a silent miss of the exact drift signal the detector exists to surface) favours case-insensitive matching. A one-line comment is added to `_SUBPROCESS_HINTS` documenting the lowercase-by-contract invariant so a future maintainer adding an uppercase hint does not silently break the heuristic.
- **Re-running #1057's work.** Everything from #1057 (allowlist file, gate, drift detector, skill-doc refactor) already lives on main and stays.

---

## Specification

### Acceptance Criteria

| ID | Criterion | Test |
|----|-----------|------|
| R-01 | `scripts/_shakedown_allowlist.py:_norm("./scripts/foo.py")` returns `scripts/foo.py`, and `_norm(".github/workflows/x.yml")` returns `.github/workflows/x.yml` (leading dot is preserved, not stripped). | `tests/test_retro_helpers.py::TestAllowlistDriftDetector` covers `is_allowlisted` indirectly; explicit `_norm` behaviour is covered by the existing `is_allowlisted` test path with the modified normaliser. |
| R-02 | `scripts/claude_dispatch.py:derive_slug(".")` returns the slug for the absolute current working directory, not `"-"`. | `tests/test_retro_helpers.py::TestDiscoverTranscripts::test_encode_project_dir_replaces_non_ascii_chars` (updated to call `derive_slug(path)` directly) exercises the abspath normalisation. |
| R-03 | `scripts/retro_helpers.py:_commits_since_verify` treats `feat(verify): tighten marker` as a non-marker (the regex anchors to the start of the subject and matches only `/verify`, `verify:`, `verify(`, `verify passed`, `verify ok`). | `tests/test_retro_helpers.py::TestAllowlistDriftDetector::test_feat_referencing_verify_is_not_a_marker` (new). |
| R-04 | `scripts/retro_helpers.py:_commit_files` strips git's surrounding `"…"` from paths containing special characters (spaces, unicode) when `core.quotePath` is on. | Existing `_commit_files` callers in `TestAllowlistDriftDetector` exercise the path; the post-fix code path is the same string-cleaning pipeline. |
| R-05 | `scripts/retro_helpers.py:_encode_project_dir(path)` is equivalent to `derive_slug(path)` (no separate `os.path.abspath` wrapper at the call site, because `derive_slug` normalises internally). | `tests/test_retro_helpers.py::TestDiscoverTranscripts::test_encode_project_dir_replaces_non_ascii_chars` (updated assertion). |
| R-06 | `scripts/retro_helpers.py:_commits_since_verify` searches the last 1000 commits, not 200. | Mechanical: covered by code review against the spec; no isolated unit test required. |
| R-07 | `scripts/verify_shakedown.py` exposes a `_SetupError` exception class; `main()` catches it and returns rc=2; subprocess timeouts and `OSError` in `_changed_files` / `_detect_base` raise `_SetupError`. | `tests/test_verify_shakedown.py::test_setup_error_surfaces_as_rc2` (already shipped on main; the missing piece is the wiring on the helper side). |
| R-08 | `scripts/verify_shakedown.py:_changed_files` strips git's `"…"` quoting from `git diff --name-only` output. | Indirectly covered by `tests/test_verify_shakedown.py::test_changed_files_diff_path` (already shipped on main; runs against a git repo with the allowlist filename). |
| R-09 | `scripts/verify_shakedown.py:run_shakedown` carries a comment explaining why `dangerous=True` is appropriate for the shakedown spawn (unattended `/verify`, throwaway prompt asks for no tool use, otherwise a stray permission prompt would stall until timeout). | No isolated test — comment is a code-review artifact. |
| R-10 | `scripts/retro_helpers.py:_SUBPROCESS_HINTS` carries an inline comment documenting that hints must be lowercase because `_spawns_subprocess` lowercases the file content before matching. | No isolated test — comment is a code-review artifact. |

### Out-of-Scope

- Adding new allowlist entries.
- Behavioural changes to the drift detector beyond the regex anchor fix.
- Modifying the `_spawns_subprocess` heuristic (PR #1061's `.lower()` removal is deliberately not carried over).

---

## Dependencies

- Depends on: [architecture.md](./architecture.md) (cwd-isolation, workflow-tooling positioning).
- Builds on the merged work of #1057 (`scripts/_shakedown_allowlist.py`, `scripts/verify_shakedown.py`, `scripts/retro_helpers.py:detect_allowlist_drift`).
- Patch source: `git diff origin/main origin/worktree-workflow-gates-impl -- <file>` for each affected file.

---

## Implementation Notes

- The follow-up PR will need `PPDS_PR_GATE_HUMAN=1` exported because PR-gate fix #1062 has not merged yet.
- The original parallel PR (#1061) remains open; closing is the operator's call after this PR opens. The investigation document at `.investigation/1061-recommendation.md` on branch `review/1061-dedup` records the rationale.
