# Plugin Registration Surfaces Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver full PRT-parity plugin registration browsing and management across Extension, TUI, MCP, and CLI — covering plugins, service endpoints, webhooks, custom APIs, and data providers.

**Architecture:** Four domain specs share a single UI container per surface: one Extension webview panel ("Plugins"), one TUI screen (`PluginRegistrationScreen`), shared RPC endpoints, and read-only MCP tools. All business logic lives in Application Services (Constitution A1/A2). The service layer is built first, then surfaces are wired independently.

**Tech Stack:** C# (.NET 10), TypeScript, Terminal.Gui 1.19+, xUnit, Vitest, System.CommandLine

**Specs:**
- `specs/plugins.md` — Core plugins + shared container
- `specs/service-endpoints.md` — Service Bus + Webhooks
- `specs/custom-apis.md` — Custom APIs + parameters
- `specs/data-providers.md` — Virtual entity data providers

---

## Phase 1: Annotation Library Enhancements

Adds missing properties to the PPDS.Plugins NuGet package. This is a standalone library targeting .NET 4.6.2 — no Dataverse dependency.

### Task 1: Add PluginDeployment and PluginInvocationSource enums

**Files:**
- Create: `src/PPDS.Plugins/Enums/PluginDeployment.cs`
- Create: `src/PPDS.Plugins/Enums/PluginInvocationSource.cs`
- Test: `tests/PPDS.Plugins.Tests/PluginDeploymentTests.cs`
- Test: `tests/PPDS.Plugins.Tests/PluginInvocationSourceTests.cs`

- [ ] **Step 1: Write tests for PluginDeployment enum values**

```csharp
[Fact]
public void PluginDeployment_ServerOnly_HasValue0()
{
    ((int)PluginDeployment.ServerOnly).Should().Be(0);
}

[Fact]
public void PluginDeployment_Offline_HasValue1()
{
    ((int)PluginDeployment.Offline).Should().Be(1);
}

[Fact]
public void PluginDeployment_Both_HasValue2()
{
    ((int)PluginDeployment.Both).Should().Be(2);
}
```

- [ ] **Step 2: Write tests for PluginInvocationSource enum values**

```csharp
[Fact]
public void PluginInvocationSource_Parent_HasValue0()
{
    ((int)PluginInvocationSource.Parent).Should().Be(0);
}

[Fact]
public void PluginInvocationSource_Child_HasValue1()
{
    ((int)PluginInvocationSource.Child).Should().Be(1);
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Plugins.Tests/ -v q`
Expected: FAIL — types not defined

- [ ] **Step 4: Implement both enums**

`PluginDeployment.cs`:
```csharp
namespace PPDS.Plugins
{
    public enum PluginDeployment
    {
        ServerOnly = 0,
        Offline = 1,
        Both = 2
    }
}
```

`PluginInvocationSource.cs`:
```csharp
namespace PPDS.Plugins
{
    public enum PluginInvocationSource
    {
        Parent = 0,
        Child = 1
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/PPDS.Plugins.Tests/ -v q`
Expected: PASS

- [ ] **Step 6: Commit**

```
feat(plugins): add PluginDeployment and PluginInvocationSource enums
```

### Task 2: Add new properties to PluginStepAttribute

**Files:**
- Modify: `src/PPDS.Plugins/Attributes/PluginStepAttribute.cs`
- Test: `tests/PPDS.Plugins.Tests/PluginStepAttributeTests.cs`

- [ ] **Step 1: Write tests for new property defaults**

```csharp
[Fact]
public void Deployment_DefaultsTo_ServerOnly()
{
    var attr = new PluginStepAttribute();
    attr.Deployment.Should().Be(PluginDeployment.ServerOnly);
}

[Fact]
public void RunAsUser_DefaultsTo_Null()
{
    var attr = new PluginStepAttribute();
    attr.RunAsUser.Should().BeNull();
}

[Fact]
public void CanBeBypassed_DefaultsTo_True()
{
    var attr = new PluginStepAttribute();
    attr.CanBeBypassed.Should().BeTrue();
}

[Fact]
public void CanUseReadOnlyConnection_DefaultsTo_False()
{
    var attr = new PluginStepAttribute();
    attr.CanUseReadOnlyConnection.Should().BeFalse();
}

[Fact]
public void InvocationSource_DefaultsTo_Parent()
{
    var attr = new PluginStepAttribute();
    attr.InvocationSource.Should().Be(PluginInvocationSource.Parent);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Plugins.Tests/ -v q`
Expected: FAIL — properties not defined

- [ ] **Step 3: Add properties to PluginStepAttribute**

Add after `AsyncAutoDelete` property in `PluginStepAttribute.cs`:

```csharp
/// <summary>
/// Gets or sets the deployment target.
/// Default: ServerOnly.
/// </summary>
public PluginDeployment Deployment { get; set; } = PluginDeployment.ServerOnly;

/// <summary>
/// Gets or sets the impersonation user for step execution.
/// Values: null (calling user), "CallingUser", "System", a GUID, email, or domain name.
/// </summary>
public string? RunAsUser { get; set; }

/// <summary>
/// Gets or sets whether this step can be bypassed via BypassBusinessLogicExecution.
/// Default: true (matches Dataverse default).
/// </summary>
public bool CanBeBypassed { get; set; } = true;

/// <summary>
/// Gets or sets whether this step can use a read-only database connection.
/// Set to true for steps that only read data, for improved performance.
/// Default: false.
/// </summary>
public bool CanUseReadOnlyConnection { get; set; }

/// <summary>
/// Gets or sets the pipeline invocation source.
/// Parent executes in the parent pipeline, Child in child pipelines only.
/// Default: Parent.
/// </summary>
public PluginInvocationSource InvocationSource { get; set; } = PluginInvocationSource.Parent;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PPDS.Plugins.Tests/ -v q`
Expected: PASS

- [ ] **Step 5: Commit**

```
feat(plugins): add Deployment, RunAsUser, CanBeBypassed, CanUseReadOnlyConnection, InvocationSource to PluginStepAttribute
```

### Task 3: Add new properties to PluginImageAttribute

**Files:**
- Modify: `src/PPDS.Plugins/Attributes/PluginImageAttribute.cs`
- Test: `tests/PPDS.Plugins.Tests/PluginImageAttributeTests.cs`

- [ ] **Step 1: Write tests for new property defaults**

```csharp
[Fact]
public void Description_DefaultsTo_Null()
{
    var attr = new PluginImageAttribute();
    attr.Description.Should().BeNull();
}

[Fact]
public void MessagePropertyName_DefaultsTo_Null()
{
    var attr = new PluginImageAttribute();
    attr.MessagePropertyName.Should().BeNull();
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Plugins.Tests/ -v q`
Expected: FAIL

- [ ] **Step 3: Add properties to PluginImageAttribute**

Add after `StepId` property:

```csharp
/// <summary>
/// Gets or sets a description of this image's purpose.
/// Stored as metadata in Dataverse.
/// </summary>
public string? Description { get; set; }

/// <summary>
/// Gets or sets the message property name that carries the entity.
/// If null, auto-inferred from the message name (e.g., "Target" for Create/Update).
/// Override for non-standard messages.
/// </summary>
public string? MessagePropertyName { get; set; }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PPDS.Plugins.Tests/ -v q`
Expected: PASS

- [ ] **Step 5: Commit**

```
feat(plugins): add Description and MessagePropertyName to PluginImageAttribute
```

### Task 4: Add Custom API annotation attributes

**Files:**
- Create: `src/PPDS.Plugins/Attributes/CustomApiAttribute.cs`
- Create: `src/PPDS.Plugins/Attributes/CustomApiParameterAttribute.cs`
- Create: `src/PPDS.Plugins/Enums/ApiBindingType.cs`
- Create: `src/PPDS.Plugins/Enums/ApiParameterType.cs`
- Create: `src/PPDS.Plugins/Enums/ParameterDirection.cs`
- Create: `src/PPDS.Plugins/Enums/ApiProcessingStepType.cs`
- Test: `tests/PPDS.Plugins.Tests/CustomApiAttributeTests.cs`
- Test: `tests/PPDS.Plugins.Tests/CustomApiParameterAttributeTests.cs`

- [ ] **Step 1: Write tests for CustomApiAttribute defaults**

```csharp
[Fact]
public void UniqueName_DefaultsTo_Empty()
{
    var attr = new CustomApiAttribute();
    attr.UniqueName.Should().BeEmpty();
}

[Fact]
public void BindingType_DefaultsTo_Global()
{
    var attr = new CustomApiAttribute();
    attr.BindingType.Should().Be(ApiBindingType.Global);
}

[Fact]
public void IsFunction_DefaultsTo_False()
{
    var attr = new CustomApiAttribute();
    attr.IsFunction.Should().BeFalse();
}

[Fact]
public void IsPrivate_DefaultsTo_False()
{
    var attr = new CustomApiAttribute();
    attr.IsPrivate.Should().BeFalse();
}

[Fact]
public void AllowedProcessingStepType_DefaultsTo_None()
{
    var attr = new CustomApiAttribute();
    attr.AllowedProcessingStepType.Should().Be(ApiProcessingStepType.None);
}
```

- [ ] **Step 2: Write tests for CustomApiParameterAttribute defaults**

```csharp
[Fact]
public void Name_DefaultsTo_Empty()
{
    var attr = new CustomApiParameterAttribute();
    attr.Name.Should().BeEmpty();
}

[Fact]
public void IsOptional_DefaultsTo_False()
{
    var attr = new CustomApiParameterAttribute();
    attr.IsOptional.Should().BeFalse();
}

[Fact]
public void Direction_DefaultsTo_Input()
{
    var attr = new CustomApiParameterAttribute();
    attr.Direction.Should().Be(ParameterDirection.Input);
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Plugins.Tests/ -v q`
Expected: FAIL

- [ ] **Step 4: Implement enums**

`ApiBindingType.cs`: Global=0, Entity=1, EntityCollection=2
`ApiParameterType.cs`: Boolean=0, DateTime=1, Decimal=2, Entity=3, EntityCollection=4, EntityReference=5, Float=6, Integer=7, Money=8, Picklist=9, String=10, StringArray=11, Guid=12
`ParameterDirection.cs`: Input=0, Output=1
`ApiProcessingStepType.cs`: None=0, AsyncOnly=1, SyncAndAsync=2

- [ ] **Step 5: Implement CustomApiAttribute**

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CustomApiAttribute : Attribute
{
    public string UniqueName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Description { get; set; }
    public ApiBindingType BindingType { get; set; } = ApiBindingType.Global;
    public string? BoundEntity { get; set; }
    public bool IsFunction { get; set; }
    public bool IsPrivate { get; set; }
    public string? ExecutePrivilegeName { get; set; }
    public ApiProcessingStepType AllowedProcessingStepType { get; set; } = ApiProcessingStepType.None;
}
```

- [ ] **Step 6: Implement CustomApiParameterAttribute**

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class CustomApiParameterAttribute : Attribute
{
    public string Name { get; set; } = string.Empty;
    public string? UniqueName { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public ApiParameterType Type { get; set; }
    public string? LogicalEntityName { get; set; }
    public bool IsOptional { get; set; }
    public ParameterDirection Direction { get; set; } = ParameterDirection.Input;
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/PPDS.Plugins.Tests/ -v q`
Expected: PASS

- [ ] **Step 8: Commit**

```
feat(plugins): add CustomApiAttribute, CustomApiParameterAttribute, and supporting enums
```

---

## Phase 2: Service Layer — Core Plugin Gaps

Closes gaps in the existing `IPluginRegistrationService` and extraction pipeline.

### Task 5: Add enable/disable to PluginRegistrationService

**Files:**
- Modify: `src/PPDS.Cli/Plugins/Registration/IPluginRegistrationService.cs`
- Modify: `src/PPDS.Cli/Plugins/Registration/PluginRegistrationService.cs`
- Test: `tests/PPDS.Cli.Tests/Plugins/Registration/PluginRegistrationServiceTests.cs`

- [ ] **Step 1: Write test for EnableStepAsync**

```csharp
[Fact]
public async Task EnableStepAsync_SetsStateCodeToEnabled()
{
    // Arrange — mock pool, return a step entity
    // Act
    await _service.EnableStepAsync(stepId, CancellationToken.None);
    // Assert — verify SetStateRequest with StateCode=0, StatusCode=-1
}
```

- [ ] **Step 2: Write test for DisableStepAsync**

```csharp
[Fact]
public async Task DisableStepAsync_SetsStateCodeToDisabled()
{
    // Arrange
    // Act
    await _service.DisableStepAsync(stepId, CancellationToken.None);
    // Assert — verify SetStateRequest with StateCode=1, StatusCode=-1
}
```

- [ ] **Step 3: Run tests, verify fail**
- [ ] **Step 4: Add interface methods**

Add to `IPluginRegistrationService.cs`:
```csharp
Task EnableStepAsync(Guid stepId, CancellationToken cancellationToken = default);
Task DisableStepAsync(Guid stepId, CancellationToken cancellationToken = default);
```

- [ ] **Step 5: Implement in PluginRegistrationService**

Use `SetStateRequest` to toggle `StateCode` between Enabled(0) and Disabled(1).

- [ ] **Step 6: Run tests, verify pass**
- [ ] **Step 7: Commit**

```
feat(plugins): add EnableStepAsync/DisableStepAsync to service
```

### Task 6: Update extraction to read new annotation properties

**Files:**
- Modify: `src/PPDS.Cli/Plugins/Extraction/AssemblyExtractor.cs`
- Modify: `src/PPDS.Cli/Plugins/Models/PluginRegistrationConfig.cs`
- Test: `tests/PPDS.Cli.Tests/Plugins/Extraction/AssemblyExtractorTests.cs`

- [ ] **Step 1: Add new properties to PluginStepConfig**

Add to `PluginRegistrationConfig.cs` `PluginStepConfig` class:
```csharp
[JsonPropertyName("canBeBypassed")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public bool? CanBeBypassed { get; set; }

[JsonPropertyName("canUseReadOnlyConnection")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public bool? CanUseReadOnlyConnection { get; set; }

[JsonPropertyName("invocationSource")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public string? InvocationSource { get; set; }

[JsonPropertyName("secureConfiguration")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public string? SecureConfiguration { get; set; }
```

- [ ] **Step 2: Add new properties to PluginImageConfig**

```csharp
[JsonPropertyName("description")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public string? Description { get; set; }

[JsonPropertyName("messagePropertyName")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public string? MessagePropertyName { get; set; }
```

- [ ] **Step 3: Write tests for extraction of new properties**

Test that `AssemblyExtractor` reads `Deployment`, `RunAsUser`, `CanBeBypassed`, `CanUseReadOnlyConnection`, `InvocationSource` from `PluginStepAttribute` and `Description`, `MessagePropertyName` from `PluginImageAttribute`.

- [ ] **Step 4: Update MapStepAttribute in AssemblyExtractor**

Read the new properties from the attribute metadata and map them to `PluginStepConfig`/`PluginImageConfig`.

- [ ] **Step 5: Run tests, verify pass**
- [ ] **Step 6: Commit**

```
feat(plugins): extract new annotation properties (Deployment, RunAsUser, CanBeBypassed, etc.)
```

### Task 7: Update deploy to write new step/image properties

**Files:**
- Modify: `src/PPDS.Cli/Plugins/Registration/PluginRegistrationService.cs`
- Test: `tests/PPDS.Cli.Tests/Plugins/Registration/PluginRegistrationServiceTests.cs`

- [ ] **Step 1: Write test for CanBeBypassed being set on upsert**
- [ ] **Step 2: Write test for InvocationSource being set (not hardcoded to Internal)**
- [ ] **Step 3: Write test for SecureConfiguration creating SdkMessageProcessingStepSecureConfig entity**
- [ ] **Step 4: Write test for image Description and MessagePropertyName**
- [ ] **Step 5: Run tests, verify fail**
- [ ] **Step 6: Update UpsertStepAsync to set new fields**

In `PluginRegistrationService.cs` `UpsertStepAsync` method:
- Map `CanBeBypassed` → `sdkmessageprocessingstep.CanBeBypassed`
- Map `CanUseReadOnlyConnection` → `sdkmessageprocessingstep.CanUseReadOnlyConnection`
- Map `InvocationSource` → `sdkmessageprocessingstep.InvocationSource` (remove hardcoded Internal)
- Map `SecureConfiguration` → create/update `SdkMessageProcessingStepSecureConfig` entity, link via `SdkMessageProcessingStepSecureConfigId`

- [ ] **Step 7: Update UpsertImageAsync to set Description, MessagePropertyName**
- [ ] **Step 8: Run tests, verify pass**
- [ ] **Step 9: Commit**

```
feat(plugins): deploy new step/image properties and secure configuration
```

### Task 8: Add CLI enable/disable commands

**Files:**
- Create: `src/PPDS.Cli/Commands/Plugins/EnableCommand.cs`
- Create: `src/PPDS.Cli/Commands/Plugins/DisableCommand.cs`
- Modify: `src/PPDS.Cli/Commands/Plugins/PluginsCommandGroup.cs`

- [ ] **Step 1: Implement EnableCommand**

Follow pattern from `src/PPDS.Cli/Commands/Plugins/ListCommand.cs`. Accept `<step-name-or-id>` argument. Resolve step, call `EnableStepAsync`.

- [ ] **Step 2: Implement DisableCommand**

Same pattern, call `DisableStepAsync`.

- [ ] **Step 3: Register both in PluginsCommandGroup**

- [ ] **Step 4: Wire new step properties into update commands**

Modify `src/PPDS.Cli/Commands/Plugins/UpdateCommand.cs` to accept `--can-be-bypassed`, `--can-use-readonly`, `--invocation-source` flags and pass them through to `UpdateStepAsync`.

- [ ] **Step 5: Test via CLI**

Run: `dotnet run --project src/PPDS.Cli -- plugins enable --help`
Expected: Shows help with step-name-or-id argument

- [ ] **Step 6: Commit**

```
feat(cli): add ppds plugins enable/disable commands
```

---

## Phase 3: Service Layer — New Domains

New service interfaces and implementations for service endpoints, custom APIs, and data providers.

### Task 9: Implement IServiceEndpointService

**Files:**
- Create: `src/PPDS.Cli/Services/IServiceEndpointService.cs`
- Create: `src/PPDS.Cli/Services/ServiceEndpointService.cs`
- Modify: `src/PPDS.Cli/Services/ServiceRegistration.cs` (register in DI)
- Test: `tests/PPDS.Cli.Tests/Services/ServiceEndpointServiceTests.cs`

- [ ] **Step 1: Define IServiceEndpointService interface**

Methods: `ListAsync`, `GetByIdAsync`, `GetByNameAsync`, `RegisterWebhookAsync`, `RegisterServiceBusAsync`, `UpdateAsync`, `UnregisterAsync`. All accept `CancellationToken`. `UnregisterAsync` accepts `IProgressReporter` (Constitution A3).

- [ ] **Step 2: Write tests for ListAsync** — returns service endpoints and webhooks
- [ ] **Step 3: Run tests, verify fail**
- [ ] **Step 4: Implement ServiceEndpointService** — query `serviceendpoint` entity, map to `ServiceEndpointInfo` records
- [ ] **Step 5: Write tests for RegisterWebhookAsync** — validates URL, auth type, creates entity
- [ ] **Step 6: Implement RegisterWebhookAsync** — set Contract=Webhook(8), AuthType, AuthValue (XML for header/querystring, plain for webhookkey)
- [ ] **Step 7: Write tests for RegisterServiceBusAsync** — validates namespace, SAS key length (44 chars)
- [ ] **Step 8: Implement RegisterServiceBusAsync** — set Contract (Queue/Topic/EventHub), SASKey, SASKeyName, MessageFormat
- [ ] **Step 9: Write tests for UpdateAsync, UnregisterAsync**
- [ ] **Step 10: Implement UpdateAsync, UnregisterAsync** — cascade delete child steps on unregister
- [ ] **Step 11: Register in DI**
- [ ] **Step 12: Run all tests, verify pass**
- [ ] **Step 13: Commit**

```
feat: add IServiceEndpointService with full CRUD for endpoints and webhooks
```

### Task 10: Implement ICustomApiService

**Files:**
- Create: `src/PPDS.Cli/Services/ICustomApiService.cs`
- Create: `src/PPDS.Cli/Services/CustomApiService.cs`
- Modify: `src/PPDS.Cli/Services/ServiceRegistration.cs`
- Test: `tests/PPDS.Cli.Tests/Services/CustomApiServiceTests.cs`

- [ ] **Step 1: Define ICustomApiService interface**

Methods: `ListAsync`, `GetAsync`, `RegisterAsync`, `UpdateAsync`, `UnregisterAsync`, `AddParameterAsync`, `UpdateParameterAsync`, `RemoveParameterAsync`. All accept `CancellationToken`. `UnregisterAsync` and `RegisterAsync` (batch params) accept `IProgressReporter` (Constitution A3). `UpdateParameterAsync` updates a single parameter by ID.

- [ ] **Step 2: Write tests for ListAsync** — returns custom APIs with parameters
- [ ] **Step 3: Run tests, verify fail**
- [ ] **Step 4: Implement CustomApiService** — query `customapi`, `customapirequestparameter`, `customapiresponseproperty` entities
- [ ] **Step 5: Write tests for RegisterAsync** — creates API + parameters in single operation
- [ ] **Step 6: Implement RegisterAsync** — create `customapi`, then create request params + response properties
- [ ] **Step 7: Write tests for parameter CRUD**
- [ ] **Step 8: Implement AddParameterAsync, UpdateParameterAsync, RemoveParameterAsync**
- [ ] **Step 9: Register in DI**
- [ ] **Step 10: Run all tests, verify pass**
- [ ] **Step 11: Commit**

```
feat: add ICustomApiService with full CRUD for custom APIs and parameters
```

### Task 11: Implement IDataProviderService

**Files:**
- Create: `src/PPDS.Cli/Services/IDataProviderService.cs`
- Create: `src/PPDS.Cli/Services/DataProviderService.cs`
- Modify: `src/PPDS.Cli/Services/ServiceRegistration.cs`
- Test: `tests/PPDS.Cli.Tests/Services/DataProviderServiceTests.cs`

- [ ] **Step 1: Define IDataProviderService interface**

Methods: `ListDataSourcesAsync`, `GetDataSourceAsync`, `RegisterDataSourceAsync`, `UpdateDataSourceAsync`, `UnregisterDataSourceAsync`, `ListDataProvidersAsync`, `GetDataProviderAsync`, `RegisterDataProviderAsync`, `UpdateDataProviderAsync`, `UnregisterDataProviderAsync`. `UnregisterDataSourceAsync` accepts `IProgressReporter` (Constitution A3 — cascade deletes providers).

- [ ] **Step 2: Write tests for data source CRUD**
- [ ] **Step 3: Run tests, verify fail**
- [ ] **Step 4: Implement data source operations** — query `entitydatasource` entity
- [ ] **Step 5: Write tests for data provider CRUD** — all 5 plugin bindings
- [ ] **Step 6: Implement data provider operations** — query `entitydataprovider`, map plugin type GUIDs
- [ ] **Step 7: Register in DI**
- [ ] **Step 8: Run all tests, verify pass**
- [ ] **Step 9: Commit**

```
feat: add IDataProviderService with full CRUD for data sources and providers
```

### Task 12: Update Custom API extraction pipeline

**Files:**
- Modify: `src/PPDS.Cli/Plugins/Extraction/AssemblyExtractor.cs`
- Modify: `src/PPDS.Cli/Plugins/Models/PluginRegistrationConfig.cs`
- Modify: `src/PPDS.Cli/Commands/Plugins/DeployCommand.cs`
- Test: `tests/PPDS.Cli.Tests/Plugins/Extraction/AssemblyExtractorTests.cs`

- [ ] **Step 1: Add CustomApiConfig and CustomApiParameterConfig to PluginRegistrationConfig**

```csharp
public sealed class CustomApiConfig
{
    public string UniqueName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string PluginTypeName { get; set; } = "";
    public string? BindingType { get; set; }
    public string? BoundEntity { get; set; }
    public bool IsFunction { get; set; }
    public bool IsPrivate { get; set; }
    public string? ExecutePrivilegeName { get; set; }
    public string? AllowedProcessingStepType { get; set; }
    public List<CustomApiParameterConfig> Parameters { get; set; } = [];
}
```

Add `List<CustomApiConfig> CustomApis` to `PluginRegistrationConfig`.

- [ ] **Step 2: Write test for extracting CustomApiAttribute from assembly**
- [ ] **Step 3: Update AssemblyExtractor to read CustomApiAttribute and CustomApiParameterAttribute**
- [ ] **Step 4: Update DeployCommand to upsert custom APIs during deploy**
- [ ] **Step 5: Run tests, verify pass**
- [ ] **Step 6: Commit**

```
feat(plugins): extract and deploy custom APIs from annotated assemblies
```

---

## Phase 4: CLI Commands — New Domains

### Task 13: Add service-endpoints CLI commands

**Files:**
- Create: `src/PPDS.Cli/Commands/ServiceEndpoints/ServiceEndpointsCommandGroup.cs`
- Create: `src/PPDS.Cli/Commands/ServiceEndpoints/ListCommand.cs`
- Create: `src/PPDS.Cli/Commands/ServiceEndpoints/GetCommand.cs`
- Create: `src/PPDS.Cli/Commands/ServiceEndpoints/RegisterCommand.cs`
- Create: `src/PPDS.Cli/Commands/ServiceEndpoints/UpdateCommand.cs`
- Create: `src/PPDS.Cli/Commands/ServiceEndpoints/UnregisterCommand.cs`
- Modify: `src/PPDS.Cli/Program.cs` (register command group)

- [ ] **Step 1: Create ServiceEndpointsCommandGroup** with `ppds service-endpoints` root
- [ ] **Step 2: Implement ListCommand** — calls `IServiceEndpointService.ListAsync`
- [ ] **Step 3: Implement GetCommand** — calls `GetByNameAsync` or `GetByIdAsync`
- [ ] **Step 4: Implement RegisterCommand** — subcommands: `webhook`, `queue`, `topic`, `eventhub`
- [ ] **Step 5: Implement UpdateCommand** — accepts endpoint name/id + optional fields
- [ ] **Step 6: Implement UnregisterCommand** — accepts name/id + `--force`
- [ ] **Step 7: Register in Program.cs**
- [ ] **Step 8: Test via CLI help**

Run: `dotnet run --project src/PPDS.Cli -- service-endpoints --help`

- [ ] **Step 9: Commit**

```
feat(cli): add ppds service-endpoints commands
```

### Task 14: Add custom-apis CLI commands

**Files:**
- Create: `src/PPDS.Cli/Commands/CustomApis/CustomApisCommandGroup.cs`
- Create: `src/PPDS.Cli/Commands/CustomApis/ListCommand.cs`
- Create: `src/PPDS.Cli/Commands/CustomApis/GetCommand.cs`
- Create: `src/PPDS.Cli/Commands/CustomApis/RegisterCommand.cs`
- Create: `src/PPDS.Cli/Commands/CustomApis/UpdateCommand.cs`
- Create: `src/PPDS.Cli/Commands/CustomApis/UnregisterCommand.cs`
- Create: `src/PPDS.Cli/Commands/CustomApis/AddParameterCommand.cs`
- Create: `src/PPDS.Cli/Commands/CustomApis/RemoveParameterCommand.cs`
- Modify: `src/PPDS.Cli/Program.cs`

- [ ] **Step 1-7: Implement each command** following same pattern as Task 13
- [ ] **Step 8: Register in Program.cs**
- [ ] **Step 9: Commit**

```
feat(cli): add ppds custom-apis commands
```

### Task 15: Add data-providers CLI commands

**Files:**
- Create: `src/PPDS.Cli/Commands/DataProviders/DataProvidersCommandGroup.cs`
- Create: `src/PPDS.Cli/Commands/DataProviders/ListCommand.cs`
- Create: `src/PPDS.Cli/Commands/DataProviders/GetCommand.cs`
- Create: `src/PPDS.Cli/Commands/DataProviders/RegisterCommand.cs`
- Create: `src/PPDS.Cli/Commands/DataProviders/UpdateCommand.cs`
- Create: `src/PPDS.Cli/Commands/DataProviders/UnregisterCommand.cs`
- Create: `src/PPDS.Cli/Commands/DataSources/DataSourcesCommandGroup.cs`
- Create: `src/PPDS.Cli/Commands/DataSources/ListCommand.cs`
- Create: `src/PPDS.Cli/Commands/DataSources/GetCommand.cs`
- Create: `src/PPDS.Cli/Commands/DataSources/RegisterCommand.cs`
- Create: `src/PPDS.Cli/Commands/DataSources/UpdateCommand.cs`
- Create: `src/PPDS.Cli/Commands/DataSources/UnregisterCommand.cs`
- Modify: `src/PPDS.Cli/Program.cs`

- [ ] **Step 1-7: Implement data-providers commands**
- [ ] **Step 8-12: Implement data-sources commands**
- [ ] **Step 13: Register both in Program.cs**
- [ ] **Step 14: Commit**

```
feat(cli): add ppds data-providers and data-sources commands
```

---

## Phase 5: RPC Endpoints

Adds RPC handlers to support the Extension panel and other RPC clients.

### Task 16: Add plugin mutation RPC endpoints

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

- [ ] **Step 1: Add `plugins/get` handler** — accepts `type` and `id`, returns detailed info
- [ ] **Step 2: Add `plugins/messages` handler** — returns available SDK messages
- [ ] **Step 3: Add `plugins/entityAttributes` handler** — returns entity attributes for a given logical name
- [ ] **Step 4: Add `plugins/toggleStep` handler** — calls EnableStepAsync/DisableStepAsync
- [ ] **Step 5: Add `plugins/registerAssembly` handler** — accepts base64 DLL content + solutionName
- [ ] **Step 6: Add `plugins/registerPackage` handler** — accepts base64 nupkg content + solutionName
- [ ] **Step 7: Add `plugins/registerStep` handler**
- [ ] **Step 8: Add `plugins/registerImage` handler**
- [ ] **Step 9: Add `plugins/updateStep` handler**
- [ ] **Step 10: Add `plugins/updateImage` handler**
- [ ] **Step 11: Add `plugins/unregister` handler** — generic, accepts type + id + force
- [ ] **Step 12: Add `plugins/downloadBinary` handler** — returns base64 content
- [ ] **Step 13: Add response DTOs for each endpoint**
- [ ] **Step 14: Commit**

```
feat(rpc): add plugin mutation and browsing RPC endpoints
```

### Task 17: Add service endpoint RPC endpoints

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

- [ ] **Step 1: Add `serviceEndpoints/list` handler**
- [ ] **Step 2: Add `serviceEndpoints/get` handler**
- [ ] **Step 3: Add `serviceEndpoints/register` handler**
- [ ] **Step 4: Add `serviceEndpoints/update` handler**
- [ ] **Step 5: Add `serviceEndpoints/unregister` handler**
- [ ] **Step 6: Add DTOs**
- [ ] **Step 7: Commit**

```
feat(rpc): add service endpoint RPC endpoints
```

### Task 18: Add custom API RPC endpoints

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

- [ ] **Step 1: Add `customApis/list` handler**
- [ ] **Step 2: Add `customApis/get` handler**
- [ ] **Step 3: Add `customApis/register` handler**
- [ ] **Step 4: Add `customApis/update` handler**
- [ ] **Step 5: Add `customApis/unregister` handler**
- [ ] **Step 6: Add `customApis/addParameter` handler**
- [ ] **Step 7: Add `customApis/removeParameter` handler**
- [ ] **Step 8: Add DTOs**
- [ ] **Step 9: Commit**

```
feat(rpc): add custom API RPC endpoints
```

### Task 19: Add data provider RPC endpoints

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

- [ ] **Step 1: Add `dataProviders/list`, `dataProviders/get`, `dataProviders/register`, `dataProviders/update`, `dataProviders/unregister`**
- [ ] **Step 2: Add `dataSources/list`, `dataSources/get`, `dataSources/register`, `dataSources/update`, `dataSources/unregister`**
- [ ] **Step 3: Add DTOs**
- [ ] **Step 4: Commit**

```
feat(rpc): add data provider and data source RPC endpoints
```

---

## Phase 6: Extension DaemonClient

Wire the Extension to call the new RPC endpoints.

### Task 20: Add DaemonClient methods for all domains

**Files:**
- Modify: `src/PPDS.Extension/src/daemonClient.ts`
- Modify: `src/PPDS.Extension/src/panels/webview/shared/message-types.ts`

- [ ] **Step 1: Add response type interfaces**

In `daemonClient.ts`, add interfaces for all RPC responses:
- `PluginsGetResponse`, `PluginsMessagesResponse`, `PluginsEntityAttributesResponse`
- `ServiceEndpointsListResponse`, `ServiceEndpointsGetResponse`
- `CustomApisListResponse`, `CustomApisGetResponse`
- `DataProvidersListResponse`, `DataSourcesListResponse`
- Registration/update/unregister response types

- [ ] **Step 2: Add plugin browsing/mutation methods**

```typescript
async pluginsGet(type: string, id: string, environmentUrl?: string): Promise<PluginsGetResponse>
async pluginsMessages(filter?: string, environmentUrl?: string): Promise<PluginsMessagesResponse>
async pluginsEntityAttributes(entityLogicalName: string, environmentUrl?: string): Promise<PluginsEntityAttributesResponse>
async pluginsToggleStep(id: string, enabled: boolean, environmentUrl?: string): Promise<SuccessResponse>
async pluginsRegisterStep(config: StepRegistrationDto, environmentUrl?: string): Promise<RegisterResponse>
async pluginsUnregister(type: string, id: string, force?: boolean, environmentUrl?: string): Promise<UnregisterResponse>
// ... etc
```

- [ ] **Step 3: Add service endpoint methods**
- [ ] **Step 4: Add custom API methods**
- [ ] **Step 5: Add data provider methods**
- [ ] **Step 6: Add message types for PluginsPanel** in `message-types.ts`

Add `PluginsPanelWebviewToHost` and `PluginsPanelHostToWebview` discriminated unions per the spec.

- [ ] **Step 7: Run typecheck**

Run: `npm run typecheck` from `src/PPDS.Extension/`
Expected: PASS

- [ ] **Step 8: Commit**

```
feat(ext): add DaemonClient methods and message types for Plugins panel
```

---

## Phase 7: Extension Panel — Plugins

The main UI surface. This is the largest single task.

### Task 21: Create PluginsPanel host class

**Files:**
- Create: `src/PPDS.Extension/src/panels/PluginsPanel.ts`

- [ ] **Step 1: Create PluginsPanel extending WebviewPanelBase**

Follow pattern from `PluginTracesPanel.ts`:
- Static `show()` factory method with instance pooling
- `handleMessage()` switch for all `PluginsPanelWebviewToHost` commands
- `onInitialized()` loads root tree data
- `onEnvironmentChanged()` reloads
- `getHtmlContent()` generates webview HTML with nonce CSP

- [ ] **Step 2: Implement tree data loading**

`loadTree()` method:
- Call `daemon.pluginsList()` for assemblies/packages
- Call `daemon.serviceEndpointsList()` for endpoints/webhooks
- Call `daemon.customApisList()` for custom APIs
- Call `daemon.dataProvidersList()` for data sources/providers
- Post `treeLoaded` message with assembled hierarchy

- [ ] **Step 3: Implement lazy child loading**

Handle `expandNode` message:
- Assembly → load types via `plugins/get`
- Type → load steps
- Step → load images
- Post `childrenLoaded` message

- [ ] **Step 4: Implement mutation handlers**

Handle `registerEntity`, `updateEntity`, `toggleStep`, `unregister`, `downloadBinary` messages.
Show VS Code progress notification during operations.

- [ ] **Step 5: Run typecheck**

Run: `npm run typecheck`
Expected: PASS

- [ ] **Step 6: Commit**

```
feat(ext): create PluginsPanel host class
```

### Task 22: Create plugins-panel.ts — tree view core

**Files:**
- Create: `src/PPDS.Extension/src/panels/webview/plugins-panel.ts`
- Test: `src/PPDS.Extension/src/__tests__/panels/pluginsPanel.test.ts`

**Note:** All DOM rendering in the webview MUST use `textContent` or proper escaping — never `innerHTML` with untrusted data (Constitution S1).

- [ ] **Step 1: Write tests for tree node rendering**

Test that `renderNode()` creates correct DOM structure with indentation, icons, badges.

- [ ] **Step 2: Set up webview entry point**

```typescript
const vscode = getVsCodeApi<PluginsPanelWebviewToHost>();
installErrorHandler((msg) => vscode.postMessage(msg as PluginsPanelWebviewToHost));
```

- [ ] **Step 3: Implement tree view renderer**

- Render tree nodes with indentation, toggle triangles, icons, badges
- Virtual scrolling: fixed 30px node height, viewport calculation, 10-node overscan
- Click handlers for expand/collapse, selection
- Right-click context menu via `data-vscode-context`

- [ ] **Step 4: Implement message handling**

Handle all `PluginsPanelHostToWebview` commands in switch statement. Post `ready` on load.

- [ ] **Step 5: Run tests**

Run: `npm run ext:test`

- [ ] **Step 6: Commit**

```
feat(ext): create plugins-panel.ts with tree view and virtual scrolling
```

### Task 22b: Add view modes, filtering, and detail panel

**Files:**
- Modify: `src/PPDS.Extension/src/panels/webview/plugins-panel.ts`
- Modify: `src/PPDS.Extension/src/__tests__/panels/pluginsPanel.test.ts`

- [ ] **Step 1: Write tests for view mode data transformation**

Test that Assembly→Message→Entity view re-organizations produce correct hierarchies.

- [ ] **Step 2: Implement view mode switcher**

Dropdown for Assembly/Message/Entity. Cache raw data, transform client-side on mode switch:
- Assembly view: Package → Assembly → Type → Step → Image + root endpoints/APIs/providers
- Message view: SDK Message → Entity → Step → Image + Custom APIs
- Entity view: Entity → Message → Step → Image

- [ ] **Step 3: Implement filter bar**

Hide hidden steps toggle, Hide Microsoft assemblies toggle, text search with branch expansion.

- [ ] **Step 4: Implement detail panel**

Collapsible panel below tree. Key-value grid. Type-appropriate properties per node type.

- [ ] **Step 5: Run tests**
- [ ] **Step 6: Commit**

```
feat(ext): add view modes, filtering, search, and detail panel
```

### Task 22c: Add plugin registration forms

**Files:**
- Modify: `src/PPDS.Extension/src/panels/webview/plugins-panel.ts`

- [ ] **Step 1: Implement step registration form**

Modal dialog: Message autocomplete → load entities. Stage constrains Mode (async only for PostOperation). Filtering attributes multi-select picker. Conditional secure config, deployment, user context fields. Validate before submit.

- [ ] **Step 2: Implement image registration form**

ImageType checkboxes, Name, EntityAlias, Attributes picker, Description, MessagePropertyName.

- [ ] **Step 3: Implement assembly/package update form** — file picker for DLL/nupkg
- [ ] **Step 4: Commit**

```
feat(ext): add plugin step and image registration forms
```

### Task 22d: Add service endpoint, custom API, and data provider forms

**Files:**
- Modify: `src/PPDS.Extension/src/panels/webview/plugins-panel.ts`

- [ ] **Step 1: Implement webhook registration form**

Auth type conditional: HttpHeader/HttpQueryString show key-value grid, WebhookKey shows password field. URL validation.

- [ ] **Step 2: Implement service bus endpoint form**

Contract type conditional: Queue/Topic/EventHub path labels differ. SASKey/SASToken auth. MessageFormat options.

- [ ] **Step 3: Implement custom API registration form**

Conditional BoundEntity field (BindingType=Entity). Parameter sub-form with add/edit/delete rows.

- [ ] **Step 4: Implement data provider registration form**

Assembly → plugin cascade dropdowns. All 5 operation bindings visible.

- [ ] **Step 5: Commit**

```
feat(ext): add service endpoint, custom API, and data provider forms
```

### Task 23: Create plugins-panel.css styles

**Files:**
- Create: `src/PPDS.Extension/src/panels/styles/plugins-panel.css`

- [ ] **Step 1: Create CSS**

Import shared.css. Add styles for:
- Tree nodes (indentation, icons, badges, expand/collapse)
- Virtual scrolling container
- Detail panel (key-value grid)
- Filter bar, search input
- Registration form modals
- View mode dropdown
- Node state indicators (enabled ✓, disabled ✗, managed, hidden)

- [ ] **Step 2: Commit**

```
feat(ext): add plugins-panel.css styles
```

### Task 24: Register panel in Extension

**Files:**
- Modify: `src/PPDS.Extension/src/extension.ts` (import + command registration)
- Modify: `src/PPDS.Extension/package.json` (command + menu entries)
- Modify: `src/PPDS.Extension/esbuild.js` (build entries for webview JS + CSS)

- [ ] **Step 1: Add import and command in extension.ts**

```typescript
import { PluginsPanel } from './panels/PluginsPanel.js';
// In registerPanelCommands:
context.subscriptions.push(
    vscode.commands.registerCommand('ppds.openPlugins', () => PluginsPanel.show(context.extensionUri, client))
);
```

- [ ] **Step 2: Add command entries in package.json**

```json
{
    "command": "ppds.openPlugins",
    "title": "Open Plugins",
    "category": "PPDS",
    "icon": "$(extensions)"
},
{
    "command": "ppds.openPluginsForEnv",
    "title": "Open Plugins",
    "icon": "$(extensions)"
}
```

- [ ] **Step 3: Add esbuild entries**

In `builds` array, add:
```javascript
{ entryPoints: ['src/panels/webview/plugins-panel.ts'], outfile: 'dist/plugins-panel.js', /* IIFE */ }
{ entryPoints: ['src/panels/styles/plugins-panel.css'], outfile: 'dist/plugins-panel.css' }
```

- [ ] **Step 4: Build and verify**

Run: `npm run build` from `src/PPDS.Extension/`
Expected: No errors, `dist/plugins-panel.js` and `dist/plugins-panel.css` exist

- [ ] **Step 5: Commit**

```
feat(ext): register Plugins panel in extension
```

---

## Phase 8: TUI Screen

### Task 25: Implement PluginRegistrationScreen

**Files:**
- Create: `src/PPDS.Cli/Tui/Screens/PluginRegistrationScreen.cs`
- Create: `src/PPDS.Cli/Tui/Dialogs/ConfirmDestructiveActionDialog.cs` (if not already exists)
- Modify: `src/PPDS.Cli/Tui/TuiShell.cs` (add menu entry)

- [ ] **Step 1: Create PluginRegistrationScreen**

Extends `TuiScreenBase`. Layout:
- Left: `TreeView<PluginTreeNode>` with lazy-loaded children
- Right: `FrameView` detail panel showing properties of selected node

```csharp
internal sealed class PluginRegistrationScreen : TuiScreenBase
{
    private readonly TreeView<PluginTreeNode> _tree;
    private readonly FrameView _detailPanel;

    public override string Title => $"Plugin Registration - {EnvironmentDisplayName}";
}
```

- [ ] **Step 2: Implement tree loading**

On activation: call `ListPackagesAsync()` + `ListAssembliesAsync()` (standalone).
On expand: lazy-load children per node type.

- [ ] **Step 3: Implement detail panel**

On selection change: populate detail panel with type-appropriate properties.

- [ ] **Step 4: Implement hotkeys**

| Hotkey | Action |
|--------|--------|
| Space | Toggle step enabled/disabled |
| Delete | Open ConfirmDestructiveActionDialog |
| Ctrl+D | Download assembly/package binary |
| F5 | Refresh tree |

- [ ] **Step 5: Implement ConfirmDestructiveActionDialog** (if needed)

Shared dialog: Normal (OK/Cancel) and High severity (type "DELETE" to confirm).

- [ ] **Step 6: Register in TuiShell menu**

Add "Plugin Registration" under Tools menu.

- [ ] **Step 7: Implement state capture** for `ITuiStateCapture<PluginRegistrationScreenState>`

- [ ] **Step 8: Run TUI tests**

Run: `dotnet test --filter "Category=TuiUnit" -v q`

- [ ] **Step 9: Commit**

```
feat(tui): add PluginRegistrationScreen with tree view and detail panel
```

---

## Phase 9: MCP Tools

### Task 26: Add read-only MCP tools

**Files:**
- Modify: `src/PPDS.Mcp/Tools/PluginsListTool.cs` (already exists — add `includeHidden`, `includeMicrosoft` params)
- Create: `src/PPDS.Mcp/Tools/PluginsGetTool.cs`
- Create: `src/PPDS.Mcp/Tools/ServiceEndpointsListTool.cs`
- Create: `src/PPDS.Mcp/Tools/CustomApisListTool.cs`
- Create: `src/PPDS.Mcp/Tools/DataProvidersListTool.cs`

- [ ] **Step 1: Update PluginsListTool** — add `includeHidden` and `includeMicrosoft` parameters

```csharp
[McpServerToolType]
public sealed class PluginsListTool
{
    [McpServerTool(Name = "ppds_plugins_list")]
    [Description("List registered plugin assemblies, packages, types, steps, and images")]
    public async Task<PluginsListResult> ExecuteAsync(
        [Description("Filter by assembly name")] string? assembly = null,
        [Description("Filter by package name")] string? package = null,
        [Description("Include hidden system steps")] bool includeHidden = false,
        [Description("Include Microsoft assemblies")] bool includeMicrosoft = false,
        CancellationToken cancellationToken = default)
}
```

- [ ] **Step 2: Implement PluginsGetTool**
- [ ] **Step 3: Implement ServiceEndpointsListTool**
- [ ] **Step 4: Implement CustomApisListTool**
- [ ] **Step 5: Implement DataProvidersListTool**
- [ ] **Step 6: Run MCP tests**

Run: `dotnet test tests/PPDS.Mcp.Tests/ -v q`

- [ ] **Step 7: Commit**

```
feat(mcp): add read-only MCP tools for plugins, endpoints, APIs, and providers
```

---

## Phase 10: Integration Testing and Polish

### Task 27: Extension host-side unit tests

**Files:**
- Create: `src/PPDS.Extension/src/__tests__/panels/pluginsPanelHost.test.ts`

- [ ] **Step 1: Write tests for PluginsPanel message handling** (host side)
- [ ] **Step 2: Write tests for tree data loading and assembly**
- [ ] **Step 3: Write tests for mutation dispatch (toggle, unregister, register)**
- [ ] **Step 4: Run extension tests**

Run: `npm run ext:test`

- [ ] **Step 5: Commit**

```
test(ext): add PluginsPanel host-side unit tests
```

### Task 28: Update plugins/list RPC to include all domains

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

The existing `plugins/list` RPC currently returns only assemblies and packages. Update it to also include service endpoints, webhooks, custom APIs, and data sources/providers — so the Extension panel can load the complete tree in one call.

- [ ] **Step 1: Add service endpoint, custom API, data provider fields to PluginsListResponse**
- [ ] **Step 2: Populate from all three services in PluginsListAsync**
- [ ] **Step 3: Add DTOs for the new types**
- [ ] **Step 4: Commit**

```
feat(rpc): expand plugins/list to include endpoints, APIs, and providers
```

### Task 29: Run full quality gates

- [ ] **Step 1: Run dotnet build**

Run: `dotnet build PPDS.sln -v q`

- [ ] **Step 2: Run dotnet test (unit)**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`

- [ ] **Step 3: Run extension typecheck + lint**

Run: `npm run typecheck:all && npm run lint`

- [ ] **Step 4: Run extension tests**

Run: `npm run ext:test`

- [ ] **Step 5: Fix any failures**
- [ ] **Step 6: Commit any fixes**

```
fix: address quality gate findings
```

---

## Task Dependency Graph

```
Phase 1 (Annotations)
  Tasks 1-4 → sequential (each builds on prior enums)

Phase 2 (Core Plugin Gaps)
  Task 5 (enable/disable service) → Task 8 (enable/disable CLI)
  Task 6 (extraction) → Task 7 (deploy)

Phase 3 (New Domain Services)
  Tasks 9-11 → independent, can parallelize
  Task 12 (custom API extraction) → depends on Task 10

Phase 4 (CLI Commands)
  Task 13 → depends on Task 9
  Task 14 → depends on Task 10
  Task 15 → depends on Task 11

Phase 5 (RPC)
  Task 16 → depends on Tasks 5-7
  Tasks 17-19 → depend on Tasks 9-11

Phase 6 (DaemonClient)
  Task 20 → depends on Tasks 16-19

Phase 7 (Extension Panel)
  Task 21 (host) → depends on Task 20
  Task 22 (tree core) → depends on Task 21
  Task 22b (view modes/filters) → depends on Task 22
  Task 22c (plugin forms) → depends on Task 22b
  Task 22d (domain forms) → depends on Task 22b
  Tasks 22c and 22d → can parallelize
  Task 23 (CSS) → independent, can start with Task 22
  Task 24 (registration) → depends on Tasks 21-23

Phase 8 (TUI)
  Task 25 → depends on Tasks 5, 9-11 (services only)

Phase 9 (MCP)
  Task 26 → depends on Tasks 5, 9-11 (services only)

Phase 10 (Polish)
  Tasks 27-29 → depend on everything
```

Phases 8 and 9 (TUI and MCP) only depend on services (Phases 2-3) and can run in parallel with Phases 5-7 (RPC and Extension).
