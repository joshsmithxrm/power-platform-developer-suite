# Auth DI Registration Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Eliminate manual `new ProfileStore()`, `new NativeCredentialStore()`, and `new EnvironmentConfigStore()` instantiation across the codebase by introducing proper DI registration in `PPDS.Auth` and migrating all consumers (CLI commands, TUI, MCP, RPC) to resolve from DI.

**Architecture:** `PPDS.Auth` gets a new `AddAuthServices()` extension method (mirroring `PPDS.Dataverse`'s `RegisterDataverseServices()`). All four connection-independent stores/services become DI-managed singletons. Each consumer registers auth services once at startup. CLI commands that don't connect to Dataverse use a lightweight `CreateLocalProvider()` helper. `PPDS.Cli`'s `AddCliApplicationServices()` removes its duplicate `ProfileStore` registration and adds `IEnvironmentConfigService`.

**Tech Stack:** C# (.NET 8/9/10), Microsoft.Extensions.DependencyInjection, xUnit

**Worktree:** `.worktrees/tui-polish` on branch `fix/tui-colors` (or new branch — decide before execution)

**Build & test commands:**
```
dotnet build --nologo --verbosity quiet
dotnet test --filter "Category!=Integration" --nologo --verbosity quiet
```

**Important context for implementors:**
- `ProfileStore` lives in `PPDS.Auth/Profiles/ProfileStore.cs` — implements `IDisposable`, uses `SemaphoreSlim` for thread safety
- `NativeCredentialStore` lives in `PPDS.Auth/Credentials/NativeCredentialStore.cs` — implements `ISecureCredentialStore, IDisposable`
- `EnvironmentConfigStore` lives in `PPDS.Auth/Profiles/EnvironmentConfigStore.cs` — implements `IDisposable`, uses `SemaphoreSlim`
- `EnvironmentConfigService` lives in `PPDS.Cli/Services/Environment/EnvironmentConfigService.cs` — implements `IEnvironmentConfigService`
- `PPDS.Dataverse` has the pattern to follow: `DependencyInjection/ServiceCollectionExtensions.cs` with `RegisterDataverseServices()`
- `PPDS.Auth` does NOT currently reference `Microsoft.Extensions.DependencyInjection.Abstractions`
- The existing `AddCliApplicationServices()` in `PPDS.Cli/Services/ServiceRegistration.cs` registers `ProfileStore` as Singleton (line 37) — this will move to `AddAuthServices()`
- `NativeCredentialStore` constructor: `public NativeCredentialStore(bool allowCleartextFallback = false)` — parameterless creation is fine for DI
- All stores are thread-safe (semaphore-based) and safe as singletons

---

## Phase 1: Foundation — Create AddAuthServices in PPDS.Auth

### Task 1: Add DI abstractions dependency to PPDS.Auth

**Files:**
- Modify: `src/PPDS.Auth/PPDS.Auth.csproj:52-53`

**Step 1: Add the NuGet package reference**

In `PPDS.Auth.csproj`, after the `Microsoft.Extensions.Logging.Abstractions` reference (line 53), add:

```xml
    <!-- Dependency injection abstractions for AddAuthServices registration -->
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
```

**Step 2: Restore packages**

Run: `dotnet restore src/PPDS.Auth/PPDS.Auth.csproj`
Expected: Restore succeeded

**Step 3: Build to verify**

Run: `dotnet build src/PPDS.Auth/PPDS.Auth.csproj --nologo -v q`
Expected: Build succeeded, no errors

**Step 4: Commit**

```
chore(auth): add Microsoft.Extensions.DependencyInjection.Abstractions package reference
```

---

### Task 2: Create AddAuthServices extension method

**Files:**
- Create: `src/PPDS.Auth/DependencyInjection/ServiceCollectionExtensions.cs`

**Step 1: Create the extension method**

Create `src/PPDS.Auth/DependencyInjection/ServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;

namespace PPDS.Auth.DependencyInjection;

/// <summary>
/// Extension methods for registering PPDS Auth services with dependency injection.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers PPDS Auth connection-independent services as singletons.
    /// Call this once per DI container — stores are file-based singletons
    /// that should be shared across the application lifetime.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAuthServices(this IServiceCollection services)
    {
        services.AddSingleton<ProfileStore>();
        services.AddSingleton<EnvironmentConfigStore>();
        services.AddSingleton<ISecureCredentialStore, NativeCredentialStore>();

        return services;
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build src/PPDS.Auth/PPDS.Auth.csproj --nologo -v q`
Expected: Build succeeded

**Step 3: Commit**

```
feat(auth): add AddAuthServices DI registration for ProfileStore, EnvironmentConfigStore, NativeCredentialStore
```

---

### Task 3: Update AddCliApplicationServices to use AddAuthServices

**Files:**
- Modify: `src/PPDS.Cli/Services/ServiceRegistration.cs:1-37`

**Step 1: Remove duplicate ProfileStore registration and add auth + environment config services**

In `ServiceRegistration.cs`:

1. Add using directive at top: `using PPDS.Auth.DependencyInjection;`
2. Remove line 37: `services.AddSingleton<ProfileStore>();`
3. Add `services.AddAuthServices();` at the top of the method body (before line 38)
4. Add `IEnvironmentConfigService` registration after the `ITuiThemeService` registration (after line 77):

```csharp
        // Environment configuration
        services.AddSingleton<IEnvironmentConfigService>(sp =>
            new EnvironmentConfigService(sp.GetRequiredService<EnvironmentConfigStore>()));
```

The method should now start with:

```csharp
    public static IServiceCollection AddCliApplicationServices(this IServiceCollection services)
    {
        // Auth services (ProfileStore, EnvironmentConfigStore, NativeCredentialStore)
        services.AddAuthServices();

        // Profile management services
        services.AddTransient<IProfileService, ProfileService>();
        services.AddTransient<IEnvironmentService, EnvironmentService>();
        // ... rest unchanged
```

**Step 2: Build to verify**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo -v q`
Expected: Build succeeded

**Step 3: Run all non-integration tests**

Run: `dotnet test --filter "Category!=Integration" --nologo -v q`
Expected: All pass — this is a pure DI registration change, no behavior change

**Step 4: Commit**

```
refactor(cli): use AddAuthServices in AddCliApplicationServices, add IEnvironmentConfigService registration
```

---

## Phase 2: Migrate ProfileServiceFactory

### Task 4: Refactor ProfileServiceFactory to accept stores from DI

**Files:**
- Modify: `src/PPDS.Cli/Infrastructure/ProfileServiceFactory.cs:61-131,184-231,284-327`

This is the trickiest migration. `CreateFromProfileAsync` creates both `ProfileStore` (line 70) and `NativeCredentialStore` (line 92) and uses `store` in a closure (`onProfileUpdated` callback at line 103). The stores need to be passed in or the method needs to resolve them.

**Step 1: Remove NativeCredentialStore from CreateProviderFromSources signature**

`CreateProviderFromSources` (line 284) currently receives `ISecureCredentialStore credentialStore` and registers it as a singleton. Since `AddAuthServices()` now registers `ISecureCredentialStore`, remove the parameter:

Replace lines 284-297:
```csharp
    private static ServiceProvider CreateProviderFromSources(
        IConnectionSource[] sources,
        ResolvedConnectionInfo connectionInfo,
        bool verbose,
        bool debug)
    {
        var services = new ServiceCollection();
        ConfigureLogging(services, verbose, debug);

        services.AddSingleton(connectionInfo);

        var dataverseOptions = new DataverseOptions();
```

Remove line 297 (`services.AddSingleton<ISecureCredentialStore>(credentialStore);`) — it's now handled by `AddAuthServices()` via `AddCliApplicationServices()`.

**Step 2: Update CreateFromProfileAsync to resolve stores from the container**

Replace lines 70-92 in `CreateFromProfileAsync`:

```csharp
        var store = new ProfileStore();
```

becomes:

```csharp
        using var localServices = CreateLocalProvider();
        var store = localServices.GetRequiredService<ProfileStore>();
```

And line 92:

```csharp
        var credentialStore = new NativeCredentialStore();
```

becomes:

```csharp
        var credentialStore = localServices.GetRequiredService<ISecureCredentialStore>();
```

Update the call to `CreateProviderFromSources` (line 130) — remove the `credentialStore` argument:

```csharp
        return CreateProviderFromSources(new[] { adapter }, connectionInfo, verbose, debug);
```

**Step 3: Update CreateFromProfilesAsync similarly**

Replace lines 201-211 in `CreateFromProfilesAsync`:

```csharp
        var store = new ProfileStore();
```

becomes:

```csharp
        using var localServices = CreateLocalProvider();
        var store = localServices.GetRequiredService<ProfileStore>();
```

Line 211:

```csharp
        var credentialStore = new NativeCredentialStore();
```

becomes:

```csharp
        var credentialStore = localServices.GetRequiredService<ISecureCredentialStore>();
```

Update line 230 call to `CreateProviderFromSources` — remove `credentialStore` argument.

**Step 4: Add CreateLocalProvider helper**

Add this public static method to `ProfileServiceFactory`:

```csharp
    /// <summary>
    /// Creates a lightweight service provider with auth services only (no Dataverse connection).
    /// Use for commands that need ProfileStore, EnvironmentConfigStore, or NativeCredentialStore
    /// but don't connect to a Dataverse environment.
    /// </summary>
    public static ServiceProvider CreateLocalProvider()
    {
        var services = new ServiceCollection();
        services.AddCliApplicationServices();
        return services.BuildServiceProvider();
    }
```

Note: `AddCliApplicationServices()` calls `AddAuthServices()` internally, so this gives access to all auth stores plus CLI services like `IEnvironmentConfigService`. Some registrations (ISqlQueryService, IConnectionService) will fail if resolved since they need a connection pool — but they won't be resolved by local-only commands.

**Step 5: Build and test**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo -v q`
Expected: Build succeeded

Run: `dotnet test --filter "Category!=Integration" --nologo -v q`
Expected: All pass

**Step 6: Commit**

```
refactor(cli): migrate ProfileServiceFactory to resolve stores from DI
```

---

## Phase 3: Migrate CLI Commands

### Task 5: Migrate AuthCommandGroup

**Files:**
- Modify: `src/PPDS.Cli/Commands/Auth/AuthCommandGroup.cs`

All 8 `using var store = new ProfileStore()` sites and 1 `using var credentialStore = new NativeCredentialStore()` site need to use `CreateLocalProvider()`.

**Step 1: Migrate all ProfileStore sites**

For each of these methods, replace the manual creation pattern:

```csharp
// OLD:
using var store = new ProfileStore();

// NEW:
await using var localProvider = ProfileServiceFactory.CreateLocalProvider();
var store = localProvider.GetRequiredService<ProfileStore>();
```

Apply to lines: 229, 658, 818, 914, 1046, 1159, 1212, 1271.

For line 1227 (`new NativeCredentialStore()`), use:

```csharp
var credentialStore = localProvider.GetRequiredService<ISecureCredentialStore>();
```

Since each method already had `using var store`, the provider's `await using` replaces the store's `using`. The DI container disposes the stores when the provider is disposed.

Important: Some methods already create a `ServiceProvider` via `ProfileServiceFactory.CreateFromProfileAsync()` — those don't need a local provider since the connected provider already includes auth services. Only methods that work **without** a Dataverse connection need `CreateLocalProvider()`. Review each method to determine which pattern applies.

Methods that are local-only (need `CreateLocalProvider()`):
- `ExecuteListAsync` (line 658) — lists profiles from file
- `ExecuteSelectAsync` (line 818) — selects active profile
- `ExecuteDeleteAsync` (line 914) — deletes profile
- `ExecuteRenameAsync` (line 1046) — renames profile
- `ExecuteNameAsync` (line 1159) — shows active profile name
- `ExecuteClearAsync` (line 1212) — clears profiles + credentials
- `ExecuteWhoAsync` (line 1271) — shows current profile info

Methods that connect to Dataverse (use connected provider):
- `ExecuteCreateAsync` (line 229) — creates profile and tests connection

For `ExecuteCreateAsync`, the `ProfileStore` usage at line 229 happens **before** the connection is made (to check for name conflicts). Use `CreateLocalProvider()` here too.

**Step 2: Add required using directives**

Add at top of file if not already present:
```csharp
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
```

**Step 3: Build and test**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo -v q`
Expected: Build succeeded

Run: `dotnet test --filter "Category!=Integration" --nologo -v q`
Expected: All pass

**Step 4: Commit**

```
refactor(auth): migrate AuthCommandGroup to resolve stores from DI
```

---

### Task 6: Migrate EnvCommandGroup

**Files:**
- Modify: `src/PPDS.Cli/Commands/Env/EnvCommandGroup.cs`

5 manual instantiation sites: ProfileStore (lines 91, 257, 356), EnvironmentConfigStore (lines 576, 722), plus NativeCredentialStore (line 271).

**Step 1: Migrate local-only commands**

For `ExecuteListAsync` (line 91), `ExecuteShowAsync` (line 356):

```csharp
// OLD:
using var store = new ProfileStore();

// NEW:
await using var localProvider = ProfileServiceFactory.CreateLocalProvider();
var store = localProvider.GetRequiredService<ProfileStore>();
```

For `ExecuteSelectAsync` (line 257) which also creates `NativeCredentialStore` at line 271:

```csharp
// OLD:
using var store = new ProfileStore();
// ... later:
using var credentialStore = new NativeCredentialStore();

// NEW:
await using var localProvider = ProfileServiceFactory.CreateLocalProvider();
var store = localProvider.GetRequiredService<ProfileStore>();
// ... later:
var credentialStore = localProvider.GetRequiredService<ISecureCredentialStore>();
```

**Step 2: Migrate config and type commands**

For config command (line 576) and type command (line 722):

```csharp
// OLD:
using var store = new EnvironmentConfigStore();
var service = new EnvironmentConfigService(store);

// NEW:
await using var localProvider = ProfileServiceFactory.CreateLocalProvider();
var service = localProvider.GetRequiredService<IEnvironmentConfigService>();
```

**Step 3: Add required using directives**

Add at top if not present:
```csharp
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
```

**Step 4: Build and test**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo -v q`
Expected: Build succeeded

Run: `dotnet test --filter "Category!=Integration" --nologo -v q`
Expected: All pass

**Step 5: Commit**

```
refactor(cli): migrate EnvCommandGroup to resolve stores from DI
```

---

## Phase 4: Migrate RPC Daemon

### Task 7: Migrate RpcMethodHandler

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/ServeCommand.cs:46-49`
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs:27-38,65,100,177,227,290,339,727`

**Step 1: Create a local provider in ServeCommand and pass to RpcMethodHandler**

In `ServeCommand.ExecuteAsync` (lines 46-49), replace:

```csharp
        await using var poolManager = new DaemonConnectionPoolManager();
        var handler = new RpcMethodHandler(poolManager);
```

with:

```csharp
        await using var authProvider = ProfileServiceFactory.CreateLocalProvider();
        await using var poolManager = new DaemonConnectionPoolManager(
            loadProfilesAsync: async ct =>
            {
                var store = authProvider.GetRequiredService<ProfileStore>();
                return await store.LoadAsync(ct).ConfigureAwait(false);
            });
        var handler = new RpcMethodHandler(poolManager, authProvider);
```

**Step 2: Update RpcMethodHandler constructor to accept IServiceProvider**

In `RpcMethodHandler.cs`, update the constructor:

```csharp
    private readonly IDaemonConnectionPoolManager _poolManager;
    private readonly IServiceProvider _authServices;
    private JsonRpc? _rpc;

    public RpcMethodHandler(IDaemonConnectionPoolManager poolManager, IServiceProvider authServices)
    {
        _poolManager = poolManager ?? throw new ArgumentNullException(nameof(poolManager));
        _authServices = authServices ?? throw new ArgumentNullException(nameof(authServices));
    }
```

**Step 3: Migrate all 7 ProfileStore sites and NativeCredentialStore site**

Replace every occurrence of:
```csharp
using var store = new ProfileStore();
```

with:
```csharp
var store = _authServices.GetRequiredService<ProfileStore>();
```

And replace:
```csharp
using var credentialStore = new NativeCredentialStore();
```

with:
```csharp
var credentialStore = _authServices.GetRequiredService<ISecureCredentialStore>();
```

Remove the `using` keyword since the DI container manages disposal.

Apply to lines: 65, 100, 177, 227, 290, 300, 339, 727.

**Step 4: Build and test**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo -v q`
Expected: Build succeeded

Run: `dotnet test --filter "Category!=Integration" --nologo -v q`
Expected: All pass

**Step 5: Commit**

```
refactor(rpc): migrate RpcMethodHandler to resolve stores from DI
```

---

### Task 8: Migrate DaemonConnectionPoolManager

**Files:**
- Modify: `src/PPDS.Cli/Infrastructure/DaemonConnectionPoolManager.cs:40-57`

**Step 1: Replace DefaultLoadProfilesAsync with injectable delegate**

The `DaemonConnectionPoolManager` already accepts a `Func<CancellationToken, Task<ProfileCollection>>` delegate (line 42). The `ServeCommand` now passes this delegate using the DI-resolved `ProfileStore` (done in Task 7). The only remaining issue is the `DefaultLoadProfilesAsync` fallback (lines 53-57) which still does `new ProfileStore()`.

Replace the default delegate at lines 53-57:

```csharp
    private static async Task<ProfileCollection> DefaultLoadProfilesAsync(CancellationToken cancellationToken)
    {
        using var store = new ProfileStore();
        return await store.LoadAsync(cancellationToken).ConfigureAwait(false);
    }
```

Since ServeCommand now always passes the delegate, this fallback is only used by tests. Keep it for test backward compatibility but update to use DI:

```csharp
    private static async Task<ProfileCollection> DefaultLoadProfilesAsync(CancellationToken cancellationToken)
    {
        await using var provider = ProfileServiceFactory.CreateLocalProvider();
        var store = provider.GetRequiredService<ProfileStore>();
        return await store.LoadAsync(cancellationToken).ConfigureAwait(false);
    }
```

**Step 2: Build and test**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo -v q`
Expected: Build succeeded

**Step 3: Commit**

```
refactor(cli): migrate DaemonConnectionPoolManager default profile loader to DI
```

---

## Phase 5: Migrate MCP Server

### Task 9: Register auth services in MCP host and migrate McpToolContext

**Files:**
- Modify: `src/PPDS.Mcp/Program.cs:29-33`
- Modify: `src/PPDS.Mcp/Infrastructure/McpToolContext.cs:34-40,48-58,109-129`

**Step 1: Add AddAuthServices to MCP Program.cs**

In `Program.cs`, after line 33 (`builder.Services.RegisterDataverseServices();`), add:

```csharp
builder.Services.AddAuthServices();
```

Add using directive: `using PPDS.Auth.DependencyInjection;`

**Step 2: Update McpToolContext constructor to accept ProfileStore and ISecureCredentialStore**

Replace constructor (lines 34-40):

```csharp
    private readonly IMcpConnectionPoolManager _poolManager;
    private readonly ProfileStore _profileStore;
    private readonly ISecureCredentialStore _credentialStore;
    private readonly ILoggerFactory _loggerFactory;

    public McpToolContext(
        IMcpConnectionPoolManager poolManager,
        ProfileStore profileStore,
        ISecureCredentialStore credentialStore,
        ILoggerFactory? loggerFactory = null)
    {
        _poolManager = poolManager ?? throw new ArgumentNullException(nameof(poolManager));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }
```

**Step 3: Migrate all ProfileStore and NativeCredentialStore sites in McpToolContext**

Replace all `using var store = new ProfileStore();` with `var store = _profileStore;` (lines 50, 67, ~155).

Replace `new NativeCredentialStore()` with `_credentialStore` (line ~119 in CreateServiceProviderAsync).

**Step 4: Build and test**

Run: `dotnet build src/PPDS.Mcp/PPDS.Mcp.csproj --nologo -v q`
Expected: Build succeeded

Run: `dotnet test tests/PPDS.Mcp.Tests --nologo -v q`
Expected: All pass (or pass with existing skips)

**Step 5: Commit**

```
refactor(mcp): register auth services in Host DI and inject into McpToolContext
```

---

### Task 10: Migrate McpConnectionPoolManager

**Files:**
- Modify: `src/PPDS.Mcp/Infrastructure/McpConnectionPoolManager.cs:40-57`

**Step 1: Accept ProfileStore via constructor injection**

Update constructor to accept `ProfileStore`:

```csharp
    public McpConnectionPoolManager(
        ProfileStore? profileStore = null,
        ILoggerFactory? loggerFactory = null,
        Func<CancellationToken, Task<ProfileCollection>>? loadProfilesAsync = null,
        TimeSpan? poolCreationTimeout = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _loadProfilesAsync = loadProfilesAsync ?? (profileStore != null
            ? (ct => profileStore.LoadAsync(ct))
            : DefaultLoadProfilesAsync);
        _poolCreationTimeout = poolCreationTimeout ?? DefaultPoolCreationTimeout;
    }
```

When `ProfileStore` is injected (MCP host), it uses the injected instance. When null (test scenarios), falls back to `DefaultLoadProfilesAsync`.

**Step 2: Build and test**

Run: `dotnet build src/PPDS.Mcp/PPDS.Mcp.csproj --nologo -v q`
Expected: Build succeeded

Run: `dotnet test tests/PPDS.Mcp.Tests --nologo -v q`
Expected: All pass

**Step 3: Commit**

```
refactor(mcp): inject ProfileStore into McpConnectionPoolManager
```

---

## Phase 6: Migrate TUI

### Task 11: Migrate InteractiveSession and PpdsApplication

**Files:**
- Modify: `src/PPDS.Cli/Tui/PpdsApplication.cs:38-88`
- Modify: `src/PPDS.Cli/Tui/InteractiveSession.cs:40-57,108-130`

**Step 1: Update PpdsApplication to create a local provider and pass stores to InteractiveSession**

In `PpdsApplication.Run()`, replace line 45:

```csharp
        _profileStore = new ProfileStore();
```

with:

```csharp
        _authProvider = ProfileServiceFactory.CreateLocalProvider();
        _profileStore = _authProvider.GetRequiredService<ProfileStore>();
```

Add field: `private ServiceProvider? _authProvider;`

Dispose the provider in the finally block (after session disposal):

```csharp
finally
{
    // ... existing session disposal ...
    _authProvider?.Dispose();
}
```

Update the `InteractiveSession` constructor call (line ~88) to pass `EnvironmentConfigStore`:

```csharp
        _session = new InteractiveSession(
            _profileName,
            _profileStore,
            _authProvider.GetRequiredService<EnvironmentConfigStore>(),
            serviceProviderFactory: null,
            _deviceCodeCallback,
            beforeInteractiveAuth);
```

**Step 2: Update InteractiveSession constructor to accept EnvironmentConfigStore**

In `InteractiveSession.cs`, replace the field declaration (around line 49):

```csharp
// OLD:
private readonly EnvironmentConfigStore _envConfigStore = new();

// NEW:
private readonly EnvironmentConfigStore _envConfigStore;
```

Update the constructor signature (line 108) to accept it:

```csharp
    public InteractiveSession(
        string? profileName,
        ProfileStore profileStore,
        EnvironmentConfigStore envConfigStore,
        IServiceProviderFactory? serviceProviderFactory = null,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        Func<Action<DeviceCodeInfo>?, PreAuthDialogResult>? beforeInteractiveAuth = null)
    {
        _profileName = profileName ?? string.Empty;
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _envConfigStore = envConfigStore ?? throw new ArgumentNullException(nameof(envConfigStore));
        // ... rest unchanged
```

**Step 3: Build and test**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo -v q`
Expected: Build succeeded

Run: `dotnet test --filter "Category=TuiUnit" --nologo -v q`
Expected: All pass

**Step 4: Commit**

```
refactor(tui): inject EnvironmentConfigStore into InteractiveSession via DI
```

---

## Phase 7: Update Test Infrastructure

### Task 12: Update MockServiceProviderFactory and related tests

**Files:**
- Modify: `tests/PPDS.Cli.Tests/Mocks/MockServiceProviderFactory.cs`
- Check/Modify: any tests that construct `InteractiveSession` directly

**Step 1: Update MockServiceProviderFactory**

In `MockServiceProviderFactory.cs`, the `CreateAsync` method builds a ServiceCollection (lines 74-82). Add auth services if not already covered by the `_configureServices` callback:

```csharp
var services = new ServiceCollection();
services.AddAuthServices();  // Add this line
services.AddSingleton<ISqlQueryService, FakeSqlQueryService>();
services.AddSingleton<IQueryHistoryService, FakeQueryHistoryService>();
services.AddSingleton<IExportService, FakeExportService>();
_configureServices?.Invoke(services);
return services.BuildServiceProvider();
```

Add using: `using PPDS.Auth.DependencyInjection;`

**Step 2: Find and fix any tests constructing InteractiveSession directly**

Search for `new InteractiveSession(` in test files. The constructor signature changed — now requires `EnvironmentConfigStore` as the third parameter. Update any test call sites to pass a `new EnvironmentConfigStore()` or a test double.

Run: Search `tests/` directory for `new InteractiveSession(`

**Step 3: Build and run all tests**

Run: `dotnet test --filter "Category!=Integration" --nologo -v q`
Expected: All pass

**Step 4: Commit**

```
test: update MockServiceProviderFactory and test infrastructure for auth DI registration
```

---

## Phase 8: Final Verification

### Task 13: Verify no remaining manual instantiation and run full test suite

**Step 1: Search for remaining manual store creation**

Search codebase for:
- `new ProfileStore()` — should only appear in `DefaultLoadProfilesAsync` fallback (DaemonConnectionPoolManager) and possibly PPDS.Auth internal code (ServiceClientFactory, ConnectionResolver)
- `new NativeCredentialStore()` — should have zero hits in PPDS.Cli and PPDS.Mcp
- `new EnvironmentConfigStore()` — should have zero hits anywhere

**Step 2: Run full build**

Run: `dotnet build --nologo -v q`
Expected: Clean build, zero errors, zero warnings from our changes

**Step 3: Run full test suite**

Run: `dotnet test --filter "Category!=Integration" --nologo -v q`
Expected: All pass across PPDS.Cli.Tests, PPDS.Dataverse.Tests, PPDS.Auth.Tests, PPDS.Mcp.Tests

**Step 4: Final commit if any adjustments needed**

---

## Task Parallelization Guide

```
Time →

Phase 1 (Tasks 1-3):    ████████████████  (sequential — each depends on previous)
Phase 2 (Task 4):        ████████          (depends on Phase 1)
Phase 3 (Tasks 5-6):         ████████████  (parallel — independent command groups)
Phase 4 (Tasks 7-8):         ████████████  (parallel with Phase 3 — different files)
Phase 5 (Tasks 9-10):        ████████████  (parallel with Phases 3-4 — different project)
Phase 6 (Task 11):                ████████  (depends on Phase 1 only)
Phase 7 (Task 12):                    ████  (after all migration phases)
Phase 8 (Task 13):                      ██  (last — full verification)
```

**Parallelizable batches:**
- Batch 1: Tasks 5+6 (CLI commands) vs Tasks 7+8 (RPC) vs Tasks 9+10 (MCP) vs Task 11 (TUI)
- These four batches touch completely different files and can run simultaneously

## Summary Table

| Task | Phase | Scope | Files Changed |
|------|-------|-------|---------------|
| 1 | Foundation | Add DI package to PPDS.Auth | 1 (csproj) |
| 2 | Foundation | Create AddAuthServices | 1 (new file) |
| 3 | Foundation | Update AddCliApplicationServices | 1 (ServiceRegistration.cs) |
| 4 | Factory | Migrate ProfileServiceFactory | 1 (ProfileServiceFactory.cs) |
| 5 | CLI | Migrate AuthCommandGroup | 1 (AuthCommandGroup.cs) |
| 6 | CLI | Migrate EnvCommandGroup | 1 (EnvCommandGroup.cs) |
| 7 | RPC | Migrate RpcMethodHandler + ServeCommand | 2 (RpcMethodHandler.cs, ServeCommand.cs) |
| 8 | RPC | Migrate DaemonConnectionPoolManager | 1 (DaemonConnectionPoolManager.cs) |
| 9 | MCP | Migrate McpToolContext + Program.cs | 2 (McpToolContext.cs, Program.cs) |
| 10 | MCP | Migrate McpConnectionPoolManager | 1 (McpConnectionPoolManager.cs) |
| 11 | TUI | Migrate InteractiveSession + PpdsApplication | 2 (InteractiveSession.cs, PpdsApplication.cs) |
| 12 | Tests | Update test infrastructure | 1-3 (MockServiceProviderFactory.cs + test files) |
| 13 | Verify | Full build + test + grep audit | 0 (verification only) |
