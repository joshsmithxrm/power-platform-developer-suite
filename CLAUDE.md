# CLAUDE.md - ppds-sdk

NuGet packages & CLI for Power Platform: plugin attributes, Dataverse connectivity, migration tooling.

## NEVER

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
| Access `~/.ppds/` files directly from UI code | Use Application Services; they handle caching, locking (ADR-0024) |
| Implement data/business logic in UI layer | UIs are dumb views; logic belongs in Application Services |
| Write progress directly to console from services | Accept `IProgressReporter`; let UI render (ADR-0025) |
| Throw raw exceptions from Application Services | Wrap in `PpdsException` with ErrorCode/UserMessage (ADR-0026) |

## ALWAYS

| Rule | Why |
|------|-----|
| Use connection pool for multi-request scenarios | See `.claude/rules/DATAVERSE_PATTERNS.md` |
| Use bulk APIs (`CreateMultiple`, `UpdateMultiple`) | 5x faster than `ExecuteMultiple` |
| Add new services to `RegisterDataverseServices()` | Keeps CLI and library DI in sync |
| Use Application Services for all persistent state | Single code path for CLI/TUI/RPC (ADR-0024) |
| Accept `IProgressReporter` for operations >1 second | All UIs need feedback for long operations (ADR-0025) |
| Include ErrorCode in `PpdsException` | Enables programmatic handling (retry, re-auth) (ADR-0026) |
| Make new user data accessible via `ppds serve` | VS Code extension needs same data as CLI/TUI |

---

## 💻 Tech Stack

| Technology | Version | Purpose |
|------------|---------|---------|
| .NET | 4.6.2, 8.0, 9.0, 10.0 | Plugins: 4.6.2 only; libraries/CLI: 8.0+ |
| C# | Latest (LangVersion) | Primary language |
| Strong Naming | .snk file | Required for Dataverse plugin assemblies |
| Terminal.Gui | 1.19+ | TUI application framework |
| Spectre.Console | 0.54+ | CLI command output |

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
│       ├── Commands/        # CLI command handlers
│       ├── Services/        # Application Services (ADR-0015)
│       └── Tui/             # Terminal.Gui application
├── tests/                   # Unit, integration, and live tests
├── docs/adr/                # Architecture Decision Records
└── CHANGELOG.md
```

## 🏛️ Platform Architecture

PPDS is a **multi-interface platform**, not just a CLI tool. The TUI is the primary development interface, with VS Code extension and other frontends consuming the same services.

```
┌─────────────────────────────────────────────────────────────┐
│                      User Interfaces                         │
├───────────────┬───────────────┬───────────────┬─────────────┤
│  CLI Commands │  TUI App      │  VS Code Ext  │  Future     │
│  (ppds data)  │  (ppds -i)    │  (RPC client) │  (Web, etc) │
│               │               │               │             │
│ Spectre.Console│ Terminal.Gui │  JSON-RPC     │             │
├───────────────┴───────────────┴───────────────┴─────────────┤
│                 ppds serve (RPC Server)                      │
│          Long-running service for extensions                 │
├─────────────────────────────────────────────────────────────┤
│              Application Services Layer (ADR-0015)           │
│   ISqlQueryService, IDataMigrationService, IPluginService   │
│   • Accepts IProgressReporter (ADR-0025)                    │
│   • Throws PpdsException (ADR-0026)                         │
│   • Reads/writes ~/.ppds/ (ADR-0024)                        │
├─────────────────────────────────────────────────────────────┤
│         PPDS.Dataverse / PPDS.Migration / PPDS.Auth         │
└─────────────────────────────────────────────────────────────┘
```

### Design Principles

| Principle | Implication |
|-----------|-------------|
| **TUI-first** | Build features in TUI first, then expose via RPC for extensions |
| **Service layer** | All business logic in Application Services, never in UI code |
| **Shared local state** | All UIs access same `~/.ppds/` data via services (ADR-0024) |
| **Framework choice** | CLI: Spectre.Console, TUI: Terminal.Gui, Extension: RPC client |

### Shared Local State

All user data lives in `~/.ppds/` and is accessed via Application Services:

```
~/.ppds/
├── profiles.json           # Auth profiles (IProfileService)
├── history/                # Query history per-environment (IQueryHistoryService)
├── settings.json           # User preferences (ISettingsService)
├── msal_token_cache.bin    # MSAL token cache
└── ppds.credentials.dat    # Encrypted credentials
```

**Access pattern:** `CLI/TUI/VSCode → Application Service → ~/.ppds/`

---

## 🏗️ Generated Entities

Early-bound in `src/PPDS.Dataverse/Generated/`. Use `EntityLogicalName` and `Fields.*` constants.

Late-bound only when: entity type is runtime-determined, or no generated class exists.

Regenerate: `.\scripts\Generate-EarlyBoundModels.ps1 -Force`

## Dataverse Performance

**Read ADRs 0002/0005 before any multi-record code.** Reference: `BulkOperationExecutor.cs`

Key: Get client INSIDE parallel loops. Use `pool.GetTotalRecommendedParallelism()` as DOP ceiling.

## Versioning

MinVer tags: `{Package}-v{version}` (e.g., `Cli-v1.0.0-beta.11`)

## CLI Command Groups

| Command | Purpose |
|---------|---------|
| `ppds auth` | Authentication profiles (create, list, delete) |
| `ppds env` | Environment selection and management |
| `ppds query` | Execute FetchXML (`fetch`) and SQL (`sql`) queries |
| `ppds data` | Data operations (export, import, load, update, delete, truncate, schema) |
| `ppds plugins` | Plugin management (list, deploy, diff, extract, clean) |
| `ppds solutions` | Solution operations |
| `ppds flows` | Cloud flow management |
| `ppds metadata` | Entity/attribute metadata |
| `ppds users` | User management |
| `ppds roles` | Security role operations |
| `ppds connections` | Connection management |
| `ppds connection-references` | Connection reference operations |
| `ppds environment-variables` | Environment variable operations |
| `ppds deployment-settings` | Deployment settings generation |
| `ppds import-jobs` | Import job monitoring |
| `ppds serve` | RPC server for IDE integration |

## Commands

| Command | Purpose |
|---------|---------|
| `/pre-pr` | Validate before PR |
| `/triage` | Batch triage issues |
| `/ppds-help` | CLI quick reference |
| `/setup-ecosystem` | Set up PPDS repos on new machine |

Hook: `pre-commit-validate.py` runs build + unit tests on commit (~10s)

## Rules

- `.claude/rules/DATAVERSE_PATTERNS.md` - Pool usage, parallelism
- `.claude/rules/TESTING.md` - Test categories, CI behavior
- `docs/adr/` - Architecture decisions
