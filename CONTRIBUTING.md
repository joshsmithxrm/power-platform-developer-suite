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

3. Build the solution:
   ```bash
   dotnet build PPDS.sln
   ```

4. Run unit tests:
   ```bash
   dotnet test --filter Category!=Integration
   ```

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

The pre-commit hook automatically runs unit tests (~10s).

## Pull Request Process

1. **Create a PR** targeting `main`
2. **Fill out the PR template** with:
   - Summary of changes
   - Test plan
   - Related issues (use `Closes #N` on separate lines)
3. **Wait for CI** - All checks must pass
4. **Address review feedback** - Respond to comments and make requested changes
5. **Squash and merge** - Once approved

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
│   ├── PPDS.Cli/            # CLI tool + TUI
│   └── PPDS.Mcp/            # MCP server
├── extension/               # VS Code extension
├── tests/                   # Test projects
├── docs/specs/             # Feature specifications
└── templates/claude/       # Claude Code integration
```

## Getting Help

- **Questions**: Open a [Discussion](https://github.com/joshsmithxrm/power-platform-developer-suite/discussions)
- **Bugs**: Open an [Issue](https://github.com/joshsmithxrm/power-platform-developer-suite/issues)
- **Architecture**: Check `docs/specs/` for design decisions

## License

By contributing, you agree that your contributions will be licensed under the MIT License.
