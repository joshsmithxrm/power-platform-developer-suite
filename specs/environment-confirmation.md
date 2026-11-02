# Environment confirmation

**Status:** Implemented; live fixture validation pending
**Code:** src/PPDS.Cli/Services/Environment/ src/PPDS.Mcp/Infrastructure/

## Contract

Selected mutating application services enforce target acknowledgement independently of their surface. Reads and dry runs require no acknowledgement. Read-only sessions reject every write. Effective Production/Test protection requires acknowledgement; Unknown without a configured override fails protected through WriteProtectionResolver. A supplied mismatched URL is always rejected. CLI uses `--confirm-env <url>`, distinct from operation-level confirmation; MCP uses `confirmEnvironment`, distinct from allowed environment switching.

Each operation captures an immutable resolved target, authenticated identity and session. The connection pool and guard use that same target. Acknowledgement is memory-only, scoped to session/identity/target/current policy. Missing context fails closed. Changing an external active profile cannot redirect a running MCP session. Environment switches affect new calls and do not dispose pools used by in-flight calls.

Coverage: every FormService writer, the flow activation service, and all nine existing ViewService writers. Other domains retain their existing guards. This is not a claim of complete #1015 coverage. View CLI acknowledgements are distinct from target selection and entity-wide publication; the original read-service constructor fails closed for writes without context.

## Acceptance criteria

| ID | Criterion | Test | Status |
|---|---|---|---|
| AC-01 | Protected/unknown/development/override/read-only/mismatched targets follow policy and denied operations do not write. | EnvironmentConfirmationServiceTests.PolicyMatrix, Mismatch_IsRejectedEvenForDevelopment, ReadOnly_CannotBeAcknowledgedAndDoesNotReadPolicy, MissingContext_FailsClosed | ✅ |
| AC-02 | Acknowledgement is isolated by session, identity, target and policy; concurrent calls cannot cross-authorize. | EnvironmentConfirmationServiceTests.Acknowledgement_DoesNotCrossIdentitySessionOrTarget, PolicyChange_InvalidatesPriorAcknowledgement, ConcurrentAcknowledgements_DoNotAuthorizeAnotherTarget | ✅ |
| AC-03 | MCP captures startup identity/target; allowed switching changes actual new request targets while overlapping calls retain theirs. | McpSessionSnapshotTests.StartupSnapshot_DoesNotFollowActiveProfileOrReturnedObjectChanges, InFlightPoolRequest_UsesCapturedTargetWhileLaterRequestUsesSwitch_WithoutGlobalMutation, AuthWho_AndProviderUseSameOverrideAndSwitchedTarget_WithTruthfulMetadata | ✅ |
| AC-04 | All selected service entry points fail closed without context and preserve operation-level guards. | FormConfirmationCoverageTests.EveryExistingWriter_FailsClosedWithoutContext_BeforeClientAcquisition; FormAuthoringTests.ReadOnly_DryRunPlansWithoutAcknowledgementOrAnyWrites_ActualNoOpRejected; FlowActivationServiceTests.Execute_ReadOnlyRejectsActualEntryPointEvenForNoopButAllowsDryRun, Execute_GuardFailureStopsBeforeWrite | ✅ combined Release matrix |
| AC-05 | CLI/MCP return structured errors and allowed-env alone never confirms writing. | FormsCommandGroupTests.EveryMutation_ExposesExactEnvironmentAcknowledgement_SeparateFromPublication; FormsProtocolTests.ClientReceivesStructuredGuardError_AndInvalidInputNeverCallsService; EnvironmentConfirmationServiceTests.PolicyMatrix | ✅ |
| AC-06 | Cancellation, progress and resource ownership propagate through service and adapter boundaries. | FormAuthoringTests.CancelledBeforePlanning_PropagatesCancellation_AndDoesNotAcquireClient; FormsProtocolTests.ClientListsBoundedTools_AndCallsReadDraftPublishJourneyWithProgress; McpSessionSnapshotTests.AuthWho_AndProviderUseSameOverrideAndSwitchedTarget_WithTruthfulMetadata | ✅ |
| AC-07 | All nine existing view writers and CLI adapters reuse the same scoped guard; reads retain access, denied/cancelled/shakedown writes acquire no client, and legacy construction cannot bypass confirmation. | ViewConfirmationCoverageTests.EveryWriter_FailsClosedBeforeClientAcquisition, EveryWriter_ShakedownAndCancellationStopBeforeClientAcquisition, LegacyConstructor_PreservesSignatureAndReadAccess; ViewsCommandGroupTests.EveryMutation_ForwardsAcknowledgementAndOptions, EveryMutation_RejectsMismatchedTargetWithStructuredError | ✅ |

Local validation uses SDK fakes and real in-process MCP JSON-RPC transports. The separate approved live form fixture roundtrip passed on 2026-10-31 with completed cleanup, documented in `tests/PPDS.LiveTests/Fixtures/Forms/README.md`. Flow live validation remains separately tracked in the flow specification.
