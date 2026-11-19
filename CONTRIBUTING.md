# Contributing to PPDS

Thank you for your interest in contributing to Power Platform Developer Suite.
This guide covers setup, building and testing each surface, and what to expect
when you open a pull request. The project rules that are easy to miss in the code
are summarised in [AGENTS.md](AGENTS.md); read it whether or not you use an AI tool.

## Prerequisites

Install only what the surface you are changing needs:

| You are changing | You need |
|------------------|----------|
| CLI, TUI, MCP server or a NuGet library | .NET SDK 10.0.115 or a later 10.0.1xx patch (selected by `global.json`), plus the .NET 8 and 9 runtimes to run the test commands below |
| VS Code extension | The .NET SDK above, Node.js 26 (the version in `.nvmrc`) and VS Code |
| TUI snapshot tests | The .NET SDK and Node.js |
| Python scripts under `scripts/` or `tests/` | Python 3 with `pytest` and `pyyaml` |
| PowerShell scripts under `scripts/` | PowerShell 7 |

Building a CLI fix needs only the .NET SDK and Git. Running the tests needs the
.NET 8 and 9 runtimes as well: most test projects target `net8.0;net9.0;net10.0`
and the `PPDS.DocsGen.*` test projects target `net8.0` only, so `dotnet test
PPDS.sln` runs on all three. With only the SDK, test one multi-targeted project
on .NET 10, for example
`dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj -f net10.0 --filter "Category!=Integration"`.

The Node version above is for repository tooling. The extension itself runs on
the Node bundled with VS Code and must stay compatible with Node 22 and the
minimum VS Code version in `src/PPDS.Extension/package.json`; CI checks both.

## Get the code

```bash
git clone https://github.com/joshsmithxrm/power-platform-developer-suite.git
cd power-platform-developer-suite
```

Opening `ppds.code-workspace` in VS Code gives you solution navigation and the
debug configurations below. You can also open the root folder for .NET-only work,
or `src/PPDS.Extension/` for extension-only work. A dev container is defined in
`.devcontainer/`.

| Debug configuration (F5) | Purpose |
|--------------------------|---------|
| `.NET: Debug TUI` | Launch the interactive TUI |
| `.NET: Debug CLI` | Debug the CLI with custom arguments |
| `.NET: Debug Daemon` | Run the RPC daemon the extension talks to |
| `Extension: Run` | Launch the extension development host |
| `Full-Stack: Daemon + Extension` | Debug both sides of the RPC connection |

## Build and test

Unit tests need no Dataverse environment and no credentials.

### .NET (CLI, TUI, MCP, libraries)

```bash
dotnet build PPDS.sln -v q
dotnet test PPDS.sln --filter "Category!=Integration" -v q

# One test project, which is much faster
dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category!=Integration"

# TUI logic only
dotnet test PPDS.sln --filter "Category=TuiUnit"
```

To try your build as the installed `ppds` tool, run `pwsh scripts/Install-LocalCli.ps1`.
It packs the CLI and reinstalls it as a global tool.

If a Windows build fails with "used by another process", something (often a
running `ppds` daemon, TUI or VS Code window) holds files under `bin/`. Close it,
or build only the projects you changed, for example
`dotnet build src/PPDS.Cli/PPDS.Cli.csproj -v q`.

### VS Code extension

```bash
npm install --prefix src/PPDS.Extension
npm run typecheck:all --prefix src/PPDS.Extension
npm run ext:lint
npm run ext:test
npm run ext:compile
```

`npm run ext:compile` runs esbuild, which does not type-check. Run
`typecheck:all` too, or type errors reach CI (and users) unnoticed. CI also runs
the end-to-end suite (`npm run ext:test:e2e`, which downloads VS Code) and an
advisory dead-code check (`npm run dead-code --prefix src/PPDS.Extension`).
To install a dev-versioned build into your own VS Code, run `npm run ext:local`.

See [src/PPDS.Extension/DEVELOPMENT.md](src/PPDS.Extension/DEVELOPMENT.md) for how
the extension is put together and how to add panels and daemon methods.

### TUI snapshot tests

```bash
npm install --prefix tests/PPDS.Tui.E2eTests
dotnet build src/PPDS.Cli/PPDS.Cli.csproj -f net10.0
npm run tui:test
```

Snapshots in `tests/PPDS.Tui.E2eTests/tests/__snapshots__/` are committed
baselines. After an intentional visual change, regenerate them with
`npm run tui:test:update` and commit them with the code that changed them.

### Python scripts and CI policy

```bash
pip install pytest pyyaml
python -m pytest tests/ -q
```

### Live and integration tests

Tests marked `[Trait("Category", "Integration")]`, mostly in `tests/PPDS.LiveTests`,
run against a real Dataverse environment and skip themselves when no credentials
are configured. You do not need to run them. No workflow that runs on pull
requests uses Dataverse credentials, so they never run on fork PRs; the
`Integration Tests` workflow is currently held and runs no live tests at all.
If you do run them locally, do not point them at an environment you care about.

### Single-file plugin extraction

Changes to plugin extraction are checked by the `single-file-extraction` job in
`.github/workflows/test.yml`, which publishes the real single-file CLI for every
shipped runtime and runs `scripts/ci/smoke_single_file_extraction.py` against it.
The job's steps are the commands to reproduce it locally. The smoke script requires
the account's reference-assembly cache to be absent and never removes an existing
one: use `--allow-existing-cache` for an explicitly reported local warm-cache
check, which does not prove cold creation.

## Writing tests

| Area | Kind of test | Category trait | Notes |
|------|--------------|----------------|-------|
| Application Services | Unit, mocked dependencies | `Unit` | Mock `IDataverseConnectionPool` and `IProgressReporter` |
| Dataverse SDK behaviour | FakeXrmEasy | none needed | In `tests/PPDS.Dataverse.IntegrationTests`, based on `FakeXrmEasyTestsBase`; runs in the unit filter |
| Query engine | Unit, pure functions | `Unit` | Deterministic transforms |
| CLI commands and MCP tools | Unit, mocked services | `Unit` | Handlers are thin; test the service, plus parameter validation |
| TUI logic | Unit | `TuiUnit` | Extracted logic, not Terminal.Gui rendering |
| Extension panels | Vitest | n/a | Message contracts and handler behaviour |
| Live Dataverse | Integration | `Integration` | In `tests/PPDS.LiveTests` |

Tests mirror the source path under `tests/`, and test classes are named
`{ClassUnderTest}Tests`:

```
src/PPDS.Cli/Services/ConnectionService.cs
tests/PPDS.Cli.Tests/Services/ConnectionServiceTests.cs
```

When a test covers an acceptance criterion from a spec in `specs/`, tag it with
`[Trait("AC", "AC-12")]` and reference the test from the spec's AC table.

`codecov.yml` configures a non-informational patch-coverage status for new code
with an 80% target and a 5% threshold, so Codecov reports it as pass or fail. Branch protection requires
only `build-status` and `test-status`, so the Codecov status does not block merging.
`scripts/Test-NewCodeCoverage.ps1` gives a similar local view.

## The optional pre-commit hook

`scripts/hooks/pre-commit` runs checks on what you have staged. It is not
installed when you clone. Running `npm install --prefix src/PPDS.Extension` turns
it on (the extension's `prepare` script sets `core.hooksPath`), or enable it yourself:

```bash
git config core.hooksPath scripts/hooks
```

When C# files are staged it runs a full `dotnet build PPDS.sln` and the whole .NET
unit suite, which takes minutes, not seconds. When extension TypeScript is staged
it runs `typecheck:all`, ESLint and Vitest. Skip it for one commit with
`git commit --no-verify`, or turn it off with `git config --unset core.hooksPath`.
CI runs the same checks either way.

## Making changes

Create a branch from `main` (`feat/...`, `fix/...` or `chore/...`), keep commits
focused, follow the patterns in the surrounding code, and add or update tests.

Maintainers follow the shared [contributor lifecycle](docs/CONTRIBUTOR-LIFECYCLE.md)
when choosing where work lands, verifying actual squash credit, and assigning
post-landing communication and source-PR disposition. This policy applies with
any tool; the local provenance check does not require an AI client.

### Changes that cross surfaces

If one change touches the same concept in more than one surface (for example a
daemon RPC method in `src/PPDS.Cli/Commands/Serve/Handlers/` and its TypeScript
types in `src/PPDS.Extension/src/types.ts`, or an MCP tool and a CLI command), check:

- Field names and types match on both sides.
- Nullability agrees between the C# DTOs and the TypeScript types.
- Error codes defined in one layer are the ones the other layer handles.
- Default values are the same on every surface.

### Rebasing and `PublicAPI.Unshipped.txt`

`src/PPDS.Auth/PublicAPI.Unshipped.txt` tracks that package's public API. If it
conflicts during a rebase, do not hand-merge it: resolve it with
`git checkout --theirs src/PPDS.Auth/PublicAPI.Unshipped.txt`, finish the rebase
and rebuild. The public API analyzer (RS0016, RS0017) then reports any entry that
is missing or stale, and the IDE code fix adds it. Hand-merged edits produce
phantom API-drift conflicts.

## Pull requests

### What to send

- Bug fixes, tests, documentation fixes and small focused improvements are
  welcome without asking first.
- Open an issue before starting a new feature, a new command or panel, a change
  to public library API, or anything that changes behaviour across surfaces, so
  we can agree on the approach before you invest time.
- Keep each PR to one fix or feature. PRs over 50 files or 2000 changed lines are
  flagged as large; they are not blocked, but split them where you can.

### Do I need a spec?

Not for a bug fix or a small change. Specs in `specs/` describe larger features;
if your change affects one, say so in the PR and a maintainer will update the spec.
If you are proposing a substantial new feature, the issue is the place to discuss
whether it needs one.

### Checks

Branch protection requires two checks: `build-status` (the .NET build and the
extension's typecheck, lint, build, unit and end-to-end tests) and `test-status`
(the .NET unit tests, single-file extraction, VSIX and Python checks). Everything
else is advisory: Codecov, CodeQL, the Pre-Merge Gate (PR size reporting and a
rule for Dependabot major updates), the TUI end-to-end workflow, and any
automated review comments. No approving review is required by branch protection,
but a maintainer reviews every outside contribution before merging.

### Process

1. Open the PR against `main` and fill in the template: a summary, what you ran
   to test it, and any breaking changes. Put `Closes #N` on its own line for
   each issue it resolves. Use conventional-commit form,
   `type(scope): summary` (for example `fix(cli): ...` or `docs: ...`), for the PR
   title and for your commit subjects: PRs are squash-merged, and the squash commit
   on `main` takes its subject from the PR title, or from the commit subject when
   the PR has a single commit.
2. Update the affected package's `src/<package>/CHANGELOG.md` for user-visible changes.
   Do not change version numbers; versions come from git tags (MinVer).
3. Wait for CI, then respond to review comments.
4. A maintainer squash-merges the PR. Merge mechanics are in
   [docs/MERGE-POLICY.md](docs/MERGE-POLICY.md).

Workflow changes must follow the [Actions token permission policy](docs/CI-PERMISSIONS.md).
Never commit secrets (`.env` files, credentials, certificates or key files).

## AI-assisted contributions

AI-assisted contributions are welcome. You are responsible for the change: you
should understand it, run the tests, and say in the PR what you verified.

[AGENTS.md](AGENTS.md) holds the project rules and commands for any coding agent;
`CLAUDE.md` imports it for Claude Code. The `.claude/` folder contains skills and
two safety hooks used by the maintainer (one refuses edits to strong-name `.snk`
key files; one guards `ppds` commands against live environments and is off until
you configure an allowlist, see `docs/SAFE-SHAKEDOWN.md`). All of it is optional,
and nothing in it is needed to get a PR merged. Put your own Claude Code
permission preferences in `.claude/settings.local.json`, which is gitignored.
The `ppds` guard does not read that file: set your own safe environments in the
`PPDS_SAFE_ENVS` environment variable, and keep the tracked `.claude/settings.json`
list for environments the whole team shares.

## Code standards

The architecture rules are in [AGENTS.md](AGENTS.md) and, in full, in
[specs/CONSTITUTION.md](specs/CONSTITUTION.md). For C# in addition:

- Use file-scoped namespaces.
- Use the early-bound entity classes with their `EntityLogicalName` and `Fields.*`
  constants, not late-bound `Entity` and magic strings.
- Add XML documentation to public APIs.
- Avoid hardcoded paths and GUIDs.

The analyzers in `src/PPDS.Analyzers` run on PPDS.Cli and PPDS.Mcp. PPDS001 to
PPDS013 are at suggestion severity, so they show in the IDE but do not fail the
build. PPDS015 (CLI command descriptions) and PPDS016 (MCP tool metadata) are
warnings, and both projects treat warnings as errors, so those do fail it. PPDS014
(XML docs) reports only in PPDS.Dataverse, PPDS.Migration, PPDS.Auth and
PPDS.Plugins, none of which loads the analyzers, so it does not fire in any build.

| Pattern | Where to look |
|---------|---------------|
| Connection pooling | `src/PPDS.Dataverse/Pooling/DataverseConnectionPool.cs` |
| Bulk operations | `src/PPDS.Dataverse/BulkOperations/BulkOperationExecutor.cs` |
| Application Services | `src/PPDS.Cli/Services/` |
| Structured errors | `src/PPDS.Cli/Infrastructure/Errors/` |

For a guided tour of the core libraries, see [docs/ONBOARDING.md](docs/ONBOARDING.md).

## Project structure

```
power-platform-developer-suite/
├── src/
│   ├── PPDS.Plugins/      # Plugin registration attributes (net462)
│   ├── PPDS.Dataverse/    # Connection pooling, bulk operations, metadata
│   ├── PPDS.Migration/    # Data migration engine
│   ├── PPDS.Auth/         # Authentication profiles and credential storage
│   ├── PPDS.Query/        # SQL query engine and ADO.NET provider
│   ├── PPDS.Cli/          # CLI, TUI, RPC daemon and Application Services (command: ppds)
│   ├── PPDS.Mcp/          # MCP server
│   ├── PPDS.Analyzers/    # Roslyn analyzers for the project's rules
│   └── PPDS.Extension/    # VS Code extension
├── tests/                 # Test projects, TUI snapshot tests, Python CI tests
├── scripts/               # Build, install and CI helper scripts
├── specs/                 # Feature specifications and the constitution
└── docs/                  # Maintainer and process documentation
```

Each package has its own README: [Plugins](src/PPDS.Plugins/README.md),
[Dataverse](src/PPDS.Dataverse/README.md), [Migration](src/PPDS.Migration/README.md),
[Auth](src/PPDS.Auth/README.md), [Query](src/PPDS.Query/README.md),
[Cli](src/PPDS.Cli/README.md), [Mcp](src/PPDS.Mcp/README.md) and
[Extension](src/PPDS.Extension/README.md).

## Getting help

- Questions: open a [Discussion](https://github.com/joshsmithxrm/power-platform-developer-suite/discussions).
- Bugs: open an [Issue](https://github.com/joshsmithxrm/power-platform-developer-suite/issues).
- Design background: see `specs/`.

## License

By contributing, you agree that your contributions will be licensed under the MIT License.
