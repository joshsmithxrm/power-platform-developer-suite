# Design: Bug Fixes - Bypass Plugins and Mapping

## Issues

| # | Title | Type | Priority | Size |
|---|-------|------|----------|------|
| 199 | fix: apply bypass plugins to M2M associations and individual operation fallbacks | bug | P1-High | M |
| 200 | bug: investigate bypass plugins not working for bulk operations on certain entities | bug | P1-High | S |
| 202 | fix: distinguish user-provided mapping from auto-created default in summary | bug | P2-Medium | S |

## Context

The bypass plugins feature (`BypassCustomPluginExecution`) is not being applied consistently across all code paths in the data import pipeline. This causes performance degradation when importing data to environments with heavy plugin logic.

### Issue #199 - M2M Associations and Fallbacks
- M2M (many-to-many) association operations don't use bypass plugins
- Individual record fallbacks (when bulk fails) don't inherit the bypass setting
- Location: `src/PPDS.Dataverse/BulkOperations/` and `src/PPDS.Migration/`

### Issue #200 - Certain Entities
- Some entities may have special handling that bypasses the bypass logic
- Need investigation to identify which entities and why
- Likely in entity-specific handlers or metadata checks

### Issue #202 - Mapping Display
- Import summary shows mappings but doesn't distinguish:
  - User-provided explicit mappings
  - Auto-generated default mappings (field name matches)
- Makes it hard to audit what was actually configured vs auto-matched

## Key Files to Investigate

```
src/PPDS.Dataverse/BulkOperations/BulkOperationExecutor.cs
src/PPDS.Migration/Import/DataImporter.cs
src/PPDS.Migration/Import/AssociationImporter.cs
src/PPDS.Migration/Mapping/FieldMapper.cs
src/PPDS.Cli/Commands/Data/ImportCommand.cs
```

## Suggested Implementation Order

1. **#200 first** - Investigation to understand the full scope
2. **#199 second** - Apply bypass to M2M and fallbacks (depends on #200 findings)
3. **#202 last** - UI/display change, independent of the others

## Acceptance Criteria

### #199
- [ ] M2M AssociateRequest uses `BypassCustomPluginExecution` when enabled
- [ ] Individual record fallbacks inherit bypass setting from bulk operation
- [ ] Tests verify bypass is applied in fallback scenarios

### #200
- [ ] Document which entities bypass plugins don't work for
- [ ] Root cause identified
- [ ] Fix or workaround implemented

### #202
- [ ] Import summary shows `(auto)` or similar indicator for auto-matched fields
- [ ] User-provided mappings shown without indicator
- [ ] Clear distinction in both console and JSON output
