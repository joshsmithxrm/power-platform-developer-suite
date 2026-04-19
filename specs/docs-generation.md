# Docs Generation

**Status:** Draft
**Last Updated:** 2026-04-18
**Code:** [scripts/docs-gen/](../scripts/docs-gen/), [src/PPDS.Analyzers/Rules/](../src/PPDS.Analyzers/Rules/), [.github/workflows/docs-smoke.yml](../.github/workflows/docs-smoke.yml), [.github/workflows/docs-release.yml](../.github/workflows/docs-release.yml)
**Surfaces:** CLI | Libraries | MCP | Extension

---

## Overview

Generates reference documentation for CLI command groups, PPDS libraries (PPDS.Dataverse, PPDS.Migration, PPDS.Auth, PPDS.Plugins), MCP tools, and VS Code extension commands directly from source. Enforces public-API annotation via build-time analyzers so reference output stays consistent with the product. Compiles fenced C# code blocks in `ppds-docs` guides against current product assemblies. Opens a cross-repo pull request into `ppds-docs` on each release tag.

### Goals

- **Zero factual drift** between product source and reference docs on every tagged release
- **Build-time enforcement** — missing public-API annotations fail the build locally, in pre-commit, and in CI
- **Curated public surface** — library reference covers types that consumers actually call; implementation-detail types are hidden via `[EditorBrowsable(Never)]`
- **Compile-verified code samples** — every fenced C# block in guides compiles against the current product build
- **Self-serve contributions** — generator source lives next to product source; contributors change product and docs in one commit

### Non-Goals

- Auto-generated narrative content (guides, tutorials) — those stay hand-authored
- Release notes generation — per-package `CHANGELOG.md` files already cover this
- Versioned docs snapshots (Docusaurus `docs-version`)
- Translation / i18n
- TUI screen-by-screen reference (deferred pending demand)

---

## Architecture

```
ppds monorepo                                                      ppds-docs repo
┌─────────────────────────────────────────────────────────────┐    ┌────────────────────────┐
│  src/                                                       │    │                        │
│   PPDS.{Dataverse,Migration,Auth,Plugins}  (annotated)      │    │  docs/reference/       │
│   PPDS.Cli/Commands/**                     (Spectre)        │    │    cli/{group}/*.md    │
│   PPDS.Mcp/Tools/**                        ([McpServerTool])│    │    libraries/{pkg}/*.md│
│   PPDS.Extension/package.json              (contributes)    │    │    mcp/{tool}/*.md     │
│   PPDS.Analyzers/Rules/                                     │    │    extension/          │
│     XmlDocOnPublicApiAnalyzer              (PPDS014)        │    │      commands.md       │
│     CliCommandNeedsDescriptionAnalyzer     (PPDS015)        │    │                        │
│     McpToolNeedsMetadataAnalyzer           (PPDS016)        │    │  docs/guides/*.md      │
│                                                             │    │   (fenced C# blocks)   │
│  scripts/docs-gen/                                          │    │                        │
│    cli-reflect/    (C# tool → markdown)                     │──▶ │                        │
│    libs-reflect/   (Roslyn walker → markdown)               │──▶ │                        │
│    mcp-reflect/    (C# tool → markdown)                     │──▶ │                        │
│    ext-reflect/    (Node, reads package.json)               │──▶ │                        │
│    smoke/          (extracts fenced C#, compiles)           │◀── │                        │
│    lint-extension-contributions.js                          │    │                        │
│                                                             │    │                        │
│  PublicAPI.{Shipped,Unshipped}.txt per library              │    │                        │
│                                                             │    │                        │
│  .github/workflows/                                         │    │                        │
│    docs-smoke.yml    (PR gate + tag trigger)                │◀── │ (workflow_call)        │
│    docs-release.yml  (tag → cross-repo PR via GitHub App)   │──▶ │ (pull_request)         │
└─────────────────────────────────────────────────────────────┘    └────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `PPDS014 XmlDocOnPublicApi` | Analyzer: fails build if a public type/member in the four libraries lacks `/// <summary>` and is not marked `[EditorBrowsable(Never)]` |
| `PPDS015 CliCommandNeedsDescription` | Analyzer: fails build if a `System.CommandLine.Command`, `Option<T>`, or `Argument<T>` creation expression lacks a non-empty Description (via 2-arg `Command` ctor or object initializer) |
| `PPDS016 McpToolNeedsMetadata` | Analyzer: fails build if a method with `[McpServerTool]` lacks `Name` or `Description` |
| `Microsoft.CodeAnalysis.PublicApiAnalyzers` | Vendored: enforces `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` baseline convention (RS0016, RS0017) |
| `scripts/docs-gen/cli-reflect` | C# console tool: loads the built CLI assembly in an `AssemblyLoadContext`, invokes every `public static Command Create()` on `*CommandGroup` types, walks the resulting `Command.Subcommands` tree → one markdown file per leaf command plus per-group index |
| `scripts/docs-gen/libs-reflect` | C# console tool with Roslyn: walks public surface of four libraries filtered by `[EditorBrowsable(Never)]` → markdown per type |
| `scripts/docs-gen/mcp-reflect` | C# console tool: enumerates `[McpServerTool]` methods → markdown per tool |
| `scripts/docs-gen/ext-reflect` | Node script: reads `src/PPDS.Extension/package.json` `contributes` → `extension/commands.md` table |
| `scripts/docs-gen/smoke` | C# console tool: extracts fenced `csharp` blocks from a ppds-docs checkout and `dotnet build`s them against current assemblies |
| `scripts/docs-gen/lint-extension-contributions.js` | Node script: pre-commit hook check — every `contributes.commands[]` entry has `title` and `category` |
| `.github/workflows/docs-smoke.yml` | Runs `smoke` on ppds-docs PRs and on this repo's release tags |
| `.github/workflows/docs-release.yml` | On `v*` tag: runs all four generators, opens cross-repo PR via GitHub App installation token |

### Dependencies

- Depends on: [analyzers.md](./analyzers.md) — extends PPDS.Analyzers with three new rules (PPDS014-PPDS016)
- Depends on: [cli.md](./cli.md) — the command tree cli-reflect walks
- Depends on: [mcp.md](./mcp.md) — the tool registry mcp-reflect enumerates
- Uses: [Microsoft.CodeAnalysis.PublicApiAnalyzers](https://www.nuget.org/packages/Microsoft.CodeAnalysis.PublicApiAnalyzers/) — vendored public-surface detection
- Uses: GitHub App (PPDS Docs Bot) installed on both `ppds` and `ppds-docs` — short-lived installation tokens for cross-repo PR

Note on Constitution A1/A2: generators are tooling, not Application Services. They contain no business logic, never connect to Dataverse, and are out of the service-pattern's scope by construction.

---

## Specification

### Core Requirements

1. **Curated public-API annotation.** Every public type and member in PPDS.Dataverse, PPDS.Migration, PPDS.Auth, and PPDS.Plugins must have either `/// <summary>` documentation or be marked `[EditorBrowsable(Never)]`. The rule applies to everything in the baseline (Phase 0) and to every future addition (drift check).
2. **Public-surface baseline.** Each of the four libraries has committed `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` files. Any change to the public surface requires a corresponding baseline update or the build fails.
3. **CLI command annotation.** Every `System.CommandLine.Command`, `Option<T>`, and `Argument<T>` creation site in the CLI factory code must supply a non-empty Description — for `Command`, via the 2-arg constructor `Command(name, description)` or via an object initializer `Description = "..."`; for `Option<T>` / `Argument<T>`, only via object initializer since their constructors take only name/aliases. Enforced at build time by PPDS015.
4. **MCP tool annotation.** Every method annotated `[McpServerTool]` must supply `Name` and `Description`. Enforced at build time.
5. **Extension contribution annotation.** Every entry in `src/PPDS.Extension/package.json` `contributes.commands` must supply `title` and `category`. Enforced by pre-commit lint script.
6. **Reference generation.** Four generators emit markdown into a well-defined output tree matching the ppds-docs layout under `docs/reference/`.
7. **Code-sample smoke test.** A CI job compiles every fenced `csharp` code block in ppds-docs against current product assemblies. A failure fails the ppds-docs PR and fails this repo's release pipeline.
8. **Cross-repo release PR.** On each `v*` tag in this repo, a GitHub Actions workflow runs all four generators, commits the output to a branch in ppds-docs, and opens a pull request. Authentication uses a GitHub App installation token. No auto-merge; human approval gates the merge.
9. **MDX-safe output.** Generated markdown must parse under Docusaurus strict MDX. Normative escape rule applied uniformly across all four generators:
   - **Every type reference in body text renders as inline code** (single-backtick), produced via `MdxEscape.InlineCode`. Inline code blocks are parsed verbatim by MDX, so `` `Task<T>` `` is safe.
   - **Only free-form prose from XML-doc `<summary>` bodies** goes through `MdxEscape.Prose`, which HTML-entity-encodes `<` and `>` outside code fences.
   - No generator emits a bare angle-bracket type reference anywhere in body text. No exceptions; no surface-specific deviations.
10. **Terminology.** Generated output uses "PPDS libraries" for the four NuGet packages collectively, never "SDK" (which the codebase reserves for Microsoft's Dataverse SDK). Enforced by a generator unit test that scans generated markdown for the regex `\bSDK\b` in any heading or prose; occurrences must appear only in code fences or quoted Microsoft docs references.

### Primary Flows

**Developer adds a public type to PPDS.Dataverse:**

1. Developer writes `public class NewThing { public void Do() {} }` with no XML docs
2. Developer runs `dotnet build`
3. PPDS014 fires: `error PPDS014: Public type 'NewThing' needs '/// <summary>' or [EditorBrowsable(Never)]`
4. RS0016 fires (PublicApiAnalyzers): `error RS0016: NewThing not declared in PublicAPI.Unshipped.txt`
5. Developer adds `/// <summary>` comment explaining purpose and adds line to `PublicAPI.Unshipped.txt`
6. Build passes; `Do()` member without summary still fails PPDS014 — developer adds summary
7. PR diff shows the Unshipped.txt addition — reviewer sees exactly what public surface is growing

**Release tag ships docs:**

1. Maintainer tags `v1.1.0`
2. `docs-release.yml` fires on tag push
3. Workflow builds all four libraries + CLI + MCP assemblies in Release
4. Workflow runs `cli-reflect`, `libs-reflect`, `mcp-reflect`, `ext-reflect` in parallel; outputs land in temp directory
5. Workflow obtains GitHub App installation token
6. **Ppds-docs PR:** Clones `ppds-docs main`; writes generated output under `docs/reference/`; commits on branch `release/v1.1.0-ref-{run_id}` (run-id suffix makes each branch unique, avoiding stale-branch collisions); opens PR: `chore(reference): regenerate for v1.1.0`. PR body includes surface-change summary from `PublicAPI.Shipped.txt` diff (added/removed/changed counts + first 20 entries of each).
7. **Baseline rollover PR:** In this repo, workflow creates a second branch `release/v1.1.0-baseline-rollover`, moves all entries from each library's `PublicAPI.Unshipped.txt` into its `PublicAPI.Shipped.txt` (sorted, deduplicated), clears Unshipped to empty, opens PR: `chore(release): v1.1.0 baseline rollover`.
8. Human reviews both PRs and merges.

**Rollover race safety:** If a new `v*` tag fires before the previous rollover PR merges, the new rollover PR will contain the accumulated Unshipped entries from both releases. This is correct (the contract is "Unshipped is empty after rollover PR merges," not "after tag fires"). The release workflow detects and fails if the current `main` of this repo has an open rollover PR from a prior tag — the maintainer must merge or close it before proceeding.

**Release workflow dry-run mode:**

Triggered by `workflow_dispatch` with input `dry_run=true`. Runs steps 3-4 normally, but instead of opening PRs:

- Prints the generated file tree to the job log (relative paths, byte counts)
- Prints the PR body that would be sent (surface-change summary)
- Prints the baseline rollover diff that would be applied
- Uploads the generated output as a workflow artifact so a human can inspect before the real run

Dry-run does not require the GitHub App token (no cross-repo writes). Used for CI-side validation and by `AC-19`.

**Docs guide PR modifies a code sample:**

1. Contributor edits `ppds-docs/docs/guides/authentication.md`, changes a fenced `csharp` block
2. Contributor opens PR in ppds-docs
3. `ppds-docs`'s workflow calls this repo's `docs-smoke.yml` via `workflow_call` (pinned to `main`)
4. `smoke` tool checks out this repo, builds libraries + CLI assemblies, extracts every `csharp` block from the docs PR branch, writes each to a temporary `.csproj`, runs `dotnet build`
5. Any compile failure fails the check with a pointer to the offending file and line
6. Contributor fixes the sample or the product; re-push re-runs the smoke

### Surface-Specific Behavior

#### CLI Surface (`cli-reflect`)

- Input: `PPDS.Cli.dll` (built in Release) loaded in a real `AssemblyLoadContext` (not `MetadataLoadContext`); each `public static Command Create()` method on a public `*CommandGroup` type is invoked and the returned `System.CommandLine.Command` is walked for metadata
- Output: `docs/reference/cli/{group}/{command}.md` + `docs/reference/cli/{group}/_index.md`
- Group name: the root `Command.Name` returned by the `Create()` factory. Per-group markdown files cover the group's direct subcommands; nested subgroups are out of scope for v1 (no consumer uses them in current CLI source)
- Per-command fields emitted: name, description, arguments (name, description, required — inferred from absence of `DefaultValueFactory`), options (long name from `Command.Name`, short name from the first dash-prefixed alias, type display from `Option<T>` generic argument, default from `DefaultValue` property where set)
- Filter: subcommands with `Command.Hidden = true` (System.CommandLine's built-in hide flag) are skipped; factory types or methods annotated `[EditorBrowsable(Never)]` are skipped at the group level
- MDX escaping: applies Core Requirement 9 — type references render as inline code; prose from descriptions passes through `MdxEscape.Prose`
- Trust boundary: the generator invokes product factory code; all CLI factories must be side-effect-free (construct Commands and return — no DI registration, no I/O). PPDS CLI factories adhere to this by construction

#### Libraries Surface (`libs-reflect`)

- Input: `PPDS.{Dataverse,Migration,Auth,Plugins}.dll` + their corresponding `.xml` docfiles (MSBuild's `DocumentationFile` property emits XML docs alongside the DLL)
- Output: `docs/reference/libraries/{package}/{namespace}/{type}.md` + per-package `_index.md`
- Filter: types marked `[EditorBrowsable(Never)]` are excluded. Every included type is fully annotated (type-level summary AND every public member summary) — PPDS014 enforces this at build time, so the generator treats any missing summary as a bug and fails with a clear diagnostic rather than emitting placeholders
- Per-type fields: summary, namespace, assembly, kind (class/interface/record/struct), base type, implemented interfaces, public members with summaries and signatures
- Cross-references: internal links to other types in the same package; external type references render as plain text
- MDX escaping: applies Core Requirement 9 — all type references (including generics like `Task<T>`) render as inline code via `MdxEscape.InlineCode`; summary bodies pass through `MdxEscape.Prose`
- **XML-doc feature support.** In scope for v1: `<summary>`, `<param>`, `<returns>`, `<remarks>`, `<see cref="..."/>` (rendered as bare inline code with the type-id prefix stripped — not a hyperlink), `<paramref>`, `<typeparamref>` (rendered as inline code), `<c>` / `<code>` (rendered as inline code), and `<inheritdoc />` / `<inheritdoc cref="..."/>` resolved against the inheritance chain (explicit cref → first documented interface → first documented base class). Out of scope for v1: cross-type hyperlinks from `<see cref>`, member overload disambiguation beyond declaration-order fallback, and generic-arity conversion from XML IDs back to C# source form (best-effort only). These were audited against PPDS.Auth (the only library with XML docs today) and none of the out-of-scope cases are exercised.

#### MCP Surface (`mcp-reflect`)

- Input: `PPDS.Mcp.dll` + reflection over `[McpServerTool]`-attributed methods
- Output: `docs/reference/mcp/tools/{tool-name}.md` + `docs/reference/mcp/_index.md`
- Per-tool fields: tool name, description, input schema (parameters, types, required flags, descriptions from `[Description]` attributes on parameters), output schema (if declared)
- Example invocation: emitted only when the tool's class or method carries a `[McpToolExample]` attribute (new attribute introduced in this spec, ~20 LOC addition to PPDS.Mcp). If absent, the example section is omitted — not placeholder-filled
- Index: categorized list grouping tools by domain (auth, environment, query, plugin-traces, etc.) based on their namespace
- MDX escaping: applies Core Requirement 9

#### Extension Surface (`ext-reflect`)

- Input: `src/PPDS.Extension/package.json`
- Output: `docs/reference/extension/commands.md` (single file — flat command table)
- Columns: command ID, title, category, default keybinding (if any), palette visibility (`when` clause summary)
- Also emits: `docs/reference/extension/configuration.md` (contribution `configuration` section) and `docs/reference/extension/views.md` (views and view containers)

### Constraints

- All generators emit reproducible output: same source → byte-identical markdown, so release PRs have clean diffs
- No generator depends on network or Dataverse credentials — everything reflects over local source and compiled assemblies
- Generated files are never hand-edited in ppds-docs; they carry a top-of-file banner: `<!-- Auto-generated by scripts/docs-gen/{tool} — edits will be overwritten on next release. -->`
- Generator C# tools may reference PPDS library assemblies but must not instantiate services requiring Dataverse connections

(The ~300 LOC per-generator target is a design heuristic, not an enforced constraint — see Design Decisions.)

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| `PublicAPI.Shipped.txt` | Must be UTF-8, LF line endings, sorted ascending | RS0019 |
| `PublicAPI.Unshipped.txt` | Same as Shipped. May accumulate entries on main; must be empty only after the release-rollover PR merges | RS0024 |
| Generated markdown banner | First line matches `<!-- Auto-generated by scripts/docs-gen/` | Smoke: regeneration drift detected |
| Cross-repo PR title | Must start with `chore(reference):` | Release workflow rejects |

---

## Acceptance Criteria

Test projects follow the convention `tests/PPDS.DocsGen.{Surface}.Tests/` for generators and smoke, `tests/PPDS.Analyzers.Tests/` for analyzer rules (existing project), and `tests/PPDS.DocsGen.Workflow.Tests/` for workflow orchestration logic (e.g., rollover diff assembly, app-token helper).

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | PPDS014 fails build for a public type in a library project with no `/// <summary>` and no `[EditorBrowsable(Never)]` | `PPDS.Analyzers.Tests/XmlDocOnPublicApiAnalyzerTests.FlagsUndocumentedPublicType` | 🔲 |
| AC-02 | PPDS014 fails build for a public member inside an included type when the member has no `/// <summary>` | `PPDS.Analyzers.Tests/XmlDocOnPublicApiAnalyzerTests.FlagsUndocumentedPublicMember` | 🔲 |
| AC-03 | PPDS014 allows a public type marked `[EditorBrowsable(Never)]` without a summary | `PPDS.Analyzers.Tests/XmlDocOnPublicApiAnalyzerTests.AllowsEditorBrowsableNever` | 🔲 |
| AC-04 | PPDS014 allows a public type with `/// <summary>` covering every public member | `PPDS.Analyzers.Tests/XmlDocOnPublicApiAnalyzerTests.AllowsFullyDocumentedType` | 🔲 |
| AC-05 | PPDS014 skips types marked `[GeneratedCode]` | `PPDS.Analyzers.Tests/XmlDocOnPublicApiAnalyzerTests.SkipsGeneratedCodeAttribute` | 🔲 |
| AC-06 | PPDS014 skips files whose path contains `/Generated/` or `\Generated\` | `PPDS.Analyzers.Tests/XmlDocOnPublicApiAnalyzerTests.SkipsGeneratedDirectory` | 🔲 |
| AC-07 | PublicApiAnalyzers (RS0016) fails build when a new public type is not listed in `PublicAPI.Unshipped.txt` in each of the four library projects (Dataverse, Migration, Auth, Plugins) | `PPDS.DocsGen.Workflow.Tests/PublicApiBaselineTests.FailsOnUnbaselinedAddition` (parametrized over all four libs) | 🔲 |
| AC-08 | PPDS015 fails build for a Spectre command type (either subclass of `Command<T>` or attributed with `[CommandFor]` / analogous attribute) whose `Description` property is not set via attribute, property initializer, or ctor assignment | `PPDS.Analyzers.Tests/CliCommandNeedsDescriptionAnalyzerTests.FlagsMissingDescription` | 🔲 |
| AC-09 | PPDS015 allows Description set via attribute (`[Description("...")]` on the type) | `PPDS.Analyzers.Tests/CliCommandNeedsDescriptionAnalyzerTests.AllowsAttributeDescription` | 🔲 |
| AC-10 | PPDS015 allows Description set via property initializer or constructor assignment | `PPDS.Analyzers.Tests/CliCommandNeedsDescriptionAnalyzerTests.AllowsPropertyOrCtorDescription` | 🔲 |
| AC-11 | PPDS016 fails build for a method `[McpServerTool]` with no `Name` argument | `PPDS.Analyzers.Tests/McpToolNeedsMetadataAnalyzerTests.FlagsMissingName` | 🔲 |
| AC-12 | PPDS016 fails build for a method `[McpServerTool]` with no `Description` argument | `PPDS.Analyzers.Tests/McpToolNeedsMetadataAnalyzerTests.FlagsMissingDescription` | 🔲 |
| AC-13 | `lint-extension-contributions.js` exits non-zero when a `contributes.commands` entry lacks `title` | `PPDS.DocsGen.Extension.Tests/LintExtensionContributionsTests.FailsOnMissingTitle` | 🔲 |
| AC-14 | `lint-extension-contributions.js` exits non-zero when a `contributes.commands` entry lacks `category` | `PPDS.DocsGen.Extension.Tests/LintExtensionContributionsTests.FailsOnMissingCategory` | 🔲 |
| AC-15 | `cli-reflect` emits one markdown file per System.CommandLine leaf command plus per-group `_index.md`, with members ordered alphabetically within each kind (commands alphabetical within a group; arguments in declaration order; options alphabetical by long name), matching a golden-file fixture byte-for-byte under the .NET SDK pinned in `global.json` | `PPDS.DocsGen.Cli.Tests/CliReflectTests.EmitsExpectedMarkdownForFixtureCommandTree` | 🔲 |
| AC-16 | `libs-reflect` emits markdown per public type that has `/// <summary>` (directly or via resolved `<inheritdoc />`); excludes types marked `[EditorBrowsable(Never)]`; members ordered alphabetically within each kind (types, methods, properties); matches a golden-file fixture byte-for-byte under the .NET SDK pinned in `global.json` | `PPDS.DocsGen.Libs.Tests/LibsReflectTests.EmitsOnlyDocumentedCustomerFacingTypes` | 🔲 |
| AC-17 | `mcp-reflect` emits one markdown file per `[McpServerTool]` method with name, description, input schema; tools ordered alphabetically within category; matches a golden-file fixture byte-for-byte under the .NET SDK pinned in `global.json` | `PPDS.DocsGen.Mcp.Tests/McpReflectTests.EmitsExpectedMarkdownForFixtureToolSet` | 🔲 |
| AC-18 | `ext-reflect` emits `commands.md` from a fixture `package.json` with one row per `contributes.commands` entry, ordered alphabetically by command ID, matching a golden-file fixture byte-for-byte | `PPDS.DocsGen.Extension.Tests/ExtReflectTests.EmitsExpectedCommandsTable` | 🔲 |
| AC-19 | All four generators are deterministic: running each twice on the same input produces byte-identical output, under the .NET SDK pinned in `global.json` | `PPDS.DocsGen.*.Tests/*.DeterministicOutputAcrossRuns` (one per generator) | 🔲 |
| AC-20 | `smoke` compiles a fenced `csharp` block extracted from a guide against current product assemblies and reports success | `PPDS.DocsGen.Smoke.Tests/SmokeTests.CompilesValidFencedBlock` | 🔲 |
| AC-21 | `smoke` fails with file-and-line diagnostics when a fenced block references a nonexistent type | `PPDS.DocsGen.Smoke.Tests/SmokeTests.ReportsCompileErrorWithLocation` | 🔲 |
| AC-22 | `smoke` honors `// ignore-smoke` marker on the opening fence line and excludes that block from compilation | `PPDS.DocsGen.Smoke.Tests/SmokeTests.HonorsIgnoreMarker` | 🔲 |
| AC-23 | Generated markdown parses under Docusaurus strict MDX for fixture inputs covering (a) simple generics (`Task<T>`, `List<int>`, `Dictionary<K, V>`), (b) deeply-nested generics (`List<Dictionary<string, Task<T>>>`), and (c) a `<see cref="..."/>` inside prose whose target is a generic type, rendered as inline code within a surrounding sentence — all four generators' outputs tested | `PPDS.DocsGen.Workflow.Tests/MdxParseTests.StrictMdxAcceptsGeneratedReference` | 🔲 |
| AC-24 | Generated output does not contain the bare word "SDK" in prose or headings outside of quoted Microsoft references or code fences (enforces Core Requirement 10) | `PPDS.DocsGen.Workflow.Tests/TerminologyTests.NoSdkInProse` | 🔲 |
| AC-25 | `docs-release.yml` in dry-run mode (workflow_dispatch with `dry_run=true`) produces the expected file tree, a PR body summary, and a baseline rollover diff — all visible as workflow artifacts, without opening any PR | `PPDS.DocsGen.Workflow.Tests/DocsReleaseWorkflowTests.DryRunProducesExpectedArtifacts` | 🔲 |
| AC-26 | `docs-release.yml` rollover logic moves all entries from each library's `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt` (sorted, deduplicated) and clears Unshipped to empty | `PPDS.DocsGen.Workflow.Tests/DocsReleaseWorkflowTests.RolloverMovesUnshippedEntries` | 🔲 |
| AC-27 | `docs-release.yml` aborts when an open rollover PR from a prior tag exists on `main` — exits non-zero with a diagnostic listing the open PR | `PPDS.DocsGen.Workflow.Tests/DocsReleaseWorkflowTests.AbortsOnOpenPriorRolloverPr` | 🔲 |
| AC-28 | After Phase 0 completes, running `dotnet build` on each of the four library projects with `WarningsAsErrors` for PPDS014-016/RS0016-0017 produces zero errors | `PPDS.DocsGen.Workflow.Tests/PhaseZeroCompletionTests.AllFourLibrariesBuildClean` | 🔲 |
| AC-29 | `Microsoft.CodeAnalysis.PublicApiAnalyzers` returns zero RS0016/RS0017 diagnostics against the committed source of each of the four library projects — gated by Phase 0 completion across all four libs (Phase 0 is currently pilot-only: PPDS.Auth has baselines; PPDS.Dataverse / PPDS.Migration / PPDS.Plugins baselines are pending). AC tracked at 🔲 until all four libs reach zero-diagnostic state. | `PPDS.DocsGen.Workflow.Tests/PhaseZeroCompletionTests.RS0016ReturnsZeroDiagnostics` | 🔲 |
| AC-30 | `docs-smoke.yml` runs as `workflow_call` from a caller repository, accepts the caller's docs path as input, and fails the check with stdout pointing at the offending block when compilation fails | `PPDS.DocsGen.Workflow.Tests/DocsSmokeWorkflowTests.FailsOnBadBlockViaWorkflowCall` | 🔲 |

Status key: ✅ covered by passing test · ⚠️ test exists but failing · ❌ no test yet · 🔲 not yet implemented

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Public type inherits from a documented base | `class B : A { }` with no summary on B | PPDS014 fires — inheritance doesn't cascade summaries |
| Generic type parameter | `Task<T>` referenced in prose | MDX parses without error; HTML-entity escaping in prose, inline-code in fenced blocks |
| `[EditorBrowsable(Never)]` on a member | Member hidden, type documented | Type appears in reference; hidden member absent |
| Fenced block using internal-only API | Guide references `internal class Helper` | Smoke test compile fails with CS0122 (inaccessible) — legitimate failure |
| Fenced block with `// ignore-smoke` comment on opening fence | ` ```csharp // ignore-smoke ` as opening fence | Smoke test skips this block; generator emits metadata noting skipped count (AC-22) |
| Library with zero currently-documented public types | Phase 0 incomplete state | `libs-reflect` emits empty `_index.md` with placeholder; not an error |
| Command Description assigned on a separate statement after `new Command(name)` | `var c = new Command("foo"); c.Description = "...";` | PPDS015 inspects the creation expression only; separate-statement assignment is not tracked. The PPDS CLI convention is to pass Description in the 2-arg ctor or object initializer, and no current factory uses the separate-statement pattern. |
| PR moves Unshipped entries to Shipped without tagging | Developer manually edits Shipped in a non-release PR | Not mechanically prevented — treated as a code-review concern. The release workflow is the conventional mover; manual moves are discouraged but legal. |

---

## Core Types

### IReferenceGenerator

Shared contract across the three C#-based generators (`cli-reflect`, `libs-reflect`, `mcp-reflect`). The Node-based `ext-reflect` does not implement this interface — its input is JSON rather than a .NET assembly — but follows the same input/output shape conceptually.

```csharp
public interface IReferenceGenerator
{
    Task<GenerationResult> GenerateAsync(GenerationInput input, CancellationToken ct);
}

public record GenerationInput(string SourceAssemblyPath, string OutputRoot);
public record GenerationResult(IReadOnlyList<GeneratedFile> Files, IReadOnlyList<string> Diagnostics);
public record GeneratedFile(string RelativePath, string Contents);
```

### MdxEscape

Shared utility used by all generators to produce MDX-safe markdown.

```csharp
public static class MdxEscape
{
    public static string Prose(string raw);     // < → &lt;, > → &gt; outside code fences / inline-code spans
    public static string InlineCode(string raw); // adaptive delimiter: (maxBacktickRun+1) backticks;
                                                 // pads leading/trailing space if content starts/ends
                                                 // with a backtick so CommonMark parses correctly
}
```

### Usage Pattern

```csharp
var generator = new CliReferenceGenerator();
var input = new GenerationInput("artifacts/PPDS.Cli.dll", "out/reference/cli");
var result = await generator.GenerateAsync(input, ct);
foreach (var file in result.Files) File.WriteAllText(Path.Combine(input.OutputRoot, file.RelativePath), file.Contents);
```

---

## Error Handling

### Error Types

| Diagnostic | Condition | Recovery |
|------------|-----------|----------|
| PPDS014 | Public type/member missing summary | Add `/// <summary>` or mark `[EditorBrowsable(Never)]` |
| RS0016 | New public API not in Unshipped.txt | Add entry to `PublicAPI.Unshipped.txt` |
| RS0017 | Public API signature changed without baseline update | Update Shipped/Unshipped accordingly |
| PPDS015 | CLI command missing Description | Set `Description` via attribute, initializer, or ctor |
| PPDS016 | MCP tool missing Name or Description | Add to `[McpServerTool(Name = "...", Description = "...")]` |
| Smoke compile failure | Fenced block doesn't compile | Fix the sample or the product surface it references |
| Release: app-token acquisition failure | GitHub App installation revoked or network error | Retry; rotate the app installation if persistent |
| Release: cross-repo push failure | Unique branch name collision (extremely rare — run_id suffix) | Re-run; if recurring, check for workflow retries spawning same run_id |
| Release: open prior rollover PR on main | Previous release's rollover PR not yet merged | Merge or close the open rollover PR, then re-run the release workflow (AC-27) |
| MDX parse failure in generated output | Generator emitted unescaped angle-bracket type reference in prose | Fix the generator — regression of AC-23; the MDX test fixture should reproduce |

### Recovery Strategies

- **Build-time rule violation:** blocks commit via pre-commit hook, blocks PR via CI. No silent skip — the error message points at the exact fix.
- **Release workflow failure:** PR is not created; maintainer re-runs the workflow manually after fixing the underlying issue. No partial output is pushed to ppds-docs.
- **Smoke test failure:** PR in ppds-docs cannot merge; contributor fixes the sample. If the product changed in a breaking way, a paired PR in this repo fixes both simultaneously.

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Empty `PublicAPI.Unshipped.txt` on main between releases | Expected state; release workflow empties it after moving entries to Shipped |
| Generator called with missing source assembly | Exit code 2 with clear message: `error: {assembly} not found — run 'dotnet build' first` |
| Reflection encounters a type loaded from a referenced assembly | Skip; generator only documents types in its target assembly |
| Docs site builds with `strict` disabled temporarily | Smoke test still enforces strict; this is a build-time-only concern |

---

## Design Decisions

### Why Curated Public Surface (Option D) over Blanket Annotation?

**Context:** The four libraries have 0% XML-doc coverage across ~200 public types. Blanket annotation would take 33-200 hours of writing and produce a mix of deep and rote summaries. Consumers of these packages import a narrow customer-facing API, not the entire public surface.

**Decision:** Identify the customer-facing public surface (interfaces, main services, attributes, credential providers — ~30-60 types) and annotate those thoroughly. Mark non-customer-facing public types `[EditorBrowsable(Never)]` (or make them `internal` where safe). Generators document only annotated types.

**Alternatives considered:**

- **Blanket annotation (Option A):** Rejected — 33-200 hours, many low-value summaries, defeats the goal of "useful" docs
- **Defer libraries entirely (Option B):** Rejected — libraries are arguably the hardest surface to navigate without docs; ducking the problem compounds debt
- **Lightweight surface listing (Option C):** Rejected — publishes an actively worse product (type names without meaning) and creates sunk cost that never gets replaced

**Consequences:**

- Positive: consumers get genuinely useful docs for the API they actually call; IntelliSense also improves
- Positive: analyzer enforcement keeps the curated surface true forever
- Negative: requires an upfront triage pass per library to separate customer-facing from implementation detail
- Negative: `[EditorBrowsable(Never)]` is not the same as `internal` — those types remain callable by advanced consumers who know they exist. Acceptable; they stay out of the "supported surface" but aren't hidden from determined users

### Why PublicApiAnalyzers over Custom Snapshot Tooling?

**Context:** Need to detect public-surface changes and enforce that they're intentional.

**Decision:** Adopt `Microsoft.CodeAnalysis.PublicApiAnalyzers` (`PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` convention).

**Alternatives considered:**

- **Custom Roslyn snapshot + diff:** Rejected — reinvents the wheel; weaker edge-case handling than a package used by Roslyn, dotnet/runtime, ASP.NET Core, EF Core, Serilog, NodaTime
- **CS1591 "missing XML doc" warning treated as error:** Rejected — forces blanket annotation, incompatible with Option D
- **Honor system + code review:** Rejected — already failed (current state = 0% coverage)

**Consequences:**

- Positive: battle-tested; every addition/removal/signature change detected with well-understood semantics
- Positive: the baseline diff in a PR IS the changelog — reviewers see exactly what surface is growing
- Negative: mass-refactors (rename, move) produce paired remove+add lines in the baseline file; reviewers must eyeball that pairs match
- Negative: one-time baseline-generation step needed for each of the four libraries (auto-generated in minutes via the analyzer's code-fix suggestion)

### Why Custom Roslyn Walker over DocFX + DocFxMarkdownGen?

**Context:** Need to emit markdown reference from library public surface.

**Decision:** Write a small Roslyn-based generator (~300 LOC) in `scripts/docs-gen/libs-reflect`, matching the architecture of `cli-reflect` and `mcp-reflect`.

**Alternatives considered:**

- **DocFX + DocFxMarkdownGen:** Rejected — DocFxMarkdownGen is a single-contributor GitHub project with meaningful abandonment risk. DocFX itself produces richer output (inheritance diagrams, cross-links), but those features aren't needed for Phase 1.
- **DocFX native markdown output:** Rejected — experimental and less polished; doesn't integrate cleanly with our output layout.
- **Fork DocFxMarkdownGen into the repo:** Rejected — vendoring a less-active tool for a secondary concern adds surface area we'd have to maintain anyway.

**Consequences:**

- Positive: consistent architecture across all three C# generators — one mental model, one test harness, one contribution pattern
- Positive: we own MDX escaping precisely; no fighting a third-party tool's output
- Positive: no abandonment risk
- Negative: less polished output in Phase 1 (no inheritance diagrams, thinner cross-linking); acceptable trade for Phase 1, can layer on later
- Negative: we have to write type-walking logic ourselves; cost absorbed by sharing infrastructure with the other generators

### Why CI-Only Smoke Test (No Test Project)?

**Context:** Fenced-block compilation is inherently a cross-repo concern — the blocks live in ppds-docs, the product lives here.

**Decision:** Implement as a GitHub Actions workflow (`docs-smoke.yml`) that checks out both repositories and runs a small extraction-and-compile tool. No dedicated test project.

**Alternatives considered:**

- **`tests/PPDS.Docs.Smoke.Tests/` project in this repo:** Rejected — a test project would run in unit-test-time CI but still require the cross-repo checkout; adds ceremony without value
- **Test project in ppds-docs that consumes this repo via NuGet:** Rejected — PR against unreleased product changes couldn't be smoke-tested, because the NuGet package lags

**Consequences:**

- Positive: minimal footprint; no new project scaffolding
- Positive: smoke works on in-flight changes in either repo via `workflow_call`
- Negative: slightly harder to run locally than a test project; mitigation is `scripts/docs-gen/smoke` runnable standalone with a repo path argument

### Why GitHub App over Fine-Grained PAT?

**Context:** Cross-repo PR from ppds → ppds-docs requires credentials.

**Decision:** Install a GitHub App (PPDS Docs Bot) on both repositories; workflow obtains a short-lived installation token per run.

**Alternatives considered:**

- **Fine-grained PAT with 90-day rotation:** Rejected — rotation burden forever; secret lives in Actions secrets with real-world exposure
- **`GITHUB_TOKEN`:** Rejected — default cross-repo permission is read-only; chaining via `repository_dispatch` is brittle

**Consequences:**

- Positive: no secret rotation burden — tokens auto-rotate per run
- Positive: follows the pattern Dependabot, Renovate, Changesets use
- Negative: one-time setup — create app, install on both repos, store app ID + private key as Actions secrets
- Negative: app private key is still a secret that must be rotated if compromised; acceptable because compromise is detectable (audit log) and remediation is re-creating the app

### Why One Spec, Not Multiple?

**Context:** Seven components (four generators + analyzer rules + smoke test + release workflow) could each become its own spec.

**Decision:** Single spec covering the docs-generation domain; surface-specific behavior in surface sections per Constitution SL1 (one spec per domain) and SL3 (surface-specific behavior lives in surface sections within the domain spec).

**Alternatives considered:**

- **One spec per generator:** Rejected — fragments a single domain across files; shared infrastructure (analyzer rules, PublicApiAnalyzers setup, GitHub App, MDX escape utility) would force per-generator specs to duplicate or cross-reference each other, against SL1's intent

**Consequences:**

- Positive: one reviewable document; one file to keep current
- Negative: longer than average spec; acceptable because the components share so much infrastructure that splitting them would duplicate sections

### Why a ~300 LOC Per-Generator Target?

**Context:** Complexity in a generator is debt — every line is something the next contributor has to understand before making a confident change.

**Decision:** Aim for ~300 LOC (hard upper of ~400) per generator, but do not enforce as a constraint.

**Alternatives considered:**

- **No target:** Rejected — generators would accrete edge cases until they become unreviewable
- **Enforce as CI check (line-count assertion):** Rejected — cliff-edge enforcement punishes legitimate complexity; reviewers are better positioned to judge

**Consequences:**

- Positive: keeps generators readable; pushes complex output formatting into shared utilities (MdxEscape, banner helpers)
- Negative: occasional friction when a real-world edge case needs 30 more lines; handled by reviewer judgment

### Why "Libraries" Over "SDK"?

**Context:** The parent session's v1-docs-plan.md used "SDK reference" for PPDS.Dataverse et al. But `NoSdkInPresentationAnalyzer.cs` in this repo treats "SDK" as Microsoft's Dataverse SDK (the thing PPDS wraps). Mixing the term for both creates confusion for users who encounter both in docs.

**Decision:** Use "PPDS libraries" (or just "libraries" when context is clear) for the four NuGet packages collectively. Reserve "SDK" for Microsoft's Dataverse SDK. Docs site path is `docs/reference/libraries/` not `/sdk/`.

**Alternatives considered:**

- **Keep "SDK" for PPDS libraries:** Rejected — conflates with Microsoft's SDK
- **"Packages":** Rejected — matches NuGet terminology but ambiguous in prose

**Consequences:**

- Positive: terminology is unambiguous across product, docs, and conversation
- Negative: requires one-time rename of existing `docs/reference/sdk/` paths in ppds-docs; absorbed into this project when generators start emitting into `libraries/`

---

## Extension Points

### Adding a New Reference Surface

If a future surface needs reference generation (e.g., a new TUI help system, a GraphQL schema):

1. **Create generator:** `scripts/docs-gen/{surface}-reflect/` — implement `IReferenceGenerator`
2. **Emit markdown:** output under `docs/reference/{surface}/` in ppds-docs
3. **Wire into release workflow:** add generator step to `.github/workflows/docs-release.yml`
4. **Add drift-check rule (if needed):** new analyzer in `src/PPDS.Analyzers/Rules/` for any new metadata the generator depends on
5. **Tests:** `tests/PPDS.DocsGen.{Surface}Reflect.Tests/` with fixture inputs

**Example skeleton:**

```csharp
public class TuiReferenceGenerator : IReferenceGenerator
{
    public async Task<GenerationResult> GenerateAsync(GenerationInput input, CancellationToken ct)
    {
        // Walk surface, emit markdown, return result
    }
}
```

---

## Configuration

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| `PPDS_DOCS_APP_ID` | string | Yes (release workflow only) | — | GitHub App ID for cross-repo PR |
| `PPDS_DOCS_APP_PRIVATE_KEY` | secret | Yes (release workflow only) | — | PEM-encoded private key for the App |
| `PPDS_DOCS_OUTPUT_ROOT` | path | No | `docs/reference/` | Override generator output root (for local dev) |
| `PPDS_DOCS_SMOKE_PPDS_SHA` | string | No | `main` | Git ref of ppds that `smoke` checks out and builds against. Set by `ppds-docs`'s caller workflow via `workflow_call` inputs: PR-triggered smokes pass `main` (latest product surface); release-triggered smokes pass the release tag SHA. |
| `dotnet_diagnostic.PPDS014.severity` | string | No | `warning` repo-wide (analyzer default per analyzers.md convention), escalated to `error` via `<WarningsAsErrors>` in each of the four library `.csproj` files | Can be escalated or disabled per-file via `.editorconfig`; spec expects the four libs to keep it as error |

Library `.csproj` files add:

```xml
<WarningsAsErrors>PPDS014;PPDS015;PPDS016;RS0016;RS0017</WarningsAsErrors>
```

The .NET SDK version is pinned in `global.json` at the repository root with `rollForward: latestPatch`. Golden-fixture ACs (AC-15–AC-19) depend on this pin for reproducibility; feature-level SDK bumps (e.g. 10.0.x → 10.1.x) can change reflection member ordering and source-generator output, which would flake the goldens. Update the pin deliberately when intentionally moving to a new feature band.

---

## Operations

### GitHub App (PPDS Docs Bot)

| Property | Value |
|----------|-------|
| Owner | Josh Smith (repo admin) |
| App installed on | `power-platform-developer-suite` and `ppds-docs` |
| Scope | Pull-request write on `ppds-docs` only; nothing else |
| App ID storage | GitHub Actions secret `PPDS_DOCS_APP_ID` in `power-platform-developer-suite` |
| Private key storage | GitHub Actions secret `PPDS_DOCS_APP_PRIVATE_KEY` in `power-platform-developer-suite` |
| Token lifetime | Installation tokens auto-rotate per workflow run (no long-lived secret material leaves the App) |
| Rotation policy | Annual rotation; immediate rotation on any suspicion of compromise |
| Revocation playbook | GitHub App settings page → Generate new private key → update `PPDS_DOCS_APP_PRIVATE_KEY` in Actions secrets → previous key invalidated immediately by GitHub (no workflow downtime because the new key starts being used on the next run) |
| Audit trail | GitHub App installation's audit log + per-PR commit attribution (`ppds-docs-bot[bot]` author) |

### Phase 0 Rollout Status (as of initial spec authoring)

| Library | `PublicAPI.Shipped.txt` committed | PPDS014-clean |
|---------|-----------------------------------|---------------|
| PPDS.Auth | ✅ (pilot) | ✅ |
| PPDS.Dataverse | ❌ | ❌ |
| PPDS.Migration | ❌ | ❌ |
| PPDS.Plugins | ❌ | ❌ |

AC-28 and AC-29 stay ungated until all four libraries reach pass state. The phased rollout is tracked outside this spec; completion flips both ACs to ✅ simultaneously.

---

## Related Specs

- [analyzers.md](./analyzers.md) — extended with PPDS014-PPDS016
- [architecture.md](./architecture.md) — generators respect the service-pattern constraint (no business logic in scripts)
- [cli.md](./cli.md) — surface that `cli-reflect` documents
- [mcp.md](./mcp.md) — surface that `mcp-reflect` documents

---

## Changelog

| Date | Change |
|------|--------|
| 2026-04-18 | Initial spec |
| 2026-04-18 | Retarget PPDS015 + `cli-reflect` from Spectre to System.CommandLine; add Operations section (GitHub App runbook, Phase 0 status table); pin .NET SDK via `global.json` for reproducible goldens; strengthen AC-15–19 (alphabetical member ordering + SDK pin), AC-23 (nested generics + `<see cref>` in prose), AC-29 (RS0016 zero-diagnostic gate); add inheritdoc resolution to `libs-reflect` (in-scope) with explicit non-goals for cross-type hyperlinks, overload disambiguation, and generic-arity round-trip |

---

## Roadmap

- Inheritance diagrams in library reference (requires DocFX or manual tree-walk — Phase 1 skips)
- Auto-generated example code per tool/command (requires `[Example]` attribute pattern — future)
- Versioned docs snapshots (Docusaurus `docs-version`) when a v2 release ships
- TUI screen-by-screen reference generator (deferred until demand emerges)
- Code-fix providers for PPDS014-PPDS016 (auto-insert `/// <summary>` templates) — home at `src/PPDS.Analyzers.CodeFixes/`
