# Environment Configuration Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add user-configurable environment types and colors, replacing hardcoded URL-based heuristics with a persisted config that works across CLI, TUI, and VS Code (via RPC).

**Architecture:** New `EnvironmentConfigStore` in `PPDS.Auth` persists environment configs to `~/.ppds/environments.json`. A new `IEnvironmentConfigService` Application Service in `PPDS.Cli` provides resolution logic (user config > discovery type > URL heuristics). The TUI's `TuiThemeService` delegates to this service instead of doing its own detection. CLI gets a new `ppds env config` subcommand.

**Tech Stack:** C# (.NET 8+), System.Text.Json, System.CommandLine, Terminal.Gui 1.19+, xUnit

---

### Task 1: EnvironmentColor Enum (PPDS.Auth)

**Files:**
- Create: `src/PPDS.Auth/Profiles/EnvironmentColor.cs`
- Test: `tests/PPDS.Cli.Tests/Services/Environment/EnvironmentConfigStoreTests.cs`

**Step 1: Create the enum**

```csharp
// src/PPDS.Auth/Profiles/EnvironmentColor.cs
namespace PPDS.Auth.Profiles;

/// <summary>
/// Named colors for environment theming.
/// Maps to 16-color terminal palette (works in TUI and VS Code).
/// </summary>
public enum EnvironmentColor
{
    Red,
    Green,
    Yellow,
    Cyan,
    Blue,
    Gray,
    Brown,
    White,
    BrightRed,
    BrightGreen,
    BrightYellow,
    BrightCyan,
    BrightBlue
}
```

**Step 2: Commit**

```
git add src/PPDS.Auth/Profiles/EnvironmentColor.cs
git commit -m "feat(auth): add EnvironmentColor enum for configurable environment theming"
```

---

### Task 2: EnvironmentConfig Model (PPDS.Auth)

**Files:**
- Create: `src/PPDS.Auth/Profiles/EnvironmentConfig.cs`

**Step 1: Create the model**

```csharp
// src/PPDS.Auth/Profiles/EnvironmentConfig.cs
using System.Text.Json.Serialization;

namespace PPDS.Auth.Profiles;

/// <summary>
/// User configuration for a specific Dataverse environment.
/// Stores label, type classification, and color override.
/// </summary>
public sealed class EnvironmentConfig
{
    /// <summary>
    /// Normalized environment URL (lowercase, trailing slash). This is the key.
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Short label for status bar and tab display (e.g., "Contoso Dev").
    /// Null means use the environment's DisplayName.
    /// </summary>
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>
    /// Environment type classification (e.g., "Production", "Sandbox", "UAT", "Gold").
    /// Free-text string — built-in types have default colors, custom types use typeDefaults.
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// Explicit color override for this specific environment.
    /// Takes priority over type-based color. Null means use type default.
    /// </summary>
    [JsonPropertyName("color")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public EnvironmentColor? Color { get; set; }

    /// <summary>
    /// Normalizes a URL for use as a lookup key (lowercase, ensures trailing slash).
    /// </summary>
    public static string NormalizeUrl(string url)
    {
        var normalized = url.Trim().ToLowerInvariant();
        if (!normalized.EndsWith('/'))
            normalized += '/';
        return normalized;
    }
}
```

**Step 2: Commit**

```
git add src/PPDS.Auth/Profiles/EnvironmentConfig.cs
git commit -m "feat(auth): add EnvironmentConfig model with label, type, and color"
```

---

### Task 3: EnvironmentConfigCollection Model (PPDS.Auth)

**Files:**
- Create: `src/PPDS.Auth/Profiles/EnvironmentConfigCollection.cs`

**Step 1: Create the collection**

This is the root object serialized to `environments.json`.

```csharp
// src/PPDS.Auth/Profiles/EnvironmentConfigCollection.cs
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PPDS.Auth.Profiles;

/// <summary>
/// Root object for environments.json — holds per-environment configs and custom type defaults.
/// </summary>
public sealed class EnvironmentConfigCollection
{
    /// <summary>
    /// Schema version for forward compatibility.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>
    /// Custom type definitions with default colors.
    /// Key is the type name (e.g., "UAT", "Gold"), value is the default color.
    /// Built-in types (Production, Sandbox, Development, Test, Trial) do not need entries here.
    /// </summary>
    [JsonPropertyName("typeDefaults")]
    public Dictionary<string, EnvironmentColor> TypeDefaults { get; set; } = new();

    /// <summary>
    /// Per-environment configurations keyed by normalized URL.
    /// </summary>
    [JsonPropertyName("environments")]
    public List<EnvironmentConfig> Environments { get; set; } = new();
}
```

**Step 2: Commit**

```
git add src/PPDS.Auth/Profiles/EnvironmentConfigCollection.cs
git commit -m "feat(auth): add EnvironmentConfigCollection with typeDefaults and environments"
```

---

### Task 4: EnvironmentConfigStore (PPDS.Auth)

**Files:**
- Create: `src/PPDS.Auth/Profiles/EnvironmentConfigStore.cs`
- Modify: `src/PPDS.Auth/Profiles/ProfilePaths.cs:24` — add `EnvironmentsFileName` constant

**Step 1: Add file path constant to ProfilePaths**

Add after line 24 (`ProfilesFileName`):

```csharp
/// <summary>
/// Environment configuration file name.
/// </summary>
public const string EnvironmentsFileName = "environments.json";

/// <summary>
/// Gets the full path to the environment configuration file.
/// </summary>
public static string EnvironmentsFile => Path.Combine(DataDirectory, EnvironmentsFileName);
```

**Step 2: Create EnvironmentConfigStore**

Follow the same pattern as `ProfileStore` (semaphore, caching, async/sync, IDisposable):

```csharp
// src/PPDS.Auth/Profiles/EnvironmentConfigStore.cs
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Auth.Profiles;

/// <summary>
/// Manages persistent storage of environment configurations.
/// </summary>
public sealed class EnvironmentConfigStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private EnvironmentConfigCollection? _cached;
    private bool _disposed;

    public EnvironmentConfigStore() : this(ProfilePaths.EnvironmentsFile) { }

    public EnvironmentConfigStore(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    public string FilePath => _filePath;

    public async Task<EnvironmentConfigCollection> LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached != null) return _cached;

            if (!File.Exists(_filePath))
            {
                _cached = new EnvironmentConfigCollection();
                return _cached;
            }

            var json = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
            _cached = JsonSerializer.Deserialize<EnvironmentConfigCollection>(json, JsonOptions)
                ?? new EnvironmentConfigCollection();
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(EnvironmentConfigCollection collection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collection);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ProfilePaths.EnsureDirectoryExists();
            collection.Version = 1;
            var json = JsonSerializer.Serialize(collection, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json, ct).ConfigureAwait(false);
            _cached = collection;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Gets the config for a specific environment URL, or null if not configured.
    /// </summary>
    public async Task<EnvironmentConfig?> GetConfigAsync(string url, CancellationToken ct = default)
    {
        var collection = await LoadAsync(ct).ConfigureAwait(false);
        var normalized = EnvironmentConfig.NormalizeUrl(url);
        return collection.Environments.FirstOrDefault(
            e => EnvironmentConfig.NormalizeUrl(e.Url) == normalized);
    }

    /// <summary>
    /// Saves or updates config for a specific environment. Merges non-null fields.
    /// </summary>
    public async Task<EnvironmentConfig> SaveConfigAsync(
        string url, string? label = null, string? type = null, EnvironmentColor? color = null,
        CancellationToken ct = default)
    {
        var collection = await LoadAsync(ct).ConfigureAwait(false);
        var normalized = EnvironmentConfig.NormalizeUrl(url);

        var existing = collection.Environments.FirstOrDefault(
            e => EnvironmentConfig.NormalizeUrl(e.Url) == normalized);

        if (existing != null)
        {
            if (label != null) existing.Label = label;
            if (type != null) existing.Type = type;
            if (color != null) existing.Color = color;
        }
        else
        {
            existing = new EnvironmentConfig
            {
                Url = normalized,
                Label = label,
                Type = type,
                Color = color
            };
            collection.Environments.Add(existing);
        }

        await SaveAsync(collection, ct).ConfigureAwait(false);
        return existing;
    }

    /// <summary>
    /// Removes config for a specific environment URL.
    /// </summary>
    public async Task<bool> RemoveConfigAsync(string url, CancellationToken ct = default)
    {
        var collection = await LoadAsync(ct).ConfigureAwait(false);
        var normalized = EnvironmentConfig.NormalizeUrl(url);
        var removed = collection.Environments.RemoveAll(
            e => EnvironmentConfig.NormalizeUrl(e.Url) == normalized);

        if (removed > 0)
        {
            await SaveAsync(collection, ct).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    public void ClearCache()
    {
        _lock.Wait();
        try { _cached = null; }
        finally { _lock.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _lock.Dispose();
        _disposed = true;
    }
}
```

**Step 3: Commit**

```
git add src/PPDS.Auth/Profiles/ProfilePaths.cs src/PPDS.Auth/Profiles/EnvironmentConfigStore.cs
git commit -m "feat(auth): add EnvironmentConfigStore for persistent environment configuration"
```

---

### Task 5: EnvironmentConfigStore Unit Tests

**Files:**
- Create: `tests/PPDS.Cli.Tests/Services/Environment/EnvironmentConfigStoreTests.cs`

**Step 1: Write the tests**

```csharp
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Cli.Tests.Services.Environment;

[Trait("Category", "TuiUnit")]
public class EnvironmentConfigStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly EnvironmentConfigStore _store;

    public EnvironmentConfigStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ppds-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new EnvironmentConfigStore(Path.Combine(_tempDir, "environments.json"));
    }

    [Fact]
    public async Task LoadAsync_NoFile_ReturnsEmptyCollection()
    {
        var collection = await _store.LoadAsync();
        Assert.Empty(collection.Environments);
        Assert.Empty(collection.TypeDefaults);
        Assert.Equal(1, collection.Version);
    }

    [Fact]
    public async Task SaveConfigAsync_NewEnvironment_CreatesEntry()
    {
        var config = await _store.SaveConfigAsync(
            "https://org.crm.dynamics.com",
            label: "Contoso Prod",
            type: "Production",
            color: EnvironmentColor.Red);

        Assert.Equal("contoso prod", config.Label?.ToLowerInvariant());
        Assert.Equal("Production", config.Type);
        Assert.Equal(EnvironmentColor.Red, config.Color);
    }

    [Fact]
    public async Task SaveConfigAsync_ExistingEnvironment_MergesFields()
    {
        await _store.SaveConfigAsync("https://org.crm.dynamics.com", label: "Original");
        var updated = await _store.SaveConfigAsync("https://org.crm.dynamics.com", type: "Sandbox");

        Assert.Equal("Original", updated.Label);
        Assert.Equal("Sandbox", updated.Type);
    }

    [Fact]
    public async Task SaveConfigAsync_NormalizesUrl()
    {
        await _store.SaveConfigAsync("https://ORG.CRM.DYNAMICS.COM", label: "Test");
        var config = await _store.GetConfigAsync("https://org.crm.dynamics.com/");

        Assert.NotNull(config);
        Assert.Equal("Test", config!.Label);
    }

    [Fact]
    public async Task GetConfigAsync_NotFound_ReturnsNull()
    {
        var config = await _store.GetConfigAsync("https://nonexistent.crm.dynamics.com");
        Assert.Null(config);
    }

    [Fact]
    public async Task RemoveConfigAsync_Existing_ReturnsTrue()
    {
        await _store.SaveConfigAsync("https://org.crm.dynamics.com", label: "Test");
        var removed = await _store.RemoveConfigAsync("https://org.crm.dynamics.com");

        Assert.True(removed);

        var config = await _store.GetConfigAsync("https://org.crm.dynamics.com");
        Assert.Null(config);
    }

    [Fact]
    public async Task RemoveConfigAsync_NotFound_ReturnsFalse()
    {
        var removed = await _store.RemoveConfigAsync("https://nonexistent.crm.dynamics.com");
        Assert.False(removed);
    }

    [Fact]
    public async Task RoundTrip_PersistsToDisk()
    {
        await _store.SaveConfigAsync("https://org.crm.dynamics.com",
            label: "Prod", type: "Production", color: EnvironmentColor.Red);

        // Create new store pointing to same file to verify persistence
        using var store2 = new EnvironmentConfigStore(Path.Combine(_tempDir, "environments.json"));
        var config = await store2.GetConfigAsync("https://org.crm.dynamics.com");

        Assert.NotNull(config);
        Assert.Equal("Prod", config!.Label);
        Assert.Equal("Production", config.Type);
        Assert.Equal(EnvironmentColor.Red, config.Color);
    }

    [Fact]
    public async Task TypeDefaults_RoundTrip()
    {
        var collection = await _store.LoadAsync();
        collection.TypeDefaults["UAT"] = EnvironmentColor.Brown;
        collection.TypeDefaults["Gold"] = EnvironmentColor.BrightYellow;
        await _store.SaveAsync(collection);

        _store.ClearCache();
        var reloaded = await _store.LoadAsync();

        Assert.Equal(EnvironmentColor.Brown, reloaded.TypeDefaults["UAT"]);
        Assert.Equal(EnvironmentColor.BrightYellow, reloaded.TypeDefaults["Gold"]);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* best effort */ }
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~EnvironmentConfigStoreTests" --no-build`
Expected: Build failure (store doesn't exist yet) or test failures

**Step 3: Run tests after Task 4 implementation**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~EnvironmentConfigStoreTests"`
Expected: All 8 tests PASS

**Step 4: Commit**

```
git add tests/PPDS.Cli.Tests/Services/Environment/EnvironmentConfigStoreTests.cs
git commit -m "test(auth): add EnvironmentConfigStore unit tests"
```

---

### Task 6: IEnvironmentConfigService Application Service

**Files:**
- Create: `src/PPDS.Cli/Services/Environment/IEnvironmentConfigService.cs`

**Step 1: Create the interface**

```csharp
// src/PPDS.Cli/Services/Environment/IEnvironmentConfigService.cs
using PPDS.Auth.Profiles;

namespace PPDS.Cli.Services.Environment;

/// <summary>
/// Application service for managing environment configuration (labels, types, colors).
/// Shared across CLI, TUI, and RPC interfaces.
/// </summary>
public interface IEnvironmentConfigService
{
    /// <summary>
    /// Gets the configuration for a specific environment, or null if not configured.
    /// </summary>
    Task<EnvironmentConfig?> GetConfigAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Gets all configured environments.
    /// </summary>
    Task<IReadOnlyList<EnvironmentConfig>> GetAllConfigsAsync(CancellationToken ct = default);

    /// <summary>
    /// Saves or merges configuration for a specific environment.
    /// Only non-null parameters are updated (existing values preserved).
    /// </summary>
    Task<EnvironmentConfig> SaveConfigAsync(
        string url,
        string? label = null,
        string? type = null,
        EnvironmentColor? color = null,
        CancellationToken ct = default);

    /// <summary>
    /// Removes configuration for a specific environment.
    /// </summary>
    Task<bool> RemoveConfigAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Adds or updates a custom type definition with a default color.
    /// </summary>
    Task SaveTypeDefaultAsync(string typeName, EnvironmentColor color, CancellationToken ct = default);

    /// <summary>
    /// Removes a custom type definition.
    /// </summary>
    Task<bool> RemoveTypeDefaultAsync(string typeName, CancellationToken ct = default);

    /// <summary>
    /// Gets all type definitions (built-in + custom) with their default colors.
    /// </summary>
    Task<IReadOnlyDictionary<string, EnvironmentColor>> GetAllTypeDefaultsAsync(CancellationToken ct = default);

    /// <summary>
    /// Resolves the effective color for an environment.
    /// Priority: per-env color > type default color (custom then built-in) > Gray fallback.
    /// </summary>
    Task<EnvironmentColor> ResolveColorAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Resolves the effective environment type string for an environment.
    /// Priority: user config type > discovery API type > URL heuristics > null.
    /// </summary>
    Task<string?> ResolveTypeAsync(string url, string? discoveredType = null, CancellationToken ct = default);

    /// <summary>
    /// Resolves the display label for an environment.
    /// Priority: user config label > environment DisplayName from profile.
    /// </summary>
    Task<string?> ResolveLabelAsync(string url, CancellationToken ct = default);
}
```

**Step 2: Commit**

```
git add src/PPDS.Cli/Services/Environment/IEnvironmentConfigService.cs
git commit -m "feat(cli): add IEnvironmentConfigService interface"
```

---

### Task 7: EnvironmentConfigService Implementation

**Files:**
- Create: `src/PPDS.Cli/Services/Environment/EnvironmentConfigService.cs`

**Step 1: Create the implementation**

```csharp
// src/PPDS.Cli/Services/Environment/EnvironmentConfigService.cs
using System.Text.RegularExpressions;
using PPDS.Auth.Profiles;

namespace PPDS.Cli.Services.Environment;

/// <summary>
/// Default implementation of <see cref="IEnvironmentConfigService"/>.
/// </summary>
public sealed partial class EnvironmentConfigService : IEnvironmentConfigService
{
    /// <summary>
    /// Built-in type defaults. Custom user types in environments.json override these.
    /// </summary>
    private static readonly Dictionary<string, EnvironmentColor> BuiltInTypeDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Production"] = EnvironmentColor.Red,
        ["Sandbox"] = EnvironmentColor.Brown,
        ["Development"] = EnvironmentColor.Green,
        ["Test"] = EnvironmentColor.Yellow,
        ["Trial"] = EnvironmentColor.Cyan,
    };

    private readonly EnvironmentConfigStore _store;

    public EnvironmentConfigService(EnvironmentConfigStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<EnvironmentConfig?> GetConfigAsync(string url, CancellationToken ct = default)
        => await _store.GetConfigAsync(url, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<EnvironmentConfig>> GetAllConfigsAsync(CancellationToken ct = default)
    {
        var collection = await _store.LoadAsync(ct).ConfigureAwait(false);
        return collection.Environments.AsReadOnly();
    }

    public async Task<EnvironmentConfig> SaveConfigAsync(
        string url, string? label = null, string? type = null, EnvironmentColor? color = null,
        CancellationToken ct = default)
        => await _store.SaveConfigAsync(url, label, type, color, ct).ConfigureAwait(false);

    public async Task<bool> RemoveConfigAsync(string url, CancellationToken ct = default)
        => await _store.RemoveConfigAsync(url, ct).ConfigureAwait(false);

    public async Task SaveTypeDefaultAsync(string typeName, EnvironmentColor color, CancellationToken ct = default)
    {
        var collection = await _store.LoadAsync(ct).ConfigureAwait(false);
        collection.TypeDefaults[typeName] = color;
        await _store.SaveAsync(collection, ct).ConfigureAwait(false);
    }

    public async Task<bool> RemoveTypeDefaultAsync(string typeName, CancellationToken ct = default)
    {
        var collection = await _store.LoadAsync(ct).ConfigureAwait(false);
        if (collection.TypeDefaults.Remove(typeName))
        {
            await _store.SaveAsync(collection, ct).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    public async Task<IReadOnlyDictionary<string, EnvironmentColor>> GetAllTypeDefaultsAsync(CancellationToken ct = default)
    {
        var collection = await _store.LoadAsync(ct).ConfigureAwait(false);

        // Merge built-in with custom (custom wins on conflict)
        var merged = new Dictionary<string, EnvironmentColor>(BuiltInTypeDefaults, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in collection.TypeDefaults)
        {
            merged[key] = value;
        }
        return merged;
    }

    public async Task<EnvironmentColor> ResolveColorAsync(string url, CancellationToken ct = default)
    {
        var config = await _store.GetConfigAsync(url, ct).ConfigureAwait(false);

        // Priority 1: per-environment explicit color
        if (config?.Color != null)
            return config.Color.Value;

        // Priority 2: type-based color (resolve type first)
        var type = config?.Type ?? DetectTypeFromUrl(url);
        if (type != null)
        {
            var allDefaults = await GetAllTypeDefaultsAsync(ct).ConfigureAwait(false);
            if (allDefaults.TryGetValue(type, out var typeColor))
                return typeColor;
        }

        // Priority 3: fallback
        return EnvironmentColor.Gray;
    }

    public async Task<string?> ResolveTypeAsync(string url, string? discoveredType = null, CancellationToken ct = default)
    {
        // Priority 1: user config type
        var config = await _store.GetConfigAsync(url, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(config?.Type))
            return config!.Type;

        // Priority 2: discovery API type
        if (!string.IsNullOrWhiteSpace(discoveredType))
            return discoveredType;

        // Priority 3: URL heuristics
        return DetectTypeFromUrl(url);
    }

    public async Task<string?> ResolveLabelAsync(string url, CancellationToken ct = default)
    {
        var config = await _store.GetConfigAsync(url, ct).ConfigureAwait(false);
        return config?.Label;
    }

    #region URL Heuristics (fallback only)

    [GeneratedRegex(@"\.crm\d+\.dynamics\.com", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SandboxRegex();

    private static readonly string[] DevKeywords = ["dev", "develop", "development", "test", "qa", "uat"];
    private static readonly string[] TrialKeywords = ["trial", "demo", "preview"];

    /// <summary>
    /// Last-resort URL-based detection. Only used when no config or discovery type exists.
    /// </summary>
    internal static string? DetectTypeFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var lower = url.ToLowerInvariant();

        if (DevKeywords.Any(k => lower.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return "Development";
        if (TrialKeywords.Any(k => lower.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return "Trial";
        if (SandboxRegex().IsMatch(lower))
            return "Sandbox";
        if (lower.Contains(".crm.dynamics.com"))
            return "Production";

        return null;
    }

    #endregion
}
```

**Step 2: Commit**

```
git add src/PPDS.Cli/Services/Environment/EnvironmentConfigService.cs
git commit -m "feat(cli): implement EnvironmentConfigService with type/color resolution"
```

---

### Task 8: EnvironmentConfigService Unit Tests

**Files:**
- Create: `tests/PPDS.Cli.Tests/Services/Environment/EnvironmentConfigServiceTests.cs`

**Step 1: Write the tests**

```csharp
using PPDS.Auth.Profiles;
using PPDS.Cli.Services.Environment;
using Xunit;

namespace PPDS.Cli.Tests.Services.Environment;

[Trait("Category", "TuiUnit")]
public class EnvironmentConfigServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly EnvironmentConfigStore _store;
    private readonly EnvironmentConfigService _service;

    public EnvironmentConfigServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ppds-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new EnvironmentConfigStore(Path.Combine(_tempDir, "environments.json"));
        _service = new EnvironmentConfigService(_store);
    }

    [Fact]
    public async Task ResolveColorAsync_ExplicitColor_WinsOverType()
    {
        await _service.SaveConfigAsync("https://org.crm.dynamics.com",
            type: "Production", color: EnvironmentColor.Blue);

        var color = await _service.ResolveColorAsync("https://org.crm.dynamics.com");
        Assert.Equal(EnvironmentColor.Blue, color);
    }

    [Fact]
    public async Task ResolveColorAsync_TypeDefault_UsedWhenNoExplicitColor()
    {
        await _service.SaveConfigAsync("https://org.crm.dynamics.com", type: "Production");

        var color = await _service.ResolveColorAsync("https://org.crm.dynamics.com");
        Assert.Equal(EnvironmentColor.Red, color);
    }

    [Fact]
    public async Task ResolveColorAsync_CustomType_UsesTypeDefaults()
    {
        await _service.SaveTypeDefaultAsync("Gold", EnvironmentColor.BrightYellow);
        await _service.SaveConfigAsync("https://org.crm.dynamics.com", type: "Gold");

        var color = await _service.ResolveColorAsync("https://org.crm.dynamics.com");
        Assert.Equal(EnvironmentColor.BrightYellow, color);
    }

    [Fact]
    public async Task ResolveColorAsync_NoConfig_FallsBackToUrlHeuristics()
    {
        // .crm.dynamics.com (no number) => Production => Red
        var color = await _service.ResolveColorAsync("https://org.crm.dynamics.com");
        Assert.Equal(EnvironmentColor.Red, color);
    }

    [Fact]
    public async Task ResolveColorAsync_UnknownUrl_ReturnsGray()
    {
        var color = await _service.ResolveColorAsync("https://some-random-url.example.com");
        Assert.Equal(EnvironmentColor.Gray, color);
    }

    [Fact]
    public async Task ResolveTypeAsync_UserConfig_WinsOverDiscovery()
    {
        await _service.SaveConfigAsync("https://org.crm.dynamics.com", type: "UAT");

        var type = await _service.ResolveTypeAsync("https://org.crm.dynamics.com", discoveredType: "Sandbox");
        Assert.Equal("UAT", type);
    }

    [Fact]
    public async Task ResolveTypeAsync_Discovery_WinsOverUrlHeuristics()
    {
        var type = await _service.ResolveTypeAsync("https://org.crm.dynamics.com", discoveredType: "Sandbox");
        Assert.Equal("Sandbox", type);
    }

    [Fact]
    public async Task ResolveTypeAsync_NoConfigNoDiscovery_FallsBackToUrl()
    {
        var type = await _service.ResolveTypeAsync("https://org-dev.crm.dynamics.com");
        Assert.Equal("Development", type);
    }

    [Fact]
    public async Task GetAllTypeDefaultsAsync_MergesBuiltInAndCustom()
    {
        await _service.SaveTypeDefaultAsync("Gold", EnvironmentColor.BrightYellow);

        var defaults = await _service.GetAllTypeDefaultsAsync();

        Assert.True(defaults.ContainsKey("Production"), "Should have built-in Production");
        Assert.True(defaults.ContainsKey("Gold"), "Should have custom Gold");
        Assert.Equal(EnvironmentColor.Red, defaults["Production"]);
        Assert.Equal(EnvironmentColor.BrightYellow, defaults["Gold"]);
    }

    [Fact]
    public async Task GetAllTypeDefaultsAsync_CustomOverridesBuiltIn()
    {
        // Override built-in "Production" color
        await _service.SaveTypeDefaultAsync("Production", EnvironmentColor.BrightRed);

        var defaults = await _service.GetAllTypeDefaultsAsync();
        Assert.Equal(EnvironmentColor.BrightRed, defaults["Production"]);
    }

    [Fact]
    public async Task ResolveLabelAsync_ReturnsConfiguredLabel()
    {
        await _service.SaveConfigAsync("https://org.crm.dynamics.com", label: "Contoso Prod");

        var label = await _service.ResolveLabelAsync("https://org.crm.dynamics.com");
        Assert.Equal("Contoso Prod", label);
    }

    [Fact]
    public async Task ResolveLabelAsync_NoConfig_ReturnsNull()
    {
        var label = await _service.ResolveLabelAsync("https://org.crm.dynamics.com");
        Assert.Null(label);
    }

    [Fact]
    public void DetectTypeFromUrl_ProductionUrl()
    {
        Assert.Equal("Production", EnvironmentConfigService.DetectTypeFromUrl("https://org.crm.dynamics.com"));
    }

    [Fact]
    public void DetectTypeFromUrl_SandboxUrl()
    {
        Assert.Equal("Sandbox", EnvironmentConfigService.DetectTypeFromUrl("https://org.crm9.dynamics.com"));
    }

    [Fact]
    public void DetectTypeFromUrl_DevKeyword()
    {
        Assert.Equal("Development", EnvironmentConfigService.DetectTypeFromUrl("https://org-dev.crm.dynamics.com"));
    }

    [Fact]
    public void DetectTypeFromUrl_UnknownUrl()
    {
        Assert.Null(EnvironmentConfigService.DetectTypeFromUrl("https://some-random.example.com"));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
        catch { /* best effort */ }
    }
}
```

**Step 2: Run tests**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~EnvironmentConfigServiceTests"`
Expected: All 14 tests PASS

**Step 3: Commit**

```
git add tests/PPDS.Cli.Tests/Services/Environment/EnvironmentConfigServiceTests.cs
git commit -m "test(cli): add EnvironmentConfigService unit tests for resolution priority"
```

---

### Task 9: Wire TuiThemeService to Use EnvironmentConfigService

**Files:**
- Modify: `src/PPDS.Cli/Tui/Infrastructure/ITuiThemeService.cs`
- Modify: `src/PPDS.Cli/Tui/Infrastructure/TuiThemeService.cs`
- Modify: `src/PPDS.Cli/Tui/Infrastructure/TuiColorPalette.cs:303-310` — add `EnvironmentColor` mapping

**Step 1: Add EnvironmentColor-to-ColorScheme mapping in TuiColorPalette**

Add a new method after `GetStatusBarScheme(EnvironmentType)`:

```csharp
/// <summary>
/// Gets the status bar color scheme for a user-configured environment color.
/// </summary>
public static ColorScheme GetStatusBarScheme(EnvironmentColor envColor) => envColor switch
{
    EnvironmentColor.Red => StatusBar_Production,
    EnvironmentColor.Brown => StatusBar_Sandbox,
    EnvironmentColor.Green => StatusBar_Development,
    EnvironmentColor.Cyan => StatusBar_Trial,
    EnvironmentColor.Yellow => new ColorScheme
    {
        Normal = MakeAttr(Color.Black, Color.BrightYellow),
        Focus = MakeAttr(Color.Black, Color.BrightYellow),
        HotNormal = MakeAttr(Color.Red, Color.BrightYellow),
        HotFocus = MakeAttr(Color.Red, Color.BrightYellow),
        Disabled = MakeAttr(Color.DarkGray, Color.BrightYellow)
    },
    EnvironmentColor.Blue => new ColorScheme
    {
        Normal = MakeAttr(Color.Black, Color.Blue),
        Focus = MakeAttr(Color.Black, Color.BrightBlue),
        HotNormal = MakeAttr(Color.Black, Color.Blue),
        HotFocus = MakeAttr(Color.Black, Color.BrightBlue),
        Disabled = MakeAttr(Color.Black, Color.Blue)
    },
    EnvironmentColor.Gray => StatusBar_Default,
    EnvironmentColor.BrightRed => new ColorScheme
    {
        Normal = MakeAttr(Color.White, Color.BrightRed),
        Focus = MakeAttr(Color.White, Color.BrightRed),
        HotNormal = MakeAttr(Color.BrightYellow, Color.BrightRed),
        HotFocus = MakeAttr(Color.BrightYellow, Color.BrightRed),
        Disabled = MakeAttr(Color.Gray, Color.BrightRed)
    },
    EnvironmentColor.BrightGreen => new ColorScheme
    {
        Normal = MakeAttr(Color.Black, Color.BrightGreen),
        Focus = MakeAttr(Color.Black, Color.BrightGreen),
        HotNormal = MakeAttr(Color.Black, Color.BrightGreen),
        HotFocus = MakeAttr(Color.Black, Color.BrightGreen),
        Disabled = MakeAttr(Color.DarkGray, Color.BrightGreen)
    },
    EnvironmentColor.BrightYellow => new ColorScheme
    {
        Normal = MakeAttr(Color.Black, Color.BrightYellow),
        Focus = MakeAttr(Color.Black, Color.BrightYellow),
        HotNormal = MakeAttr(Color.Red, Color.BrightYellow),
        HotFocus = MakeAttr(Color.Red, Color.BrightYellow),
        Disabled = MakeAttr(Color.DarkGray, Color.BrightYellow)
    },
    EnvironmentColor.BrightCyan => new ColorScheme
    {
        Normal = MakeAttr(Color.Black, Color.BrightCyan),
        Focus = MakeAttr(Color.Black, Color.BrightCyan),
        HotNormal = MakeAttr(Color.Black, Color.BrightCyan),
        HotFocus = MakeAttr(Color.Black, Color.BrightCyan),
        Disabled = MakeAttr(Color.Black, Color.BrightCyan)
    },
    EnvironmentColor.BrightBlue => new ColorScheme
    {
        Normal = MakeAttr(Color.Black, Color.BrightBlue),
        Focus = MakeAttr(Color.Black, Color.BrightBlue),
        HotNormal = MakeAttr(Color.Black, Color.BrightBlue),
        HotFocus = MakeAttr(Color.Black, Color.BrightBlue),
        Disabled = MakeAttr(Color.Black, Color.BrightBlue)
    },
    EnvironmentColor.White => new ColorScheme
    {
        Normal = MakeAttr(Color.Black, Color.White),
        Focus = MakeAttr(Color.Black, Color.White),
        HotNormal = MakeAttr(Color.Blue, Color.White),
        HotFocus = MakeAttr(Color.Blue, Color.White),
        Disabled = MakeAttr(Color.DarkGray, Color.White)
    },
    _ => StatusBar_Default
};

/// <summary>
/// Maps EnvironmentColor to Terminal.Gui foreground Color (for tab tinting).
/// </summary>
public static Color GetForegroundColor(EnvironmentColor envColor) => envColor switch
{
    EnvironmentColor.Red => Color.Red,
    EnvironmentColor.Green => Color.Green,
    EnvironmentColor.Yellow => Color.BrightYellow,
    EnvironmentColor.Cyan => Color.Cyan,
    EnvironmentColor.Blue => Color.Blue,
    EnvironmentColor.Gray => Color.Gray,
    EnvironmentColor.Brown => Color.Brown,
    EnvironmentColor.BrightRed => Color.BrightRed,
    EnvironmentColor.BrightGreen => Color.BrightGreen,
    EnvironmentColor.BrightYellow => Color.BrightYellow,
    EnvironmentColor.BrightCyan => Color.BrightCyan,
    EnvironmentColor.BrightBlue => Color.BrightBlue,
    EnvironmentColor.White => Color.White,
    _ => Color.Gray
};
```

**Step 2: Update ITuiThemeService — add config-aware methods**

Add to the interface:

```csharp
/// <summary>
/// Gets the status bar color scheme using the environment config service.
/// Falls back to URL-based detection if no config exists.
/// </summary>
ColorScheme GetStatusBarSchemeForUrl(string? environmentUrl);

/// <summary>
/// Gets the environment label using the config service.
/// Falls back to URL-based detection if no config exists.
/// </summary>
string GetEnvironmentLabelForUrl(string? environmentUrl);

/// <summary>
/// Gets the resolved environment color for tab tinting.
/// </summary>
EnvironmentColor GetResolvedColor(string? environmentUrl);
```

**Step 3: Update TuiThemeService — inject IEnvironmentConfigService**

The `TuiThemeService` constructor should accept an optional `IEnvironmentConfigService`. When present, the new methods use it. The old `DetectEnvironmentType` method stays for backward compatibility but is no longer the primary path.

```csharp
public sealed partial class TuiThemeService : ITuiThemeService
{
    private readonly IEnvironmentConfigService? _configService;

    public TuiThemeService(IEnvironmentConfigService? configService = null)
    {
        _configService = configService;
    }

    // ... existing methods stay ...

    public ColorScheme GetStatusBarSchemeForUrl(string? environmentUrl)
    {
        if (string.IsNullOrWhiteSpace(environmentUrl))
            return TuiColorPalette.StatusBar_Default;

        if (_configService != null)
        {
            // Synchronous wrapper — theme service is called from UI thread
            var color = _configService.ResolveColorAsync(environmentUrl).GetAwaiter().GetResult();
            return TuiColorPalette.GetStatusBarScheme(color);
        }

        // Fallback to old detection
        var envType = DetectEnvironmentType(environmentUrl);
        return TuiColorPalette.GetStatusBarScheme(envType);
    }

    public string GetEnvironmentLabelForUrl(string? environmentUrl)
    {
        if (string.IsNullOrWhiteSpace(environmentUrl))
            return "";

        if (_configService != null)
        {
            var type = _configService.ResolveTypeAsync(environmentUrl).GetAwaiter().GetResult();
            return type?.ToUpperInvariant() switch
            {
                "PRODUCTION" => "PROD",
                "DEVELOPMENT" => "DEV",
                var t when t != null && t.Length <= 8 => t,
                var t when t != null => t[..8],
                _ => ""
            };
        }

        return GetEnvironmentLabel(DetectEnvironmentType(environmentUrl));
    }

    public EnvironmentColor GetResolvedColor(string? environmentUrl)
    {
        if (string.IsNullOrWhiteSpace(environmentUrl))
            return EnvironmentColor.Gray;

        if (_configService != null)
            return _configService.ResolveColorAsync(environmentUrl).GetAwaiter().GetResult();

        // Fallback: map old EnvironmentType to EnvironmentColor
        var envType = DetectEnvironmentType(environmentUrl);
        return envType switch
        {
            EnvironmentType.Production => EnvironmentColor.Red,
            EnvironmentType.Sandbox => EnvironmentColor.Brown,
            EnvironmentType.Development => EnvironmentColor.Green,
            EnvironmentType.Trial => EnvironmentColor.Cyan,
            _ => EnvironmentColor.Gray
        };
    }
}
```

**Step 4: Commit**

```
git add src/PPDS.Cli/Tui/Infrastructure/ITuiThemeService.cs src/PPDS.Cli/Tui/Infrastructure/TuiThemeService.cs src/PPDS.Cli/Tui/Infrastructure/TuiColorPalette.cs
git commit -m "feat(tui): wire TuiThemeService to EnvironmentConfigService for user-configurable colors"
```

---

### Task 10: Update TUI Consumers to Use Config-Aware Methods

**Files:**
- Modify: `src/PPDS.Cli/Tui/Views/TuiStatusBar.cs:111,188-189` — use `GetStatusBarSchemeForUrl` and `GetEnvironmentLabelForUrl`
- Modify: `src/PPDS.Cli/Tui/Infrastructure/TabManager.cs:41` — use `GetResolvedColor` instead of `DetectEnvironmentType`
- Modify: `src/PPDS.Cli/Tui/Views/TabBar.cs` — use `GetForegroundColor(EnvironmentColor)` for tab tinting

**Step 1: Update TuiStatusBar.Redraw and UpdateDisplay**

In `Redraw()` (line 111), change:
```csharp
// Before:
var envType = _themeService.DetectEnvironmentType(_session.CurrentEnvironmentUrl);
var colorScheme = _themeService.GetStatusBarScheme(envType);

// After:
var colorScheme = _themeService.GetStatusBarSchemeForUrl(_session.CurrentEnvironmentUrl);
```

In `UpdateDisplay()` (lines 188-189), change:
```csharp
// Before:
var envType = _themeService.DetectEnvironmentType(_session.CurrentEnvironmentUrl);
var envLabel = _themeService.GetEnvironmentLabel(envType);

// After:
var envLabel = _themeService.GetEnvironmentLabelForUrl(_session.CurrentEnvironmentUrl);
```

In `CaptureState()` (line 219), change:
```csharp
// Before:
var envType = _themeService.DetectEnvironmentType(_session.CurrentEnvironmentUrl);

// After: keep for state capture, but also capture resolved color
var envType = _themeService.DetectEnvironmentType(_session.CurrentEnvironmentUrl);
```

**Step 2: Update TabManager.AddTab**

In `AddTab()` (line 41), the `TabInfo` record needs to store `EnvironmentColor` instead of (or in addition to) `EnvironmentType`:

Update `TabInfo` record:
```csharp
internal sealed record TabInfo(
    ITuiScreen Screen,
    string? EnvironmentUrl,
    string? EnvironmentDisplayName,
    EnvironmentType EnvironmentType,
    EnvironmentColor EnvironmentColor);
```

Update `AddTab()`:
```csharp
public void AddTab(ITuiScreen screen, string? environmentUrl, string? environmentDisplayName = null)
{
    var envType = _themeService.DetectEnvironmentType(environmentUrl);
    var envColor = _themeService.GetResolvedColor(environmentUrl);
    var tab = new TabInfo(screen, environmentUrl, environmentDisplayName, envType, envColor);
    _tabs.Add(tab);
    _activeIndex = _tabs.Count - 1;

    TabsChanged?.Invoke();
    ActiveTabChanged?.Invoke();
}
```

**Step 3: Update TabBar to use EnvironmentColor for inactive tab tinting**

Find where `GetTabScheme` is called with `EnvironmentType` and update to use `EnvironmentColor`:

```csharp
// In TuiColorPalette, update GetTabScheme:
public static ColorScheme GetTabScheme(EnvironmentColor envColor, bool isActive)
{
    if (isActive) return TabActive;

    var fg = GetForegroundColor(envColor);
    return new ColorScheme
    {
        Normal = MakeAttr(fg, Color.Black),
        Focus = MakeAttr(Color.White, Color.Black),
        HotNormal = MakeAttr(fg, Color.Black),
        HotFocus = MakeAttr(Color.White, Color.Black),
        Disabled = MakeAttr(Color.DarkGray, Color.Black)
    };
}
```

Keep the old `GetTabScheme(EnvironmentType, bool)` overload for backward compatibility until all callers are migrated.

**Step 4: Commit**

```
git add src/PPDS.Cli/Tui/Views/TuiStatusBar.cs src/PPDS.Cli/Tui/Infrastructure/TabManager.cs src/PPDS.Cli/Tui/Views/TabBar.cs src/PPDS.Cli/Tui/Infrastructure/TuiColorPalette.cs
git commit -m "feat(tui): update status bar, tabs, and tab bar to use configurable environment colors"
```

---

### Task 11: CLI `ppds env config` Command

**Files:**
- Modify: `src/PPDS.Cli/Commands/Env/EnvCommandGroup.cs` — add `config` subcommand

**Step 1: Add config subcommand to Create() and CreateOrgAlias()**

In both `Create()` and `CreateOrgAlias()`, add:
```csharp
command.Subcommands.Add(CreateConfigCommand());
command.Subcommands.Add(CreateTypeCommand());
```

**Step 2: Implement CreateConfigCommand**

```csharp
private static Command CreateConfigCommand()
{
    var urlArgument = new Argument<string?>("url")
    {
        Description = "Environment URL to configure"
    };
    urlArgument.Arity = ArgumentArity.ZeroOrOne;

    var labelOption = new Option<string?>("--label", "-l")
    {
        Description = "Short display label for status bar and tabs"
    };

    var typeOption = new Option<string?>("--type", "-t")
    {
        Description = "Environment type (e.g., Production, Sandbox, Development, Test, Trial, or custom)"
    };

    var colorOption = new Option<EnvironmentColor?>("--color", "-c")
    {
        Description = "Status bar color. Valid values: Red, Green, Yellow, Cyan, Blue, Gray, Brown, BrightRed, BrightGreen, BrightYellow, BrightCyan, BrightBlue, White"
    };

    var showOption = new Option<bool>("--show", "-s")
    {
        Description = "Show current configuration for the environment"
    };

    var listOption = new Option<bool>("--list")
    {
        Description = "List all configured environments"
    };

    var removeOption = new Option<bool>("--remove")
    {
        Description = "Remove configuration for the environment"
    };

    var command = new Command("config", "Configure environment display settings (label, type, color)")
    {
        urlArgument, labelOption, typeOption, colorOption, showOption, listOption, removeOption
    };

    command.SetAction(async (parseResult, cancellationToken) =>
    {
        var url = parseResult.GetValue(urlArgument);
        var label = parseResult.GetValue(labelOption);
        var type = parseResult.GetValue(typeOption);
        var color = parseResult.GetValue(colorOption);
        var show = parseResult.GetValue(showOption);
        var list = parseResult.GetValue(listOption);
        var remove = parseResult.GetValue(removeOption);

        using var store = new EnvironmentConfigStore();
        var service = new EnvironmentConfigService(store);

        if (list)
            return await ExecuteConfigListAsync(service, cancellationToken);

        if (string.IsNullOrWhiteSpace(url))
        {
            Console.Error.WriteLine("Error: Environment URL is required. Use --list to see all configs.");
            return ExitCodes.Failure;
        }

        if (show)
            return await ExecuteConfigShowAsync(service, url, cancellationToken);

        if (remove)
            return await ExecuteConfigRemoveAsync(service, url, cancellationToken);

        if (label == null && type == null && color == null)
        {
            // No options provided — show current config
            return await ExecuteConfigShowAsync(service, url, cancellationToken);
        }

        return await ExecuteConfigSetAsync(service, url, label, type, color, cancellationToken);
    });

    return command;
}
```

**Step 3: Implement handler methods**

```csharp
private static async Task<int> ExecuteConfigSetAsync(
    EnvironmentConfigService service, string url,
    string? label, string? type, EnvironmentColor? color,
    CancellationToken ct)
{
    var config = await service.SaveConfigAsync(url, label, type, color, ct);

    Console.ForegroundColor = ConsoleColor.Green;
    Console.Error.WriteLine("Environment configuration saved.");
    Console.ResetColor();

    WriteConfigDetails(config);
    return ExitCodes.Success;
}

private static async Task<int> ExecuteConfigShowAsync(
    EnvironmentConfigService service, string url, CancellationToken ct)
{
    var config = await service.GetConfigAsync(url, ct);
    if (config == null)
    {
        Console.Error.WriteLine($"No configuration found for: {url}");
        Console.Error.WriteLine("Use 'ppds env config <url> --label <label> --type <type> --color <color>' to configure.");
        return ExitCodes.Success;
    }

    WriteConfigDetails(config);
    return ExitCodes.Success;
}

private static async Task<int> ExecuteConfigRemoveAsync(
    EnvironmentConfigService service, string url, CancellationToken ct)
{
    var removed = await service.RemoveConfigAsync(url, ct);
    if (removed)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Error.WriteLine($"Configuration removed for: {url}");
        Console.ResetColor();
    }
    else
    {
        Console.Error.WriteLine($"No configuration found for: {url}");
    }
    return ExitCodes.Success;
}

private static async Task<int> ExecuteConfigListAsync(
    EnvironmentConfigService service, CancellationToken ct)
{
    var configs = await service.GetAllConfigsAsync(ct);
    if (configs.Count == 0)
    {
        Console.Error.WriteLine("No environments configured.");
        Console.Error.WriteLine("Use 'ppds env config <url> --label <label> --type <type> --color <color>' to add one.");
        return ExitCodes.Success;
    }

    Console.WriteLine("[Configured Environments]");
    Console.WriteLine();
    foreach (var config in configs)
    {
        WriteConfigDetails(config);
        Console.WriteLine();
    }
    Console.WriteLine($"Total: {configs.Count} environment(s)");
    return ExitCodes.Success;
}

private static void WriteConfigDetails(EnvironmentConfig config)
{
    Console.WriteLine($"  URL:   {config.Url}");
    if (config.Label != null)
        Console.WriteLine($"  Label: {config.Label}");
    if (config.Type != null)
        Console.WriteLine($"  Type:  {config.Type}");
    if (config.Color != null)
        Console.WriteLine($"  Color: {config.Color}");
}
```

**Step 4: Implement CreateTypeCommand for custom type management**

```csharp
private static Command CreateTypeCommand()
{
    var nameArgument = new Argument<string>("name")
    {
        Description = "Type name (e.g., UAT, Gold, Train)"
    };

    var colorOption = new Option<EnvironmentColor?>("--color", "-c")
    {
        Description = "Default color for this type"
    };

    var removeOption = new Option<bool>("--remove")
    {
        Description = "Remove this custom type definition"
    };

    var listOption = new Option<bool>("--list")
    {
        Description = "List all type definitions (built-in + custom)"
    };

    var command = new Command("type", "Manage custom environment type definitions")
    {
        nameArgument, colorOption, removeOption, listOption
    };

    // Allow name to be optional when --list is used
    nameArgument.Arity = ArgumentArity.ZeroOrOne;

    command.SetAction(async (parseResult, cancellationToken) =>
    {
        var name = parseResult.GetValue(nameArgument);
        var color = parseResult.GetValue(colorOption);
        var remove = parseResult.GetValue(removeOption);
        var list = parseResult.GetValue(listOption);

        using var store = new EnvironmentConfigStore();
        var service = new EnvironmentConfigService(store);

        if (list)
        {
            var defaults = await service.GetAllTypeDefaultsAsync(cancellationToken);
            Console.WriteLine("[Environment Types]");
            Console.WriteLine();
            foreach (var (typeName, typeColor) in defaults.OrderBy(d => d.Key))
            {
                Console.WriteLine($"  {typeName,-15} {typeColor}");
            }
            return ExitCodes.Success;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine("Error: Type name is required. Use --list to see all types.");
            return ExitCodes.Failure;
        }

        if (remove)
        {
            var removed = await service.RemoveTypeDefaultAsync(name, cancellationToken);
            Console.Error.WriteLine(removed
                ? $"Removed custom type '{name}'."
                : $"'{name}' is not a custom type (may be built-in).");
            return ExitCodes.Success;
        }

        if (color == null)
        {
            Console.Error.WriteLine("Error: --color is required when defining a type.");
            return ExitCodes.Failure;
        }

        await service.SaveTypeDefaultAsync(name, color.Value, cancellationToken);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Error.WriteLine($"Type '{name}' set to {color.Value}.");
        Console.ResetColor();
        return ExitCodes.Success;
    });

    return command;
}
```

**Step 5: Add required using statements**

Add to top of `EnvCommandGroup.cs`:
```csharp
using PPDS.Cli.Services.Environment;
```

**Step 6: Commit**

```
git add src/PPDS.Cli/Commands/Env/EnvCommandGroup.cs
git commit -m "feat(cli): add 'ppds env config' and 'ppds env type' commands"
```

---

### Task 12: Wire EnvironmentConfigService into DI / InteractiveSession

**Files:**
- Modify: `src/PPDS.Cli/Tui/InteractiveSession.cs` — create and expose `EnvironmentConfigStore` + `EnvironmentConfigService`
- Verify: `TuiThemeService` gets the config service injected

**Step 1: In InteractiveSession constructor or initialization**

Add `EnvironmentConfigStore` and `EnvironmentConfigService` as fields:

```csharp
private readonly EnvironmentConfigStore _envConfigStore = new();
private readonly EnvironmentConfigService _envConfigService;

// In constructor:
_envConfigService = new EnvironmentConfigService(_envConfigStore);
```

Update `GetThemeService()` to pass the config service:
```csharp
public ITuiThemeService GetThemeService()
{
    _themeService ??= new TuiThemeService(_envConfigService);
    return _themeService;
}
```

Expose for dialogs that need direct access:
```csharp
public IEnvironmentConfigService EnvironmentConfigService => _envConfigService;
```

**Step 2: Dispose the store in InteractiveSession.DisposeAsync**

```csharp
_envConfigStore.Dispose();
```

**Step 3: Commit**

```
git add src/PPDS.Cli/Tui/InteractiveSession.cs
git commit -m "feat(tui): wire EnvironmentConfigService into InteractiveSession and TuiThemeService"
```

---

### Task 13: TUI Environment Config Dialog

**Files:**
- Create: `src/PPDS.Cli/Tui/Dialogs/EnvironmentConfigDialog.cs`

**Step 1: Create the dialog**

A simple dialog with Label, Type (dropdown with free-text), and Color (dropdown with preview). Accessible from the environment selector or status bar.

The dialog should:
- Accept the environment URL and current display name
- Load existing config if any
- Show dropdowns for Type (all known types + free entry) and Color (all EnvironmentColor values)
- Save on OK, return whether changes were made
- Preview the color as a colored block next to the dropdown

**Step 2: Wire into EnvironmentSelectorDialog**

Add a "Configure" button that opens `EnvironmentConfigDialog` for the selected environment.

**Step 3: Wire into status bar right-click or menu**

Add a menu item under Settings or as an action on the environment section.

**Step 4: Commit**

```
git add src/PPDS.Cli/Tui/Dialogs/EnvironmentConfigDialog.cs
git commit -m "feat(tui): add EnvironmentConfigDialog for configuring environment label, type, and color"
```

---

### Task 14: Update Existing Tests

**Files:**
- Modify: `tests/PPDS.Cli.Tests/Tui/MultiEnvironmentSessionTests.cs` — verify tests still pass with new TabInfo shape
- Modify: any existing tests that reference `EnvironmentType` or `TuiThemeService`

**Step 1: Fix TabInfo constructor calls**

Update any test code that creates `TabInfo` to include the new `EnvironmentColor` parameter.

**Step 2: Fix TuiThemeService instantiation in tests**

Update tests that create `TuiThemeService` directly to pass `null` for the config service parameter (preserving backward compat).

**Step 3: Run full test suite**

Run: `dotnet test tests/PPDS.Cli.Tests --filter Category=TuiUnit`
Expected: All tests PASS

**Step 4: Commit**

```
git add tests/PPDS.Cli.Tests/
git commit -m "test(tui): update existing tests for EnvironmentColor addition to TabInfo and TuiThemeService"
```

---

### Task 15: Final Verification and Cleanup

**Step 1: Run all TUI tests**

Run: `dotnet test tests/PPDS.Cli.Tests --filter Category=TuiUnit`
Expected: All tests PASS

**Step 2: Run full unit test suite**

Run: `dotnet test tests/PPDS.Cli.Tests --filter Category!=Integration`
Expected: All tests PASS

**Step 3: Build all targets**

Run: `dotnet build src/PPDS.Cli/PPDS.Cli.csproj`
Expected: Build succeeded, 0 errors

**Step 4: Verify CLI help**

Run: `dotnet run --project src/PPDS.Cli -- env config --help`
Expected: Shows help with --label, --type, --color, --show, --list, --remove options

Run: `dotnet run --project src/PPDS.Cli -- env type --list`
Expected: Shows built-in types (Production=Red, Sandbox=Brown, Development=Green, Test=Yellow, Trial=Cyan)

**Step 5: Final commit if any cleanup needed**

```
git commit -m "chore: final cleanup for environment config feature"
```
