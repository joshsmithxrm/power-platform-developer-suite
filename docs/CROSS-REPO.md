# Cross-Repo Conventions

PPDS is spread over six repositories under `joshsmithxrm`. This page says what each is for and what it takes from the main repo.

| Repository | Purpose | Consumes from the main repo |
|------------|---------|-----------------------------|
| `power-platform-developer-suite` | This repo: CLI, TUI, VS Code extension, MCP server, NuGet libraries | n/a |
| `ppds-docs` | Documentation site | Generated reference markdown (CLI, MCP tools, libraries, extension) from `scripts/docs-gen`; release notes |
| `ppds-skills` | Agent skills package for using the `ppds` CLI and MCP server | Captured `ppds <command> --help` output from the published CLI and MCP packages |
| `ppds-alm` | Reusable CI/CD workflow templates for Power Platform ALM | The published `PPDS.Cli` tool, which its workflows run |
| `ppds-tools` | PowerShell module (`PPDS.Tools`) for plugin deployment and data migration | The published `PPDS.Cli` tool, which its cmdlets wrap |
| `ppds-demo` | Reference Dataverse solution that shows the extension and ecosystem in use | The `PPDS.Plugins` NuGet package and the published tools |

## Docs generation

On a release tag, `.github/workflows/docs-release.yml` regenerates the reference markdown with `scripts/docs-gen` and opens a pull request in `ppds-docs` carrying it. It also opens a pull request here that rolls `PublicAPI.Unshipped.txt` entries into `PublicAPI.Shipped.txt`. Design: `specs/docs-generation.md`. Do not hand-edit generated reference pages in `ppds-docs`; change the source (XML docs, command descriptions, MCP tool metadata) here.

## Rules for work that spans repos

- Open one pull request per repository and link them in both descriptions.
- Merge code first and docs second, so the docs never describe behaviour that is not on `main` yet. If a change is held until a release tag, the docs may merge first; say so in the pull request.
- Skills, templates and the PowerShell module track the *published* CLI, not `main`. A change to command names, flags or output needs a follow-up in those repositories after the release.
