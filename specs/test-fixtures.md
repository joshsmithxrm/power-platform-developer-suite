# Test fixtures

**Status:** Daemon safeguards implemented; plugin preflight and diagnostics implemented; full plugin ownership remains blocked
**Code:** tests/PPDS.Cli.DaemonTests/ | tests/PPDS.LiveTests/Infrastructure/FixtureProcessInterruption.cs | tests/PPDS.LiveTests/Infrastructure/PluginFixtureCleanup.cs | tests/PPDS.LiveTests/Infrastructure/CliProcessShutdownTests.cs
**Surfaces:** Tests

## Daemon lifecycle

Daemon fixtures bound asynchronous readiness, termination, stream disposal and stderr-drain waits. One cleanup worker owns the process and RPC stream. Caller timeouts do not allow a second worker to dispose shared resources or delete retained evidence. A late worker can finish serial cleanup while preserving the original failure and retained directory.

The fixture drains stderr without capturing its content. Diagnostics omit arguments, output and remote free text. An uncertain process outcome prevents fixture reuse and local-directory deletion. Confirmed parent exit is not proof that descendants stopped or that remote operations rolled back. Synchronous operating-system and filesystem operations have no hard real-time guarantee.

These fixture changes do not authorize live access, activate suites or change required workflow gates. Full plugin ownership and fixture-owned live export contracts remain outside this implementation.

## Plugin preflight and diagnostics

Plugin fixtures inspect the named assembly before entering the test body. Preexisting types or steps, malformed inventory, interrupted operations and other unverified preflight outcomes refuse the body and automatic cleanup. The refusal remains latched even if evidence persistence or reporting fails. Safe recovery evidence retains the assembly name, reason and failure type; adapter messages, stderr, malformed output and exception inner chains are not reported. Teardown reports only validated deletion counts and GUIDs, omitting remote names and warnings. A primary test-body failure remains the primary failure when teardown or reporting also fails.

This is a partial safeguard, not creation ownership. A preexisting empty assembly remains permitted for compatibility and deployment may update its bytes; evidence explicitly records that it is unowned. Main's deployment outcome journal supplies acknowledged or unknown call-level IDs, including successful `upsert` operations, but it does not establish complete current-run creation ownership. The fixture still validates list inventory by names, shapes and counts, without a receipt-bound set of exact current IDs and parent relationships.

Ordinary teardown still invokes `plugins clean`, whose step deletion can enumerate child images and whose parent deletion does not enforce a frozen ownership boundary. A foreign child added after a client-side check can remain outside that check's scope. Full cleanup requires creation-specific receipts, current relationship validation and a server-enforced cascade boundary; these remain unresolved. Passing the offline preflight/diagnostic tests does not qualify ordinary plugin cleanup for a live rerun.

## Acceptance criteria

| ID | Criterion | Test | Status |
|---|---|---|---|
| AC-01 | Asynchronous readiness, shared process stop, stream disposal and stderr drain have bounded caller waits, including ignored cancellation, blocked calls, endless synchronous output and reentry. One cleanup worker retains resource ownership until completion. | `DaemonTestFixtureTests.IgnoredReadinessCancellationIsBoundedAndRetainsEvidence`, `IgnoredStopCancellationUsesSharedDeadlineAndRefusesReuseDuringStop`, `AsyncStreamDisposalThatFailsOrIgnoresCancellationCannotSkipStop`, `StderrDrainIgnoringCancellationIsBoundedAfterParentExit`, `ContinuousSynchronousStderrCannotMonopolizeStartupOrIgnoreDisposalCancellation`, `BlockingDisposalInvocationIsBoundedAndCannotReenterSharedResources`, `InlineStartupCancellationPublishesOneDisposalBeforeReentry`, `RpcLeaveOpenPipesKeepExactlyOneStreamDisposerUntilLateCleanupFinishes` | ✅ Offline |
| AC-02 | Primary failures and stack evidence survive cleanup/reporting faults; diagnostics omit secret markers. Uncertain termination retains the owned directory and refuses reuse. Normal local RPC, drain and disposal paths remain covered. | `DaemonTestFixtureTests.FirstFailureAndStackSurviveCleanupAndEvidenceFailuresWithoutSecretDisclosure`, `StderrReadFailureIsSecretSafeAndPreventsFurtherUse`, `NativeChildStderrIsDrainedWithoutCapturingSecretOutput`, `ReadinessUsesRealLocalRpcAndNormalDisposalDeletesOnlyOwnedDirectory` | ✅ Offline |
| AC-03 | Preexisting plugin registrations and unverified preflight outcomes refuse the body and cleanup without adopting ownership; refusal survives evidence/reporting failures and interruption preserves the owned config. | `PluginFixtureCleanupTests.Run_RefusesMatchingPreexistingRegistrationsBeforeTestOrCleanup`, `Run_RefusesUnusablePreflightWithoutTestOrCleanup`, `Run_PreflightFailureSurvivesEvidenceAndReportingFailures`; `CliProcessShutdownTests.PluginCleanupSkipsAfterInterruptedBody` | ✅ Offline |
| AC-04 | Plugin preflight/teardown diagnostics omit adapter/reporter/output secrets, preserve cancellation/timeout outcome and primary body failures, and emit only validated counts/GUIDs. The ordinary empty-preflight path remains exercised without claiming ownership safety. | `PluginFixtureCleanupTests.Run_PreflightPreservesTimeoutOrCancellationOutcomeWithoutSecretMessages`, `Run_PreflightDiagnosticsExcludeStderrAndMalformedOutput`, `Cleanup_ReportsOnlyValidatedCountsAndGuidsOmittingRemoteFreeText`, `Cleanup_RejectsMalformedDiagnosticCountsAndIdentifiers`, `Run_TeardownDiagnosticsOmitAdapterAndReporterSecretsPreservingBodyFailure`, `Run_EmptyPreflightStillExercisesBodyAndOrdinaryTeardown` | ✅ Offline |
