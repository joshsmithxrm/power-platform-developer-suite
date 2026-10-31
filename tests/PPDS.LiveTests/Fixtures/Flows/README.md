# Opt-in flow activation release fixture

Status: attempt 2 passed managed and unmanaged single-flow, idempotent and batch activation checks (20 CLI JSON artifacts), then failed while creating the blocker fixture. The missing-binding live gate remains pending. The harness drives the reviewed candidate CLI as an actual subprocess and validates its JSON stdout and progress stderr. Authentication uses an explicit existing profile; no active-profile selection changes are made.

`manual-compose.json` is derived from the workflow/clientdata contract documented in [Manage cloud flows with code](https://learn.microsoft.com/en-us/power-automate/manage-flows-with-code). Every fixture has exactly one manual Button trigger and one built-in Compose action. The trigger is never invoked. There are no HTTP actions, schedules, Dataverse triggers or connector actions. Enabling these fixtures does not initiate a run. Disabling is not a promise to cancel prior runs; none are deliberately started.

## Exact proposed inventory

Target `https://orgcabef92d.crm.dynamics.com`, explicit profile `PPDS`, run ID `6d83a2e4`:

| Resource | Name |
|---|---|
| Publisher | `PPDSFlowsPublisher_6d83a2e4`, prefix `pfl6d83` |
| Source solution | `PPDSFlowsSource_6d83a2e4` version 1.0.0.0 |
| Outside solution | `PPDSFlowsOutside_6d83a2e4` version 1.0.0.0 |
| Source definitions | `PPDS flow 6d83a2e4 A`, `PPDS flow 6d83a2e4 B` |
| Outside definition | `PPDS flow 6d83a2e4 outside` |
| Preflight blocker | `PPDS flow 6d83a2e4 blocker`, created in the outside solution after successful managed/unmanaged checks |
| Assigned workflow unique names | `pfl6d83_6d83a2e4_a`, `pfl6d83_6d83a2e4_b`, `pfl6d83_6d83a2e4_outside`, `pfl6d83_6d83a2e4_blocker` |
| Declared missing reference | `pfl6d83_missing`; no connection/reference resource or connector operation is created |
| Artifacts | `<artifact-root>/flows-6d83a2e4/manifest.json`, `managed-fixture.zip`, numbered CLI JSON/stderr records |

The blocker adds one unused connection-reference mapping to the same harmless definition; no action uses it. If Dataverse rejects this draft fixture, the harness records/fails that platform constraint. It never introduces a business connector action to force acceptance.

The manifest records the primary exception independently from cleanup. A typed Dataverse `OrganizationServiceFault` returned by Create records an explicit rejection, including code, message and activity ID in `rejectedSubmissions`; timeouts, cancellation and transport errors remain `unknownSubmissions` and retain solution/publisher recovery evidence. Arbitrary SDK error details and credentials are not serialized. Cleanup diagnostics cannot replace the original exception or its stack. A rejection still fails validation; it is not treated as evidence that the blocker gate passed.

## Sequence

1. Confirm exact target through the shared environment guard, reject collisions on both display names and assigned workflow unique names, and check target metadata permits creating `workflow.uniquename` before provisioning. Create the publisher, two solutions and three harmless definitions with deterministic run-owned unique names. Store each returned ID immediately; verify its assigned unique name before recording that selector, then verify it again after solution association.
2. Unmanaged source: CLI dry run, single enable using the expected plan hash, idempotent rerun by unique name, disable; then solution batch enable/disable. Before every execution, require the plan's IDs to exactly equal the confirmed fixture IDs, reject unexpected excluded entries, and recheck harmless definitions/membership. Verify result IDs, source states and that the outside definition stays unchanged. Parse stdout as one JSON document and require progress on stderr.
3. Require the complete source solution component inventory to contain exactly the two owned workflows, export it as managed, then verify the package's root component IDs. Verify exact workflow IDs/names and source membership before deleting the two source definitions and original source solution. Import the exact recorded package. `PublishWorkflows=false` applies to classic workflows and does not guarantee cloud flows import disabled: every imported fixture is checked Off before candidate execution; unexpected state fails and cleanup handles confirmed harmless IDs. Record package hash/import job and validate successful imported solution ownership/version before adopting its ID. Resolve imported members by recorded unique names, validate the harmless definitions and repeat the same checks.
4. Create the draft blocker in the outside solution. Dry-run and execution must both fail preflight with zero state changes for either outside definition. Do not execute or activate the blocker directly.
5. Cleanup disables only confirmed harmless fixture IDs if needed, deletes confirmed unmanaged definitions, deletes the confirmed source/outside solution IDs (uninstalling managed source components), then the publisher. It verifies exact IDs, names and solution/publisher relationships first. Unknown creation/import outcomes retain evidence for manual recovery; they are never adopted by name or labeled cleaned up. No wildcard deletion or dependency expansion.

The test is `Category=DestructiveE2E` and skips without explicit confirmation. The main run is bounded to 20 minutes, each child process to two minutes, and cleanup to five minutes. It does not publish a package, merge code, invoke a trigger or change any existing business flow.

If a dispatched CLI command is interrupted or its response cannot be verified, the harness attempts to stop it and waits up to 30 seconds using a fresh token. It then saves the original failure and process-exit evidence. Every such command has an **unknown remote outcome**, even when process exit is confirmed: termination cannot roll back an in-flight Dataverse request. Both flow harnesses suppress **all** Dataverse cleanup writes in this case and retain their exact fixture IDs for manual recovery. A dry-run interruption uses the same conservative policy. The ledger is saved after the bounded stop attempt; exit confirmation is not a successful validation result.

## Approved-run command

Only after approval of the inventory above, supply the explicit reviewed candidate assembly and local artifact root:

```powershell
$env:PPDS_FLOWS_LIVE_URL = 'https://orgcabef92d.crm.dynamics.com'
$env:PPDS_FLOWS_LIVE_CONFIRM = $env:PPDS_FLOWS_LIVE_URL
$env:PPDS_FLOWS_LIVE_PROFILE = 'PPDS'
$env:PPDS_FLOWS_LIVE_RUN_ID = '6d83a2e4'
$env:PPDS_FLOWS_LIVE_CLI_ASSEMBLY = '<absolute candidate checkout>/src/PPDS.Cli/bin/Release/net8.0/ppds.dll'
$env:PPDS_FLOWS_LIVE_ARTIFACTS = '<absolute reviewed artifact root>'
dotnet test tests/PPDS.LiveTests/PPDS.LiveTests.csproj -f net10.0 --filter FullyQualifiedName~FlowActivationRoundTripLiveTests --logger 'console;verbosity=normal' -m:1 -p:UseSharedCompilation=false
```

Successful fixture creation is not a release pass. Require the CLI checks, state readback and cleanup evidence to succeed. A skipped test is not a pass. No credentials appear in subprocess arguments or the artifact manifest.

## Unique-name provenance and retry evidence

The first approved attempt on `6d83a2e4` stopped during fixture setup: Dataverse returned a null `workflow.uniquename` after Create. No candidate CLI activation command ran; the confirmed flow, both solutions and publisher were cleaned up with no unresolved submissions. Microsoft documents [`UniqueName` as optional and writable](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/reference/entities/workflow#uniquename); the minimal cloud-flow create example does not assign it. Automatic generation is therefore not a fixture contract.

The revised harness assigns the deterministic names listed above in Create, checks `IsValidForCreate` in target metadata before any provisioning, and verifies readback both before and after `AddSolutionComponent`. A failed assignment cannot prevent cleanup of a confirmed own ID: its stored unique-name selector remains empty until the exact assigned value is verified. Display names, IDs returned by Dataverse, manual Button/Compose definitions, target and cleanup scope are otherwise unchanged.

Preserve the first attempt's artifacts. After confirmed cleanup and repeated collision checks, retry the same approved run ID with `PPDS_FLOWS_LIVE_ARTIFACTS` pointing to a new `attempt2` subdirectory beneath the approved artifact root. The harness still refuses to overwrite its run directory; it does not infer permission from an old manifest.

## Attempt 2 failure evidence

Attempt 2 created and round-tripped the harmless flows successfully, including all 20 candidate CLI plan/execution calls. Blocker creation then threw before any blocker CLI invocation. The original harness incorrectly replaced that exception with its final unresolved-submission assertion, so the precise server reason is unavailable from the attempt 2 log. An absent-reference platform restriction is a hypothesis, not an established diagnosis.

The confirmed outside flow was deleted and the two managed flows were left Off. Source/outside solutions and publisher were retained for recovery because the create outcome was recorded as unknown. Recovery must verify the manifest's exact IDs, package hash/component inventory, complete publisher ownership and absence of the blocker; it must never adopt rows by name. Preserve all attempt 2 evidence. Only after verified cleanup and fresh absence checks may the coordinator perform a controlled diagnostic run of the same approved inventory in a new `attempt3` artifact directory. Adding an unbound connection-reference resource would change the approved inventory and requires a separate reviewed amendment.

Offline regression coverage: `FixtureFailureEvidenceTests.DataverseFault_RecordsAuthoritativeRejectionWithoutArbitraryErrorDetails`, `TransportTimeoutAndCancellation_KeepSubmissionUnknown`, `CleanupFailure_DoesNotReplacePrimaryExceptionOrItsOriginalStack`, and `UnknownSubmissionWithoutPrimaryFailure_StillFailsValidation`. These tests do not authenticate or call Dataverse.

## Attempt 3 diagnosis

After verified attempt 2 recovery, the coordinator ran a controlled attempt 3 with the same approved inventory and fresh collision checks. All 20 candidate CLI artifacts again passed. Blocker creation returned explicit Dataverse fault `0x80095005`: the named connection reference did not exist. The corrected harness preserved this fault and completed cleanup with empty failures/unknown lists. This establishes the provisioning constraint; the missing-binding live gate remains incomplete.

A [separate proposed binding amendment](binding-amendment.md) uses one new unbound connection-reference metadata row and two harmless manual flows. It requires separate approval before execution and does not repeat the successful managed/unmanaged roundtrip.
