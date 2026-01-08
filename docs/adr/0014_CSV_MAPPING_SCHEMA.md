# ADR-0014: CSV Mapping Schema and Versioning

**Status:** Accepted
**Date:** 2026-01-04
**Authors:** Josh, Claude

## Context

The `ppds data load` command uses JSON mapping files to configure how CSV columns map to Dataverse entity attributes. These files:

1. Configure column-to-attribute mappings
2. Define lookup resolution strategies
3. Map optionset labels to values
4. Specify date formats

As the feature evolves, the schema will change. We need a versioning strategy that:
- Allows CLI upgrades without breaking existing mapping files
- Warns users when mapping files use newer features
- Provides clear error messages when incompatibility occurs

## Decision

### Schema Format

Mapping files use JSON with JSON Schema validation:

```json
{
  "$schema": "https://raw.githubusercontent.com/joshsmithxrm/power-platform-developer-suite/main/schemas/csv-mapping.schema.json",
  "version": "1.0",
  "entity": "ppds_city",
  "columns": { ... }
}
```

### Version Format

Use semver-ish versioning: `"major.minor"` (e.g., `"1.0"`, `"1.1"`, `"2.0"`).

| Change Type | Version Bump | Example |
|-------------|--------------|---------|
| Breaking schema change | Major | `1.0` → `2.0` |
| New optional features | Minor | `1.0` → `1.1` |
| Bug fixes, documentation | None | No version change |

### Compatibility Rules

| Scenario | Behavior | Exit Code |
|----------|----------|-----------|
| Same version | Silent proceed | 0 |
| Lower file version | Silent proceed | 0 |
| Higher minor version | Warning to stderr | 0 |
| Different major version | Error with suggestion | 8 (ValidationError) |

### Underscore Prefix Convention

Properties prefixed with underscore (`_`) are metadata - ignored at runtime but help humans understand the mapping:

```json
{
  "city": {
    "field": "ppds_cityid",
    "_status": "auto-matched",
    "_note": "Lookup field - configure resolution",
    "_csvSample": ["Holtsville", "Agawam"]
  }
}
```

This enables:
- Generated mappings include helpful context
- Users can add notes without affecting behavior
- Forward compatibility - unknown properties are ignored

### Forward Compatibility

The `CsvMappingConfig` class uses `[JsonExtensionData]` to capture unknown properties:

```csharp
[JsonExtensionData]
public Dictionary<string, JsonElement>? ExtensionData { get; set; }
```

This means:
- Mapping files from newer CLIs with new properties still load
- Unknown properties are silently ignored
- No data loss when round-tripping

## Centralized Implementation

Schema versioning is centralized in `CsvMappingSchema.cs`:

```csharp
public static class CsvMappingSchema
{
    public const string CurrentVersion = "1.0";
    public const string SchemaUrl = "https://...";

    public static bool IsCompatible(string? fileVersion);
    public static void ValidateVersion(string? fileVersion, Action<string>? onWarning = null);
}
```

All version references (CLI, generator, loader) use these constants - single source of truth.

## Consequences

### Positive

- **Clear upgrade path**: Users know when to regenerate mapping files
- **Non-breaking minor updates**: New features don't require file updates
- **Human-friendly**: Underscore metadata provides context without affecting behavior
- **Forward compatible**: Newer files work (with warnings) on older CLIs

### Negative

- **Version maintenance**: Must remember to bump version for schema changes
- **Minor version warnings**: May confuse users who don't understand impact

## Related

- [ADR-0008](0008_CLI_OUTPUT_ARCHITECTURE.md): Exit codes referenced for validation errors
- [JSON Schema](../../schemas/csv-mapping.schema.json): Published schema definition
- [CsvMappingSchema.cs](../../src/PPDS.Cli/CsvLoader/CsvMappingSchema.cs): Implementation
