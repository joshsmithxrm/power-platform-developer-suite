# CLAUDE.md - ppds-sdk

**NuGet packages for Power Platform development: plugin attributes, Dataverse connectivity, and migration tooling.**

**Part of the PPDS Ecosystem** - See `C:\VS\ppds\CLAUDE.md` for cross-project context.

---

## 🚫 NEVER

| Rule | Why |
|------|-----|
| Regenerate `PPDS.Plugins.snk` | Breaks strong naming; existing assemblies won't load |
| Remove nullable reference types | Type safety prevents runtime errors |
| Skip XML documentation on public APIs | Consumers need IntelliSense documentation |
| Multi-target without testing all frameworks | Dataverse has specific .NET requirements |
| Commit with failing tests | All tests must pass before merge |
| Create new ServiceClient per request | 42,000x slower than Clone/pool pattern; wastes ~446ms per instance |
| Guess parallelism values | Use `RecommendedDegreesOfParallelism` from server; guessing degrades performance |
| Enable affinity cookie for bulk operations | Routes all requests to single backend node; 10x throughput loss |
| Store pooled clients in fields | Causes connection leaks; get per operation, dispose immediately |

---

## ✅ ALWAYS

| Rule | Why |
|------|-----|
| Strong name all assemblies | Required for Dataverse plugin sandbox |
| XML documentation for public APIs | IntelliSense support for consumers |
| Multi-target 4.6.2, 6.0, 8.0 | Dataverse compatibility across versions |
| Run `dotnet test` before PR | Ensures no regressions |
| Update `CHANGELOG.md` with changes | Release notes for consumers |
| Follow SemVer versioning | Clear compatibility expectations |
| Use connection pool for multi-request scenarios | Reuses connections, applies performance settings automatically |
| Dispose pooled clients with `await using` | Returns connections to pool; prevents leaks |
| Use bulk APIs (`CreateMultiple`, `UpdateMultiple`, `UpsertMultiple`) | 5x faster than `ExecuteMultiple` (~10M vs ~2M records/hour) |
| Reference Microsoft Learn docs in ADRs | Authoritative source for Dataverse best practices |

---

## 💻 Tech Stack

| Technology | Version | Purpose |
|------------|---------|---------|
| .NET | 4.6.2, 6.0, 8.0 | Multi-targeting for Dataverse compatibility |
| C# | Latest (LangVersion) | Primary language |
| NuGet | - | Package distribution |
| Strong Naming | .snk file | Required for Dataverse plugin assemblies |

---

## 📁 Project Structure

```
ppds-sdk/
├── src/
│   ├── PPDS.Plugins/
│   │   ├── Attributes/          # PluginStepAttribute, PluginImageAttribute
│   │   ├── Enums/               # PluginStage, PluginMode, PluginImageType
│   │   ├── PPDS.Plugins.csproj
│   │   └── PPDS.Plugins.snk     # Strong name key (DO NOT regenerate)
│   ├── PPDS.Dataverse/
│   │   ├── BulkOperations/      # CreateMultiple, UpdateMultiple, UpsertMultiple
│   │   ├── Client/              # DataverseClient, IDataverseClient
│   │   ├── Pooling/             # Connection pool, strategies
│   │   ├── Resilience/          # Throttle tracking, retry logic
│   │   └── PPDS.Dataverse.csproj
│   ├── PPDS.Migration/          # Migration engine library
│   └── PPDS.Migration.Cli/      # CLI tool (ppds-migrate)
├── tests/
│   ├── PPDS.Plugins.Tests/
│   └── PPDS.Dataverse.Tests/
├── docs/
│   ├── adr/                     # Architecture Decision Records
│   └── architecture/            # Pattern documentation
├── .github/workflows/
│   ├── build.yml               # CI build
│   ├── test.yml                # CI tests
│   └── publish-nuget.yml       # Release → NuGet.org
├── PPDS.Sdk.sln
└── CHANGELOG.md
```

---

## 🛠️ Common Commands

```powershell
# Build
dotnet build                           # Debug build
dotnet build -c Release                # Release build

# Test
dotnet test                            # Run all tests
dotnet test --logger "console;verbosity=detailed"

# Pack (local testing)
dotnet pack -c Release -o ./nupkgs     # Create NuGet package

# Clean
dotnet clean
```

---

## 🔄 Development Workflow

### Making Changes

1. Create feature branch from `main`
2. Make changes
3. Run `dotnet build` and `dotnet test`
4. Update `CHANGELOG.md`
5. Create PR to `main`

### Code Conventions

```csharp
// ✅ Correct - Use nullable reference types
public string? OptionalProperty { get; set; }

// ❌ Wrong - Missing nullability
public string OptionalProperty { get; set; }
```

```csharp
// ✅ Correct - XML documentation on public API
/// <summary>
/// Defines a plugin step registration.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class PluginStepAttribute : Attribute { }

// ❌ Wrong - No documentation
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class PluginStepAttribute : Attribute { }
```

### Namespaces

```csharp
namespace PPDS.Plugins;              // Root
namespace PPDS.Plugins.Attributes;   // Attributes
namespace PPDS.Plugins.Enums;        // Enums
```

---

## 📦 Version Management

- Version is in `src/PPDS.Plugins/PPDS.Plugins.csproj`
- Follow SemVer: `MAJOR.MINOR.PATCH`
- Update version in `.csproj` before release
- Tag releases as `vX.Y.Z`

---

## 🔀 Git Branch & Merge Strategy

| Branch | Purpose |
|--------|---------|
| `main` | Protected, always releasable |
| `feature/*` | New features |
| `fix/*` | Bug fixes |

**Merge Strategy:** Squash merge to main (clean commit history)

---

## 🚀 Release Process

1. Update version in `PPDS.Plugins.csproj`
2. Update `CHANGELOG.md`
3. Merge to `main`
4. Create GitHub Release with tag `vX.Y.Z`
5. `publish-nuget.yml` workflow automatically publishes to NuGet.org

**Required Secret:** `NUGET_API_KEY`

---

## 🔗 Dependencies & Versioning

### This Repo Produces

| Package | Distribution |
|---------|--------------|
| PPDS.Plugins | NuGet |
| PPDS.Dataverse | NuGet |
| PPDS.Migration.Cli | .NET Tool |

### Consumed By

| Consumer | How | Breaking Change Impact |
|----------|-----|------------------------|
| ppds-tools | Reflects on attributes | Must update reflection code |
| ppds-tools | Shells to `ppds-migrate` CLI | Must update CLI calls |
| ppds-demo | NuGet reference | Must update package reference |

### Version Sync Rules

| Rule | Details |
|------|---------|
| Major versions | Sync with ppds-tools when attributes have breaking changes |
| Minor/patch | Independent |
| Pre-release format | `-alphaN`, `-betaN`, `-rcN` suffix in git tag |

### Breaking Changes Requiring Coordination

- Adding required properties to `PluginStepAttribute` or `PluginImageAttribute`
- Changing attribute property types or names
- Changing `ppds-migrate` CLI arguments or output format

---

## 📋 Key Files

| File | Purpose |
|------|---------|
| `PPDS.Plugins.csproj` | Project config, version, NuGet metadata |
| `PPDS.Plugins.snk` | Strong name key (DO NOT regenerate) |
| `PPDS.Dataverse.csproj` | Dataverse client library |
| `CHANGELOG.md` | Release notes |
| `.editorconfig` | Code style settings |

---

## ⚡ Dataverse Performance (PPDS.Dataverse)

### Microsoft's Required Settings for Maximum Throughput

The connection pool automatically applies these settings. If bypassing the pool, you MUST apply them manually:

```csharp
ThreadPool.SetMinThreads(100, 100);           // Default is 4
ServicePointManager.DefaultConnectionLimit = 65000;  // Default is 2
ServicePointManager.Expect100Continue = false;
ServicePointManager.UseNagleAlgorithm = false;
```

### Service Protection Limits (Per User, Per 5-Minute Window)

| Limit | Value |
|-------|-------|
| Requests | 6,000 |
| Execution time | 20 minutes |
| Concurrent requests | 52 (check `x-ms-dop-hint` header) |

### Throughput Benchmarks (Microsoft Reference)

| Approach | Throughput |
|----------|------------|
| Single requests | ~50K records/hour |
| ExecuteMultiple | ~2M records/hour |
| CreateMultiple/UpdateMultiple | ~10M records/hour |
| Elastic tables | ~120M writes/hour |

### Key Documentation

- [Optimize performance for bulk operations](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/optimize-performance-create-update)
- [Send parallel requests](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/send-parallel-requests)
- [Service protection API limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits)
- [Use bulk operation messages](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/bulk-operations)

### Throttle Recovery (Known Limitation)

The pool handles service protection errors transparently (waits and retries). However, it currently resumes at **full parallelism** after recovery, which can cause re-throttling with extended `Retry-After` durations.

**Microsoft recommends** gradual ramp-up after throttle recovery. This is planned for a future enhancement (see ADR-0004).

**Workaround**: Use lower `MaxParallelBatches` to reduce throttle frequency:

```csharp
var options = new BulkOperationOptions { MaxParallelBatches = 10 };
await executor.UpsertMultipleAsync(entities, options);
```

---

## 🧪 Testing Requirements

- **Target 80% code coverage**
- Unit tests for all public API (attributes, enums)
- Run `dotnet test` before submitting PR
- All tests must pass before merge

---

## ⚖️ Decision Presentation

When presenting choices or asking questions:
1. **Lead with your recommendation** and rationale
2. **List alternatives considered** and why they're not preferred
3. **Ask for confirmation**, not open-ended input

❌ "What testing approach should we use?"
✅ "I recommend X because Y. Alternatives considered: A (rejected because B), C (rejected because D). Do you agree?"
