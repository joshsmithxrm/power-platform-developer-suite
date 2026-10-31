# Test gates

**Status:** Implemented incrementally
**Code:** .github/workflows/test.yml, .github/workflows/workflow-tests.yml, scripts/ci/check_test_status.py
**Surfaces:** CI

## Contract

The Test workflow reports its aggregate on every pull request to main. Its change
classifier decides whether .NET, single-file extraction, and Python regression
suites are needed. Python tests run through one reusable workflow. A needed suite
must succeed; failure, cancellation, and unexpected skip fail the aggregate.
An intentionally unneeded suite may skip. Missing classification fails closed.

Repository protection must require the aggregate for it to block merges. Workflow
code does not itself configure repository protection.

## Acceptance criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | Python-affecting paths invoke the authoritative reusable suite and are dependencies of the always-reporting test-status gate | `test_reusable_python_suite_feeds_always_reporting_gate` | ✅ |
| AC-02 | The gate accepts needed-success and intentional skips only, rejecting failure, cancellation, unexpected skip, invalid classification, and classifier failure | `test_suite_truth_table`, `test_classifier_must_succeed`, `test_needed_dotnet_cannot_unexpectedly_skip` | ✅ |

