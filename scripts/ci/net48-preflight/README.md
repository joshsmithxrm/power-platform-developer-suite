# Temporary net48 read-only preflight

This lane prepares the bounded #1450 smoke without executing it. It is isolated
from #1469 and must not be merged as release functionality.

It uses the existing test-dataverse GitHub OIDC trust and repository variables.
No client secret/certificate is supplied. No token is printed or persisted; no
authentication profile or environment setting is created or changed.

The target URL, app, tenant, organization, and environment are bound to hashes
of the already reviewed plan. Raw identifiers and credentials are omitted from
the source and report. A changed binding fails closed. The same-repository
feature-branch job has contents:read and id-token:write only; checkout does not
persist Git credentials.

The transport permits only eight named, bodyless Dataverse GET requests:
WhoAmI, current organization, caller application ID, and five exact collision
queries (package, assembly, handler, step linked to that handler, account prefix).
Collision reads select IDs only, stop at two rows, and report presence rather
than misleading total counts. Redirects and pagination are disabled.

The only POST is the existing workload-identity exchange at the fixed Microsoft
Entra token endpoint. There are no SDK/CLI mutation, deployment, batch, generic
request, profile, or cleanup execution paths. Cleanup is described only.

Run Test-ReadOnly.ps1 locally to exercise the HTTP boundary with an in-memory
handler, invalid routes/targets, identity mismatch, and static transport guards.
It performs no authentication or network requests.

Opening/updating the draft PR on codex/net48-readonly-preflight triggers the
read-only job. The redacted result is retained for seven days. Exit 0 means
identity matched and no queried collision was found; exit 2 means a collision;
exit 1 means preflight failed. Missing/denied reads never count as absence.

The scope preview comes from the reviewed extracted fixture configuration and
collision results. It is explicitly not a PPDS deployment dry-run. That CLI path
uses profile handling, which this preflight excludes. The fixture binaries and
combined runtime evidence remain in the original local review worktrees.
No server package acceptance/update/execution claim is made.

The mutation smoke still requires direct human approval and fresh collision
checks immediately before writing. This lane cannot perform that smoke.

API reference:
[RetrieveCurrentOrganization](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/webapi/reference/retrievecurrentorganization)
returns the [organization identity, type and version](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/webapi/reference/organizationdetail).
