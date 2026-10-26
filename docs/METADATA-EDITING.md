# Editing views and model-driven apps

Pin the intended profile and environment on service commands. Validate the target with
`ppds api request --path /api/data/v9.2/WhoAmI --profile <profile> --environment <url>`.
The examples below use placeholders; include those overrides on every command.

## View columns and queries

Views are `savedquery` records (Web API collection `savedqueries`). A visible column
needs a layout cell and a matching FetchXML projection. `views add-column` maintains
both, including repair when an existing cell lacks its projection. Existing filters,
sorts, primary keys and links survive the edit; `all-attributes` remains intact.

```text
ppds views get --entity account --view <view-id> --unpublished --profile <profile> --environment <url>
ppds views add-column --entity account --view <view-id> --column telephone1 --profile <profile> --environment <url>
ppds views add-column --entity account --view <view-id> --via-relationship parentaccountid --column name --profile <profile> --environment <url>
ppds views update-column --entity account --view <view-id> --column parentaccountid.name --width 220 --profile <profile> --environment <url>
```

Related cells use `alias.attribute`, such as `parentaccountid.name`; use the same
identity for inspection, width updates, removal and reordering. A base `name` cell
and two related `name` cells remain distinct. Removing or reordering cells edits the
display layout; query projections are retained because filters/other query logic
may depend on them.

There is no typed `views create` command. For raw API creation, supply both XML
documents. Resolve the actual table ObjectTypeCode for the layout `object` value;
this minimal account example uses its standard code 1:

```json
{
  "name": "Example account view",
  "returnedtypecode": "account",
  "querytype": 0,
  "fetchxml": "<fetch><entity name='account'><attribute name='accountid'/><attribute name='name'/><attribute name='telephone1'/></entity></fetch>",
  "layoutxml": "<grid name='resultset' object='1' jump='name' select='1' preview='1' icon='1'><row name='result' id='accountid'><cell name='name' width='240'/><cell name='telephone1' width='180'/></row></grid>"
}
```

Save a reviewed payload and use `api request --method POST --path
/api/data/v9.2/savedqueries --body-file <payload.json>` with the pinned profile and
environment. Use the returned view ID for later commands. Managed views can be
editable through an unmanaged layer when their managed properties permit it;
`View.ManagedComponentNotEditable` reports an actual rejected write, rather than a
blanket prohibition on managed views.

## Draft verification and publication

View edits read and verify the unpublished draft. Verification compares every
written XML field with the intended state, retrying up to three times. Formatting,
XML attribute order and the platform-maintained root FetchXML `savedqueryid`
annotation do not affect equality. Query attributes, aliases, filters and ordering
remain significant. Equivalent no-op writes succeed when the draft matches.

`View.UpdateNotPersisted` means a read returned differing XML or no record.
`View.VerificationFailed` means verification reads were unavailable. In either
case the write may have applied: inspect the draft before retrying or publishing.
Read-back confirms an observed state; it cannot prevent a later concurrent edit.

Inspect with `views get --unpublished`, then publish the intended table using
`ppds publish --type entity <logical-name>` with explicit profile/environment.
`--publish` on view mutations performs the same table-scoped publish after
verification. Ordinary view reads show published state unless `--unpublished` is
specified. Execute the saved query and inspect its returned values when validating
what an application will display.

## App selections and navigation

`model-driven-app set-views`, `set-forms` and `set-charts` accept component names or
IDs scoped to the requested table. Duplicate names produce `ComponentAmbiguous`
with candidate IDs; pass an ID to disambiguate. The complete requested set resolves
before any write, so invalid names cannot clear prior selections.

The service retains selected IDs, adds new IDs, then removes obsolete IDs.
These actions are not transactional: later server failures report possible partial
state and require inspection of the app draft before publish/retry. `--all` removes
explicit selections and returns to include-all behavior; it does not delete the
underlying component records. An empty explicit selection can broaden availability.

```text
ppds model-driven-app set-views --app <app-name> --entity account --view <view-id> --profile <profile> --environment <url>
ppds model-driven-app sitemap --app <app-name> --unpublished --profile <profile> --environment <url>
ppds model-driven-app add-table contact --app <app-name> --area <area-id> --group <group-id> --profile <profile> --environment <url>
```

Sitemap display resolves the process UI language, then English LCID 1033, inline
legacy titles, then other translations. Matching accepts all existing labels,
prioritizes IDs and rejects ambiguous labels with candidate IDs. Translations are
preserved. Stable area/group IDs are preferable for scripts used across languages.
Typed sitemap edits validate XML against the bundled schema before writing.
Use `--publish` only when ready to publish the specific app.

The separately maintained `ppds-skills` package needs corresponding guidance
updates: `savedquery` terminology, qualified managed-edit support, complete view
creation payloads and the draft/verify/publish workflow. That follow-up is tracked
in issue #1448; installed skill caches are outside this repository's source.
