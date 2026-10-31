# Opt-in two-environment notebook demonstration

Status: prepared, not executed. This addresses the approved notebook AC-08/G5 sandbox gate. The deterministic minimum/stable notebook suite remains separate evidence.

The coordinator must approve a plan naming **two distinct sandbox organization URLs** and exact existing named profiles. Two profiles on the same URL do not satisfy this gate. The live assertions also require different returned organization IDs, so aliases of one organization cannot pass.

## What runs

The native VS Code host loads the production notebook controller, serializer, renderer and `DaemonClient`. A test-only daemon bootstrap uses the production `RpcMethodHandler`, JSON-RPC framing, profile identity resolver, connection pool and query engine. It changes only the dependency-injection setup and exposes an allowlisted subset of RPC methods. This is **not** a shipped `ppds serve` entrypoint test; existing real-serve integration evidence must accompany it.

Original v2 profile/environment JSON is read directly, without invoking migration-capable stores. Only approved profile metadata and environment policies are copied into a temporary directory. Native credential/token caches are not copied, cleared or replaced. Ordinary silent authentication may refresh its existing token cache. Original metadata hashes are checked before/after; a change fails validation and is not overwritten. Query-history persistence is disabled. Environment discovery is real, but only approved targets are returned to the host and discovered metadata is saved only to the temporary store.

The harness currently requires `InteractiveBrowser` profiles with usable existing authentication. The bootstrap sets process-only `CI=true` to prevent discovery from opening a browser; the pool receives `beforeInteractiveAuth => Cancel`, preventing new login after silent authentication fails. It does not create credentials, acknowledge device codes or change machine settings.

Only these exact reads are accepted (eight-query hard cap; successful scenario uses six):

```sql
SELECT TOP 1 organizationid, name FROM organization
```

```xml
<fetch top="1"><entity name="organization"><attribute name="organizationid"/><attribute name="name"/></entity></fetch>
```

The server validates the URL/profile pair and captured opaque identity before forwarding each request. DML, TDS, paging, arbitrary queries and mutation RPCs are unavailable. No Dataverse fixtures are created, so no Dataverse cleanup is needed. Local temporary metadata, notebooks, binaries and host state are deleted after evidence capture.

## Scenarios and evidence

1. A starts two SQL cells. Its first real response is held at the client boundary. B runs FetchXML while A remains active. Change the isolated session default/profile, A's binding and notebook focus; release A's real response. A's second cell must still query A with its original profile identity. Compare distinct real organization IDs, native output attribution and record URLs.
2. Hold actual A and B responses at the client boundary, queue another A run, and interrupt A. A's token must be cancelled, its queue discarded and its late result suppressed; B retains its real result. This demonstrates client cancellation ownership, **not cancellation of a deliberately slow server query**.
3. An unbound notebook must query B after the isolated default switch. Save/reload A's changed binding under a fresh URI and verify no profile identity or acknowledgement is persisted.

Production/Unknown prompts remain real. Review and acknowledge only the already-approved read targets in the visible native host. Cancelling a prompt fails this demonstration rather than bypassing it. Binding changes use native workspace metadata edits; picker behavior remains covered by the deterministic suite.

The run is bounded to six minutes in the native host, seven minutes for its daemon and 90 seconds per real query. Evidence records VS Code version, candidate/hash, extension source hashes, host hash, request targets and opaque identities, organization IDs, native outputs, cancellation ownership, original metadata hashes and local cleanup. It does not capture raw auth/VS Code logs or token material.

## Prepare and run

Build the helper separately; it is not part of ordinary CI or automatic live tests:

```powershell
dotnet build tests/PPDS.NotebookLiveHost/PPDS.NotebookLiveHost.csproj -c Release -m:1 -p:UseSharedCompilation=false
```

Prepare an absolute JSON plan path using this shape. The CLI binary must include the optional interactive-auth policy constructor used by the helper; an older candidate is incompatible and must not be represented as validated by this harness.

```json
{
  "confirmation": "read-only-two-environment-notebook-demo",
  "candidateCliDirectory": "<absolute reviewed CLI Release/net8.0 directory>",
  "candidateSha256": "<reviewed ppds.dll SHA256>",
  "hostDirectory": "<absolute tests/PPDS.NotebookLiveHost/bin/Release/net8.0 directory>",
  "credentialConfigDirectory": "<existing PPDS configuration directory>",
  "artifactDirectory": "<new absolute evidence directory for this exact host version>",
  "targets": [
    { "url": "https://first-approved.crm.dynamics.com", "profileName": "<approved existing profile>" },
    { "url": "https://second-approved.crm.dynamics.com", "profileName": "<approved existing profile>" }
  ]
}
```

From `src/PPDS.Extension`, validate the plan without starting a host or daemon:

```powershell
node tools/run-notebook-live-tests.mjs '<absolute plan.json>'
```

Only after target/inventory review, add `--execute-approved`. Run minimum and stable with distinct new artifact directories and preserve both results:

```powershell
$env:VSCODE_E2E_VERSION = 'minimum'
node tools/run-notebook-live-tests.mjs '<absolute minimum plan.json>' --execute-approved
$env:VSCODE_E2E_VERSION = 'stable'
node tools/run-notebook-live-tests.mjs '<absolute stable plan.json>' --execute-approved
```

Use a private temporary directory with inherited trusted permissions via process-only TEMP/TMP if required by local reference-cache policy. Never relax ACLs or alter global settings to run this demonstration. A skip, timeout, missing target, auth failure, same organization, metadata change or incomplete local cleanup is not a passing gate.
