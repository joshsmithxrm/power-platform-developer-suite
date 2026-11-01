# Missing-binding live validation fixture

Status: **approved and passed on 2026-11-01, fresh attempt 2; cleanup verified**. The approved inventory added one unbound connection-reference metadata row to the earlier fixture resource types. This separate test did not repeat the already-passed managed/unmanaged activation roundtrip. Future execution still requires approval of the exact target and inventory before setting opt-in variables.

## Evidence and purpose

The original run `6d83a2e4`, attempt 3, passed all 20 managed/unmanaged CLI plan/execution checks and then received explicit Dataverse rejection `0x80095005`: the declared `pfl6d83_missing` reference did not exist. Cleanup completed with no unknown submissions or errors. The activation product was never called for that rejected blocker. That original failed run did not establish the missing-binding portion of flow acceptance criterion AC-09; the separate passing fixture below now supplies that evidence.

This independent fixture tests **an existing but unbound reference**. Microsoft documents `connectionid` as optional and `connectorid` as application-required in the [connectionreference table contract](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/reference/entities/connectionreference). The approved target accepted the row without a connection ID and the candidate reported the dependency as `unbound`. If creation is rejected, preserve the exact fault and stop without changing the definition or expanding resources.

## Recorded validation

The fresh `6d83b1f0` attempt 2 passed `FlowBindingPreflightLiveTests.CandidateCli_UnboundReference_BlocksWholeBatchWithoutStateWrites`: 1/1 tests, 34.2999 seconds. Product source was `1cba826c6ad6233e41b0d4d5197d0c7c618cd4c7`. The executed immutable copy of the Release CLI DLL had SHA-256 `2990982BD7F56DC66A27F4BAFCDFC565EDD845C68B82785F4835BE45EA89CFDA`.

Plan and execution selected exactly the two confirmed flow IDs, returned `Flow.PreflightBlocked`, and identified the reference as `unbound`. The control was `would-change` in preview and `not-attempted` in execution; the blocker remained `blocked`. Both flows stayed Off and their version numbers were unchanged. The manifest reached `state: verified` and `cleanup: completed`; all five returned resource IDs were recorded as `verified-deleted`, `confirmedIds` was empty, and primary failure, CLI interruption, unknown/rejected submissions and cleanup failures were absent.

Retained evidence: `live-validation-1cba826c6-attempt2/flow-binding-6d83b1f0/manifest.json`, `01-plan.json`, `02-execute.json`, paired stderr files, and `flow-binding-live-1cba826c6-attempt2.log`. The first binding attempt at this same product pin passed the behavioral assertions but failed automatic cleanup with a Dataverse transaction error. Its failed result and recovery evidence are preserved separately; this fresh passing attempt does not change that history or the earlier absent-reference provisioning failure.

## Exact approved inventory

Target: `https://orgcabef92d.crm.dynamics.com`, existing profile `PPDS`, fixed run ID `6d83b1f0`.

| Resource | Exact identity |
|---|---|
| Publisher | `PPDSFlowsBindingPublisher_6d83b1f0`; prefix `pfl6d83`; option-value prefix `78123` |
| Unmanaged solution | `PPDSFlowsBinding_6d83b1f0`, version `1.0.0.0` |
| Control flow | Display name `PPDS flow 6d83b1f0 control`; unique name `pfl6d83_6d83b1f0_control` |
| Blocker flow | Display name `PPDS flow 6d83b1f0 blocker`; unique name `pfl6d83_6d83b1f0_blocker` |
| One connection-reference row | Logical name `pfl6d83_6d83b1f0_unbound`; display name `PPDS unbound reference 6d83b1f0` |
| Connector identifier | `/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps` (metadata string naming the existing Dataverse connector) |
| Connection binding | `connectionid` omitted; readback must be empty, active and unmanaged |
| Artifacts | `<approved-artifact-root>/flow-binding-6d83b1f0/manifest.json`, `01-plan.json`, `02-execute.json`, paired stderr files |

Each flow has exactly one manual Request/Button trigger and one built-in Compose with the fixed text `PPDS isolated activation fixture`. The blocker additionally declares the unbound reference in `clientdata.properties.connectionReferences`; no action uses it. Neither trigger is invoked. No connection, credential, custom connector, business action, schedule or event trigger is created. There is no export/import or publication in this fixture.

The connection-reference row is created with the [documented `CreateRequest["SolutionUniqueName"]` parameter](https://learn.microsoft.com/en-us/dotnet/api/microsoft.crm.sdk.messages.addsolutioncomponentrequest?view=dataverse-sdk-latest) and its exact returned ID must appear in the fixture solution. This avoids assuming a numeric component type for a newer component. Flow membership uses type 29 and `AddRequiredComponents=false`.

## Execution and pass criteria

1. Require exact URL confirmation, fixed run ID, explicit candidate DLL path and reviewed SHA-256. Reject all publisher/solution names and both display/unique flow names or reference names already present. Verify target metadata permits the required attributes before provisioning.
2. Create the publisher, solution, unbound reference, control flow and blocker flow. Record each returned GUID immediately. Unknown create outcomes retain evidence and are never adopted by name. Verify publisher ownership, complete solution component inventory, harmless definitions, both flows Off and the reference unbound.
3. Run the real candidate subprocess `flows enable --all --solution PPDSFlowsBinding_6d83b1f0 --max-flows 2 --dry-run`. Require exactly the two confirmed flow IDs, no excluded entries, `Flow.PreflightBlocked`, control `would-change`, blocker `blocked` with `Flow.ConnectionReferenceBlocked` and reference status `unbound`.
4. Recheck complete ownership, definitions, unbound reference and both Off states; compare workflow version numbers to the baseline. Run the same batch without dry-run using the exact plan hash. Require failure before state mutation: control `not-attempted`, blocker `blocked`, both still in `preflight`. Verify both Off and unchanged workflow version numbers again.
5. Cleanup only exact returned IDs: delete the verified harmless flows (disable only if unexpectedly active), delete the verified still-unbound reference, verify the solution empty before deleting it, then verify the publisher owns no solutions before deleting it. Read back absence after each deletion. Unknown creates or any cleanup failure retain solution/publisher evidence for manual review. Never expand dependencies, retry an uncertain mutation, or delete a colliding/name-discovered resource.

The main test is bounded to 10 minutes, each CLI subprocess to two minutes and cleanup to five minutes. The primary fault is saved before cleanup and rethrown afterward; SDK rejection and unknown transport outcomes are recorded separately. Successful cleanup alone does not pass the live gate.

An interrupted dispatched CLI or unverifiable response triggers a bounded 30-second process-stop attempt, followed by saving the primary failure and exit evidence. Its remote outcome remains unknown even if the process exits. All Dataverse cleanup writes are suppressed, including flow disable/delete, reference deletion, solution deletion and publisher deletion. Retain exact confirmed IDs and obtain a separately reviewed recovery procedure; do not assume termination rolled back an in-flight request. This also applies conservatively to dry-run interruptions.

## Approved execution only

The coordinator supplies the reviewed candidate hash and artifact root after approval. The candidate DLL is the root integration checkout's `src/PPDS.Cli/bin/Release/net8.0/ppds.dll`; the harness is compiled separately in Debug and must not rebuild that pinned Release output.

```powershell
$env:PPDS_FLOW_BINDING_LIVE_CONFIRM = 'https://orgcabef92d.crm.dynamics.com'
$env:PPDS_FLOW_BINDING_LIVE_RUN_ID = '6d83b1f0'
$env:PPDS_FLOW_BINDING_LIVE_CLI_ASSEMBLY = '<root-integration-checkout>/src/PPDS.Cli/bin/Release/net8.0/ppds.dll'
$env:PPDS_FLOW_BINDING_LIVE_CLI_SHA256 = '<reviewed 64-character SHA-256>'
$env:PPDS_FLOW_BINDING_LIVE_ARTIFACTS = '<approved absolute artifact root>'
dotnet test tests/PPDS.LiveTests/PPDS.LiveTests.csproj -f net10.0 --no-build --filter FullyQualifiedName~FlowBindingPreflightLiveTests --logger 'console;verbosity=normal'
```

Acceptance mapping: `FlowBindingPreflightLiveTests.CandidateCli_UnboundReference_BlocksWholeBatchWithoutStateWrites` supplies the missing-binding portion of live flow AC-09 when it actually passes and cleanup is verified. The recorded fresh attempt 2 meets that requirement. The earlier managed/unmanaged core phases and cleanup are separate evidence; overall release QA remains a separate gate.
