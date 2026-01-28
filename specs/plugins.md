# Plugin System

**Status:** Implemented
**Version:** 2.0
**Last Updated:** 2026-01-28
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

Service interface for all plugin CRUD operations ([`IPluginRegistrationService.cs:1-464`](../src/PPDS.Cli/Plugins/Registration/IPluginRegistrationService.cs#L1-L464)). 37 methods across 8 groups:

| Group | Count | Purpose |
|-------|-------|---------|
| Query | 7 | List assemblies, packages, types, steps, images |
| Lookup | 12 | Get by name, ID, or composite key |
| Create/Upsert | 5 | Upsert assembly, package, type, step, image |
| Delete | 3 | Delete image, step, type |
| Unregister | 5 | Cascade unregister with force option |
| Download | 2 | Download assembly/package binary |
| Update | 2 | Update step/image properties |
| Solution | 1 | Add component to solution |

#### Query Operations

```csharp
Task<List<PluginAssemblyInfo>> ListAssembliesAsync(
    string? assemblyNameFilter = null, PluginListOptions? options = null,
    CancellationToken cancellationToken = default);
Task<List<PluginPackageInfo>> ListPackagesAsync(
    string? packageNameFilter = null, PluginListOptions? options = null,
    CancellationToken cancellationToken = default);
Task<List<PluginAssemblyInfo>> ListAssembliesForPackageAsync(
    Guid packageId, CancellationToken cancellationToken = default);
Task<List<PluginTypeInfo>> ListTypesForPackageAsync(
    Guid packageId, CancellationToken cancellationToken = default);
Task<List<PluginTypeInfo>> ListTypesForAssemblyAsync(
    Guid assemblyId, CancellationToken cancellationToken = default);
Task<List<PluginStepInfo>> ListStepsForTypeAsync(
    Guid pluginTypeId, PluginListOptions? options = null,
    CancellationToken cancellationToken = default);
Task<List<PluginImageInfo>> ListImagesForStepAsync(
    Guid stepId, CancellationToken cancellationToken = default);
```

#### Lookup Operations

```csharp
Task<PluginAssemblyInfo?> GetAssemblyByNameAsync(string name, CancellationToken cancellationToken = default);
Task<PluginAssemblyInfo?> GetAssemblyByIdAsync(Guid id, CancellationToken cancellationToken = default);
Task<PluginPackageInfo?> GetPackageByNameAsync(string name, CancellationToken cancellationToken = default);
Task<PluginPackageInfo?> GetPackageByIdAsync(Guid id, CancellationToken cancellationToken = default);
Task<PluginTypeInfo?> GetPluginTypeByNameOrIdAsync(string nameOrId, CancellationToken cancellationToken = default);
Task<PluginStepInfo?> GetStepByNameOrIdAsync(string nameOrId, CancellationToken cancellationToken = default);
Task<PluginImageInfo?> GetImageByNameOrIdAsync(string nameOrId, CancellationToken cancellationToken = default);
Task<Guid?> GetSdkMessageIdAsync(string messageName, CancellationToken cancellationToken = default);
Task<Guid?> GetSdkMessageFilterIdAsync(
    Guid messageId, string primaryEntity, string? secondaryEntity = null,
    CancellationToken cancellationToken = default);
Task<Guid?> GetAssemblyIdForPackageAsync(
    Guid packageId, string assemblyName, CancellationToken cancellationToken = default);
Task<PluginTypeInfo?> GetPluginTypeByNameAsync(string typeName, CancellationToken cancellationToken = default);
Task<PluginStepInfo?> GetStepByNameAsync(string stepName, CancellationToken cancellationToken = default);
```

#### Create/Upsert Operations

```csharp
Task<Guid> UpsertAssemblyAsync(
    string name, byte[] content, string? solutionName = null,
    CancellationToken cancellationToken = default);
Task<Guid> UpsertPackageAsync(
    string packageName, byte[] nupkgContent, string? solutionName = null,
    CancellationToken cancellationToken = default);
Task<Guid> UpsertPluginTypeAsync(
    Guid assemblyId, string typeName, string? solutionName = null,
    CancellationToken cancellationToken = default);
Task<Guid> UpsertStepAsync(
    Guid pluginTypeId, PluginStepConfig stepConfig, Guid messageId,
    Guid? filterId, string? solutionName = null,
    CancellationToken cancellationToken = default);
Task<Guid> UpsertImageAsync(
    Guid stepId, PluginImageConfig imageConfig, string messageName,
    CancellationToken cancellationToken = default);
```

#### Delete Operations

```csharp
Task DeleteImageAsync(Guid imageId, CancellationToken cancellationToken = default);
Task DeleteStepAsync(Guid stepId, CancellationToken cancellationToken = default);
Task DeletePluginTypeAsync(Guid pluginTypeId, CancellationToken cancellationToken = default);
```

#### Unregister Operations

Cascade unregister with optional `force` for child deletion. Returns `UnregisterResult` with counts.

```csharp
Task<UnregisterResult> UnregisterImageAsync(Guid imageId, CancellationToken cancellationToken = default);
Task<UnregisterResult> UnregisterStepAsync(
    Guid stepId, bool force = false, CancellationToken cancellationToken = default);
Task<UnregisterResult> UnregisterPluginTypeAsync(
    Guid pluginTypeId, bool force = false, CancellationToken cancellationToken = default);
Task<UnregisterResult> UnregisterAssemblyAsync(
    Guid assemblyId, bool force = false, CancellationToken cancellationToken = default);
Task<UnregisterResult> UnregisterPackageAsync(
    Guid packageId, bool force = false, CancellationToken cancellationToken = default);
```

#### Download Operations

```csharp
Task<(byte[] Content, string FileName)> DownloadAssemblyAsync(
    Guid assemblyId, CancellationToken cancellationToken = default);
Task<(byte[] Content, string FileName)> DownloadPackageAsync(
    Guid packageId, CancellationToken cancellationToken = default);
```

#### Update Operations

```csharp
Task UpdateStepAsync(Guid stepId, StepUpdateRequest request, CancellationToken cancellationToken = default);
Task UpdateImageAsync(Guid imageId, ImageUpdateRequest request, CancellationToken cancellationToken = default);
```

#### Solution Operations

```csharp
Task AddToSolutionAsync(
    Guid componentId, int componentType, string solutionName,
    CancellationToken cancellationToken = default);
```

### PluginListOptions

Filtering options for list operations ([`IPluginRegistrationService.cs:11-14`](../src/PPDS.Cli/Plugins/Registration/IPluginRegistrationService.cs#L11-L14)).

```csharp
public record PluginListOptions(
    bool IncludeHidden = false,
    bool IncludeMicrosoft = false
);
```

### Info Types

Return types used by query and lookup operations ([`PluginRegistrationService.cs:2271-2360`](../src/PPDS.Cli/Plugins/Registration/PluginRegistrationService.cs#L2271-L2360)).

**PluginAssemblyInfo** (10 properties):

| Property | Type | Description |
|----------|------|-------------|
| `Id` | Guid | Assembly ID |
| `Name` | string | Assembly name |
| `Version` | string? | Assembly version |
| `PublicKeyToken` | string? | Strong name token |
| `IsolationMode` | int | Sandbox (2) or None (1) |
| `SourceType` | int | Database (0), Disk (1), GAC (2) |
| `IsManaged` | bool | Managed solution component |
| `PackageId` | Guid? | Parent package ID (if package-deployed) |
| `CreatedOn` | DateTime? | Creation timestamp |
| `ModifiedOn` | DateTime? | Last modified timestamp |

**PluginPackageInfo** (7 properties):

| Property | Type | Description |
|----------|------|-------------|
| `Id` | Guid | Package ID |
| `Name` | string | Package name |
| `UniqueName` | string? | Package unique name |
| `Version` | string? | Package version |
| `IsManaged` | bool | Managed solution component |
| `CreatedOn` | DateTime? | Creation timestamp |
| `ModifiedOn` | DateTime? | Last modified timestamp |

**PluginTypeInfo** (8 properties):

| Property | Type | Description |
|----------|------|-------------|
| `Id` | Guid | Plugin type ID |
| `TypeName` | string | Fully qualified type name |
| `FriendlyName` | string? | Display name |
| `AssemblyId` | Guid? | Parent assembly ID |
| `AssemblyName` | string? | Parent assembly name |
| `IsManaged` | bool | Managed solution component |
| `CreatedOn` | DateTime? | Creation timestamp |
| `ModifiedOn` | DateTime? | Last modified timestamp |

**PluginStepInfo** (22 properties):

| Property | Type | Description |
|----------|------|-------------|
| `Id` | Guid | Step ID |
| `Name` | string | Step display name |
| `Message` | string | SDK message name |
| `PrimaryEntity` | string | Primary entity logical name |
| `SecondaryEntity` | string? | Secondary entity |
| `Stage` | string | Pipeline stage |
| `Mode` | string | Execution mode |
| `ExecutionOrder` | int | Execution priority |
| `FilteringAttributes` | string? | Triggering attributes |
| `Configuration` | string? | Unsecured configuration |
| `IsEnabled` | bool | Whether step is active |
| `Description` | string? | Step description |
| `Deployment` | string | ServerOnly, Offline, or Both |
| `ImpersonatingUserId` | Guid? | Run-as user ID |
| `ImpersonatingUserName` | string? | Run-as user name |
| `AsyncAutoDelete` | bool | Auto-delete async jobs |
| `PluginTypeId` | Guid? | Parent plugin type ID |
| `PluginTypeName` | string? | Parent plugin type name |
| `IsManaged` | bool | Managed solution component |
| `IsCustomizable` | bool | Whether customizable |
| `CreatedOn` | DateTime? | Creation timestamp |
| `ModifiedOn` | DateTime? | Last modified timestamp |

**PluginImageInfo** (12 properties):

| Property | Type | Description |
|----------|------|-------------|
| `Id` | Guid | Image ID |
| `Name` | string | Image name |
| `EntityAlias` | string? | Entity alias |
| `ImageType` | string | PreImage, PostImage, or Both |
| `Attributes` | string? | Comma-separated attributes |
| `MessagePropertyName` | string? | Message property name |
| `StepId` | Guid? | Parent step ID |
| `StepName` | string? | Parent step name |
| `IsManaged` | bool | Managed solution component |
| `IsCustomizable` | bool | Whether customizable |
| `CreatedOn` | DateTime? | Creation timestamp |
| `ModifiedOn` | DateTime? | Last modified timestamp |

### UnregisterResult

Result of cascade unregister operations ([`PluginRegistrationService.cs:2365-2423`](../src/PPDS.Cli/Plugins/Registration/PluginRegistrationService.cs#L2365-L2423)).

```csharp
public sealed class UnregisterResult
{
    public string EntityName { get; set; }
    public string EntityType { get; set; }     // Package, Assembly, Type, Step, Image
    public int PackagesDeleted { get; set; }
    public int AssembliesDeleted { get; set; }
    public int TypesDeleted { get; set; }
    public int StepsDeleted { get; set; }
    public int ImagesDeleted { get; set; }
    public int TotalDeleted => PackagesDeleted + AssembliesDeleted + TypesDeleted + StepsDeleted + ImagesDeleted;
    public static UnregisterResult operator +(UnregisterResult a, UnregisterResult b); // Combine results
}
```

### StepUpdateRequest and ImageUpdateRequest

Request types for update operations ([`IPluginRegistrationService.cs:447-463`](../src/PPDS.Cli/Plugins/Registration/IPluginRegistrationService.cs#L447-L463)).

```csharp
public record StepUpdateRequest(
    string? Mode = null,
    string? Stage = null,
    int? Rank = null,
    string? FilteringAttributes = null,
    string? Description = null
);

public record ImageUpdateRequest(
    string? Attributes = null,
    string? Name = null
);
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

### ppds plugins register

Imperative registration without a config file. Supports 5 subcommands.

```bash
ppds plugins register assembly <path.dll> [--solution <name>]
ppds plugins register package <path.nupkg> [--solution <name>]
ppds plugins register type <assembly-id> <type-name> [--solution <name>]
ppds plugins register step <type-id> <message> <entity> <stage> [--mode] [--rank] [--filtering] [--config]
ppds plugins register image <step-id> <name> <image-type> [--attributes] [--entity-alias]
```

### ppds plugins get

Inspects plugin entities by name or ID.

```bash
ppds plugins get assembly <name-or-id>
ppds plugins get package <name-or-id>
ppds plugins get type <name-or-id>
ppds plugins get step <name-or-id>
ppds plugins get image <name-or-id>
```

### ppds plugins download

Downloads assembly or package binary from Dataverse.

```bash
ppds plugins download assembly <name-or-id> --output <path>
ppds plugins download package <name-or-id> --output <path>
```

| Option | Description |
|--------|-------------|
| `--output, -o` | Output file path |

### ppds plugins update

Modifies existing plugin registrations.

```bash
ppds plugins update assembly <name> <path.dll>
ppds plugins update package <name> <path.nupkg>
ppds plugins update step <step-id> [--mode] [--stage] [--rank] [--filtering] [--description]
ppds plugins update image <image-id> [--attributes] [--name]
```

### ppds plugins unregister

Unregisters plugin components with cascading deletes.

```bash
ppds plugins unregister assembly <id> [--force]
ppds plugins unregister package <id> [--force]
ppds plugins unregister type <id> [--force]
ppds plugins unregister step <id> [--force]
ppds plugins unregister image <id>
```

### ppds plugins clean

Standalone orphan cleanup (separate from `deploy --clean`).

```bash
ppds plugins clean --config registrations.json
ppds plugins clean --config registrations.json --dry-run
```

| Option | Description |
|--------|-------------|
| `--config` | Path to registrations.json |
| `--dry-run` | Preview orphans without deleting |

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
  "generatedAt": "2026-01-28T00:00:00Z",
  "assemblies": [
    {
      "name": "MyPlugin",
      "type": "Assembly",
      "path": "./bin/Release/MyPlugin.dll",
      "solution": "MySolution",
      "allTypeNames": ["MyPlugin.AccountHandler", "MyPlugin.ContactHandler"],
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
              "deployment": "ServerOnly",
              "enabled": true,
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

### Config Model Types

All config types include `ExtensionData` (Dictionary\<string,JsonElement\>?) for round-trip JSON preservation of unknown properties. Source: [`PluginRegistrationConfig.cs`](../src/PPDS.Cli/Plugins/Models/PluginRegistrationConfig.cs).

**PluginRegistrationConfig** (5 properties):

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `$schema` | string? | null | JSON schema reference |
| `version` | string | "1.0" | Schema version |
| `generatedAt` | DateTimeOffset? | null | Generation timestamp (Zulu time) |
| `assemblies` | List | [] | Plugin assembly configurations |
| `ExtensionData` | Dictionary? | null | Round-trip preservation |

Also provides `Validate()` method to check all steps have valid execution order.

**PluginAssemblyConfig** (8 properties):

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `name` | string | "" | Assembly name (without extension) |
| `type` | string | "Assembly" | "Assembly" or "Nuget" |
| `path` | string? | null | Relative path to DLL |
| `packagePath` | string? | null | Relative path to .nupkg |
| `solution` | string? | null | Solution unique name |
| `allTypeNames` | List\<string\> | [] | All type names (for orphan detection during --clean) |
| `types` | List | [] | Plugin type configurations |
| `ExtensionData` | Dictionary? | null | Round-trip preservation |

**PluginTypeConfig** (3 properties):

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `typeName` | string | "" | Fully qualified type name |
| `steps` | List | [] | Step registrations |
| `ExtensionData` | Dictionary? | null | Round-trip preservation |

**PluginStepConfig** (17 properties + 2 constants):

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `name` | string? | null | Display name (auto-generated: "{Type}: {Message} of {Entity}") |
| `message` | string | "" | SDK message (Create, Update, Delete, etc.) |
| `entity` | string | "" | Primary entity logical name ("none" for global) |
| `secondaryEntity` | string? | null | Secondary entity for relationship messages |
| `stage` | string | "" | PreValidation, PreOperation, or PostOperation |
| `mode` | string | "Synchronous" | Synchronous or Asynchronous |
| `executionOrder` | int | 1 | Priority (1-999999, lower = first) |
| `filteringAttributes` | string? | null | Comma-separated triggering attributes |
| `unsecureConfiguration` | string? | null | Plain-text config for plugin constructor |
| `deployment` | string? | null | ServerOnly, Offline, or Both |
| `runAsUser` | string? | null | CallingUser, System, GUID, domain, or email |
| `enabled` | bool | true | Register but disable if false |
| `description` | string? | null | Step description metadata |
| `asyncAutoDelete` | bool? | null | Auto-delete async job on success |
| `stepId` | string? | null | ID for associating images with steps |
| `images` | List | [] | Image configurations |
| `ExtensionData` | Dictionary? | null | Round-trip preservation |

Constants: `MinExecutionOrder = 1`, `MaxExecutionOrder = 999999`

**PluginImageConfig** (5 properties):

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `name` | string | "" | Image key in PreEntityImages/PostEntityImages |
| `imageType` | string | "" | PreImage, PostImage, or Both |
| `attributes` | string? | null | Comma-separated attributes (null = all) |
| `entityAlias` | string? | null | Entity alias (defaults to name) |
| `ExtensionData` | Dictionary? | null | Round-trip preservation |

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
- [plugin-traces.md](./plugin-traces.md) - Plugin trace log inspection and management

---

## Roadmap

- Workflow assembly support (custom workflow activities)
- Plugin dependency graph visualization
- Assembly signature validation before deployment
