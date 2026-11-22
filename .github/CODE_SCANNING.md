# Code Scanning Configuration

This document explains how code scanning tools are configured for the PPDS repository.

## Overview

| Tool | Configuration | Purpose |
|------|---------------|---------|
| **CodeQL** (C#) | `.github/codeql/codeql-config.yml` | Security analysis (style rules disabled) |
| **CodeQL** (TypeScript) | `.github/codeql/codeql-typescript-config.yml` | Security analysis of the VS Code extension |
| **Copilot** | `.github/copilot-instructions.md` | PR review guidance |
| **CodeRabbit** | `.coderabbit.yaml` | PR review |
| **Roslyn Analyzers** | `src/PPDS.Analyzers/` | Compile-time enforcement |

## CodeQL Configuration

For C#, CodeQL runs the `security-and-quality` query suite with style rules disabled.

For the VS Code extension's TypeScript (`src/PPDS.Extension`, tests excluded), CodeQL runs the
`security-extended` suite: security queries only, because ESLint and the type checker already
cover code quality there. No build is needed for that analysis.

### Disabled Rules

| Rule ID | Name | Why Disabled |
|---------|------|--------------|
| `cs/linq/missed-where` | LINQ Where suggestion | Conflicts with project style (prefer explicit foreach) |
| `cs/linq/missed-select` | LINQ Select suggestion | Conflicts with project style |
| `cs/missed-ternary-operator` | Ternary suggestion | Style preference, not quality |
| `cs/nested-if-statements` | Nested if suggestion | Style preference, not quality |
| `cs/catch-of-all-exceptions` | Generic catch clause | Intentional CLI pattern (top-level handlers) |
| `cs/path-combine` | Path.Combine usage | All paths are controlled inputs in CLI context |

### Path Exclusions

Test code is excluded from analysis:
- `tests/**`
- `**/*Tests/**`
- `**/*.Tests/**`

## Bot Review Assessment

Based on PR feedback analysis:

| Bot | Value | Notes |
|-----|-------|-------|
| **Copilot** | MIXED | Good for duplication, resource leaks; noisy for style |
| **CodeQL** | LOW | Mostly style noise for this codebase |

## Roslyn Analyzers (PPDS.Analyzers)

Compile-time enforcement of architectural patterns. Analyzers run during the PPDS.Cli and PPDS.Mcp builds; PPDS001-PPDS013 show as suggestions in the IDE and PPDS015-PPDS016 as warnings, which those projects treat as errors.

### Implemented Rules (All 15)

Severity is the effective level: each analyzer defaults to Warning, and `.editorconfig` lowers PPDS001-PPDS013 to suggestion. The gap in numbering is a retired rule whose ID is not reused (see `DiagnosticIds.cs`). XML documentation on public API in PPDS.Dataverse, PPDS.Migration, PPDS.Auth and PPDS.Plugins is enforced by the compiler's CS1591, which `.editorconfig` makes an error in those four libraries.

| ID | Name | Description | Severity |
|----|------|-------------|----------|
| PPDS001 | NoDirectFileIoInUi | Flags direct File I/O in presentation layer | Suggestion |
| PPDS002 | NoConsoleInServices | Flags Console usage in Application Services | Suggestion |
| PPDS003 | NoUiFrameworkInServices | Flags Terminal.Gui/Spectre.Console in Services | Suggestion |
| PPDS004 | UseStructuredExceptions | Flags raw exception throws in Application Services | Suggestion |
| PPDS005 | NoSdkInPresentation | Flags direct SDK access in presentation layer | Suggestion |
| PPDS006 | UseEarlyBoundEntities | Flags string literals in QueryExpression | Suggestion |
| PPDS007 | PoolClientInParallel | Flags pooled clients held across multiple awaits | Suggestion |
| PPDS008 | UseBulkOperations | Flags individual CRUD calls inside loops | Suggestion |
| PPDS009 | UseAggregateForCount | Flags RetrieveMultiple used only for counting | Suggestion |
| PPDS010 | ValidateTopCount | Flags unbounded QueryExpression without TopCount | Suggestion |
| PPDS011 | PropagateCancellation | Flags async methods that drop CancellationToken | Suggestion |
| PPDS012 | NoSyncOverAsync | Flags `.GetAwaiter().GetResult()`, `.Result`, `.Wait()` | Suggestion |
| PPDS013 | NoFireAndForgetInCtor | Flags async calls in constructors without await | Suggestion |
| PPDS015 | CliCommandNeedsDescription | Flags System.CommandLine commands, options and arguments without a Description | Warning |
| PPDS016 | McpToolNeedsMetadata | Flags `[McpServerTool]` methods missing Name or Description | Warning |

### Suppressing Analyzer Warnings

To suppress a specific warning in code:

```csharp
#pragma warning disable PPDS006
var query = new QueryExpression("customentity"); // No early-bound class available
#pragma warning restore PPDS006
```

To suppress project-wide in `.editorconfig`:

```ini
[*.cs]
dotnet_diagnostic.PPDS006.severity = none
```

### Triage Process for Analyzer Findings

When an analyzer flags code, follow this decision tree:

1. **Is this a real bug?**
   - YES → Create GitHub issue, fix the code
   - NO → Continue to step 2

2. **Is it a framework/API constraint?**
   - YES → Suppress inline with WHY comment
   - NO → Continue to step 3

3. **Can code be refactored to avoid the pattern?**
   - YES → Refactor (no suppression needed)
   - NO → Suppress inline with WHY comment

### Known Safe Patterns

| Pattern | Why Safe | Example |
|---------|----------|---------|
| Sync disposal in Terminal.Gui | Framework requires sync `Application.Run()` | `PpdsApplication.Dispose()` |
| DI factory sync-over-async | Factory delegates cannot be async | `ServiceRegistration.cs` |
| Fire-and-forget with `.ContinueWith()` | Errors are explicitly handled | `SqlQueryScreen` constructor |

### Creating GitHub Issues for Real Bugs

If a finding reveals a real bug:
1. Create issue with `bug` label
2. Reference the analyzer rule (e.g., "Found by PPDS012")
3. Include the file and line number

## Related Issues

- [#231](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/231) - Tune code scanning tools to reduce noise
- [#232](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/232) - Prefer foreach over LINQ
- [#246](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/246) - Analyzer triage process and PPDS013 refinement
