# Isolated form fixtures (#1240)

Status: **live roundtrip passed on 2026-10-31**. Josh approved the exact target and inventory below. Run `4f9c2a71` passed in 8 minutes 23 seconds against candidate `c0394de1a` (product source `6932dcd52`), including unmanaged/managed MCP edits, explicit publication, locked-form rejection and cleanup. The manifest records no cleanup failures or unknown submissions. Local SDK-fake tests alone are not evidence of Dataverse managed behavior.

`timeline-form.xml` is reconstructed from the Timeline fragment in issue #1465, matching `TimelineFormFixture.Xml` in the unit suite. It is not a customer export. It includes modern root attributes, an unbraced section ID, a conventional field and Timeline JSON. The live harness substitutes its own primary column name. Managed provenance comes from exporting the newly created source solution with `Managed=true`, deleting its own unmanaged source components, and importing that exact package. The export and resource ledger are retained locally.

## Proposed release validation inventory

Target: `https://orgcabef92d.crm.dynamics.com`. Explicit profile: `PPDS` (or `PPDSDevRefresh` only if chosen for the approved run). Active-profile selection does not change. Fixed proposed run ID: `4f9c2a71`. Reusing an artifact directory or colliding with existing fixture names is rejected before mutation.

| Resource | Exact name / scope |
|---|---|
| Publisher | `PPDSFormsPublisher_4f9c2a71`, prefix `pfr4f9c` |
| Source solution | `PPDSFormsSource_4f9c2a71` version 1.0.0.0; exported then reimported as managed |
| Association solution | `PPDSFormsChanges_4f9c2a71` version 1.0.0.0, unmanaged |
| Tables | `pfr4f9c_editable`, `pfr4f9c_locked`; notes enabled; no business records |
| Columns on each | `pfr4f9c_name`, `pfr4f9c_unmanagedprobe`, `pfr4f9c_managedprobe`, plus platform system columns |
| Main forms | One platform-generated form per table; IDs recorded. Editable form customizable; locked form non-customizable. |
| Section | `44444444-4444-4444-4444-444444444444`, scoped by selected form ID |
| Publication | Entity-scoped PublishXml for the two new tables only; no PublishAll |
| Local artifacts | `<artifact-root>/forms-4f9c2a71/manifest.json`, `managed-fixture.zip`, unmanaged/managed roundtrip JSON |

## Sequence and cleanup

1. Capture explicit profile and URL in the normal MCP context. Confirm the exact target through the shared guard. Reject existing solutions/publisher and tables using the reserved prefix before any write.
2. Create publisher, source solution, two tables, probe columns and fixture XML. Associate their Main forms and publish only each fixture table for baselines.
3. Through a real MCP JSON-RPC client/server connection, list/get the editable unmanaged form, dry-run, add the unmanaged probe, read back, verify Timeline preservation and no implicit publication, retry idempotently, then explicitly publish/verify.
4. Export as managed. Verify each table's returned metadata ID and membership in this run's publisher/source solution, delete those tables while that proof remains available, then delete the recorded source solution. Import the exact local export. Record package hash, import job ID and the validated managed solution ID/version/publisher after a successful synchronous import. This transition touches only confirmed run-owned resources.
5. Create the unmanaged association solution. Repeat the MCP journey against the managed-customizable form using the second probe. Verify the managed-locked form rejects the edit and retains its draft fingerprint.
6. In `finally`, remove the recorded association solution, verified surviving unmanaged tables, recorded source solution (uninstalling managed components), then publisher. Require exact returned IDs plus expected names/publisher relationships before deletion. If a table cannot be verified, retain its source solution to preserve ownership evidence. Unknown create/import outcomes are recorded as manual recovery, never adopted or deleted by name. Cleanup errors and unknown submissions fail the test and stay in the manifest; no dependency expansion or wildcard deletion.

The test is `Category=DestructiveE2E` and also skips without `PPDS_FORMS_LIVE_CONFIRM`. Execution is bounded to 20 minutes, cleanup has a fresh five-minute token. No business records or flow executions are created. If a platform submission has an uncertain outcome, inspect the manifest and reserved names before another run.

## Approved-run command

Run only after explicit approval of the inventory above. The recorded run is complete; use a fresh artifact root and repeat collision checks for any separately authorized rerun:

```powershell
$env:PPDS_FORMS_LIVE_URL = 'https://orgcabef92d.crm.dynamics.com'
$env:PPDS_FORMS_LIVE_CONFIRM = $env:PPDS_FORMS_LIVE_URL
$env:PPDS_FORMS_LIVE_PROFILE = 'PPDS'
$env:PPDS_FORMS_LIVE_RUN_ID = '4f9c2a71'
$env:PPDS_FORMS_LIVE_ARTIFACTS = '<absolute reviewed local artifact root>'
dotnet test tests/PPDS.LiveTests/PPDS.LiveTests.csproj -f net10.0 --filter FullyQualifiedName~FormsManagedRoundTripLiveTests --logger 'console;verbosity=normal' -m:1 -p:UseSharedCompilation=false
```

Credentials are not placed in arguments or artifacts. Shared `PPDS_TEST_SOLUTION` / `PPDS_TEST_PREFIX` variables are not needed. Live acceptance criteria remain pending until validation and cleanup both succeed. A skipped test is not a pass. Maker designer inspection is an optional additional check.

## Managed view coverage inventory

#1240 also covers views. Existing `ViewServiceTests` exercise managed server rejection (`SetFetchXml_ManagedView_WriteRejected_ThrowsManagedComponentNotEditableWithGuidance`), managed readback failure (`SetFetchXml_ReadBackFails_ReportsUnverifiedWrite`), and the required `returnedtypecode` payload (`SetFetchXml_UpdatePayload_IncludesReturnedTypeCode`). The persistence and silently-dropped-write tests now run for both managed and unmanaged records (`SetFetchXml_WritePersists_Succeeds`, `SetFetchXml_WriteSilentlyDropped_ThrowsUpdateNotPersisted`). These are SDK fakes, not live managed exports.

The form fixture's source solution includes the platform-generated views of its two new tables, giving a reproducible managed-view source without relying on organization-specific business tables. The approved #1240 scope reconciles this existing managed-view unit coverage and requires the live managed FORM roundtrip. An additional live view mutation/readback check is optional; it is not an extra release gate. If separately approved, select and record a view ID from the run-owned tables and keep the same ownership/cleanup constraints.
