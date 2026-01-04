# Session Retrospective: CLI E2E Tests

**Date:** 2026-01-04
**Project:** ppds-sdk
**Branch:** musing-goldwasser (worktree)
**PR:** #117
**Duration:** ~8 hours (02:10 - 10:09)
**Outcome:** Merged - 29 CLI E2E tests for plugin commands

---

## Summary

Added comprehensive E2E test coverage for all 5 plugin CLI commands (extract, list, deploy, diff, clean). Initial implementation worked locally but required 10 fix commits after PR creation due to undocumented CI constraints and output format assumptions.

---

## Metrics

| Metric | Value |
|--------|-------|
| Commits after "done" | 10 |
| Integration test CI failures | 3 |
| Bot review comments | 25+ |
| CodeQL noise (Path.Combine) | 15 |
| Time to merge | ~8 hours |

---

## What Went Well

| Pattern | Why It Worked |
|---------|---------------|
| Two-tier test strategy | Safe (--what-if) tests in CI, destructive tests with cleanup |
| Test base classes | `CliE2ETestBase` handled isolation, cleanup automatically |
| Bot review for real issues | Gemini caught missing try-finally cleanup |
| Iterative fixing | Each CI run revealed next issue to fix |

---

## Friction Points

### 1. DPAPI/SecureCredentialStore Hang

**Issue:** Tests hung indefinitely in CI, job timed out after 6 hours

**Impact:** 4 fix commits, hours of debugging

**Root Cause:** `SecureCredentialStore` uses MSAL's `MsalCacheHelper` which requires DPAPI. GitHub Actions runners lack DPAPI access. No documentation warned about this.

**Fix Implemented:**
- Added 60s internal timeout to `ClientSecretCredentialProvider`
- CLI E2E tests auto-set `PPDS_SPN_SECRET` to bypass SecureCredentialStore
  - *Later improved:* `CredentialProviderFactory` now checks `PPDS_TEST_CLIENT_SECRET` as fallback, eliminating need for explicit bridging
- Created `SecureStorageE2ETests` with `[Trait("Category", "SecureStorage")]` for explicit testing

### 2. CLI JSON Output Format Mismatch

**Issue:** Tests expected `[...]` but CLI outputs `{"version":"1.0","data":[...]}`

**Impact:** 3 fix commits

**Root Cause:** ADR-0008 documents envelope format but wasn't referenced when writing test assertions. Classic "didn't know what I didn't know."

**Fix Implemented:** Updated all JSON assertions to expect envelope wrapper

### 3. Slow Query Not Categorized

**Issue:** Listing unfiltered plugins returned 60k+ records, taking 100+ seconds

**Impact:** CI timeout, 1 fix commit

**Root Cause:** No pre-existing `SlowIntegration` test category

**Fix Implemented:** Added `[Trait("Category", "SlowIntegration")]` and updated CI filter

### 4. CodeQL Noise

**Issue:** 15+ "Path.Combine" alerts in test files - pure noise

**Impact:** Cognitive load during triage, wasted time

**Root Cause:** CodeQL `security-and-quality` queries flag Path.Combine as potential path traversal, irrelevant in test context

**Fix Implemented:** Created `.github/codeql/codeql-config.yml` to exclude `tests/**`

### 5. Bot Review Duplicate Detection

**Issue:** Same issues reported by multiple bots (CodeQL + Copilot)

**Impact:** Triage complexity

**Root Cause:** No grouping by file+line in triage workflow

**Fix Implemented:** Updated `/review-bot-comments` to note duplication patterns

---

## Improvements Implemented

### Documentation

| File | Change |
|------|--------|
| `docs/INTEGRATION_TESTING.md` | **New** - Comprehensive testing guide |
| `CLAUDE.md` | Added test category quick reference |

### CI/Tooling

| File | Change |
|------|--------|
| `.github/codeql/codeql-config.yml` | **New** - Excludes tests from analysis |
| `.github/workflows/codeql.yml` | References config file |
| `.github/workflows/integration-tests.yml` | Excludes SecureStorage, SlowIntegration |

### Slash Commands

| Command | Change |
|---------|--------|
| `/run-integration-local` | **New** - Local integration test runner |
| `/debug-ci-failure` | **New** - CI failure analyzer |
| `/review-bot-comments` | Added Autofix alert integration |

---

## Lessons Learned

1. **Document CI constraints proactively** - DPAPI, timeouts, credential handling
2. **Test categories should exist before tests** - Don't discover them reactively
3. **Reference ADRs when writing tests** - Output format is documented, use it
4. **CodeQL needs tuning** - Default queries generate noise in test code
5. **Bot reviews need triage workflow** - Multiple bots = duplicates

---

## Prevention Checklist

For future integration test work:

- [ ] Check `docs/INTEGRATION_TESTING.md` before writing tests
- [ ] Inherit from correct base class (`LiveTestBase` or `CliE2ETestBase`)
- [ ] Consider test category (SecureStorage? SlowIntegration? DestructiveE2E?)
- [ ] Reference ADR-0008 for CLI output format expectations
- [ ] Use try-finally for destructive tests
- [ ] Run `/run-integration-local` before pushing

---

## Related

- [docs/INTEGRATION_TESTING.md](../INTEGRATION_TESTING.md) - Full testing guide
- [ADR-0008: CLI Output Architecture](../adr/0008_CLI_OUTPUT_ARCHITECTURE.md) - Output format
- PR #117 - The original PR with full commit history
