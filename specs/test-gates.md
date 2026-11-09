# Test gates

**Status:** Implemented incrementally
**Code:** .github/workflows/test.yml, .github/workflows/workflow-tests.yml, .github/workflows/integration-tests.yml, scripts/ci/check_test_status.py
**Surfaces:** CI

## Contract

The Test workflow reports its aggregate on every pull request to main. Its change
classifier decides whether .NET, single-file extraction, candidate VSIX, and
Python regression suites are needed. Python tests run through one reusable
workflow. A needed suite must succeed; failure, cancellation, and unexpected skip
fail the aggregate. An intentionally unneeded suite may skip. Missing
classification fails closed.

Repository protection must require the aggregate for it to block merges. Workflow
code does not itself configure repository protection.

## Live Integration hold

Integration has no automatic push or pull-request subscription. Its only event is
an input-free manual dispatch that runs a bounded refusal step with no granted
token scopes and exits unsuccessfully. It performs no checkout, build, test, credential lookup,
Dataverse call or cleanup, and declares no environment, action, cache or OIDC
grant. It reports that no live tests ran; skipped or absent execution is never
reported as passed. This is an explicit hold, not a replacement live test suite.

Automatic Build/Test content is pinned to reviewed baselines: Build remains at R,
while Test includes the approved .NET 10/read-only native and VSIX validation.
Their required aggregate names and fail-closed Test behavior are preserved.
This transition changes no repository protection or credentials and does not
implement a trusted issuer or approval protocol. A future live route requires
reviewed exact source/target/suite authorization and proven fixture ownership and
cleanup safety, or an approved exclusive disposable environment lifecycle.
Historical runs and other refs retain their historical workflow definitions;
the new hold does not revoke or cancel them.

## Acceptance criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | Python-affecting paths invoke the authoritative reusable suite and are dependencies of the always-reporting test-status gate | `test_reusable_python_suite_feeds_always_reporting_gate` | ✅ |
| AC-02 | The gate accepts needed-success and intentional skips only, rejecting failure, cancellation, unexpected skip, invalid classification, and classifier failure | `test_suite_truth_table`, `test_classifier_must_succeed`, `test_needed_dotnet_cannot_unexpectedly_skip` | ✅ |
| AC-03 | Integration subscribes only to input-free manual dispatch; PR, main push and all other automatic events cannot select the patched workflow. | `test_integration_subscribes_only_to_input_free_manual_dispatch`, `test_automatic_or_callable_trigger_mutants_are_rejected`, `test_manual_dispatch_input_mutant_is_rejected` | ✅ |
| AC-04 | Manual dispatch has one bounded refusal job with no token scopes, environment, actions, checkout, credentials, executable inputs or live commands. | `test_integration_is_one_bounded_tokenless_refusal`, `test_unsafe_hold_mutants_are_rejected_without_execution`, `test_integration_hold_has_no_token_scopes_or_elevated_exception` | ✅ |
| AC-05 | The actual refusal command fails and reports no tests ran; no skipped-as-passed aggregate or success placeholder remains. | `test_actual_refusal_bash_fails_and_reports_no_live_tests`, `test_unsafe_hold_mutants_are_rejected_without_execution` | ✅ |
| AC-06 | Automatic Build/Test content matches the reviewed baselines (Build at R; Test with the approved .NET 10/read-only native and VSIX validation), while required aggregate names and fail-closed Test behavior are preserved. | `test_automatic_build_and_test_git_contents_match_reviewed_baselines`, `test_automatic_events_and_required_aggregate_names_are_preserved`, `test_suite_truth_table`, `test_classifier_must_succeed`, `test_needed_dotnet_cannot_unexpectedly_skip` | ✅ |

