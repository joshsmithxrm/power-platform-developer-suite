# Plugins

**Status:** Implemented
**Version:** 2.0
**Last Updated:** 2026-01-21
**Code:** [src/PPDS.Plugins/](../src/PPDS.Plugins/)

---

## Overview

PPDS.Plugins provides declarative attributes for Dataverse plugin registration. Instead of manually configuring plugin steps through the Plugin Registration Tool, developers decorate plugin classes with attributes that define registration configuration. These attributes are extracted at build time and deployed via PPDS CLI commands.

### Goals

- **Declarative Registration**: Define plugin steps and images via attributes, keeping configuration with code
- **Attribute Extraction**: Enable automated extraction of registration config from compiled assemblies
- **Dataverse Compatibility**: Target .NET Framework 4.6.2 with strong naming for sandbox execution

### Non-Goals

- Plugin base classes or SDK wrappers (use Microsoft.Xrm.Sdk directly)
- Runtime plugin infrastructure (this is a metadata-only library)
- Secure configuration storage (secrets should never be in source control)

---

## Architecture

```
┌─────────────────────┐      Extract      ┌──────────────────────┐
│   Plugin Assembly   │─────────────────▶│  registrations.json  │
│  (with attributes)  │                   │  (PluginRegConfig)   │
└─────────────────────┘                   └──────────────────────┘
         │                                           │
         │                                           │ Deploy
         │                                           ▼
         │                                ┌──────────────────────┐
         │                                │      Dataverse       │
         │                                │  - PluginAssembly    │
         │                                │  - PluginType        │
         │                                │  - ProcessingStep    │
         │                                │  - StepImage         │
         └────────────────────────────────┴──────────────────────┘
                    Runtime execution
```

The workflow separates concerns:
1. **PPDS.Plugins** (this spec): Attribute definitions only
2. **AssemblyExtractor**: Reads attributes via `MetadataLoadContext`
3. **PluginRegistrationService**: Registers steps in Dataverse

### Components

| Component | Responsibility |
|-----------|----------------|
| `PluginStepAttribute` | Defines step registration (message, entity, stage, mode) |
| `PluginImageAttribute` | Defines pre/post images for steps |
| `PluginStage` enum | Pipeline stages (PreValidation, PreOperation, PostOperation) |
| `PluginMode` enum | Execution modes (Synchronous, Asynchronous) |
| `PluginImageType` enum | Image types (PreImage, PostImage, Both) |

### Dependencies

- Uses patterns from: [architecture.md](./architecture.md)
- Extracted by: [cli.md](./cli.md) (ppds plugins extract)
- Registered via: [connection-pool.md](./connection-pool.md) (parallel deployment)

---

## Specification

### Core Requirements

1. **Strong naming**: Assembly must be signed for Dataverse sandbox compatibility
2. **Target framework**: Must target .NET Framework 4.6.2 (Dataverse plugin requirement)
3. **No SDK dependencies**: Keep assembly lightweight; plugins reference SDK separately
4. **Multiple steps per class**: Support `AllowMultiple = true` for multi-message handlers

### Primary Flows

**Plugin Development:**

1. **Add reference**: Reference PPDS.Plugins NuGet package
2. **Decorate class**: Add `[PluginStep]` attribute(s) to plugin class
3. **Add images**: Optionally add `[PluginImage]` for entity snapshots
4. **Build**: Compile assembly with attributes embedded

**Registration Extraction:**

1. **Load assembly**: `AssemblyExtractor` uses `MetadataLoadContext` for safe metadata access
2. **Scan types**: Find exported types with `PluginStepAttribute`
3. **Map attributes**: Convert attribute data to `PluginStepConfig` objects
4. **Associate images**: Match `PluginImageAttribute` to steps via `StepId`
5. **Output JSON**: Write hierarchical `registrations.json`

### Constraints

- `SecureConfiguration` removed in v2.0 (secrets must not be in source control)
- `ExecutionOrder` must be 1-999999 (Dataverse limit)
- `PostImage` and `Both` image types only valid in PostOperation stage
- `FilteringAttributes` only applies to Update message

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| ExecutionOrder | 1 ≤ value ≤ 999999 | "Invalid executionOrder {value}. Must be between 1 and 999999." |
| Message | Required, non-empty | "Message is required" |
| EntityLogicalName | Required, use "none" for global messages | "EntityLogicalName is required" |
| Stage | Valid PluginStage value | "Invalid stage value" |

---

## Core Types

### PluginStepAttribute

Defines plugin step registration configuration. Apply to plugin classes to specify how the plugin should be registered in Dataverse ([`PluginStepAttribute.cs:24-121`](../src/PPDS.Plugins/Attributes/PluginStepAttribute.cs#L24-L121)).

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class PluginStepAttribute : Attribute
{
    public string Message { get; set; }
    public string EntityLogicalName { get; set; }
    public PluginStage Stage { get; set; }
    public PluginMode Mode { get; set; } = PluginMode.Synchronous;
}
```

| Property | Type | Required | Default | Description |
|----------|------|----------|---------|-------------|
| Message | string | Yes | - | SDK message name (Create, Update, Delete, etc.) |
| EntityLogicalName | string | Yes | - | Primary entity; "none" for global messages |
| SecondaryEntityLogicalName | string? | No | null | For relationship messages (Associate, Disassociate) |
| Stage | PluginStage | Yes | - | Pipeline stage when plugin executes |
| Mode | PluginMode | No | Synchronous | Sync (blocks) or Async (background) |
| FilteringAttributes | string? | No | null | Comma-separated attributes (Update only) |
| ExecutionOrder | int | No | 1 | Order when multiple plugins on same event |
| Name | string? | No | Auto-gen | Display name; defaults to "{Type}: {Message} of {Entity}" |
| UnsecureConfiguration | string? | No | null | Config string passed to plugin constructor |
| Description | string? | No | null | Documentation stored in Dataverse |
| AsyncAutoDelete | bool | No | false | Delete async job on success (async only) |
| StepId | string? | No | null | Links images to specific steps |

### PluginImageAttribute

Defines pre-image or post-image for a plugin step ([`PluginImageAttribute.cs:25-90`](../src/PPDS.Plugins/Attributes/PluginImageAttribute.cs#L25-L90)).

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class PluginImageAttribute : Attribute
{
    public PluginImageType ImageType { get; set; }
    public string Name { get; set; }
    public string? Attributes { get; set; }
}
```

| Property | Type | Required | Default | Description |
|----------|------|----------|---------|-------------|
| ImageType | PluginImageType | Yes | - | PreImage, PostImage, or Both |
| Name | string | Yes | - | Key for PreEntityImages/PostEntityImages |
| Attributes | string? | No | null (all) | Comma-separated attributes to include |
| EntityAlias | string? | No | Name | Alias for the image entity |
| StepId | string? | No | null | Associates with specific step |

### Enums

**PluginStage** ([`PluginStage.cs:6-25`](../src/PPDS.Plugins/Enums/PluginStage.cs#L6-L25)):
| Value | Int | Use Case |
|-------|-----|----------|
| PreValidation | 10 | Validation before database locks |
| PreOperation | 20 | Modify data before write |
| PostOperation | 40 | Actions after committed data |

**PluginMode** ([`PluginMode.cs:6-19`](../src/PPDS.Plugins/Enums/PluginMode.cs#L6-L19)):
| Value | Int | Use Case |
|-------|-----|----------|
| Synchronous | 0 | Immediate, blocks operation |
| Asynchronous | 1 | Background via async service |

**PluginImageType** ([`PluginImageType.cs:6-25`](../src/PPDS.Plugins/Enums/PluginImageType.cs#L6-L25)):
| Value | Int | Availability |
|-------|-----|--------------|
| PreImage | 0 | Pre & Post stages |
| PostImage | 1 | Post stage only |
| Both | 2 | Post stage only |

### Usage Patterns

**Basic Create Plugin:**
```csharp
[PluginStep("Create", "account", PluginStage.PreOperation)]
public class AccountCreatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) { }
}
```

**Update with Filtering and Image:**
```csharp
[PluginStep(
    Message = "Update",
    EntityLogicalName = "account",
    Stage = PluginStage.PostOperation,
    Mode = PluginMode.Asynchronous,
    FilteringAttributes = "name,telephone1")]
[PluginImage(PluginImageType.PreImage, "PreImage", "name,telephone1,revenue")]
public class AccountAuditPlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider)
    {
        var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
        var preImage = context.PreEntityImages["PreImage"];
    }
}
```

**Multi-Step Plugin with StepId:**
```csharp
[PluginStep("Create", "contact", PluginStage.PostOperation, StepId = "create")]
[PluginStep("Update", "contact", PluginStage.PostOperation, StepId = "update")]
[PluginImage(PluginImageType.PreImage, "PreImage", StepId = "update")]
public class ContactPlugin : IPlugin { }
```

---

## CLI Integration

### Commands

| Command | Description |
|---------|-------------|
| `ppds plugins extract` | Read assembly attributes, generate registrations.json |
| `ppds plugins deploy` | Register steps from registrations.json to Dataverse |
| `ppds plugins diff` | Compare local config with environment state |
| `ppds plugins list` | List registered assemblies/types/steps |
| `ppds plugins clean` | Remove orphaned registrations |

### Registration Configuration Model

The `PluginRegistrationConfig` ([`PluginRegistrationConfig.cs:35-97`](../src/PPDS.Cli/Plugins/Models/PluginRegistrationConfig.cs#L35-L97)) provides a hierarchical JSON model:

```json
{
  "version": "1.0",
  "generatedAt": "2026-01-21T12:00:00Z",
  "assemblies": [{
    "name": "MyPlugins",
    "type": "Assembly",
    "path": "MyPlugins.dll",
    "types": [{
      "typeName": "MyPlugins.AccountPlugin",
      "steps": [{
        "name": "AccountPlugin: Create of account",
        "message": "Create",
        "entity": "account",
        "stage": "PreOperation",
        "mode": "Synchronous",
        "images": []
      }]
    }]
  }]
}
```

### AssemblyExtractor

The `AssemblyExtractor` ([`AssemblyExtractor.cs:13-334`](../src/PPDS.Cli/Plugins/Extraction/AssemblyExtractor.cs#L13-L334)) uses `MetadataLoadContext` for safe, reflection-only loading:

```csharp
using var extractor = AssemblyExtractor.Create("MyPlugins.dll");
var config = extractor.Extract();
```

Key behaviors:
- Scans exported types (public, non-abstract, non-interface)
- Extracts both constructor and named arguments from attributes
- Auto-generates step names: `"{TypeName}: {Message} of {Entity}"`
- Associates images with steps via StepId matching
- Maps enum values to string names (10 → "PreValidation")

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| Assembly not found | Path invalid or DLL missing | Verify path exists |
| Invalid execution order | Value outside 1-999999 | Fix attribute value |
| Missing message/entity | Required fields empty | Add required properties |
| Image type mismatch | PostImage/Both on non-Post stage | Use PreImage or change stage |

### Recovery Strategies

- **Validation errors**: `PluginRegistrationConfig.Validate()` aggregates all errors before throwing
- **Deployment failures**: Service returns `PpdsException` with `ErrorCode` for programmatic handling

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Plugin with no attributes | Skipped during extraction |
| Image without StepId | Applies to all steps on that class |
| Empty FilteringAttributes | Triggers on any attribute change |
| Entity "none" | Used for global messages like WhoAmI |

---

## Design Decisions

### Why Declarative Attributes?

**Context:** Plugin Registration Tool requires manual configuration through a GUI, which is error-prone and not source-controlled.

**Decision:** Define registration via C# attributes that live alongside plugin code.

**Alternatives considered:**
- **JSON sidecar files**: Rejected - separates config from code, easy to get out of sync
- **Code-based registration (Fluent API)**: Rejected - requires runtime execution, attributes are metadata-only

**Consequences:**
- Positive: Registration config versioned with code, IDE autocomplete, type-safe
- Positive: Enables CI/CD automation via `ppds plugins extract && ppds plugins deploy`
- Negative: Limited to what attributes can express (no dynamic registration)

### Why Strong Naming?

**Context:** Dataverse plugins execute in a sandboxed environment that requires strong-named assemblies.

**Decision:** Sign PPDS.Plugins with `PPDS.Plugins.snk` key file.

**CRITICAL:** Never regenerate this key file. Changing it breaks:
- All assemblies referencing PPDS.Plugins
- Deployed plugins in customer environments
- NuGet package consumers

**Consequences:**
- Positive: Plugins compile successfully for Dataverse
- Negative: Must maintain the same key file forever

### Why .NET 4.6.2 Only?

**Context:** v1.0 supported multiple targets (net462, net6.0, net8.0).

**Decision:** v1.1.0 removed modern targets, targeting only net462.

**Rationale:** Dataverse sandbox plugins MUST target .NET Framework 4.6.2. Multi-targeting added complexity without benefit since plugins can't use modern features anyway.

**Consequences:**
- Positive: Simpler build, clearer messaging
- Negative: Can't use C# features unavailable in .NET 4.6.2 BCL (but can use latest language features)

### Why No Secure Configuration?

**Context:** v1.x included `SecureConfiguration` property for encrypted plugin settings.

**Decision:** v2.0 removed `SecureConfiguration` entirely.

**Rationale:**
- Secure configuration is stored in Dataverse, not source code
- Any value in attributes would be committed to Git
- Secrets in source control violate security best practices

**Consequences:**
- Positive: Cannot accidentally commit secrets
- Positive: Forces proper secrets management (environment variables, Key Vault)
- Negative: Must configure secure values manually or via separate tooling

### Why MetadataLoadContext for Extraction?

**Context:** Need to read attribute values from compiled assemblies.

**Decision:** Use `MetadataLoadContext` instead of `Assembly.LoadFrom`.

**Rationale:**
- `LoadFrom` executes static constructors and code
- MetadataLoadContext is reflection-only, no code execution
- Safer for untrusted assemblies
- No locking of DLL files

**Consequences:**
- Positive: Safe extraction without executing plugin code
- Positive: Can extract from assemblies targeting different frameworks
- Negative: More complex resolver setup for transitive dependencies

---

## Extension Points

### Adding a New Attribute Property

1. **Add property** to attribute class with XML documentation
2. **Update extractor** mapping in `AssemblyExtractor.MapStepAttribute()`
3. **Update config model** `PluginStepConfig` with JSON serialization
4. **Update service** `PluginRegistrationService` to write to Dataverse entity

### Supporting a New Message Type

Default image property names are statically defined ([`PluginRegistrationService.cs:57-74`](../src/PPDS.Cli/Plugins/Registration/PluginRegistrationService.cs#L57-L74)):

```csharp
private static readonly Dictionary<string, string> DefaultImagePropertyNames = new()
{
    ["Create"] = "id",
    ["Update"] = "Target",
    // Add new messages here
};
```

---

## Configuration

### Project Configuration

| Setting | Value | Description |
|---------|-------|-------------|
| TargetFramework | net462 | Required for Dataverse sandbox |
| SignAssembly | true | Strong naming enabled |
| AssemblyOriginatorKeyFile | PPDS.Plugins.snk | Key file (NEVER regenerate) |
| GenerateDocumentationFile | true | XML docs for NuGet |

### NuGet Package

| Property | Value |
|----------|-------|
| PackageId | PPDS.Plugins |
| MinVerTagPrefix | Plugins-v |
| SymbolPackageFormat | snupkg |

---

## Testing

### Acceptance Criteria

- [x] PluginStepAttribute accepts all valid property combinations
- [x] PluginImageAttribute correctly associates with steps via StepId
- [x] Enum values match Dataverse SDK integer constants
- [x] AssemblyExtractor produces valid JSON config
- [x] ExecutionOrder validation rejects out-of-range values
- [x] Strong naming validates in Dataverse sandbox

### Test Categories

| Category | Filter | Tests |
|----------|--------|-------|
| Unit | `--filter Category!=Integration` | Attribute construction, validation |
| Integration | `--filter Category=Integration` | Dataverse registration round-trip |

### Test Examples

```csharp
[Fact]
public void PluginStepAttribute_DefaultConstructor_SetsDefaults()
{
    var attr = new PluginStepAttribute();

    Assert.Equal(string.Empty, attr.Message);
    Assert.Equal(string.Empty, attr.EntityLogicalName);
    Assert.Equal(PluginMode.Synchronous, attr.Mode);
    Assert.Equal(1, attr.ExecutionOrder);
    Assert.False(attr.AsyncAutoDelete);
}

[Theory]
[InlineData(PluginStage.PreValidation, 10)]
[InlineData(PluginStage.PreOperation, 20)]
[InlineData(PluginStage.PostOperation, 40)]
public void PluginStage_HasCorrectDataverseValues(PluginStage stage, int expected)
{
    Assert.Equal(expected, (int)stage);
}
```

---

## Related Specs

- [architecture.md](./architecture.md) - Multi-interface patterns, project structure
- [cli.md](./cli.md) - Command taxonomy, output conventions
- [connection-pool.md](./connection-pool.md) - Parallel deployment via pooled clients
- [testing.md](./testing.md) - Test categories and patterns

---

## Roadmap

- Custom workflow activity registration attributes
- Plugin package (.nupkg) first-class support
- Dependent assembly automatic bundling
- Plugin trace correlation with registered steps
