# Roslyn Analyzers

**Status:** Partial (3 of 13 rules implemented)
**Version:** 1.0
**Last Updated:** 2026-01-27
**Code:** [src/PPDS.Analyzers/](../src/PPDS.Analyzers/)

---

## Overview

PPDS.Analyzers is a Roslyn-based static code analysis package that enforces architectural patterns and best practices at compile time. Analyzers run during compilation and surface warnings in the IDE, catching common mistakes before code is committed.

### Goals

- **Compile-Time Enforcement**: Catch architectural violations and common bugs during development, not in production
- **Zero Runtime Cost**: Analysis runs only during compilation; no runtime overhead
- **Self-Documenting**: Error messages explain both what's wrong and how to fix it

### Non-Goals

- Runtime behavior modification (analyzers only report diagnostics)
- Code fixing (code fix providers are planned but not yet implemented)
- Security scanning (use CodeQL for security vulnerabilities)

---

## Architecture

```
┌─────────────────────┐
│   Developer IDE     │  ← Real-time feedback as code is written
│   (VS/Rider/etc.)   │
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│   Roslyn Compiler   │  ← Invokes analyzers during compilation
│ (csc.exe / dotnet)  │
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  PPDS.Analyzers     │  ← DiagnosticAnalyzer implementations
│   (3 implemented,   │
│    10 planned)      │
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│   Diagnostics       │  ← Warnings in Error List / build output
│ (PPDS001-PPDS013)   │
└─────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `DiagnosticIds` | Central registry of all diagnostic codes and categories |
| `NoSyncOverAsyncAnalyzer` | Detects sync-over-async patterns that cause deadlocks |
| `NoFireAndForgetInCtorAnalyzer` | Detects unawaited async calls in constructors |
| `UseEarlyBoundEntitiesAnalyzer` | Flags string literals in QueryExpression |

### Dependencies

- Uses: [Microsoft.CodeAnalysis.CSharp](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp) (5.0.0)
- Distributed as: Development-only NuGet package (`DevelopmentDependency`)

---

## Specification

### Core Requirements

1. Analyzers must support concurrent execution (`EnableConcurrentExecution`)
2. Analyzers must skip generated code analysis (`ConfigureGeneratedCodeAnalysis`)
3. All diagnostics must have clear, actionable message formats
4. Diagnostics must be suppressible via `#pragma` or `.editorconfig`

### Diagnostic ID Ranges

| Range | Category | Purpose |
|-------|----------|---------|
| PPDS001-007 | Architecture | Layer separation, dependency rules |
| PPDS008-010 | Performance | Query efficiency, bulk operations |
| PPDS011-013 | Correctness | Async/await, cancellation, concurrency |

### Severity Levels

All implemented rules use `Warning` severity with `isEnabledByDefault: true`. This allows:
- IDE highlighting during development
- Build continues (not blocking CI)
- Suppressible when false positive

---

## Core Types

### DiagnosticIds

Central registry of all PPDS diagnostic codes ([`DiagnosticIds.cs:1-36`](../src/PPDS.Analyzers/DiagnosticIds.cs#L1-L36)):

```csharp
public static class DiagnosticIds
{
    public const string NoSyncOverAsync = "PPDS012";
    public const string NoFireAndForgetInCtor = "PPDS013";
    public const string UseEarlyBoundEntities = "PPDS006";
    // ... 10 more planned rules
}
```

### DiagnosticCategories

Categories for grouping related rules ([`DiagnosticIds.cs:30-36`](../src/PPDS.Analyzers/DiagnosticIds.cs#L30-L36)):

```csharp
public static class DiagnosticCategories
{
    public const string Architecture = "PPDS.Architecture";
    public const string Performance = "PPDS.Performance";
    public const string Correctness = "PPDS.Correctness";
    public const string Style = "PPDS.Style";
}
```

---

## Implemented Rules

### PPDS006: UseEarlyBoundEntities

**What it detects:** String literals passed to `QueryExpression` constructor instead of early-bound `EntityLogicalName` constants.

**Message:** `Use '{0}.EntityLogicalName' instead of string literal "{1}"`

**Category:** Style | **Severity:** Warning

**Implementation:** [`UseEarlyBoundEntitiesAnalyzer.cs:1-105`](../src/PPDS.Analyzers/Rules/UseEarlyBoundEntitiesAnalyzer.cs#L1-L105)

**Example violation:**
```csharp
// Bad: String literal can have typos
var query = new QueryExpression("account");

// Good: Compile-time checked constant
var query = new QueryExpression(Account.EntityLogicalName);
```

**Detection logic:**
1. Register for `ObjectCreationExpression` syntax nodes
2. Check if creating `Microsoft.Xrm.Sdk.Query.QueryExpression`
3. Check if first argument is a string literal
4. Map entity name to suggested early-bound class

**Known entity mappings:** 19 common Dataverse entities are mapped to their PascalCase class names. Unknown entities get simple first-letter capitalization.

---

### PPDS012: NoSyncOverAsync

**What it detects:** Synchronous blocking on async operations that can cause deadlocks:
- `.Result` property access
- `.Wait()` method calls
- `.GetAwaiter().GetResult()` pattern

**Message:** `'{0}' can cause deadlocks; use 'await' instead`

**Category:** Correctness | **Severity:** Warning

**Implementation:** [`NoSyncOverAsyncAnalyzer.cs:1-131`](../src/PPDS.Analyzers/Rules/NoSyncOverAsyncAnalyzer.cs#L1-L131)

**Example violations:**
```csharp
// Bad: Can deadlock in sync context
var result = someTask.Result;
someTask.Wait();
var value = someTask.GetAwaiter().GetResult();

// Good: Propagate async
var result = await someTask;
```

**Detection logic:**
1. Register for `SimpleMemberAccessExpression` (catches `.Result`)
2. Register for `InvocationExpression` (catches `.Wait()` and `.GetAwaiter().GetResult()`)
3. Verify the expression type is `Task`, `Task<T>`, `ValueTask`, or `ValueTask<T>`
4. Report diagnostic with the specific pattern as message argument

---

### PPDS013: NoFireAndForgetInCtor

**What it detects:** Async methods called in constructors without `await`, causing race conditions where async operations may complete before the object is fully initialized.

**Message:** `Async method '{0}' called in constructor without await; use Loaded event for async initialization`

**Category:** Correctness | **Severity:** Warning

**Implementation:** [`NoFireAndForgetInCtorAnalyzer.cs:1-180`](../src/PPDS.Analyzers/Rules/NoFireAndForgetInCtorAnalyzer.cs#L1-L180)

**Example violation:**
```csharp
public class MyView
{
    public MyView()
    {
        // Bad: Race condition - operation may complete after constructor
        LoadDataAsync();
    }
}

// Good: Use event-based initialization
public class MyView
{
    public MyView()
    {
        Loaded += async (s, e) => await LoadDataAsync();
    }
}
```

**Detection logic:**
1. Register for `ConstructorDeclaration` syntax nodes
2. Find all `InvocationExpression` descendants
3. Skip if invocation is awaited (check parent for `AwaitExpressionSyntax`)
4. Check if method returns `Task` or `ValueTask` variants
5. Skip safe patterns: `Task.FromResult`, `Task.FromException`, `Task.FromCanceled`
6. Skip intentional fire-and-forget with `.ContinueWith()` error handling

---

## Planned Rules

| ID | Name | Category | Description | Source |
|----|------|----------|-------------|--------|
| PPDS001 | NoDirectFileIoInUi | Architecture | UI layer using File.Read/Write directly | ADR-0024 |
| PPDS002 | NoConsoleInServices | Architecture | Service using Console.WriteLine | ADR-0015 |
| PPDS003 | NoUiFrameworkInServices | Architecture | Service referencing Spectre/Terminal.Gui | ADR-0025 |
| PPDS004 | UseStructuredExceptions | Architecture | Service throwing raw Exception | ADR-0026 |
| PPDS005 | NoSdkInPresentation | Architecture | CLI command calling ServiceClient directly | ADR-0015 |
| PPDS007 | PoolClientInParallel | Architecture | Pool client acquired outside parallel loop | ADR-0002/0005 |
| PPDS008 | UseBulkOperations | Performance | Loop with single Create/Update/Delete calls | Gemini PR#243 |
| PPDS009 | UseAggregateForCount | Performance | RetrieveMultiple used just for counting | — |
| PPDS010 | ValidateTopCount | Performance | Unbounded TopCount in query | — |
| PPDS011 | PropagateCancellation | Correctness | Async method not passing CancellationToken | Gemini PR#242 |

---

## Error Handling

### Suppression Methods

**Inline suppression:**
```csharp
#pragma warning disable PPDS006
var query = new QueryExpression("customentity"); // No early-bound class
#pragma warning restore PPDS006
```

**Project-wide via `.editorconfig`:**
```ini
[*.cs]
dotnet_diagnostic.PPDS006.severity = none
```

**Bulk suppression via `GlobalSuppressions.cs`:**
```csharp
[assembly: SuppressMessage("PPDS.Style", "PPDS006", Justification = "Custom entities")]
```

### Known Safe Patterns

| Pattern | Why Safe | Suppress? |
|---------|----------|-----------|
| Sync disposal in Terminal.Gui | Framework requires sync `Application.Run()` | Yes |
| DI factory sync-over-async | Factory delegates cannot be async | Yes |
| Fire-and-forget with `.ContinueWith()` | Errors explicitly handled | Auto-skipped |
| `Task.FromResult()` in constructor | No async operation | Auto-skipped |

---

## Design Decisions

### Why Roslyn Analyzers Over CodeQL?

**Context:** Need to enforce PPDS-specific patterns that CodeQL doesn't understand (early-bound entities, connection pooling).

**Decision:** Create custom Roslyn analyzers for architectural enforcement; keep CodeQL for security scanning.

**Alternatives considered:**
- CodeQL custom queries: Rejected—CI-only, no IDE feedback
- StyleCop/FxCop: Rejected—can't encode domain-specific patterns
- Code reviews only: Rejected—inconsistent enforcement

**Consequences:**
- Positive: Real-time IDE feedback during development
- Positive: Patterns enforced at compile time, not in CI
- Negative: Requires maintaining custom analyzer code

### Why Warning Severity (Not Error)?

**Context:** Analyzers may have false positives; blocking builds frustrates developers.

**Decision:** Default to Warning severity; allow escalation via `.editorconfig` if desired.

**Test results:**
| Approach | Developer Experience |
|----------|---------------------|
| Error severity | Blocked builds, suppression fatigue |
| Warning severity | Visible but non-blocking, better adoption |

**Alternatives considered:**
- Error by default: Rejected—too disruptive for existing codebases
- Info/Hidden: Rejected—too easy to ignore

**Consequences:**
- Positive: Gradual adoption, no build disruption
- Positive: Teams can escalate to Error via config
- Negative: Warnings may be ignored if too many

### Why netstandard2.0 Target?

**Context:** Analyzers must work with both .NET Framework and .NET Core MSBuild.

**Decision:** Target netstandard2.0 for maximum compatibility.

**Alternatives considered:**
- net6.0: Rejected—doesn't work with .NET Framework projects
- netstandard2.1: Rejected—limited .NET Framework compatibility

**Consequences:**
- Positive: Works with any project type
- Negative: Limited to C# 7.3 language features in analyzer code

### Why Skip Generated Code?

**Context:** Generated files (early-bound entities, designer files) shouldn't trigger diagnostics.

**Decision:** Use `GeneratedCodeAnalysisFlags.None` to skip generated code analysis.

**Alternatives considered:**
- Analyze all code: Rejected—noise from generated files
- Selective filtering: Rejected—complex to maintain

**Consequences:**
- Positive: No false positives from generated code
- Negative: Manual code in generated files not checked

---

## Configuration

### Package Installation

```xml
<PackageReference Include="PPDS.Analyzers" Version="1.0.0">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
</PackageReference>
```

### Severity Configuration (.editorconfig)

```ini
[*.cs]
# Escalate to error for new projects
dotnet_diagnostic.PPDS012.severity = error
dotnet_diagnostic.PPDS013.severity = error

# Disable for specific folders
[**/Generated/**/*.cs]
dotnet_diagnostic.PPDS006.severity = none
```

### Project Settings

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `AnalysisMode` | string | Default | AllEnabledByDefault / Minimum / Recommended |
| `EnforceCodeStyleInBuild` | bool | false | Run analyzers during build |
| `TreatWarningsAsErrors` | bool | false | Escalate warnings to errors |

---

## Testing

### Acceptance Criteria

- [ ] PPDS006 flags `new QueryExpression("account")` with suggestion
- [ ] PPDS006 ignores `new QueryExpression(Account.EntityLogicalName)`
- [ ] PPDS012 flags `.Result`, `.Wait()`, `.GetAwaiter().GetResult()` on Tasks
- [ ] PPDS012 ignores non-Task types with same member names
- [ ] PPDS013 flags unawaited async calls in constructors
- [ ] PPDS013 ignores awaited calls and safe patterns

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| QueryExpression with variable | `new QueryExpression(entityVar)` | No diagnostic (not a literal) |
| Task.Result on custom Task class | `myTask.Result` (non-System.Threading.Tasks) | No diagnostic |
| Awaited async in constructor | `await LoadAsync()` in async factory | No diagnostic |
| .ContinueWith error handling | `_ = LoadAsync().ContinueWith(...)` | No diagnostic (intentional) |

### Test Examples

```csharp
[Fact]
public async Task PPDS006_StringLiteralInQueryExpression_ReportsWarning()
{
    var code = """
        using Microsoft.Xrm.Sdk.Query;
        class Test
        {
            void M() => new QueryExpression("account");
        }
        """;

    var diagnostics = await GetDiagnosticsAsync(code);

    diagnostics.Should().ContainSingle()
        .Which.Id.Should().Be("PPDS006");
}

[Fact]
public async Task PPDS012_TaskResult_ReportsWarning()
{
    var code = """
        using System.Threading.Tasks;
        class Test
        {
            int M() => Task.FromResult(42).Result;
        }
        """;

    var diagnostics = await GetDiagnosticsAsync(code);

    diagnostics.Should().ContainSingle()
        .Which.Id.Should().Be("PPDS012");
}
```

---

## Related Specs

- [architecture.md](./architecture.md) - Architectural patterns these analyzers enforce
- [cli.md](./cli.md) - CLI commands where analyzer rules apply
- [connection-pooling.md](./connection-pooling.md) - Pattern enforced by planned PPDS007

---

## Roadmap

- Code fix providers for auto-remediation
- PPDS001-005: Architectural layer enforcement
- PPDS007-011: Performance and correctness rules
- Integration with `ppds lint` command for batch analysis
- VS Code extension integration for real-time diagnostics
