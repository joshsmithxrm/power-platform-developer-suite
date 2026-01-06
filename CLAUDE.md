# CLAUDE.md - ppds-sdk

**NuGet packages for Power Platform development: plugin attributes, Dataverse connectivity, and migration tooling.**

**Part of the PPDS Ecosystem** - See `C:\VS\ppds\CLAUDE.md` for cross-project context.

---

## 🚫 NEVER

| Rule | Why |
|------|-----|
| Commit directly to `main` | Branch is protected; all changes require PR |
| Regenerate `PPDS.Plugins.snk` | Breaks strong naming; existing assemblies won't load |
| Skip XML documentation on public APIs | Consumers need IntelliSense documentation |
| Commit with failing tests | All tests must pass before merge |
| Create new ServiceClient per request | 42,000x slower than Clone/pool pattern |
| Guess parallelism values | Use `RecommendedDegreesOfParallelism` from server |
| Hold single pooled client for multiple queries | Defeats pool parallelism; see `.claude/rules/DATAVERSE_PATTERNS.md` |
| Use magic strings for generated entities | Use `EntityLogicalName` and `Fields.*` constants |
| Use late-bound `Entity` for generated entity types | Use early-bound classes; compile-time safety |
| Write CLI status messages to stdout | Use `Console.Error.WriteLine` for status; stdout is for data |

---

## ✅ ALWAYS

| Rule | Why |
|------|-----|
| Strong name all assemblies | Required for Dataverse plugin sandbox |
| XML documentation for public APIs | IntelliSense support for consumers |
| Run `dotnet test` before PR | Ensures no regressions |
| Update `CHANGELOG.md` for user-facing changes | Skip internal refactoring |
| Use connection pool for multi-request scenarios | See `.claude/rules/DATAVERSE_PATTERNS.md` |
| Dispose pooled clients with `await using` | Returns connections to pool |
| Use bulk APIs (`CreateMultiple`, `UpdateMultiple`) | 5x faster than `ExecuteMultiple` |
| Use early-bound classes for generated entities | Type safety, IntelliSense support |
| Read ADRs 0002/0005 before Dataverse multi-record code | Pool patterns are non-obvious |
| Add new services to `RegisterDataverseServices()` | Keeps CLI and library DI in sync |

---

## 💻 Tech Stack

| Technology | Version | Purpose |
|------------|---------|---------|
| .NET | 4.6.2, 8.0, 9.0, 10.0 | Plugins: 4.6.2 only; libraries/CLI: 8.0+ |
| C# | Latest (LangVersion) | Primary language |
| Strong Naming | .snk file | Required for Dataverse plugin assemblies |

---

## 📁 Project Structure

```
ppds-sdk/
├── src/
│   ├── PPDS.Plugins/        # Plugin attributes (PluginStep, PluginImage)
│   ├── PPDS.Dataverse/      # Connection pool, bulk operations, metadata
│   │   └── Generated/       # Early-bound entity classes (DO NOT edit)
│   ├── PPDS.Migration/      # Migration engine library
│   ├── PPDS.Auth/           # Authentication profiles
│   └── PPDS.Cli/            # CLI tool (ppds command)
├── tests/                   # Unit, integration, and live tests
├── docs/adr/                # Architecture Decision Records
└── CHANGELOG.md
```

---

## 🏗️ Generated Entities

Early-bound classes in `src/PPDS.Dataverse/Generated/` provide type safety.

**Available:** `PluginAssembly`, `PluginPackage`, `PluginType`, `SdkMessage`, `SdkMessageFilter`, `SdkMessageProcessingStep`, `SdkMessageProcessingStepImage`, `Solution`, `SolutionComponent`, `AsyncOperation`, `ImportJob`, `SystemUser`, `Role`, `Publisher`, `EnvironmentVariableDefinition`, `EnvironmentVariableValue`, `Workflow`, `ConnectionReference`

```csharp
// ✅ Correct - Early-bound with constants
var query = new QueryExpression(PluginAssembly.EntityLogicalName)
{
    ColumnSet = new ColumnSet(PluginAssembly.Fields.Name)
};

// ❌ Wrong - Magic strings
var query = new QueryExpression("pluginassembly");
```

**Late-bound is acceptable only when:** Entity type is determined at runtime (migration), building generic tooling, or entity has no generated class.

**Regenerating:** `.\scripts\Generate-EarlyBoundModels.ps1 -Force` (requires `pac auth`)

---

## 🛠️ Common Commands

```powershell
dotnet build                    # Debug build
dotnet build -c Release         # Release build
dotnet test                     # Run all tests
dotnet pack -c Release -o ./nupkgs
```

---

## 🔄 Development Workflow

1. Create feature branch from `main`
2. Make changes + **add tests for new classes**
3. Update `CHANGELOG.md` (same commit)
4. Run `/pre-pr` before committing
5. Create PR to `main`
6. Run `/review-bot-comments` after bots comment

### Plan Mode Checklist

- [ ] **Shared utilities identified** - Will logic be needed in multiple files?
- [ ] **Constants centralized** - Magic numbers/strings that should be shared?
- [ ] **Existing patterns checked** - Similar functionality to extend?

**Anti-pattern:** Planning WHAT without WHERE. Always consider where shared logic should live to avoid expensive refactoring.

### Code Conventions

- Use nullable reference types (`string?` not `string`)
- XML documentation on public APIs
- Comments explain WHY, not WHAT
- Namespaces: `PPDS.{Package}.{Area}` (e.g., `PPDS.Auth.Credentials`)

---

## 📦 Version Management

Each package has independent versioning using MinVer:

| Package | Tag Format |
|---------|------------|
| PPDS.Plugins | `Plugins-v{version}` |
| PPDS.Dataverse | `Dataverse-v{version}` |
| PPDS.Migration | `Migration-v{version}` |
| PPDS.Auth | `Auth-v{version}` |
| PPDS.Cli | `Cli-v{version}` |

Pre-release: `-alpha.N`, `-beta.N`, `-rc.N` suffix

---

## 🔀 Git Strategy

| Branch | Purpose |
|--------|---------|
| `main` | Protected, always releasable |
| `feature/*` | New features |
| `fix/*` | Bug fixes |

**Merge:** Squash merge to main. Pre-release = fix pattern issues now, don't defer.

---

## 🚀 Release Process

1. Update per-package `CHANGELOG.md` (in `src/{package}/`)
2. Merge to `main`
3. Create GitHub Release with package-specific tag
4. `publish-nuget.yml` workflow publishes to NuGet.org

---

## ⚡ Dataverse Performance

**See `.claude/rules/DATAVERSE_PATTERNS.md` for pool usage patterns, DOP parallelism, and code examples.**

Key points:
- Get client INSIDE parallel loops (not outside)
- Use `pool.GetTotalRecommendedParallelism()` as DOP ceiling
- Reference impl: `BulkOperationExecutor.cs`

### Architecture Decision Records

| ADR | Summary |
|-----|---------|
| [0001](docs/adr/0001_DISABLE_AFFINITY_COOKIE.md) | Disable affinity cookie for 10x throughput |
| [0002](docs/adr/0002_MULTI_CONNECTION_POOLING.md) | Multiple Application Users multiply API quota |
| [0003](docs/adr/0003_THROTTLE_AWARE_SELECTION.md) | Route away from throttled connections |
| [0004](docs/adr/0004_THROTTLE_RECOVERY_STRATEGY.md) | Transparent throttle waiting |
| [0005](docs/adr/0005_DOP_BASED_PARALLELISM.md) | DOP-based parallelism |
| [0006](docs/adr/0006_CONNECTION_SOURCE_ABSTRACTION.md) | IConnectionSource for custom auth |
| [0007](docs/adr/0007_UNIFIED_CLI_AND_AUTH.md) | Unified CLI and auth profiles |
| [0008](docs/adr/0008_CLI_OUTPUT_ARCHITECTURE.md) | CLI stdout/stderr separation |
| [0009](docs/adr/0009_CLI_COMMAND_TAXONOMY.md) | CLI command taxonomy |
| [0010](docs/adr/0010_PUBLISHED_UNPUBLISHED_DEFAULT.md) | Default to published content |
| [0011](docs/adr/0011_DEPLOYMENT_SETTINGS_FORMAT.md) | Deployment settings format |
| [0012](docs/adr/0012_HYBRID_FILTER_DESIGN.md) | Hybrid filter design |
| [0013](docs/adr/0013_CLI_DRY_RUN_CONVENTION.md) | CLI --dry-run convention |
| [0014](docs/adr/0014_CSV_MAPPING_SCHEMA.md) | CSV mapping schema |

---

## 🖥️ CLI (PPDS.Cli)

See [CLI README](src/PPDS.Cli/README.md) for full documentation.

Quick start:
```bash
ppds auth create --name dev    # Create profile
ppds env select                # Select environment
ppds data export --schema schema.xml --output data.zip
```

**Output conventions:** stdout = data, stderr = status messages.

---

## 🔌 DI Registration

Two DI paths must stay synchronized:
- **Library:** `AddDataverseConnectionPool(config)`
- **CLI:** `ProfileServiceFactory.CreateFromProfileAsync()`

Both call `RegisterDataverseServices()` to register shared services. When adding a service, add it there (not to individual paths).

---

## 🧪 Testing

**See `.claude/rules/TESTING.md` for test categories, CI behavior, and local setup.**

Key rules:
- New public class → must have test class
- Run `/run-integration-local` for live tests
- CI: Unit tests on commits, full tests on PRs

---

## 🤖 Bot Review Handling

PRs reviewed by Copilot, Gemini, CodeQL. Not all findings are valid.

| Finding Type | Action |
|--------------|--------|
| Unused code, resource leaks | Usually valid - fix |
| Style suggestions | Often preference - dismiss with reason |
| Logic errors | Verify manually |

Run `/review-bot-comments [PR#]` to triage.

---

## 🛠️ Claude Commands

| Command | Purpose |
|---------|---------|
| `/plan-work <issues...>` | Triage issues, create worktrees |
| `/pre-pr` | Validate before PR |
| `/review-bot-comments [PR#]` | Triage bot findings |
| `/run-integration-local [filter]` | Run integration tests |
| `/debug-ci-failure [run-id]` | Analyze CI failure |

**Hook:** `pre-commit-validate.py` - Build + unit tests on commit (~10s)

---

## 📦 Consumer Templates

Projects using PPDS: see `templates/claude/` for Claude Code integration.
