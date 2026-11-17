# Registration compatibility and reconciliation

This change restores established Update clears and adds explicit registration
file version 2.0, including version 2.0 owned-image pruning under `--clean` (see
"Version 2.0 image pruning" below). It does not activate CI or publish packages.
The plugin repair is separate from NuGet trusted-publishing adoption.

## Old/new/reason/compatibility ledger

| Area | R / initial candidate behavior | Revised contract and reason | Compatibility boundary |
| --- | --- | --- | --- |
| Update empty text | R wrote empty description/filtering/image attributes; the initial candidate dropped them | Preserve the exact empty SDK writes; null/absence remains preserve | Correction of the introduced regression, distinct from register placeholders |
| File opt-in | Version 1.0 was metadata and absent/null intent collapsed | Enforce missing/1.0 legacy semantics and explicit 2.0 reconciliation; reject unknown versions before connection | Existing files do not silently adopt omission resets |
| Declared defaults | Initial candidate mixed managed defaults with optional preservation | V2 explicitly manages supported nonsecret fields and documented defaults | V1 optional null/empty remains preserve; explicit CallingUser and false fixes remain |
| Model JSON | Initial type converters selected Deploy and erased secure input on any round trip | Generic model round trips keep input values and Patch intent; command/service codecs apply file version | Extraction/output uses explicit secure-redacting serialization |
| Public update records | Initial appended positional members replaced old constructors/deconstruction | Keep original eight/two-argument positional contracts; new fields are init properties | Old constructor and deconstruction signatures remain available |
| Provider interface | Initial user resolver added an abstract obligation | Default implementation handles CallingUser and refuses unverifiable explicit identities | Existing implementations can load; unsupported resolution is not fabricated |
| Register/RPC | Initial nullable mode/rank could omit result keys and change defaults | Keep established register defaults and non-null result fields; append new wire options after target/profile | Register placeholders and Update clears remain operation-specific |
| Diff | Partial comparison could appear clean; initial incomplete result used success=true | Keep data[] with explicit status/coverage, 0/1/12 exits (12 = incomplete, result unknown; 2 stays a failure), success=false when incomplete. Under missing/1.0, step `filteringAttributes` and image `attributes` are always compared, as in CLI 1.4.2: when the file omits them (or supplies null/empty), the stored value is compared against none under the same order-, case- and whitespace-insensitive attribute-set normalisation, and a non-empty stored value is a modified-step/modified-image change `(none) -> <stored>` (drift, exit 1). Every other omitted optional property is not compared under 1.0 (advisory only, below) | Completed drift remains a completed comparison with exit 1; no global writer reinterpretation. The 1.4.2-parity drift is accompanied, once per text run on stderr and as a `data[].warnings` entry in JSON, by guidance: 1.0 deploy preserves the value; to resolve it, set the property to match (for `filteringAttributes`, only on Update/UpdateMultiple steps, because other messages reject a non-empty value), or remove the property from the step or image (an explicit null or empty value too, because `resetFields` is rejected while the property is present) and list it in that object's `resetFields` to have deploy clear it, or adopt version 2.0. The guidance offers only the options that apply to the drift found. Incomplete still outranks this drift (exit 12). Pinned by the AC-138 tests in `specs/plugins.md`. Exit 12 means only an incomplete comparison (the result is unknown) and exit 2 only a general failure, so scripts can tell them apart by exit code; `metadata.status` (`match`/`drift`/`incomplete`) is also available, and an error result carries an `error` object and no comparison metadata. Pinned by `ActualDiff_RetainsArrayAndReportsTruthfulSuccessStatusAndExit` (unknown property: exit 12, `success=false`, `incomplete`), `RealCommand_JsonAndExitCode_ReportTruthfulStatus` and `RealCommand_IncludeHidden_ReversesReadExclusion` (hidden step without `--include-hidden`) |
| Legacy visibility | Missing/1.0 files silently preserved omitted optional values that differ from 2.0 defaults | Diff (text notice on stderr, additive `data[].preservedLegacyValues`) and deploy (one stderr notice, additive `data[].preservedLegacyValues`) list stored values that 2.0 would reset, with an adoption pointer. Diff omits `filteringAttributes` and image `attributes` from its list because it reports them as drift (see Diff); deploy's list still includes them, under a separate heading saying they are preserved by deploy but reported as drift by `plugins diff` | The notice itself is advisory: it does not change 1.0 writes, what diff compares, status or exit codes (deploy's exit code is unaffected; diff's exit code for the two compared properties is set by the Diff row above); unsecure configuration values are redacted and secure values are never listed. Deploy uses rows it already read |
| Extraction | Existing optional overrides/reset lists could remain sticky | V2 fresh defaults are authoritative; v1 retains its deployment overlay | Explicit --registration-version selects migration; absence keeps the existing/new-file contract |
| Secure input | Non-null including empty is an existing write-only request | Preserve that write path; null/absence preserves; a supplied value is listed as excluded (write-only, never read back) and is not compared | No equality, secret reset/deletion, plaintext/hash output or new secret authority. A supplied value does not make a comparison incomplete, so it does not change `status`, `success` or the exit code, and a successful 2.0 deploy that supplies it succeeds |
| Verification | SDK acknowledgement could be mistaken for stored convergence | V2 real deployment reads back through the nonsecret comparison; drift/incomplete is non-success | Dry-run is preview-only; failures retain effects, without rollback |
| Extra registrations | Existing clean deletes orphan steps and their current children | Ordinary deploy leaves extra images and steps as drift. V2 `deploy --clean` and `plugins clean` prune only owned extra images on configured steps (conditions below); they never delete steps, types or assemblies | V1 clean retains its existing orphan-step (and, for `plugins clean`, empty-type) scope. Under v2 an orphaned step stays drift, so real v2 deploy verification exits 1 until it is removed separately with `ppds plugins unregister step <step-id>` (`--force` also deletes its images) |
| System selector | Documentation advertised a registration shorthand not implemented at R | CallingUser and explicit valid users remain registration choices | Runtime CreateOrganizationService(null) SYSTEM behavior is separate plug-in code |
| Publisher/version train | Initial candidate also bundled OIDC and replacement library notes | Keep that reviewed source/evidence separate | No trust activation, token exchange, release tags or version publication here |

Defaults are PPDS contract defaults, not inferred platform defaults. In particular
canBeBypassed=true is deliberate; read-only and applicable async auto-delete default
false. All-attributes images and cleared filtering can broaden workload. Review the
versioned plan before execution. The comparison does not cover assembly/package
bytes, solution membership, Custom APIs or unrelated environment state.

## Version 2.0 image pruning — implemented scope

Under a version 2.0 file, `ppds plugins deploy --clean` and `ppds plugins clean`
use one shared image-only planner. What the code enforces:

- Every configured step must carry an explicit `images` list. `images: []`
  authorizes removing that step's owned images. An omitted list, or a step that
  supplies the `images` key twice (in any casing), is rejected before connecting.
  Property names bind case-insensitively, so a single `"Images": []` is an explicit
  empty list and authorizes pruning every owned image on that step.
- Only images on configured steps that resolve to exactly one owned environment step
  are candidates. Assembly ownership comes from the assembly name or, for packages,
  the validated local nupkg identity, exact package ID and matching assembly parent.
  Names, filters and solution membership alone do not establish ownership.
- A stored image is configured when its name equals a configured image name ignoring
  case, the same rule deploy (update versus create), `plugins diff` and 2.0 deploy
  verification use. A case-only difference is never a deletion candidate: deploy
  updates that row and writes the configured name, and diff reports it beforehand as a
  modified-image `name` change, not as a missing image plus an orphaned one. Only case
  is covered: if the environment's collation also equates other differences (for
  example accents or trailing spaces), the write can update a row this rule does not
  pair, and the plan can list that row for deletion. `deploy --clean` then stops at
  the pre-delete name recheck without deleting it; standalone `plugins clean` would
  delete it. This is not established for Dataverse and is a known unpaired case.
- Configured image names on one step that collide ignoring case are rejected as a file
  error (exit 3) before connecting, by deploy, diff and 2.0 cleanup (version 1.0
  `plugins clean` never pairs images and does not apply the rule). For a same-named
  pre-image and post-image the message suggests one entry with `imageType: "Both"`.
- If a configured step's stored images have names that collide ignoring case, cleanup
  refuses the whole plan before any mutation (exit 3), naming the stored images and
  their IDs and whether one of them is configured; remove the extra one (or, when
  neither is configured, one or both) with `ppds plugins unregister image <image-id>`.
  Diff reports the same rows as incomplete when one is configured, and as ordinary
  orphans when neither is.
- Hidden, managed, non-customizable, unavailable or ambiguous targets fail the whole
  plan visibly instead of being skipped. The complete plan for every configured
  scope is built before the first mutation; `deploy --clean` applies it only after
  all ordinary writes succeed.
- Only the planned image IDs are deleted, each after a fresh ownership recheck.
  Images added after planning are retained and counted. Deletion uses the existing
  unconditional image delete: there is no row-version or transaction guarantee, so a
  change after the final read remains possible. Acknowledged and unknown outcomes
  are retained; there is no rollback or automatic retry.
- `--dry-run` deletes nothing. JSON output lists the plan (`imageCleanup.plan.images`,
  with `imagesDeleted` counting performed deletes only, so 0 in a preview). Text
  output lists each planned image (name, ID, step identity and step ID) under
  "Planned image deletions (dry-run; not performed)" and a planned total.

Not implemented and still future work: deleting parent steps, types or assemblies
under version 2.0. Their cascades can reach children added after a client check,
and parent row-version checks or transaction grouping do not by themselves prove an
atomic exact-child-set guarantee. Under 2.0 an orphaned step is reported as drift
(real deploy verification exits 1) until the operator removes it explicitly with
`ppds plugins unregister step <step-id>` (add `--force` when it still has images).
Version 1.0 cleanup keeps its legacy orphan-step scope. No live prune validation
has been performed; the guarantees above are covered by offline tests only.
