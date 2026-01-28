# Plugin System

**Status:** Implemented
**Version:** 2.0
**Last Updated:** 2026-01-27
**Code:** [src/PPDS.Plugins/](../src/PPDS.Plugins/) | [src/PPDS.Cli/Plugins/](../src/PPDS.Cli/Plugins/)

---

## Overview

The plugin system enables code-first registration of Dataverse plugins using declarative attributes. Developers annotate plugin classes with `PluginStepAttribute` and `PluginImageAttribute`, then use CLI commands to extract metadata and deploy registrations to Dataverse environments.

### Goals

- **Declarative Registration**: Define plugin steps and images via C# attributes instead of manual registration
- **Code-First Workflow**: Extract registration metadata from compiled assemblies, version-control configuration
- **Idempotent Deployment**: Upsert operations enable safe re-deployment without duplicates

### Non-Goals

- Plugin runtime/execution (that's Dataverse's responsibility)
- Plugin base class implementation (use Microsoft's `IPlugin` interface)
- Secure configuration storage (use environment variables or Azure Key Vault)

---

## Architecture

```
┌─────────────────────┐
│   Plugin Assembly   │  ← Developer annotates classes with attributes
│  (PPDS.Plugins ref) │
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  AssemblyExtractor  │  ← Reads attributes via MetadataLoadContext
│   / NupkgExtractor  │
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│ registrations.json  │  ← Version-controlled configuration
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│ PluginRegistration  │  ← Upserts to Dataverse
│      Service        │
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│     Dataverse       │  ← PluginAssembly, PluginType,
│  (Live Environment) │     SdkMessageProcessingStep, ...Image
└─────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `PPDS.Plugins` | Attribute library referenced by plugin assemblies |
| `AssemblyExtractor` | Reads plugin metadata from DLLs without execution |
| `NupkgExtractor` | Extracts plugins from NuGet packages |
| `PluginRegistrationService` | CRUD operations for Dataverse plugin entities |
| `DeployCommand` | Orchestrates configuration-driven deployment |

### Dependencies

- Uses patterns from: [architecture.md](./architecture.md) (Application Services, Connection Pooling)
- Uses: [connection-pooling.md](./connection-pooling.md) for Dataverse access

---

## Specification

### Core Requirements

1. Plugin attributes must be extractable without loading executable code
2. Extraction must support both loose DLLs and NuGet packages
3. Deployment must be idempotent (upsert semantics)
4. Configuration must be serializable to JSON for version control

### Primary Flows

**Extract → Deploy Workflow:**

1. **Annotate**: Developer adds `PluginStepAttribute` and `PluginImageAttribute` to plugin classes
2. **Build**: Compile assembly referencing `PPDS.Plugins`
3. **Extract**: Run `ppds plugins extract <assembly.dll>` to generate `registrations.json`
4. **Deploy**: Run `ppds plugins deploy registrations.json` to upsert to Dataverse
5. **Clean** (optional): Run with `--clean` to remove orphaned registrations

**Imperative Registration:**

1. **Register Assembly**: `ppds plugins register assembly <path.dll>`
2. **Register Type**: `ppds plugins register type <assembly-id> <type-name>`
3. **Register Step**: `ppds plugins register step <type-id> <message> <entity> <stage>`
4. **Register Image**: `ppds plugins register image <step-id> <name> <image-type>`

### Constraints

- Plugin assemblies must target .NET 4.6.2 (Dataverse sandbox requirement)
- Assemblies must be strong-named for Dataverse registration
- `ExecutionOrder` must be 1-999999

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| ExecutionOrder | 1 ≤ value ≤ 999999 | "Execution order must be between 1 and 999999" |
| Message | Non-empty SDK message name | "Message is required" |
| Entity | Valid logical name or "none" | "Entity is required" |
| Stage | Valid PluginStage enum | "Invalid stage" |

---

## Core Types

### PluginStepAttribute

Defines plugin step registration configuration. Applied to plugin classes to specify how the plugin registers in Dataverse. Multiple attributes create multiple step registrations.

```csharp
[PluginStep(
    Message = "Update",
    EntityLogicalName = "account",
    Stage = PluginStage.PostOperation,
    Mode = PluginMode.Asynchronous,
    FilteringAttributes = "name,telephone1")]
public class AccountUpdatePlugin : PluginBase { }
```

The implementation ([`PluginStepAttribute.cs:1-122`](../src/PPDS.Plugins/Attributes/PluginStepAttribute.cs#L1-L122)) provides properties for:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Message` | string | Required | SDK message (Create, Update, Delete, etc.) |
| `EntityLogicalName` | string | Required | Entity logical name ("none" for entity-agnostic) |
| `SecondaryEntityLogicalName` | string | null | Secondary entity for relationship-based messages (Associate, Disassociate) |
| `Stage` | PluginStage | Required | Pipeline stage (PreValidation, PreOperation, PostOperation) |
| `Mode` | PluginMode | Synchronous | Execution mode |
| `FilteringAttributes` | string | null | Comma-separated attributes that trigger plugin |
| `ExecutionOrder` | int | 1 | Execution priority (lower = first) |
| `Name` | string | null | Display name for step (auto-generated if not set: "{Type}: {Message} of {Entity}") |
| `UnsecureConfiguration` | string | null | Plain-text configuration string passed to plugin constructor |
| `Description` | string | null | Description of what the step does (stored as Dataverse metadata) |
| `StepId` | string | null | ID for associating images with specific steps |
| `AsyncAutoDelete` | bool | false | Auto-delete async job on success |

### PluginImageAttribute

Defines pre-image or post-image configuration. Images provide entity snapshots before/after operations.

```csharp
[PluginImage(
    ImageType = PluginImageType.PreImage,
    Name = "PreImage",
    Attributes = "name,telephone1,revenue")]
```

The implementation ([`PluginImageAttribute.cs:1-91`](../src/PPDS.Plugins/Attributes/PluginImageAttribute.cs#L1-L91)) provides properties for:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ImageType` | PluginImageType | Required | PreImage, PostImage, or Both |
| `Name` | string | Required | Key in PreEntityImages/PostEntityImages |
| `Attributes` | string | null | Comma-separated attributes (null = all) |
| `EntityAlias` | string | null | Entity alias (defaults to Name) |
| `StepId` | string | null | Associates image with specific step |

### PluginStage Enum

```csharp
public enum PluginStage
{
    PreValidation = 10,   // Before main validation
    PreOperation = 20,    // Before database write
    PostOperation = 40    // After database commit
}
```

([`PluginStage.cs:1-26`](../src/PPDS.Plugins/Enums/PluginStage.cs#L1-L26))

### PluginMode Enum

```csharp
public enum PluginMode
{
    Synchronous = 0,   // Blocks operation
    Asynchronous = 1   // Background execution
}
```

([`PluginMode.cs:1-20`](../src/PPDS.Plugins/Enums/PluginMode.cs#L1-L20))

### PluginImageType Enum

```csharp
public enum PluginImageType
{
    PreImage = 0,   // Snapshot before operation
    PostImage = 1,  // Snapshot after operation
    Both = 2        // Both snapshots (PostOperation only)
}
```

([`PluginImageType.cs:1-26`](../src/PPDS.Plugins/Enums/PluginImageType.cs#L1-L26))

### IPluginRegistrationService

Service interface for all plugin CRUD operations ([`IPluginRegistrationService.cs:1-464`](../src/PPDS.Cli/Plugins/Registration/IPluginRegistrationService.cs#L1-L464)):

```csharp
public interface IPluginRegistrationService
{
    // Query operations
    Task<List<PluginAssemblyInfo>> ListAssembliesAsync(...);
    Task<List<PluginStepInfo>> ListStepsForTypeAsync(...);

    // Upsert operations
    Task<Guid> UpsertAssemblyAsync(string name, byte[] content, string? solution);
    Task<Guid> UpsertStepAsync(Guid typeId, PluginStepConfig config, ...);

    // Unregister operations (cascading)
    Task<UnregisterResult> UnregisterAssemblyAsync(Guid id, bool force);
}
```

---

## CLI Commands

### ppds plugins extract

Extracts plugin metadata from compiled assembly or NuGet package.

```bash
ppds plugins extract MyPlugin.dll -o registrations.json
ppds plugins extract MyPlugin.1.0.0.nupkg -o registrations.json
```

| Option | Description |
|--------|-------------|
| `-o, --output` | Output JSON file path |
| `-s, --solution` | Solution unique name for registration |
| `-f, --force` | Overwrite existing output file |

### ppds plugins deploy

Deploys plugin registrations from configuration file.

```bash
ppds plugins deploy registrations.json
ppds plugins deploy registrations.json --clean --dry-run
```

| Option | Description |
|--------|-------------|
| `--clean` | Remove orphaned registrations |
| `--dry-run` | Preview changes without applying |

### ppds plugins diff

Compares local configuration against Dataverse environment.

```bash
ppds plugins diff registrations.json
```

Returns exit code 1 if drift detected (useful for CI/CD).

### ppds plugins list

Lists registered plugins in the environment.

```bash
ppds plugins list
ppds plugins list --assembly "MyPlugin"
ppds plugins list --package "MyPackage"
```

### ppds plugins unregister

Unregisters plugin components with cascading deletes.

```bash
ppds plugins unregister assembly <id>
ppds plugins unregister step <id> --force
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `Plugin.AssemblyNotFound` | Assembly ID doesn't exist | Verify ID with `list` command |
| `Plugin.UserNotFound` | Impersonation user doesn't exist | Check RunAsUser domain/email |
| `Plugin.ImageNotSupported` | Message doesn't support images | Only use images for Create/Update/Delete/etc. |
| `Plugin.CascadeConstraint` | Children exist on unregister | Use `--force` flag |

### Recovery Strategies

- **Assembly not found**: Use `ppds plugins list` to get valid IDs
- **Cascade constraint**: Add `--force` to unregister children first
- **Drift detected**: Run `ppds plugins deploy --clean` to sync

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Empty registrations.json | No-op, no error |
| Duplicate step names | Each gets unique GUID |
| Missing FilteringAttributes | Triggers on any attribute change |

---

## Design Decisions

### Why Attribute-Based Registration?

**Context:** Traditional plugin registration requires manual configuration in Plugin Registration Tool or deployment XML.

**Decision:** Use C# attributes to define registration inline with plugin code.

**Alternatives considered:**
- XML configuration files: Rejected—separate from code, easy to get out of sync
- Plugin Registration Tool: Rejected—manual, not automatable
- CRM Package Deployer: Rejected—heavyweight for plugin-only scenarios

**Consequences:**
- Positive: Single source of truth, version-controllable, type-safe
- Positive: IntelliSense support in IDE
- Negative: Requires reference to PPDS.Plugins assembly

### Why MetadataLoadContext for Extraction?

**Context:** Need to read custom attributes from plugin assemblies.

**Decision:** Use `System.Reflection.MetadataLoadContext` instead of `Assembly.LoadFrom`.

**Alternatives considered:**
- `Assembly.LoadFrom`: Rejected—loads executable code, requires all dependencies
- Mono.Cecil: Rejected—additional dependency, more complex API

**Consequences:**
- Positive: No code execution (security)
- Positive: No dependency resolution needed
- Negative: Cannot access attribute constructors' computed values

### Why Upsert Semantics?

**Context:** Deploying same configuration multiple times should be safe.

**Decision:** Check existence by name, update if exists, create if not.

**Alternatives considered:**
- Delete-and-recreate: Rejected—loses GUIDs, breaks references
- Create-only: Rejected—fails on re-deployment

**Consequences:**
- Positive: Idempotent deployments, safe for CI/CD
- Positive: Preserves entity GUIDs across deployments
- Negative: Slight overhead for existence check

### Why NuGet Package Support?

**Context:** Modern .NET development uses NuGet for package distribution.

**Decision:** Support extracting plugins directly from .nupkg files.

**Alternatives considered:**
- Require extracted DLLs only: Rejected—extra step for developers

**Consequences:**
- Positive: Single-file deployment artifact
- Positive: Version metadata from .nuspec
- Negative: Temporary extraction to filesystem

---

## Extension Points

### Adding a New SDK Message

No code changes required—just use the message name in `PluginStepAttribute.Message`:

```csharp
[PluginStep(Message = "CustomMessage", EntityLogicalName = "account", Stage = PluginStage.PostOperation)]
```

### Adding Custom Extraction Logic

Extend `AssemblyExtractor` to read additional metadata:

1. **Add property to config model** in [`PluginRegistrationConfig.cs`](../src/PPDS.Cli/Plugins/Models/PluginRegistrationConfig.cs)
2. **Extract in MapStepAttribute** at [`AssemblyExtractor.cs:126-204`](../src/PPDS.Cli/Plugins/Extraction/AssemblyExtractor.cs#L126-L204)

---

## Configuration

### registrations.json Schema

```json
{
  "$schema": "https://ppds.dev/schemas/registrations.json",
  "version": "1.0",
  "generatedAt": "2026-01-27T00:00:00Z",
  "assemblies": [
    {
      "name": "MyPlugin",
      "type": "Assembly",
      "path": "./bin/Release/MyPlugin.dll",
      "solution": "MySolution",
      "types": [
        {
          "typeName": "MyPlugin.AccountHandler",
          "steps": [
            {
              "message": "Update",
              "entity": "account",
              "stage": "PostOperation",
              "mode": "Synchronous",
              "executionOrder": 1,
              "filteringAttributes": "name,telephone1",
              "images": [
                {
                  "name": "PreImage",
                  "imageType": "PreImage",
                  "attributes": "name,telephone1"
                }
              ]
            }
          ]
        }
      ]
    }
  ]
}
```

| Setting | Type | Required | Description |
|---------|------|----------|-------------|
| `solution` | string | No | Solution unique name to add registrations to |
| `path` | string | Yes | Path to assembly DLL |
| `packagePath` | string | No | Path to NuGet package (for package deployments) |

---

## Testing

### Acceptance Criteria

- [ ] Extracting assembly with PluginStepAttribute produces valid JSON
- [ ] Extracting assembly with PluginImageAttribute associates images with steps
- [ ] Deploying configuration creates PluginAssembly in Dataverse
- [ ] Deploying with `--clean` removes orphaned steps
- [ ] Re-deploying same configuration is idempotent

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| No plugin types in assembly | Empty DLL | Empty types array in config |
| Multiple steps on one class | Class with 3 attributes | 3 step entries in config |
| Image without StepId | Single-step class | Image associated with that step |
| Image with mismatched StepId | StepId not matching any step | Image ignored (warning logged) |

### Test Examples

```csharp
[Fact]
public async Task Extract_AssemblyWithPluginStep_ReturnsValidConfig()
{
    // Arrange
    var extractor = AssemblyExtractor.Create("TestPlugin.dll");

    // Act
    var config = extractor.Extract();

    // Assert
    config.Assemblies.Should().HaveCount(1);
    config.Assemblies[0].Types.Should().NotBeEmpty();
}

[Fact]
public async Task Deploy_IdempotentOnSecondRun()
{
    // Arrange
    var service = new PluginRegistrationService(pool, logger);

    // Act
    var id1 = await service.UpsertAssemblyAsync("Test", bytes, null);
    var id2 = await service.UpsertAssemblyAsync("Test", bytes, null);

    // Assert
    id1.Should().Be(id2); // Same GUID
}
```

---

## Related Specs

- [architecture.md](./architecture.md) - Application Services pattern used by PluginRegistrationService
- [connection-pooling.md](./connection-pooling.md) - Connection pool used for Dataverse access
- [cli.md](./cli.md) - CLI command structure

---

## Roadmap

- Workflow assembly support (custom workflow activities)
- Plugin dependency graph visualization
- Assembly signature validation before deployment
