# Code Scanning Configuration

This document explains how code scanning tools are configured for the PPDS repository.

## Overview

| Tool | Configuration | Purpose |
|------|---------------|---------|
| **CodeQL** | `.github/codeql/codeql-config.yml` | Security analysis (style rules disabled) |
| **Copilot** | `.github/copilot-instructions.md` | PR review guidance |
| **Gemini** | `.gemini/config.yaml`, `.gemini/styleguide.md` | PR review with architecture focus |
| **Roslyn Analyzers** | `src/PPDS.Analyzers/` | Compile-time enforcement |

## CodeQL Configuration

CodeQL runs the `security-and-quality` query suite with style rules disabled.

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
| **Gemini** | HIGH | Catches real bugs: concurrency, performance, logic errors |
| **Copilot** | MIXED | Good for duplication, resource leaks; noisy for style |
| **CodeQL** | LOW | Mostly style noise for this codebase |

## Roslyn Analyzers (PPDS.Analyzers)

Compile-time enforcement of architectural patterns. Analyzers run during build and show as warnings in IDE.

### Implemented Rules

| ID | Name | Description | Severity |
|----|------|-------------|----------|
| PPDS006 | UseEarlyBoundEntities | Flags string literals in QueryExpression | Warning |
| PPDS012 | NoSyncOverAsync | Flags `.GetAwaiter().GetResult()`, `.Result`, `.Wait()` | Warning |
| PPDS013 | NoFireAndForgetInCtor | Flags async calls in constructors without await | Warning |

### Planned Rules

| ID | Name | Description | Source |
|----|------|-------------|--------|
| PPDS001 | NoDirectFileIoInUi | UI layer using File.Read/Write directly | ADR-0024 |
| PPDS002 | NoConsoleInServices | Service using Console.WriteLine | ADR-0015 |
| PPDS003 | NoUiFrameworkInServices | Service referencing Spectre/Terminal.Gui | ADR-0025 |
| PPDS004 | UseStructuredExceptions | Service throwing raw Exception | ADR-0026 |
| PPDS005 | NoSdkInPresentation | CLI command calling ServiceClient directly | ADR-0015 |
| PPDS007 | PoolClientInParallel | Pool client acquired outside parallel loop | ADR-0002/0005 |
| PPDS008 | UseBulkOperations | Loop with single Delete/Update calls | Gemini PR#243 |
| PPDS011 | PropagateCancellation | Async method not passing CancellationToken | Gemini PR#242 |

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

## Architecture (ADRs)

Bot instructions reference these Architecture Decision Records:

| ADR | Summary |
|-----|---------|
| [0015](../docs/adr/0015_APPLICATION_SERVICE_LAYER.md) | Application Services for CLI/TUI/Daemon |
| [0024](../docs/adr/0024_SHARED_LOCAL_STATE.md) | Shared local state architecture |
| [0025](../docs/adr/0025_UI_AGNOSTIC_PROGRESS.md) | UI-agnostic progress reporting |
| [0026](../docs/adr/0026_STRUCTURED_ERROR_MODEL.md) | Structured error model |

## Related Issues

- [#231](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/231) - Tune code scanning tools to reduce noise
- [#232](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/232) - ADR-0024 (style) - Prefer foreach over LINQ
- [#246](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/246) - Analyzer triage process and PPDS013 refinement
