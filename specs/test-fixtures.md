# Test fixtures

**Status:** Daemon lifecycle safeguards implemented and validated offline
**Code:** tests/PPDS.Cli.DaemonTests/ | tests/PPDS.LiveTests/Infrastructure/FixtureProcessInterruption.cs
**Surfaces:** Tests

## Daemon lifecycle

Daemon fixtures bound asynchronous readiness, termination, stream disposal and stderr-drain waits. One cleanup worker owns the process and RPC stream. Caller timeouts do not allow a second worker to dispose shared resources or delete retained evidence. A late worker can finish serial cleanup while preserving the original failure and retained directory.

The fixture drains stderr without capturing its content. Diagnostics omit arguments, output and remote free text. An uncertain process outcome prevents fixture reuse and local-directory deletion. Confirmed parent exit is not proof that descendants stopped or that remote operations rolled back. Synchronous operating-system and filesystem operations have no hard real-time guarantee.

These fixture changes do not authorize live access, activate suites or change required workflow gates. General plugin cleanup and fixture-owned live export contracts remain outside this implementation.

## Acceptance criteria

| ID | Criterion | Test | Status |
|---|---|---|---|
| AC-01 | Asynchronous readiness, shared process stop, stream disposal and stderr drain have bounded caller waits, including ignored cancellation, blocked calls, endless synchronous output and reentry. One cleanup worker retains resource ownership until completion. | `DaemonTestFixtureTests.IgnoredReadinessCancellationIsBoundedAndRetainsEvidence`, `IgnoredStopCancellationUsesSharedDeadlineAndRefusesReuseDuringStop`, `AsyncStreamDisposalThatFailsOrIgnoresCancellationCannotSkipStop`, `StderrDrainIgnoringCancellationIsBoundedAfterParentExit`, `ContinuousSynchronousStderrCannotMonopolizeStartupOrIgnoreDisposalCancellation`, `BlockingDisposalInvocationIsBoundedAndCannotReenterSharedResources`, `InlineStartupCancellationPublishesOneDisposalBeforeReentry`, `RpcLeaveOpenPipesKeepExactlyOneStreamDisposerUntilLateCleanupFinishes` | ✅ Offline |
| AC-02 | Primary failures and stack evidence survive cleanup/reporting faults; diagnostics omit secret markers. Uncertain termination retains the owned directory and refuses reuse. Normal local RPC, drain and disposal paths remain covered. | `DaemonTestFixtureTests.FirstFailureAndStackSurviveCleanupAndEvidenceFailuresWithoutSecretDisclosure`, `StderrReadFailureIsSecretSafeAndPreventsFurtherUse`, `NativeChildStderrIsDrainedWithoutCapturingSecretOutput`, `ReadinessUsesRealLocalRpcAndNormalDisposalDeletesOnlyOwnedDirectory` | ✅ Offline |
