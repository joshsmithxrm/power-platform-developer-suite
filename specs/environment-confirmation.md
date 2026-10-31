# Environment confirmation

**Status:** Implementing
**Code:** src/PPDS.Cli/Services/Environment/ src/PPDS.Mcp/Infrastructure/

## Contract

Selected mutating application services enforce target acknowledgement independently of their surface. Reads and dry runs require no acknowledgement. Read-only sessions reject every write. Effective Production/Test protection requires acknowledgement; Unknown without a configured override fails protected through WriteProtectionResolver. A supplied mismatched URL is always rejected. CLI uses `--confirm-env <url>`, distinct from operation-level confirmation; MCP uses `confirmEnvironment`, distinct from allowed environment switching.

Each operation captures an immutable resolved target, authenticated identity and session. The connection pool and guard use that same target. Acknowledgement is memory-only, scoped to session/identity/target/current policy. Missing context fails closed. Changing an external active profile cannot redirect a running MCP session. Environment switches affect new calls and do not dispose pools used by in-flight calls.

Coverage in this increment: every FormService writer and the flow activation service. Other domains retain their existing guards. This is not a claim of complete #1015 coverage.

## Acceptance criteria

| ID | Criterion | Test | Status |
|---|---|---|---|
| AC-01 | Protected/unknown/development/override/read-only/mismatched targets follow policy and denied operations do not write. | EnvironmentConfirmationServiceTests | Pending |
| AC-02 | Acknowledgement is isolated by session, identity, target and policy; concurrent calls cannot cross-authorize. | EnvironmentConfirmationServiceTests | Pending |
| AC-03 | MCP captures startup identity/target; allowed switching changes actual new request targets while overlapping calls retain theirs. | McpToolContextTests | Pending |
| AC-04 | All selected service entry points fail closed without context and preserve operation-level guards. | FormServiceConfirmationTests; FlowActivationServiceTests | Pending |
| AC-05 | CLI/MCP return structured errors and allowed-env alone never confirms writing. | FormsCommandTests; FormsToolsTests | Pending |
| AC-06 | Cancellation, progress and resource ownership propagate through service and adapter boundaries. | FormAuthoringTests; McpToolContextTests | Pending |
