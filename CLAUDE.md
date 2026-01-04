# CLAUDE.md - ppds-sdk

**NuGet packages for Power Platform development: plugin attributes, Dataverse connectivity, and migration tooling.**

**Part of the PPDS Ecosystem** - See `C:\VS\ppds\CLAUDE.md` for cross-project context.

**Consumption guidance:** See [CONSUMPTION_PATTERNS.md](../docs/CONSUMPTION_PATTERNS.md) for when consumers should use library vs CLI vs Tools.

---

## 🚫 NEVER

| Rule | Why |
|------|-----|
| Commit directly to `main` | Branch is protected; all changes require PR |
| Regenerate `PPDS.Plugins.snk` | Breaks strong naming; existing assemblies won't load |
| Remove nullable reference types | Type safety prevents runtime errors |
| Skip XML documentation on public APIs | Consumers need IntelliSense documentation |
| Multi-target without testing all frameworks | Dataverse has specific .NET requirements |
| Commit with failing tests | All tests must pass before merge |
| Create new ServiceClient per request | 42,000x slower than Clone/pool pattern; wastes ~446ms per instance |
| Guess parallelism values | Use `RecommendedDegreesOfParallelism` from server; guessing degrades performance |
| Enable affinity cookie for bulk operations | Routes all requests to single backend node; 10x throughput loss |
| Store pooled clients in fields | Causes connection leaks; get per operation, dispose immediately |
| Use magic strings for generated entities | Use `EntityLogicalName` and `Fields.*` constants; see [Generated Entities](#-generated-entities) |
| Use late-bound `Entity` for generated entity types | Use early-bound classes (`PluginAssembly`, `SystemUser`, etc.); compile-time safety |
| Write CLI status messages to stdout | Use `Console.Error.WriteLine` for status; stdout is for data only; see [ADR-0008](docs/adr/0008_CLI_OUTPUT_ARCHITECTURE.md) |
| Hold single pooled client for multiple queries | Defeats pool parallelism; see [Dataverse Query Patterns](#-dataverse-query-patterns) |

---

## ✅ ALWAYS

| Rule | Why |
|------|-----|
| Strong name all assemblies | Required for Dataverse plugin sandbox |
| XML documentation for public APIs | IntelliSense support for consumers |
| Multi-target appropriately | PPDS.Plugins: 4.6.2 only; libraries: 8.0, 9.0, 10.0 |
| Run `dotnet test` before PR | Ensures no regressions |
| Update `CHANGELOG.md` for user-facing changes only | Skip internal refactoring, tooling, generated code |
| Follow SemVer versioning | Clear compatibility expectations |
| Use connection pool for multi-request scenarios | Reuses connections, applies performance settings automatically |
| Dispose pooled clients with `await using` | Returns connections to pool; prevents leaks |
| Use bulk APIs (`CreateMultiple`, `UpdateMultiple`, `UpsertMultiple`) | 5x faster than `ExecuteMultiple` (~10M vs ~2M records/hour) |
| Reference Microsoft Learn docs in ADRs | Authoritative source for Dataverse best practices |
| Scale throughput by adding Application Users | Each user has independent API quota; DOP × connections = total parallelism |
| Use early-bound classes for generated entities | Type safety, IntelliSense, refactoring support |
| Ask user before using late-bound for ambiguous cases | If unsure whether dynamic entity handling is needed, ask first |
| Read ADRs 0002/0005 before Dataverse multi-record code | Pool patterns are non-obvious; see [ADR index](#architecture-decision-records) |
| Compare against `src/PPDS.Dataverse/BulkOperations/BulkOperationExecutor.cs` for parallel patterns | Reference implementation for correct pool usage |
| Fix pattern issues immediately during pre-release | Don't defer; pre-release is the time to refactor |
| Add new services to `RegisterDataverseServices()` | Shared method ensures CLI and library stay in sync; see [DI Architecture](#-di-registration-architecture) |

---

## 💻 Tech Stack

| Technology | Version | Purpose |
|------------|---------|---------|
| .NET | 4.6.2, 8.0, 9.0, 10.0 | Plugins: 4.6.2 only; libraries/CLI: 8.0, 9.0, 10.0 |
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
│   │   ├── Generated/           # Early-bound entity classes (DO NOT manually edit)
│   │   ├── Pooling/             # Connection pool, strategies
│   │   ├── Resilience/          # Throttle tracking, retry logic
│   │   └── PPDS.Dataverse.csproj
│   ├── PPDS.Migration/          # Migration engine library
│   ├── PPDS.Auth/               # Authentication profiles and credentials
│   └── PPDS.Cli/                # Unified CLI tool (ppds command)
├── tests/
│   ├── PPDS.Plugins.Tests/          # Unit tests
│   ├── PPDS.Dataverse.Tests/        # Unit tests
│   ├── PPDS.Cli.Tests/              # Unit tests
│   ├── PPDS.Auth.Tests/             # Unit tests
│   ├── PPDS.Migration.Tests/        # Unit tests
│   ├── PPDS.Auth.IntegrationTests/  # Auth smoke tests
│   ├── PPDS.Dataverse.IntegrationTests/  # FakeXrmEasy mocked tests
│   └── PPDS.LiveTests/              # Live Dataverse + CLI E2E tests
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

## 🏗️ Generated Entities

Early-bound entity classes in `src/PPDS.Dataverse/Generated/` provide compile-time type safety.

### Available Entities

| Entity Class | Logical Name | Used For |
|--------------|--------------|----------|
| `PluginAssembly` | `pluginassembly` | Plugin registration |
| `PluginPackage` | `pluginpackage` | NuGet plugin packages |
| `PluginType` | `plugintype` | Plugin type registration |
| `SdkMessage` | `sdkmessage` | Message lookups |
| `SdkMessageFilter` | `sdkmessagefilter` | Entity/message filtering |
| `SdkMessageProcessingStep` | `sdkmessageprocessingstep` | Step registration |
| `SdkMessageProcessingStepImage` | `sdkmessageprocessingstepimage` | Pre/post images |
| `Solution` | `solution` | Solution operations |
| `SolutionComponent` | `solutioncomponent` | Solution components |
| `AsyncOperation` | `asyncoperation` | System jobs / async operations |
| `ImportJob` | `importjob` | Solution import jobs |
| `SystemUser` | `systemuser` | User mapping |
| `Publisher` | `publisher` | Solution publishers |

### Usage Patterns

```csharp
// ✅ Correct - Early-bound with constants
var query = new QueryExpression(PluginAssembly.EntityLogicalName)
{
    ColumnSet = new ColumnSet(
        PluginAssembly.Fields.Name,
        PluginAssembly.Fields.Version)
};

var assembly = new PluginAssembly
{
    Name = "MyPlugin",
    IsolationMode = pluginassembly_isolationmode.Sandbox
};

// ❌ Wrong - Magic strings
var query = new QueryExpression("pluginassembly")
{
    ColumnSet = new ColumnSet("name", "version")
};
```

### When Late-Bound Is Acceptable

Late-bound `new Entity(logicalName)` is correct **only** when:
- Entity type is determined at runtime (migration import/export)
- Building generic tooling for arbitrary entities
- Entity doesn't have a generated class

```csharp
// ✅ Correct - Dynamic entity from schema (migration scenario)
var entity = new Entity(record.LogicalName);

// ❌ Wrong - Known entity type, should use early-bound
var entity = new Entity("pluginassembly");
```

### Regenerating Entities

If Dataverse schema changes or new entities are needed:

```powershell
.\scripts\Generate-EarlyBoundModels.ps1 -Force
```

Requires `pac auth` to be configured. See script for entity list.

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
3. **Add tests for new classes** (no new code without tests)
4. Update `CHANGELOG.md` (same commit, not after)
5. Run `/pre-pr` before committing
6. Create PR to `main`
7. Run `/review-bot-comments` after bots comment

### Plan Mode Checklist

When designing implementation in plan mode, verify:

- [ ] **Shared utilities identified** - Will any logic be needed in multiple files?
- [ ] **Constants centralized** - Are there magic numbers/strings that should be shared?
- [ ] **Code reuse anticipated** - Does Feature A's logic overlap with Feature B?
- [ ] **Existing patterns checked** - Does similar functionality already exist to extend?

**Anti-pattern:** Planning WHAT without WHERE

| Planned | Should Also Plan |
|---------|------------------|
| "Add prefix-aware matching" | "Who else needs this matching? Extract to shared class?" |
| "Implement `--analyze` mode" | "Does analyze share logic with load? Factor out common code?" |
| "Add schema version validation" | "Where should version constants live? Single source of truth?" |

**Lesson learned:** Duplication discovered during bot review is expensive. Anticipate shared code during planning, not after implementation.

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

### Code Comments

Comments explain WHY, not WHAT.

```csharp
// ❌ Bad - explains what
// Loop through all options
foreach (var option in command.Options)

// ✅ Good - explains why
// Required=false hides the default suffix; we show [Required] in description instead
option.Required = false;
```

### Namespaces

Follow existing patterns: `PPDS.{Package}.{Area}` (e.g., `PPDS.Auth.Credentials`). Infer from code.

---

## 📦 Version Management

Each package has independent versioning using [MinVer](https://github.com/adamralph/minver):

| Package | Tag Format | Example |
|---------|------------|---------|
| PPDS.Plugins | `Plugins-v{version}` | `Plugins-v1.2.0` |
| PPDS.Dataverse | `Dataverse-v{version}` | `Dataverse-v1.0.0` |
| PPDS.Migration | `Migration-v{version}` | `Migration-v1.0.0` |
| PPDS.Auth | `Auth-v{version}` | `Auth-v1.0.0` |
| PPDS.Cli | `Cli-v{version}` | `Cli-v1.0.0` |

- Follow SemVer: `MAJOR.MINOR.PATCH`
- Pre-release: `-alpha.N`, `-beta.N`, `-rc.N` suffix

---

## 🔀 Git Branch & Merge Strategy

| Branch | Purpose |
|--------|---------|
| `main` | Protected, always releasable |
| `feature/*` | New features |
| `fix/*` | Bug fixes |

**Merge Strategy:** Squash merge to main (clean commit history)

### Pre-Release Mindset

This project is in pre-release. When bot reviews or code analysis identifies pattern issues:

- **Fix now, don't defer.** Pre-release is the time for breaking changes and refactoring.
- **Question existing patterns.** Not all existing code follows ADRs; validate against docs.
- **Prioritize correctness over velocity.** Wrong patterns are expensive to fix post-release.

---

## 🚀 Release Process

1. Update per-package `CHANGELOG.md` (in `src/{package}/`)
2. Merge to `main`
3. Create GitHub Release with package-specific tag (e.g., `Dataverse-v1.0.0`)
4. `publish-nuget.yml` workflow automatically publishes to NuGet.org

**Required Secret:** `NUGET_API_KEY`

See per-package changelogs:
- [PPDS.Plugins](src/PPDS.Plugins/CHANGELOG.md)
- [PPDS.Dataverse](src/PPDS.Dataverse/CHANGELOG.md)
- [PPDS.Migration](src/PPDS.Migration/CHANGELOG.md)
- [PPDS.Auth](src/PPDS.Auth/CHANGELOG.md)
- [PPDS.Cli](src/PPDS.Cli/CHANGELOG.md)

---

## 🔗 Dependencies & Versioning

### This Repo Produces

| Package | Distribution |
|---------|--------------|
| PPDS.Plugins | NuGet |
| PPDS.Dataverse | NuGet |
| PPDS.Migration | NuGet |
| PPDS.Auth | NuGet |
| PPDS.Cli | .NET Tool |

### Consumed By

| Consumer | How | Breaking Change Impact |
|----------|-----|------------------------|
| ppds-tools | Reflects on attributes | Must update reflection code |
| ppds-tools | Shells to `ppds` CLI | Must update CLI calls |
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
- Changing `ppds` CLI arguments or output format

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

### DOP-Based Parallelism

The pool uses Microsoft's `RecommendedDegreesOfParallelism` (from `x-ms-dop-hint` header) as the parallelism limit:

```
Total Parallelism = sum(DOP per connection)
```

- **DOP varies by environment**: Trial environments report ~4, production can report up to 50
- **Hard cap of 52 per user**: Microsoft's enforced limit per Application User
- **Scale by adding connections**: 2 users at DOP=4 = 8 parallel requests

**Scaling Strategy:**
```
1 Application User  @ DOP=4  →  4 parallel requests
2 Application Users @ DOP=4  →  8 parallel requests
4 Application Users @ DOP=4  → 16 parallel requests
```

See [ADR-0005](docs/adr/0005_DOP_BASED_PARALLELISM.md) for details.

### ADR Quick Reference

**Read ADRs 0002 and 0005 before implementing any multi-record Dataverse operation.**

| ADR | Key Pattern | Anti-Pattern |
|-----|-------------|--------------|
| **0002** | Get client INSIDE parallel loops; each Application User has independent 6,000 req/5min quota | Hold single client for entire operation |
| **0005** | Use `pool.GetTotalRecommendedParallelism()` as DOP ceiling | Hardcode parallelism values |

**Reference implementation:** `src/PPDS.Dataverse/BulkOperations/BulkOperationExecutor.cs` shows correct pool usage.

### Architecture Decision Records

| ADR | Summary |
|-----|---------|
| [0001](docs/adr/0001_DISABLE_AFFINITY_COOKIE.md) | Disable affinity cookie for 10x throughput |
| [0002](docs/adr/0002_MULTI_CONNECTION_POOLING.md) | Multiple Application Users multiply API quota |
| [0003](docs/adr/0003_THROTTLE_AWARE_SELECTION.md) | Route away from throttled connections |
| [0004](docs/adr/0004_THROTTLE_RECOVERY_STRATEGY.md) | Transparent throttle waiting without blocking |
| [0005](docs/adr/0005_DOP_BASED_PARALLELISM.md) | DOP-based parallelism (server-recommended limits) |
| [0006](docs/adr/0006_CONNECTION_SOURCE_ABSTRACTION.md) | IConnectionSource for custom auth methods |
| [0007](docs/adr/0007_UNIFIED_CLI_AND_AUTH.md) | Unified CLI and shared authentication profiles |
| [0008](docs/adr/0008_CLI_OUTPUT_ARCHITECTURE.md) | CLI output architecture (structured errors, stdout/stderr separation) |

---

## 🔄 Dataverse Query Patterns

Before writing code that queries Dataverse, read [ADR-0002](docs/adr/0002_MULTI_CONNECTION_POOLING.md) and [ADR-0005](docs/adr/0005_DOP_BASED_PARALLELISM.md).

**Reference implementation:** `BulkOperationExecutor` - study this before writing parallel Dataverse code.

### ❌ Wrong - Single client for multiple queries

```csharp
// WRONG: Holds one client for entire operation, defeats pool parallelism
await using var client = await pool.GetClientAsync(...);
foreach (var item in items)  // Sequential, same client
    await DoQuery(client, item);
```

### ✅ Correct - Client per parallel operation

```csharp
// CORRECT: Each parallel task gets its own client from the pool
var parallelism = pool.GetTotalRecommendedParallelism();
await Parallel.ForEachAsync(items,
    new ParallelOptions { MaxDegreeOfParallelism = parallelism },
    async (item, ct) =>
    {
        await using var client = await pool.GetClientAsync(cancellationToken: ct);
        await DoQuery(client, item);
    });
```

### Why This Matters

- Pool manages DOP limits, throttle tracking, and connection rotation
- Each Application User has independent API quota (6,000 requests/5 min)
- Getting client inside parallel loop enables true parallelism
- Pool waits for non-throttled connection automatically

---

## 🖥️ CLI (PPDS.Cli)

The unified CLI (`ppds`) uses stored authentication profiles. Create a profile once, then all commands use it automatically.

### Command Structure

```
ppds
├── auth      Authentication profile management
├── env       Environment discovery and selection
├── data      Data operations (export, import, copy, analyze, schema, users)
└── plugins   Plugin registration management
```

### Quick Start

```bash
# Create profile (opens browser)
ppds auth create --name dev

# Select environment
ppds env select --environment "My Environment"

# Run commands
ppds data export --schema schema.xml --output data.zip
```

### Authentication Methods

| Method | Flags | Use Case |
|--------|-------|----------|
| Interactive Browser | (default) | Development |
| Device Code | `--deviceCode` | Headless/SSH |
| Client Secret | `--applicationId` + `--clientSecret` + `--tenant` | CI/CD |
| Certificate | `--applicationId` + `--certificateDiskPath` + `--tenant` | Automated |
| Managed Identity | `--managedIdentity` | Azure-hosted |
| GitHub OIDC | `--githubFederated` + `--applicationId` + `--tenant` | GitHub Actions |
| Azure DevOps OIDC | `--azureDevOpsFederated` + `--applicationId` + `--tenant` | Azure Pipelines |

### Output Conventions

The CLI follows Unix conventions for output streams (see [ADR-0008](docs/adr/0008_CLI_OUTPUT_ARCHITECTURE.md)):

| Stream | Content | Example |
|--------|---------|---------|
| **stdout** | Command results (data) | Profile info, exported data, JSON output |
| **stderr** | Operational messages | "Connecting...", "Authenticating...", progress |

```csharp
// ✅ Correct - Status messages to stderr
Console.Error.WriteLine("Authenticating...");

// ✅ Correct - Results to stdout
Console.WriteLine(JsonSerializer.Serialize(profile));

// ❌ Wrong - Status pollutes pipeable output
Console.WriteLine("Connecting to environment...");
```

This enables clean piping: `ppds data export -f json | jq '.data'`

See [CLI README](src/PPDS.Cli/README.md) for full documentation.

---

## 🔌 DI Registration Architecture

The library has two DI paths that must stay synchronized:

| Path | Entry Point | Use Case |
|------|-------------|----------|
| **Library** | `AddDataverseConnectionPool(config)` | NuGet consumers with configuration files |
| **CLI** | `ProfileServiceFactory.CreateFromProfileAsync()` | CLI with auth profiles |

Both call `RegisterDataverseServices()` to register shared services:

```csharp
// In ServiceCollectionExtensions.cs (public, shared between library and CLI)
public static IServiceCollection RegisterDataverseServices(this IServiceCollection services)
{
    services.AddSingleton<IThrottleTracker, ThrottleTracker>();
    services.AddTransient<IBulkOperationExecutor, BulkOperationExecutor>();
    services.AddTransient<IMetadataService, DataverseMetadataService>();
    return services;
}
```

**Why the split?** `IDataverseConnectionPool` is registered separately because:
- **Library:** Direct type registration from `IOptions<DataverseOptions>`
- **CLI:** Factory delegate with `IConnectionSource[]` from auth profiles

**When adding a new service:**
1. Add to `RegisterDataverseServices()` (NOT directly to either path)
2. Run tests - `RegisterDataverseServicesTests` verifies the registration
3. Both library and CLI automatically get the new service

---

## 🧪 Testing Requirements

### Test Projects

| Package | Unit Tests | Integration Tests | Status |
|---------|------------|-------------------|--------|
| PPDS.Plugins | PPDS.Plugins.Tests | - | ✅ |
| PPDS.Dataverse | PPDS.Dataverse.Tests | PPDS.Dataverse.IntegrationTests (FakeXrmEasy) | ✅ |
| PPDS.Cli | PPDS.Cli.Tests | PPDS.LiveTests/Cli (E2E) | ✅ |
| PPDS.Auth | PPDS.Auth.Tests | PPDS.LiveTests/Authentication | ✅ |
| PPDS.Migration | PPDS.Migration.Tests | - | ✅ |

### Live Tests (PPDS.LiveTests)

Live integration tests against real Dataverse environment:
- `Authentication/` - Client secret, certificate, GitHub OIDC, Azure DevOps OIDC
- `Pooling/` - Connection pool, DOP detection
- `Resilience/` - Throttle detection
- `BulkOperations/` - Live bulk operation execution
- `Cli/` - CLI E2E tests (auth, env, data schema commands)

**Rules:**
- New public class → must have corresponding test class
- New public method → must have test coverage
- Mark integration tests with `[Trait("Category", "Integration")]`

### Test Categories Quick Reference

| Category/Attribute | Purpose | CI Behavior |
|--------------------|---------|-------------|
| `Integration` | Live Dataverse tests | Runs in integration-tests.yml |
| `SecureStorage` | DPAPI/credential store tests | **Excluded** - DPAPI unavailable |
| `SlowIntegration` | 60+ second queries | **Excluded** - keeps CI fast |
| `DestructiveE2E` | Modifies Dataverse data | Runs (with cleanup) |
| `[CliE2EFact]` | CLI tests, .NET 8.0 only | Runs |
| `[CliE2EWithCredentials]` | CLI tests + auth | Runs if credentials available |

**CI constraint:** DPAPI unavailable on GitHub runners. Use `PPDS_SPN_SECRET` env var to bypass `SecureCredentialStore`.

See [docs/INTEGRATION_TESTING.md](docs/INTEGRATION_TESTING.md) for full guide.

**Test filtering:**
- **Commits:** Unit tests only (`--filter Category!=Integration`)
- **PRs:** All tests including integration

### Local Integration Test Setup

Integration tests require Dataverse credentials. Setup:

1. **Copy environment template:**
   ```powershell
   Copy-Item .env.example .env.local
   ```

2. **Edit `.env.local`** with your values:
   ```
   DATAVERSE_URL=https://yourorg.crm.dynamics.com
   PPDS_TEST_APP_ID=your-app-id
   PPDS_TEST_CLIENT_SECRET=your-secret
   PPDS_TEST_TENANT_ID=your-tenant-id
   ```

3. **Load into session and run tests:**
   ```powershell
   . .\scripts\Load-TestEnv.ps1
   dotnet test --filter "Category=Integration"
   ```

**Note:** `.env.local` is gitignored. Tests skip gracefully when credentials are missing.

---

## 🤖 Bot Review Handling

PRs get reviewed by Copilot, Gemini, and CodeQL. **Not all findings are valid.**

| Finding Type | Action |
|--------------|--------|
| Unused code, resource leaks, missing tests | Usually valid - fix |
| "Use .Where()", style suggestions | Often preference - dismiss with reason |
| Logic errors (OR/AND) | Verify manually - bots misread DeMorgan |

**Workflow:** After PR created, run `/review-bot-comments [PR#]` to triage.

---

## 🛠️ Claude Commands & Hooks

### Commands

| Command | Purpose |
|---------|---------|
| `/plan-work <issues...>` | Triage issues, create worktrees, generate session prompts |
| `/pre-pr` | Validate before PR (build, test, changelog) |
| `/review-bot-comments [PR#]` | Triage bot review findings (PR comments + Autofix) |
| `/run-integration-local [filter]` | Load .env.local and run integration tests |
| `/debug-ci-failure [run-id]` | Analyze CI workflow failure logs |
| `/handoff` | Session summary (workspace) |
| `/create-issue [repo]` | Create issue (workspace) |

### Hooks (Automatic)

| Hook | Trigger | Action |
|------|---------|--------|
| `pre-commit-validate.py` | `git commit` | Build + unit tests (skips integration), blocks if failed |

Hooks in `.claude/settings.json`. Pre-commit runs ~10s, keeps broken code out of commits.

---

## ⚖️ Decision Presentation

When presenting choices or asking questions:
1. **Lead with your recommendation** and rationale
2. **List alternatives considered** and why they're not preferred
3. **Ask for confirmation**, not open-ended input

❌ "What testing approach should we use?"
✅ "I recommend X because Y. Alternatives considered: A (rejected because B), C (rejected because D). Do you agree?"
