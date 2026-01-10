# CLAUDE.md - Power Platform Developer Suite

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
| Hold single pooled client for multiple queries | Defeats pool parallelism; see ADR-0002 |
| Use magic strings for generated entities | Use `EntityLogicalName` and `Fields.*` constants |
| Use late-bound `Entity` for generated entity types | Use early-bound classes; compile-time safety |
| Write CLI status messages to stdout | Use `Console.Error.WriteLine` for status; stdout is for data |
| Access `~/.ppds/` files directly from UI code | Use Application Services; they handle caching, locking (ADR-0024) |
| Implement data/business logic in UI layer | UIs are dumb views; logic belongs in Application Services |
| Write progress directly to console from services | Accept `IProgressReporter`; let UI render (ADR-0025) |
| Throw raw exceptions from Application Services | Wrap in `PpdsException` with ErrorCode/UserMessage (ADR-0026) |
| Use comma-separated issues in `Closes` | GitHub only auto-closes first; use separate `Closes #N` lines |
| Add TUI service code without tests | Use MockServiceProviderFactory for testability (ADR-0028) |
| Use bash-specific syntax in C# process commands | `2>/dev/null`, `||`, pipes don't work on Windows; handle errors in code |
| File issues in wrong repo | Issues belong in target repo (ppds-docs issues in ppds-docs, not ppds) |
| Start implementation without plan citations | Cite `docs/patterns/` or ADRs in plan's "Patterns I'll Follow" section |
| Omit "What I'm NOT Doing" from plans | Explicit boundaries prevent scope creep; required for approval |
| Implement in design sessions | Design sessions produce plans and issues; workers implement |

## ALWAYS

| Rule | Why |
|------|-----|
| Use connection pool for multi-request scenarios | See ADR-0002, ADR-0005 |
| Use bulk APIs (`CreateMultiple`, `UpdateMultiple`) | 5x faster than `ExecuteMultiple` |
| Add new services to `RegisterDataverseServices()` | Keeps CLI and library DI in sync |
| Use Application Services for all persistent state | Single code path for CLI/TUI/RPC (ADR-0024) |
| Accept `IProgressReporter` for operations >1 second | All UIs need feedback for long operations (ADR-0025) |
| Include ErrorCode in `PpdsException` | Enables programmatic handling (retry, re-auth) (ADR-0026) |
| Make new user data accessible via `ppds serve` | VS Code extension needs same data as CLI/TUI |
| Link related issues in PR body | Use separate `Closes #N` per issue; comma syntax only closes first |
| Use JSON for config files, JSONL for streaming | No YAML; consistency with CLI output (ADR-0016) |
| Test TUI services with `Category=TuiUnit` | Enables autonomous iteration without manual testing (ADR-0028) |
| Use `IServiceProviderFactory` in InteractiveSession | Required for mock injection in tests (ADR-0028) |
| Wait for required CI checks only in /ship | `Integration Tests` requires live Dataverse (ADR-0029) |
| Check `docs/patterns/` before implementing | Canonical patterns exist; cite them in plan |
| Restate issue understanding in plan | "My Understanding" section catches drift before implementation |
| Create issues after `/design` plan approval | Enables parallel workers; maintains orchestration visibility |

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
power-platform-developer-suite/
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
├── extension/               # VS Code extension (TypeScript)
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
│  (ppds data)  │  (ppds)       │  (RPC client) │  (Web, etc) │
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
| `/design` | Design conversation for new feature |
| `/design-ui` | Reference-driven UI design with wireframes |
| `/orchestrate` | Orchestrate parallel work sessions |
| `/start-work` | Begin work session from issues |
| `/commit` | Phase-aware intermediate commit |
| `/test` | Run tests with auto-detection |
| `/ship` | Validate, commit, PR, handle CI/bot feedback |
| `/create-worktree` | Quick worktree + Claude session |
| `/triage` | Batch triage issues |
| `/spec` | Generate contributor-ready implementation guides |
| `/create-issue` | Create GitHub issue with triage |
| `/release` | Package releases |
| `/prune` | Clean up branches and worktrees |
| `/setup` | Set up PPDS repos on new machine |
| `/ppds-help` | CLI quick reference |
| `/monitor-import` | Monitor running data imports |

See `.claude/workflows/` for process documentation.

Hook: `pre-commit-validate.py` runs build + unit tests on commit (~10s)

## Testing

| Category | Purpose | Filter |
|----------|---------|--------|
| Unit (default) | Fast tests, no external deps | `--filter Category!=Integration` |
| `Integration` | Live Dataverse tests | `--filter Category=Integration` |
| `TuiUnit` | TUI session lifecycle | `--filter Category=TuiUnit` |

Pre-commit hook runs unit tests (~10s). See ADR-0028 (TUI), ADR-0029 (full strategy).

## CI Check Classification

`/ship` should only block on required checks:

| Required (must pass) | Optional (informational) |
|----------------------|--------------------------|
| build, build-status | Integration Tests |
| test, test-status | claude, claude-review |
| extension | codecov/patch |
| Analyze C# (CodeQL) | |
| dependency-review | |

Integration Tests runs against live Dataverse - failures don't block PR merge.

## Documentation

- `docs/adr/` - Architecture Decision Records (detailed patterns, "why" context)
- `docs/patterns/` - Canonical code patterns (bulk ops, services, TUI, CLI, pooling)
- CLAUDE.md - Brief reminders (<100 tokens each), auto-loaded into all conversations

**Guidance:** If you need examples, code snippets, or rationale → create/update an ADR or pattern file. CLAUDE.md is for one-liner "don't do X" rules only.
