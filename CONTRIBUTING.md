# Contributing to PPDS

Thank you for your interest in contributing to Power Platform Developer Suite! This document provides guidelines for contributing to the project.

## Getting Started

### Prerequisites

- .NET SDK 8.0+
- Node.js 20+ (for extension development)
- PowerShell 7+ (for scripts)
- Git

### Setting Up the Development Environment

1. Clone the repository:
   ```bash
   git clone https://github.com/joshsmithxrm/power-platform-developer-suite.git
   cd power-platform-developer-suite
   ```

2. Open the workspace in VS Code (recommended):
   ```bash
   code ppds.code-workspace
   ```

   This provides .NET solution navigation with C# Dev Kit, extension F5 debugging with correct path resolution, unified build/test tasks for both .NET and TypeScript, and compound debugging for CLI + Extension integration testing.

   Alternatives: open the root folder for .NET-only work, or `src/PPDS.Extension/` for extension-only work.

3. Build the solution:
   ```bash
   dotnet build PPDS.sln

   # Build extension
   cd src/PPDS.Extension && npm run compile
   ```

4. Run unit tests:
   ```bash
   dotnet test --filter Category!=Integration
   ```

### Debugging (F5)

| Configuration | Purpose |
|---------------|---------|
| `.NET: Debug TUI` | Launch interactive TUI |
| `.NET: Debug CLI` | Debug CLI with custom args |
| `.NET: Debug Daemon` | Run RPC daemon for extension |
| `Extension: Run` | Launch extension dev host |
| `Full-Stack: Daemon + Extension` | Debug both sides of RPC |

## Development Workflow

### Branch Strategy

- `main` - Protected branch, always deployable
- `feat/*` - New features
- `fix/*` - Bug fixes
- `chore/*` - Maintenance tasks

Create a branch from `main` for your work:
```bash
git checkout -b feat/your-feature-name
```

### Making Changes

1. Make your changes in small, focused commits
2. Follow existing code patterns and conventions
3. Add or update tests for your changes
4. Ensure all tests pass before submitting a PR

### Testing

| Test Category | Command | When to Run |
|---------------|---------|-------------|
| Unit tests | `dotnet test --filter Category!=Integration` | Before every commit |
| TUI tests | `dotnet test --filter Category=TuiUnit` | When modifying TUI code |
| Integration tests | `dotnet test --filter Category=Integration` | Requires Dataverse connection |

The pre-commit hook automatically runs unit tests (~10s). It is configured
by `npm install`; if you need to enable it manually:

```bash
git config core.hooksPath scripts/hooks
```

The hook runs:
- **C# staged:** `dotnet build` + `dotnet test` (unit only)
- **TS staged:** typecheck + eslint + Vitest

Drop-in scripts under `scripts/hooks/pre-commit.d/` run before the .NET / TS
gates. See `docs/CLAUDE-MD-GOVERNANCE.md` for the CLAUDE.md gate that lives there.

### Coverage Bar

PRs must achieve at least **80% patch coverage** on new code, enforced by
Codecov. See `Test-NewCodeCoverage.ps1` (in `scripts/`) for the local
check that mirrors CI.

### File Placement

Tests mirror the source path under `tests/`:

```
src/PPDS.Cli/Services/AuthService.cs
tests/PPDS.Cli.Tests/Services/AuthServiceTests.cs
```

Per-area conventions (which framework to use where, trait categories) live
in the `test-conventions` skill: `.claude/skills/test-conventions/SKILL.md`.

## Pull Request Process

1. **Create a PR** targeting `main`
2. **Fill out the PR template** with:
   - Summary of changes
   - Test plan
   - Related issues (use `Closes #N` on separate lines)
3. **Wait for CI** - All checks must pass
4. **Address review feedback** - Respond to comments and make requested changes
5. **Squash and merge** - Once approved

For merge mechanics — when auto-merge is appropriate, when it isn't, and squash/branch-protection rules — see [`docs/MERGE-POLICY.md`](docs/MERGE-POLICY.md).

### PR Guidelines

- Keep PRs focused - one feature/fix per PR
- Include tests for new functionality
- Update documentation if needed
- Don't commit files with secrets (.env, credentials.json)

## Code Standards

### C# Conventions

- Use file-scoped namespaces
- Use early-bound entity classes (not late-bound `Entity`)
- Use `EntityLogicalName` and `Fields.*` constants
- Add XML documentation to public APIs
- Follow patterns in existing code

### Key Patterns

| Pattern | Reference |
|---------|-----------|
| Connection pooling | `ServiceClientPool.cs` |
| Bulk operations | `BulkOperationExecutor.cs` |
| CLI output | `src/PPDS.Cli/Services/` |
| Application services | `src/PPDS.Cli/Services/` |

### What to Avoid

- Creating new `ServiceClient` per request (use pooling)
- Magic strings for entity names
- `Console.WriteLine` for status (use `Console.Error.WriteLine`)
- Hardcoded paths or GUIDs

## Project Structure

```
power-platform-developer-suite/
├── src/
│   ├── PPDS.Plugins/        # Plugin attributes
│   ├── PPDS.Dataverse/      # Connection pooling, bulk ops
│   ├── PPDS.Migration/      # Data migration engine
│   ├── PPDS.Auth/           # Authentication profiles
│   ├── PPDS.Query/          # SQL query engine + ADO.NET provider
│   ├── PPDS.Cli/            # CLI tool + TUI (NuGet package name; installed tool command is `ppds`)
│   ├── PPDS.Mcp/            # MCP server
│   └── PPDS.Extension/       # VS Code extension
├── tests/                   # Test projects
├── specs/                  # Feature specifications
└── templates/claude/       # Claude Code integration
```

### Per-package READMEs

| Package | README |
|---------|--------|
| PPDS.Plugins | [src/PPDS.Plugins/README.md](src/PPDS.Plugins/README.md) |
| PPDS.Dataverse | [src/PPDS.Dataverse/README.md](src/PPDS.Dataverse/README.md) |
| PPDS.Migration | [src/PPDS.Migration/README.md](src/PPDS.Migration/README.md) |
| PPDS.Auth | [src/PPDS.Auth/README.md](src/PPDS.Auth/README.md) |
| PPDS.Query | [src/PPDS.Query/README.md](src/PPDS.Query/README.md) |
| PPDS.Cli | [src/PPDS.Cli/README.md](src/PPDS.Cli/README.md) |
| PPDS.Mcp | [src/PPDS.Mcp/README.md](src/PPDS.Mcp/README.md) |
| PPDS.Extension | [src/PPDS.Extension/README.md](src/PPDS.Extension/README.md) |

## Getting Help

- **Questions**: Open a [Discussion](https://github.com/joshsmithxrm/power-platform-developer-suite/discussions)
- **Bugs**: Open an [Issue](https://github.com/joshsmithxrm/power-platform-developer-suite/issues)
- **Architecture**: Check `specs/` for design decisions

## License

By contributing, you agree that your contributions will be licensed under the MIT License.
