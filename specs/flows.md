# Flows

**Status:** Implementing activation; read commands implemented
**Last Updated:** 2026-10-31
**Code:** src/PPDS.Cli/Services/Flows/ | src/PPDS.Cli/Commands/Flows/
**Surfaces:** CLI (read and activation); other surfaces retain existing read behavior

## Contract

Flow activation completes the post-deployment analyze/bind/enable workflow for solution-aware modern cloud-flow definitions. Enable/disable supports exactly one ID, unique name, or all eligible flows in one named solution. No implicit import activation, connection binding, ownership changes, trigger invocation, or rollback.

Application Services own selection, complete paging, preflight, confirmation, transitions, and verification. An activation coordinator reuses flow and connection-reference readers without creating a circular dependency. The connection pool leases a client per service operation; progress and cancellation propagate throughout.

### Selection and preview

CLI: `ppds flows enable|disable (--id <guid>|--name <unique-name>|--all --solution <unique-name>)`.

Additional options: existing profile/environment/output-format, `--dry-run`, `--confirm-env <url>`, `--timeout <seconds>` (verification, default 60), `--max-flows <n>` (batch default 100), and optional `--expected-plan-hash <hash>`.

Only category 5, type 1 definitions may mutate. Unsupported entries are listed separately with counts/reasons, not silently dropped. A supplied single-flow solution is a membership constraint. Ambiguous names fail. A missing solution fails; an existing empty selection reports zero changes. Enumeration follows all pages and deduplicates IDs; exceeding the cap fails without writes rather than truncating.

Dry run makes no writes and reports a deterministic plan hash over target, selectors, membership, states, and reference diagnostics. Execution resolves a fresh plan. A supplied expected hash must match before writes; otherwise the preview is informational.

### Safety and state changes

For activation, resolve required connection references across the target environment. Missing, inactive, unbound, or unreadable dependencies block preflight for the whole selection. A valid binding does not prove credentials, licensing, DLP, child-flow dependencies, or runtime connector health. Disabling does not require healthy bindings.

The shared environment-confirmation service enforces read-only and protected-target acknowledgement in the service layer. Allowed switching is not write acknowledgement. Freeze execution target and IDs. Re-read each flow and preconditions before its write.

Enable sets state/status 1/2; disable sets 0/1. Already matching state is successful `unchanged`. Process sequentially in deterministic ID order, poll readback to a bounded deadline, and stop on the first runtime failure, cancellation, stale precondition, or unknown outcome. Never blindly retry an ambiguous write or roll back earlier changes. Disabling does not cancel existing runs.

### Results

Return target, selection, requested state, plan hash, dry-run, counts, eligible per-flow results, excluded entries, and safe diagnostics. Per-flow states distinguish would-change, unchanged, blocked, succeeded, failed, unknown, cancelled, and not-attempted. Preserve completed/uncertain results when later work fails. Success requires every eligible item succeeded/unchanged, or a dry run without blockers. Other outcomes return nonzero. stdout is data; stderr is progress/diagnostics.

## Acceptance criteria

| ID | Criterion | Test | Status |
|---|---|---|---|
| AC-01 | Exact selectors, unique-name ambiguity, solution constraints, supported category/type; no writes on invalid selection. | `StateCommandTests.InvalidSelectionFailsParsingBeforeAuthentication`; `FlowActivationReaderTests.Get_AmbiguousUniqueNameDoesNotChooseFirst`; `FlowActivationServiceTests.Plan_SingleFlowMembershipAndSolutionAreRequired` | Automated passing |
| AC-02 | Correct enable/disable transitions, idempotent no-op, verified target state. | `Execute_WritesStateStatusAndVerifiesReadback`; `Execute_AlreadyMatchingIsUnchangedAndDoesNotWrite` in `FlowActivationServiceTests`; sandbox roundtrip pending | Automated passing; live pending |
| AC-03 | Complete multi-page batch, solution isolation, deduplication, visible exclusions, cap checked before writes. | `FlowActivationReaderTests.List_FollowsPagingCookieDeduplicatesAndReleasesEachLease`; `Plan_FiltersDefinitionsReportsExclusionsAndNeverWritesOrConfirms`; `Plan_BatchLimitReportsFullCountWithoutTruncationOrWrites` | Automated passing |
| AC-04 | Write-free dry run, deterministic expected-plan check, shared read-only/protected target enforcement. | `Execute_ExpectedPlanMismatchPreventsWrites`; `Execute_ReadOnlyRejectsActualEntryPointEvenForNoopButAllowsDryRun`; `Execute_GuardFailureStopsBeforeWrite` | Automated passing |
| AC-05 | Specific missing/inactive/unbound/malformed dependency diagnostics; disable remains possible. | `Plan_KnownReferenceBlockerPreventsEntireBatch`; `Plan_UnparseableDependenciesBlockActivationButNotDisable`; `Plan_UnreadableReferenceIsPerFlowBlockerAndRetainsEntirePlan`; parser matrix in `FlowActivationReaderTests` | Automated passing |
| AC-06 | Zero writes for any known preflight blocker; runtime failure/cancellation stops remaining writes and preserves outcomes. | `Execute_RuntimeFailurePreservesEarlierSuccessAndStopsRemainingWrites`; `Execute_PreconditionChangeStopsBeforeSubmission`; `Execute_CancellationAfterSubmissionIsUnknownAndDoesNotWriteNextFlow` | Automated passing |
| AC-07 | Bounded verification and honest ambiguous outcomes; no blind mutation retries or rollback. | `Execute_VerificationDeadlineCancelsBlockedReadAndReportsUnknown`; `Execute_LateReadCannotWinDeadlineOrCancellation`; `Execute_AcceptedWriteFollowedByReadFaultIsUnknownNotRejected`; `Execute_ServerRejectionIsFailedAndPreservesFaultReason` | Automated passing |
| AC-08 | CLI structured output, exit codes, progress, cancellation and rerun contracts. | `StateCommandTests.InvocationRoutesTargetAndOptionsAndKeepsProgressOffStdout`, `ProviderFailureProducesStructuredErrorAndNonzeroExit`, `PartialResultRetainsDataAndNonzeroExit`; service cancellation/rerun tests; credentialed CLI subprocess pending | Automated passing; subprocess live pending |
| AC-09 | Harmless managed/unmanaged live single/batch fixtures with isolation and cleanup evidence. | Requires authorized sandbox fixtures | Pending |

## Platform references

- [Cloud flows through Dataverse](https://learn.microsoft.com/en-us/power-automate/manage-flows-with-code)
- [Workflow state/status metadata](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/reference/entities/workflow)
