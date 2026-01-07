# CLAUDE.md - ppds-sdk

NuGet packages & CLI for Power Platform: plugin attributes, Dataverse connectivity, migration tooling.

## NEVER

| Rule | Why |
|------|-----|
| Regenerate `PPDS.Plugins.snk` | Breaks strong naming |
| Create new ServiceClient per request | 42,000x slower than pool |
| Hold single pooled client for multiple queries | Defeats pool parallelism |
| Use magic strings for generated entities | Use `EntityLogicalName` and `Fields.*` |
| Write CLI status messages to stdout | stdout = data, stderr = status |

## ALWAYS

| Rule | Why |
|------|-----|
| Use connection pool for multi-request scenarios | See `.claude/rules/DATAVERSE_PATTERNS.md` |
| Use bulk APIs (`CreateMultiple`, `UpdateMultiple`) | 5x faster than `ExecuteMultiple` |
| Add new services to `RegisterDataverseServices()` | Keeps CLI and library DI in sync |

## Structure

```
src/
├── PPDS.Plugins/     # Plugin attributes (PluginStep, PluginImage)
├── PPDS.Dataverse/   # Connection pool, bulk operations
│   └── Generated/    # Early-bound entities (DO NOT edit)
├── PPDS.Migration/   # Migration engine
├── PPDS.Auth/        # Auth profiles
└── PPDS.Cli/         # CLI (ppds command)
```

## Generated Entities

Early-bound in `src/PPDS.Dataverse/Generated/`. Use `EntityLogicalName` and `Fields.*` constants.

Late-bound only when: entity type is runtime-determined, or no generated class exists.

Regenerate: `.\scripts\Generate-EarlyBoundModels.ps1 -Force`

## Dataverse Performance

**Read ADRs 0002/0005 before any multi-record code.** Reference: `BulkOperationExecutor.cs`

Key: Get client INSIDE parallel loops. Use `pool.GetTotalRecommendedParallelism()` as DOP ceiling.

## Versioning

MinVer tags: `{Package}-v{version}` (e.g., `Cli-v1.0.0-beta.11`)

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
