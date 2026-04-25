# scripts/docs-gen

Documentation generators for the PPDS platform. Reflect over product source and emit markdown reference into the [ppds-docs](https://github.com/joshsmithxrm/ppds-docs) repo. See [`specs/docs-generation.md`](../../specs/docs-generation.md) for the full design.

## Generators

| Tool | Input | Output | Test project |
|---|---|---|---|
| [`cli-reflect/`](./cli-reflect/) | `PPDS.Cli.dll` | `docs/reference/cli/{group}/{command}.md` | `tests/PPDS.DocsGen.Cli.Tests/` |
| [`libs-reflect/`](./libs-reflect/) | Library DLLs + .xml doc files | `docs/reference/libraries/{package}/{namespace}/{type}.md` | `tests/PPDS.DocsGen.Libs.Tests/` |
| [`mcp-reflect/`](./mcp-reflect/) | `PPDS.Mcp.dll` | `docs/reference/mcp/tools/{tool}.md` | `tests/PPDS.DocsGen.Mcp.Tests/` |
| [`ext-reflect/`](./ext-reflect/) (Node) | `src/PPDS.Extension/package.json` | `docs/reference/extension/{commands,configuration,views}.md` | `tests/PPDS.DocsGen.Extension.Tests/` |

All four emit deterministic, byte-identical output on repeat runs (AC-19). All type references render as inline code via `MdxEscape.InlineCode`; prose from XML-doc summaries passes through `MdxEscape.Prose` (HTML-entity escape outside fences and inline-code spans).

## Supporting tools

| Tool | Purpose |
|---|---|
| [`PPDS.DocsGen.Common/`](./PPDS.DocsGen.Common/) | Shared C# library: `IReferenceGenerator`, `MdxEscape`, `BannerHelper` |
| [`smoke/`](./smoke/) | CI tool — extracts fenced `csharp` blocks from ppds-docs guides, wraps in one of three forms (complete file / top-level statements / method body), compiles via Roslyn |
| [`lint-extension-contributions.js`](./lint-extension-contributions.js) | Pre-commit check — every `contributes.commands` entry in the extension's `package.json` has `title` + `category` |
| [`app-token/`](./app-token/) | C# console helper — mints a GitHub App installation token (superseded by `actions/create-github-app-token@v3` in the workflow; retained for local debugging) |
| [`compute-rollover-diff.sh`](./compute-rollover-diff.sh) | Moves `PublicAPI.Unshipped.txt` entries to `PublicAPI.Shipped.txt` at release time |
| [`check-open-rollover.sh`](./check-open-rollover.sh) | Aborts a release when a prior rollover PR is still open |
| [`compute-surface-summary.sh`](./compute-surface-summary.sh) | Produces the release-PR body listing added/removed/modified public API |

## Running locally

```bash
# Build everything
dotnet build PPDS.sln -c Release

# CLI reference
dotnet run --project scripts/docs-gen/cli-reflect -- \
  --assembly src/PPDS.Cli/bin/Release/net10.0/PPDS.Cli.dll \
  --output artifacts/docs/reference/cli

# Libraries reference (reads .xml doc files alongside the DLLs)
dotnet run --project scripts/docs-gen/libs-reflect -- \
  --assemblies artifacts/bin \
  --output artifacts/docs/reference/libraries

# MCP tool catalog
dotnet run --project scripts/docs-gen/mcp-reflect -- \
  --assembly src/PPDS.Mcp/bin/Release/net10.0/PPDS.Mcp.dll \
  --output artifacts/docs/reference/mcp

# Extension command table
node scripts/docs-gen/ext-reflect/generate.js \
  --package-json src/PPDS.Extension/package.json \
  --output artifacts/docs/reference/extension
```

Each generator writes diagnostics to stderr and a deterministic file list to stdout (Constitution I1).

## Documentation style guide

When annotating public types in the four libraries (Phase 0 curation), follow these rules:

1. **Explain WHY, not WHAT.** A summary that just restates the type name is noise. "Gets the value" on a property named `Value` should be deleted, not shipped.
2. **One-line synopsis in `<summary>`, non-obvious invariants in `<remarks>`.** Reserve `<remarks>` for constraints a consumer needs to know before calling (thread safety, disposal rules, required call order).
3. **Use `<param>` for every parameter, `<returns>` for every non-void method, `<exception>` for every documented throw condition.**
4. **Cross-reference with `<see cref="..."/>`.** Helps IntelliSense and docs site navigation.
5. **Don't document implementation details.** If a consumer shouldn't rely on something, mark it `[EditorBrowsable(EditorBrowsableState.Never)]` and skip the summary.

The Phase 0 triage decides per type whether it's **customer-facing** (annotate fully), **implementation-detail-but-public** (mark `[EditorBrowsable(Never)]`), or **safe-to-internalize** (`public` → `internal` + `[InternalsVisibleTo]` if needed).

## GitHub App setup (release workflow)

The release workflow (`.github/workflows/docs-release.yml`) uses a GitHub App to open cross-repo PRs in ppds-docs. The workflow uses [`actions/create-github-app-token@v2`](https://github.com/actions/create-github-app-token) to mint short-lived installation tokens — no custom token logic needed.

Setup is a **one-time** step per repo admin:

1. **Create the App** — Settings → Developer settings → GitHub Apps → New GitHub App
   - Name: `ppds-docs-bot`
   - Homepage: the ppds repo URL
   - Permissions:
     - **Contents** — Read & write (push branches)
     - **Pull requests** — Read & write (open PRs)
     - **Metadata** — Read
   - Subscribe to no events (the app is polled, not pushed)
2. **Generate private key** — scroll to "Private keys" → "Generate a private key" — download the `.pem` file and keep it secret.
3. **Install on both repos** — from the App's public page, click "Install" and select `joshsmithxrm/ppds` and `joshsmithxrm/ppds-docs`.
4. **Store in the ppds repo:**
   - `PPDS_DOCS_APP_ID` (Actions **variable**, not secret) — the numeric App ID (public value). Set via Settings → Secrets and variables → Actions → Variables tab.
   - `PPDS_DOCS_APP_PRIVATE_KEY` (Actions **secret**) — the full PEM contents, including `-----BEGIN RSA PRIVATE KEY-----` / `-----END RSA PRIVATE KEY-----`
   - `PPDS_DOCS_REPO` (Actions **variable**) — owner/name of the docs repo (e.g. `joshsmithxrm/ppds-docs`)
5. **Verify** — re-run `docs-release.yml` with `workflow_dispatch: dry_run=false` and confirm a PR lands in ppds-docs.

If the App private key is ever compromised: generate a new one in the App settings, update `PPDS_DOCS_APP_PRIVATE_KEY`, delete the old key. No code change required.

## Recovering a broken `PublicAPI.Shipped.txt` baseline

If a library's baseline gets corrupted (accidental commit, bad merge):

1. Revert to the last known-good `PublicAPI.Shipped.txt` from git history.
2. If no known-good reference exists, regenerate: open the solution in Visual Studio / Rider, trigger "Add all to public API" bulk code fix on every `RS0016` diagnostic, then run `compute-rollover-diff.sh --root .` to move the resulting Unshipped entries into Shipped.
3. If that headless path is unavailable, the regex-based extraction in the recovery section below is a best-effort fallback — but it's brittle against PublicApiAnalyzers' changing message format, and should only be used when an IDE isn't available.

### Recovery fallback (headless regex)

```bash
for proj in Dataverse Migration Auth Plugins; do
  cd src/PPDS.$proj
  : > PublicAPI.Unshipped.txt
  dotnet build 2>&1 | grep "RS0016:" | \
    sed -E "s/.*Symbol '([^']+)'.*/\1/" | sort -u > PublicAPI.Unshipped.txt.new
  # Inspect and clean up the format manually, then:
  mv PublicAPI.Unshipped.txt.new PublicAPI.Unshipped.txt
  cat PublicAPI.Unshipped.txt >> PublicAPI.Shipped.txt
  sort -u PublicAPI.Shipped.txt -o PublicAPI.Shipped.txt
  : > PublicAPI.Unshipped.txt
  cd -
done
```

## Troubleshooting

### PPDS014: "Public type/member requires /// <summary> or [EditorBrowsable(Never)]"

The analyzer fires on public API in the four libraries without XML docs. Fix options:
- Add a meaningful `/// <summary>` (see style guide above)
- Mark the type `[EditorBrowsable(EditorBrowsableState.Never)]` (hides from IntelliSense but stays public)
- Change `public` → `internal` (add `[InternalsVisibleTo]` in `AssemblyInfo.cs` if cross-project consumers need it)

### RS0016: "Public API is not part of the declared API"

A new public symbol was added without recording it in `PublicAPI.Unshipped.txt`. Easiest fix:
- In Visual Studio / Rider: click the lightbulb on the diagnostic → "Add symbol to public API"
- From the command line: append the suggested symbol line to `src/PPDS.{Package}/PublicAPI.Unshipped.txt`

### RS0017: "Public API signature has changed"

Rare — indicates a signature mutation not reflected in the baseline. Update Shipped/Unshipped accordingly; confirm the change is intentional.

### PPDS015: "CLI command requires a Description"

A new `Command`/`AsyncCommand` subclass was added without setting `Description`. Add one of:
- `[Description("...")]` on the class
- `public override string Description { get; } = "...";`
- `Description = "...";` in the constructor

### PPDS016: "MCP tool requires Name/Description"

A `[McpServerTool]` method is missing metadata. Set `Name` via the attribute's named argument, then supply a description via a sibling `[System.ComponentModel.Description("...")]` attribute on the same method (the PPDS codebase convention).

## Design references

- [`specs/docs-generation.md`](../../specs/docs-generation.md) — the full spec with 30 ACs
- [`specs/CONSTITUTION.md`](../../specs/CONSTITUTION.md) — non-negotiable principles
- [`specs/analyzers.md`](../../specs/analyzers.md) — PPDS.Analyzers project conventions (extended by PPDS014/015/016)
