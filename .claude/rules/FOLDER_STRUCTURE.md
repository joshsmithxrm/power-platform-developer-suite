# PPDS Repository Structure

PPDS repos use `ppds*` naming convention in a shared parent directory:

| Folder | Purpose |
|--------|---------|
| `ppds` | Power Platform Developer Suite: CLI, TUI, MCP, Auth, Migration, Plugins, Dataverse, Extension |
| `ppds-docs` | Documentation site (Docusaurus) |
| `ppds-alm` | CI/CD templates |
| `ppds-tools` | PowerShell module |
| `ppds-demo` | Reference implementation |
| `ppds-*` (worktrees) | Parallel development (e.g., `ppds-repo-consolidation`) |

The `ppds` folder contains the .NET solution with multiple projects - these are NOT separate repos.
