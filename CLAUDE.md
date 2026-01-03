# CLAUDE.md - ppds-sdk

**NuGet packages for Power Platform development: plugin attributes, Dataverse connectivity, and migration tooling.**

**Part of the PPDS Ecosystem** - See `C:\VS\ppds\CLAUDE.md` for cross-project context.

**Consumption guidance:** See [CONSUMPTION_PATTERNS.md](../docs/CONSUMPTION_PATTERNS.md) for when consumers should use library vs CLI vs Tools.

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
| Multi-target appropriately | PPDS.Plugins: 4.6.2 only; libraries: 8.0, 9.0, 10.0 |
| Run `dotnet test` before PR | Ensures no regressions |
| Update `CHANGELOG.md` with changes | Release notes for consumers |
| Follow SemVer versioning | Clear compatibility expectations |
| Use connection pool for multi-request scenarios | Reuses connections, applies performance settings automatically |
| Dispose pooled clients with `await using` | Returns connections to pool; prevents leaks |
| Use bulk APIs (`CreateMultiple`, `UpdateMultiple`, `UpsertMultiple`) | 5x faster than `ExecuteMultiple` (~10M vs ~2M records/hour) |
| Reference Microsoft Learn docs in ADRs | Authoritative source for Dataverse best practices |
| Scale throughput by adding Application Users | Each user has independent API quota; DOP × connections = total parallelism |

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
│   ├── PPDS.Auth.IntegrationTests/  # Auth smoke tests
│   ├── PPDS.Dataverse.IntegrationTests/  # FakeXrmEasy mocked tests
│   └── PPDS.LiveTests/              # Live Dataverse integration tests
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
3. **Add tests for new classes** (no new code without tests)
4. Update `CHANGELOG.md` (same commit, not after)
5. Run `/pre-pr` before committing
6. Create PR to `main`
7. Run `/review-bot-comments` after bots comment

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

---

## 🖥️ CLI (PPDS.Cli)

The unified CLI (`ppds`) uses stored authentication profiles. Create a profile once, then all commands use it automatically.

### Command Structure

```
ppds
├── auth      Authentication profile management
├── env       Environment discovery and selection
├── data      Data operations (export, import, copy, analyze)
├── schema    Schema generation and entity listing
└── users     User mapping for cross-environment migrations
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

See [CLI README](src/PPDS.Cli/README.md) for full documentation.

---

## 🧪 Testing Requirements

### Test Projects

| Package | Unit Tests | Integration Tests | Status |
|---------|------------|-------------------|--------|
| PPDS.Plugins | PPDS.Plugins.Tests | - | ✅ |
| PPDS.Dataverse | PPDS.Dataverse.Tests | PPDS.Dataverse.IntegrationTests (FakeXrmEasy) | ✅ |
| PPDS.Cli | PPDS.Cli.Tests | PPDS.LiveTests/Cli (E2E) | ⏳ E2E pending |
| PPDS.Auth | **Needs unit tests** | PPDS.LiveTests/Authentication | ❌ Unit pending |
| PPDS.Migration | **Needs unit tests** | - | ❌ Unit pending |

### Live Tests (PPDS.LiveTests)

Live integration tests against real Dataverse environment:
- `Authentication/` - Client secret, certificate, GitHub OIDC, Azure DevOps OIDC
- `Pooling/` - Connection pool, DOP detection
- `Resilience/` - Throttle detection
- `BulkOperations/` - Live bulk operation execution
- `Cli/` - CLI E2E tests (pending)

**Rules:**
- New public class → must have corresponding test class
- New public method → must have test coverage
- Mark integration tests with `[Trait("Category", "Integration")]`

**Test filtering:**
- **Commits:** Unit tests only (`--filter Category!=Integration`)
- **PRs:** All tests including integration

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
| `/pre-pr` | Validate before PR (build, test, changelog) |
| `/review-bot-comments [PR#]` | Triage bot review findings |
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
