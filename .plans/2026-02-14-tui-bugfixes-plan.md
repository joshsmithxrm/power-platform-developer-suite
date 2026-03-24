# TUI Bug Fixes Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix 5 TUI bugs (device code copy, device code auto-close, Linux keybindings, TextInput focus visibility, Type field dropdown) plus 1 found bug (DetectProtectionLevel string mapping).

**Architecture:** Fixes are ordered by dependency — TextInput focus and keybindings are independent leaves; EnvironmentType enum move must happen before dialog/service changes; DeviceCodeDialog is self-contained. TDD throughout: write failing test, implement, verify, commit.

**Tech Stack:** .NET 9.0, Terminal.Gui 1.19, xUnit, PPDS.Auth, PPDS.Cli

---

### Task 1: Fix TextInput Focus Visibility

The simplest fix — one line change to `TuiColorPalette.TextInput.Focus` so TextFields show a visible background when focused.

**Files:**
- Modify: `src/PPDS.Cli/Tui/Infrastructure/TuiColorPalette.cs:70`
- Test: `tests/PPDS.Cli.Tests/Tui/Infrastructure/TuiColorPaletteTests.cs`

**Step 1: Write the failing test**

Add to `tests/PPDS.Cli.Tests/Tui/Infrastructure/TuiColorPaletteTests.cs`:

```csharp
[Fact]
public void TextInput_FocusBackground_DiffersFromNormalBackground()
{
    var scheme = TuiColorPalette.TextInput;
    Assert.NotEqual(scheme.Normal.Background, scheme.Focus.Background);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~TextInput_FocusBackground" --no-restore -v minimal`

Expected: FAIL — currently both are `Color.Black`.

**Step 3: Implement the fix**

In `src/PPDS.Cli/Tui/Infrastructure/TuiColorPalette.cs:67-74`, change line 70 from:

```csharp
Focus = MakeAttr(Color.White, Color.Black),
```

to:

```csharp
Focus = MakeAttr(Color.White, Color.DarkGray),
```

Also update the doc comment on line 65 from:

```csharp
/// No background change on focus - block cursor provides visibility.
```

to:

```csharp
/// DarkGray background on focus for visible cursor indication.
```

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~TextInput_FocusBackground" --no-restore -v minimal`

Expected: PASS

**Step 5: Run existing palette tests to verify no regressions**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~TuiColorPaletteTests" --no-restore -v minimal`

Expected: All pass (cyan background rule is not violated — DarkGray background doesn't trigger the rule).

**Step 6: Commit**

```bash
git add src/PPDS.Cli/Tui/Infrastructure/TuiColorPalette.cs tests/PPDS.Cli.Tests/Tui/Infrastructure/TuiColorPaletteTests.cs
git commit -m "fix(tui): add DarkGray background to TextInput Focus for visible cursor"
```

---

### Task 2: Add Linux-Compatible F-Key Bindings

Add F7/F8/F9 as cross-platform alternatives for Ctrl+Shift+E/H/F.

**Files:**
- Modify: `src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs:78-90` (menu labels) and `:334-349` (hotkey registrations)
- Test: `tests/PPDS.Cli.Tests/Tui/Dialogs/DialogStateCaptureTests.cs` (KeyboardShortcuts state captures registered hotkeys)

**Step 1: Write the failing test**

The keyboard shortcuts dialog uses `HotkeyRegistry` to list all registered shortcuts. Add a test that verifies F7/F8/F9 appear in the registry.

Add to `tests/PPDS.Cli.Tests/Tui/Screens/` a new file `SqlQueryScreenHotkeyTests.cs`:

```csharp
using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Screens;

[Trait("Category", "TuiUnit")]
public class SqlQueryScreenHotkeyTests
{
    [Theory]
    [InlineData(Key.F7, "Show execution plan")]
    [InlineData(Key.F8, "Query history")]
    [InlineData(Key.F9, "Show FetchXML")]
    public void FKeyAlternatives_AreRegistered(Key expectedKey, string expectedDescription)
    {
        var registry = new HotkeyRegistry();

        // Simulate what SqlQueryScreen.RegisterHotkeys does by registering the expected keys
        // This test validates the key-to-description mapping exists after registration
        registry.Register(expectedKey, HotkeyScope.Screen, expectedDescription, () => { }, owner: null);

        var shortcuts = registry.GetShortcuts();
        Assert.Contains(shortcuts, s =>
            s.Key == expectedKey && s.Description.Contains(expectedDescription, StringComparison.OrdinalIgnoreCase));
    }
}
```

Actually, let's test more directly. The `RegisterHotkeys` method is `protected override` on `SqlQueryScreen`. Since we can't easily instantiate SqlQueryScreen in a unit test (it needs a session), let's verify the hotkey format strings instead.

Better approach — write a test that validates the menu item shortcut labels include the F-key alternatives:

```csharp
using Xunit;

namespace PPDS.Cli.Tests.Tui.Screens;

/// <summary>
/// Validates that F-key alternatives exist for Linux compatibility.
/// These can't easily be tested via SqlQueryScreen instantiation, so we test
/// by verifying the HotkeyRegistry format produces expected key names.
/// </summary>
[Trait("Category", "TuiUnit")]
public class SqlQueryScreenHotkeyTests
{
    [Theory]
    [InlineData(Terminal.Gui.Key.F7)]
    [InlineData(Terminal.Gui.Key.F8)]
    [InlineData(Terminal.Gui.Key.F9)]
    public void HotkeyRegistry_FormatsLinuxFKeys_Correctly(Terminal.Gui.Key key)
    {
        var registry = new PPDS.Cli.Tui.Infrastructure.HotkeyRegistry();
        registry.Register(key, PPDS.Cli.Tui.Infrastructure.HotkeyScope.Screen, "test", () => { });
        var shortcuts = registry.GetShortcuts();
        Assert.Single(shortcuts);
        Assert.StartsWith("F", shortcuts[0].KeyDisplay);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~SqlQueryScreenHotkeyTests" --no-restore -v minimal`

Expected: PASS (this test validates the registry, not the screen; it should pass immediately since HotkeyRegistry already formats F-keys). This is a pre-condition test. The actual fix is in Step 3.

**Step 3: Add F-key registrations to SqlQueryScreen**

In `src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs`, in the `RegisterHotkeys` method (around line 334), add after the existing registrations:

```csharp
// F-key alternatives for Linux compatibility (Ctrl+Shift combos don't work on Linux terminals)
RegisterHotkey(registry, Key.F7, "Show execution plan", ShowExecutionPlanDialog);
RegisterHotkey(registry, Key.F8, "Query history", ShowHistoryDialog);
RegisterHotkey(registry, Key.F9, "Show FetchXML", ShowFetchXmlDialog);
```

**Step 4: Update menu labels to show both shortcuts**

In the menu bar definition (around lines 78-90), update the shortcut display strings:

```csharp
new("Show FetchXML", "Ctrl+Shift+F / F9", ShowFetchXmlDialog),
new("Show Execution Plan", "Ctrl+Shift+E / F7", ShowExecutionPlanDialog),
new("History", "Ctrl+Shift+H / F8", ShowHistoryDialog),
```

**Step 5: Run all TUI tests**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "Category=TuiUnit" --no-restore -v minimal`

Expected: All pass.

**Step 6: Commit**

```bash
git add src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs tests/PPDS.Cli.Tests/Tui/Screens/SqlQueryScreenHotkeyTests.cs
git commit -m "fix(tui): add F7/F8/F9 keybindings for Linux terminal compatibility"
```

---

### Task 3: Move EnvironmentType Enum to PPDS.Auth

This is a prerequisite for Tasks 4-6. Move the enum from TUI-only to the shared Auth layer so both `EnvironmentConfig` and `DmlSafetyGuard` can reference it.

**Files:**
- Create: `src/PPDS.Auth/Profiles/EnvironmentType.cs`
- Delete: `src/PPDS.Cli/Tui/Infrastructure/EnvironmentType.cs`
- Modify: All files that `using PPDS.Cli.Tui.Infrastructure` for `EnvironmentType` — update to `using PPDS.Auth.Profiles`

**Step 1: Create the enum in PPDS.Auth**

Create `src/PPDS.Auth/Profiles/EnvironmentType.cs`:

```csharp
namespace PPDS.Auth.Profiles;

/// <summary>
/// Classification of Dataverse environment types.
/// Drives protection levels and default color theming.
/// </summary>
public enum EnvironmentType
{
    /// <summary>Unknown or unconfigured — auto-detect from Discovery API or URL heuristics.</summary>
    Unknown,

    /// <summary>Production environment — DML blocked by default, requires confirmation with preview.</summary>
    Production,

    /// <summary>Sandbox/staging environment — unrestricted DML.</summary>
    Sandbox,

    /// <summary>Development environment — unrestricted DML.</summary>
    Development,

    /// <summary>Test/QA/UAT environment — unrestricted DML.</summary>
    Test,

    /// <summary>Trial environment — unrestricted DML.</summary>
    Trial
}
```

**Step 2: Verify it compiles**

Run: `dotnet build src/PPDS.Auth --no-restore -v minimal`

Expected: Build success.

**Step 3: Delete old enum and update callers**

Delete `src/PPDS.Cli/Tui/Infrastructure/EnvironmentType.cs`.

Update `using` statements in all files that referenced `PPDS.Cli.Tui.Infrastructure.EnvironmentType`. The key files are:

- `src/PPDS.Cli/Tui/Infrastructure/TuiColorPalette.cs` — change `using PPDS.Cli.Tui.Infrastructure` references to also `using PPDS.Auth.Profiles` (already present for `EnvironmentColor`). The `GetTabScheme(EnvironmentType)` and `GetStatusBarScheme(EnvironmentType)` methods reference the enum.
- `src/PPDS.Cli/Tui/Dialogs/EnvironmentSelectorDialog.cs` — references `EnvironmentType` for display
- `src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs` — if it references `EnvironmentType`
- `tests/PPDS.Cli.Tests/Tui/Infrastructure/TuiColorPaletteTests.cs` — references `EnvironmentType` in test parameters

Since `EnvironmentType` was in the `PPDS.Cli.Tui.Infrastructure` namespace and callers in the same namespace didn't need a `using`, search for all references:

```bash
grep -rn "EnvironmentType" src/PPDS.Cli/ tests/PPDS.Cli.Tests/ --include="*.cs" | grep -v "obj/"
```

For each file: if it already has `using PPDS.Auth.Profiles`, no change needed. If not, add `using PPDS.Auth.Profiles`.

**Step 4: Verify full build**

Run: `dotnet build --no-restore -v minimal`

Expected: Build success with no errors.

**Step 5: Run all tests**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "Category=TuiUnit" --no-restore -v minimal`

Expected: All pass (pure namespace move, no behavior change).

**Step 6: Commit**

```bash
git add -A
git commit -m "refactor: move EnvironmentType enum from PPDS.Cli.Tui to PPDS.Auth.Profiles"
```

---

### Task 4: Fix DetectProtectionLevel — Enum-Based Mapping

Replace the fragile string-based `DetectProtectionLevel` with an enum-based version. Only Production maps to `ProtectionLevel.Production`; everything else maps to `ProtectionLevel.Development`.

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/DmlSafetyGuard.cs:55-66`
- Modify: `tests/PPDS.Cli.Tests/Services/Query/DmlSafetyGuardTests.cs:593-601`
- Modify: All callers of `DetectProtectionLevel` (search for them)

**Step 1: Write the failing test**

Replace the existing test in `tests/PPDS.Cli.Tests/Services/Query/DmlSafetyGuardTests.cs` around line 593:

```csharp
[Theory]
[InlineData(EnvironmentType.Production, ProtectionLevel.Production)]
[InlineData(EnvironmentType.Sandbox, ProtectionLevel.Development)]
[InlineData(EnvironmentType.Development, ProtectionLevel.Development)]
[InlineData(EnvironmentType.Test, ProtectionLevel.Development)]
[InlineData(EnvironmentType.Trial, ProtectionLevel.Development)]
[InlineData(EnvironmentType.Unknown, ProtectionLevel.Development)]
public void DetectProtectionLevel_MapsEnvironmentType_Correctly(EnvironmentType envType, ProtectionLevel expected)
{
    Assert.Equal(expected, DmlSafetyGuard.DetectProtectionLevel(envType));
}
```

Add `using PPDS.Auth.Profiles;` at top if not already present (for `EnvironmentType`).

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~DetectProtectionLevel_MapsEnvironmentType" --no-restore -v minimal`

Expected: FAIL — `DetectProtectionLevel` currently takes `string?`, not `EnvironmentType`.

**Step 3: Implement the fix**

In `src/PPDS.Cli/Services/Query/DmlSafetyGuard.cs`, replace the method at line 55-66:

```csharp
/// <summary>
/// Maps a Dataverse environment type to a protection level.
/// Only Production environments are locked down; everything else is unrestricted.
/// </summary>
public static ProtectionLevel DetectProtectionLevel(EnvironmentType environmentType) => environmentType switch
{
    EnvironmentType.Production => ProtectionLevel.Production,
    _ => ProtectionLevel.Development
};
```

Add `using PPDS.Auth.Profiles;` at top if not already present.

**Step 4: Fix callers**

Search for all callers of `DetectProtectionLevel`:

```bash
grep -rn "DetectProtectionLevel" src/ --include="*.cs" | grep -v "obj/"
```

Each caller currently passes a `string?`. Update them to pass an `EnvironmentType` enum value instead. If the caller has a string from the Discovery API, parse it first via a helper (see Task 5 for that helper). For now, callers that previously passed a string should use `EnvironmentType.Unknown` as a safe default if conversion isn't straightforward yet.

**Step 5: Remove old test, run all tests**

Remove the old `DetectProtectionLevel_Maps_Correctly` test (the `[Fact]` one at line 593).

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~DmlSafetyGuardTests" --no-restore -v minimal`

Expected: All pass.

**Step 6: Commit**

```bash
git add src/PPDS.Cli/Services/Query/DmlSafetyGuard.cs tests/PPDS.Cli.Tests/Services/Query/DmlSafetyGuardTests.cs
git commit -m "fix(safety): DetectProtectionLevel uses EnvironmentType enum, only Production is locked"
```

---

### Task 5: Update EnvironmentConfig Model and Services

Change `EnvironmentConfig.Type` from `string?` to `EnvironmentType?`, add `DiscoveredType`, update `EnvironmentConfigService` and `EnvironmentConfigStore`.

**Files:**
- Modify: `src/PPDS.Auth/Profiles/EnvironmentConfig.cs`
- Modify: `src/PPDS.Auth/Profiles/EnvironmentConfigStore.cs` (`SaveConfigAsync` signature)
- Modify: `src/PPDS.Cli/Services/Environment/EnvironmentConfigService.cs`
- Modify: `src/PPDS.Cli/Services/Environment/IEnvironmentConfigService.cs`
- Modify: `tests/PPDS.Cli.Tests/Services/Environment/EnvironmentConfigServiceTests.cs`
- Modify: `tests/PPDS.Cli.Tests/Services/Environment/EnvironmentConfigStoreTests.cs`

**Step 1: Write the failing test for enum-based ResolveTypeAsync**

In `tests/PPDS.Cli.Tests/Services/Environment/EnvironmentConfigServiceTests.cs`, add:

```csharp
[Fact]
public async Task ResolveTypeAsync_UserConfigType_ReturnsEnum()
{
    await _service.SaveConfigAsync("https://org.crm.dynamics.com", type: EnvironmentType.Sandbox);
    var result = await _service.ResolveTypeAsync("https://org.crm.dynamics.com");
    Assert.Equal(EnvironmentType.Sandbox, result);
}

[Fact]
public async Task ResolveTypeAsync_NoConfig_ReturnsUnknown()
{
    var result = await _service.ResolveTypeAsync("https://org.crm.dynamics.com");
    Assert.Equal(EnvironmentType.Unknown, result);
}

[Fact]
public async Task ResolveTypeAsync_DiscoveredType_ParsedToEnum()
{
    var result = await _service.ResolveTypeAsync("https://org.crm.dynamics.com", discoveredType: "Sandbox");
    Assert.Equal(EnvironmentType.Sandbox, result);
}

[Fact]
public async Task ResolveTypeAsync_DiscoveredDeveloper_MapsToDevelopment()
{
    var result = await _service.ResolveTypeAsync("https://org.crm.dynamics.com", discoveredType: "Developer");
    Assert.Equal(EnvironmentType.Development, result);
}
```

**Step 2: Run to verify they fail**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~ResolveTypeAsync_UserConfigType_ReturnsEnum" --no-restore -v minimal`

Expected: FAIL — `SaveConfigAsync` takes `string? type`, not `EnvironmentType?`.

**Step 3: Update EnvironmentConfig model**

In `src/PPDS.Auth/Profiles/EnvironmentConfig.cs`:

Change `Type` property:

```csharp
/// <summary>
/// User-configured environment type override.
/// Drives protection levels and default color theming.
/// Null means auto-detect from DiscoveredType or URL heuristics.
/// </summary>
[JsonPropertyName("type")]
[JsonConverter(typeof(JsonStringEnumConverter))]
public EnvironmentType? Type { get; set; }
```

Add `DiscoveredType` property:

```csharp
/// <summary>
/// Raw environment type from the Discovery API (e.g., "Sandbox", "Developer", "Production").
/// Stored separately from user Type override. Not user-editable.
/// </summary>
[JsonPropertyName("discovered_type")]
public string? DiscoveredType { get; set; }
```

**Step 4: Update EnvironmentConfigStore.SaveConfigAsync signature**

In `src/PPDS.Auth/Profiles/EnvironmentConfigStore.cs`, change the `type` parameter from `string?` to `EnvironmentType?`:

```csharp
public async Task<EnvironmentConfig> SaveConfigAsync(
    string url, string? label = null, EnvironmentType? type = null, EnvironmentColor? color = null,
    bool clearColor = false,
    CancellationToken ct = default)
```

Update the body to set `config.Type = type` directly (no string assignment).

Also add `discoveredType` parameter:

```csharp
public async Task<EnvironmentConfig> SaveConfigAsync(
    string url, string? label = null, EnvironmentType? type = null, EnvironmentColor? color = null,
    bool clearColor = false, string? discoveredType = null,
    CancellationToken ct = default)
```

And in the body: `if (discoveredType != null) config.DiscoveredType = discoveredType;`

**Step 5: Update IEnvironmentConfigService and EnvironmentConfigService**

In `src/PPDS.Cli/Services/Environment/IEnvironmentConfigService.cs`:

- Change `SaveConfigAsync` `type` param: `EnvironmentType? type = null`
- Change `ResolveTypeAsync` return type: `Task<EnvironmentType>`
- Add `discoveredType` param: `string? discoveredType = null`

In `src/PPDS.Cli/Services/Environment/EnvironmentConfigService.cs`:

Update `SaveConfigAsync` to pass enum through.

Update `ResolveTypeAsync`:

```csharp
public async Task<EnvironmentType> ResolveTypeAsync(string url, string? discoveredType = null, CancellationToken ct = default)
{
    // Priority 1: user config type
    var config = await _store.GetConfigAsync(url, ct).ConfigureAwait(false);
    if (config?.Type != null)
        return config.Type.Value;

    // Priority 2: discovery API type (parse string to enum)
    if (!string.IsNullOrWhiteSpace(discoveredType))
    {
        var parsed = ParseDiscoveryType(discoveredType);
        if (parsed != EnvironmentType.Unknown)
            return parsed;
    }

    // Priority 3: URL heuristics
    var heuristic = DetectTypeFromUrl(url);
    if (heuristic != EnvironmentType.Unknown)
        return heuristic;

    return EnvironmentType.Unknown;
}
```

Add a `ParseDiscoveryType` helper:

```csharp
/// <summary>
/// Maps Discovery API type strings to EnvironmentType enum values.
/// </summary>
internal static EnvironmentType ParseDiscoveryType(string? discoveryType) => discoveryType?.ToLowerInvariant() switch
{
    "production" => EnvironmentType.Production,
    "sandbox" => EnvironmentType.Sandbox,
    "developer" => EnvironmentType.Development,
    "development" => EnvironmentType.Development,
    "trial" => EnvironmentType.Trial,
    "test" => EnvironmentType.Test,
    _ => EnvironmentType.Unknown
};
```

Update `DetectTypeFromUrl` to return `EnvironmentType` instead of `string?`:

```csharp
internal static EnvironmentType DetectTypeFromUrl(string? url)
{
    // ... existing segment extraction logic ...
    if (segments.Any(s => DevKeywords.Any(k => s.Equals(k, StringComparison.OrdinalIgnoreCase))))
        return EnvironmentType.Development;
    if (segments.Any(s => TestKeywords.Any(k => s.Equals(k, StringComparison.OrdinalIgnoreCase))))
        return EnvironmentType.Test;
    if (segments.Any(s => TrialKeywords.Any(k => s.Equals(k, StringComparison.OrdinalIgnoreCase))))
        return EnvironmentType.Trial;
    return EnvironmentType.Unknown;
}
```

Update `BuiltInTypeDefaults` from `Dictionary<string, EnvironmentColor>` to `Dictionary<EnvironmentType, EnvironmentColor>`:

```csharp
private static readonly Dictionary<EnvironmentType, EnvironmentColor> BuiltInTypeDefaults = new()
{
    [EnvironmentType.Production] = EnvironmentColor.Red,
    [EnvironmentType.Sandbox] = EnvironmentColor.Brown,
    [EnvironmentType.Development] = EnvironmentColor.Green,
    [EnvironmentType.Test] = EnvironmentColor.Yellow,
    [EnvironmentType.Trial] = EnvironmentColor.Cyan,
};
```

Update `ResolveColorAsync` to use `EnvironmentType`:

```csharp
public async Task<EnvironmentColor> ResolveColorAsync(string url, CancellationToken ct = default)
{
    var config = await _store.GetConfigAsync(url, ct).ConfigureAwait(false);

    // Priority 1: per-environment explicit color
    if (config is not null && config.Color != null)
        return config.Color.Value;

    // Priority 2: type-based color
    var envType = config?.Type ?? DetectTypeFromUrl(url);
    if (envType != EnvironmentType.Unknown)
    {
        var allDefaults = await GetAllTypeDefaultsAsync(ct).ConfigureAwait(false);
        if (allDefaults.TryGetValue(envType, out var typeColor))
            return typeColor;
    }

    return EnvironmentColor.Gray;
}
```

Update `GetAllTypeDefaultsAsync`, `SaveTypeDefaultAsync`, `RemoveTypeDefaultAsync` to use `EnvironmentType` key instead of string.

**Step 6: Update existing tests**

In `tests/PPDS.Cli.Tests/Services/Environment/EnvironmentConfigServiceTests.cs`, update calls from `type: "Production"` to `type: EnvironmentType.Production`, etc.

In `tests/PPDS.Cli.Tests/Services/Environment/EnvironmentConfigStoreTests.cs`, update similarly.

Remove any tests for custom string types like "Gold" or "UAT" — these are no longer valid. The type-defaults system should be keyed by `EnvironmentType` now.

**Step 7: Run all tests**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "Category=TuiUnit" --no-restore -v minimal`

Expected: All pass.

**Step 8: Commit**

```bash
git add -A
git commit -m "refactor(config): change EnvironmentConfig.Type from string to EnvironmentType enum"
```

---

### Task 6: Update EnvironmentConfigDialog — Type Dropdown

Replace the free-text `TextField` with a `ListView` dropdown for the Type field.

**Files:**
- Modify: `src/PPDS.Cli/Tui/Dialogs/EnvironmentConfigDialog.cs`
- Modify: `src/PPDS.Cli/Tui/Testing/States/EnvironmentConfigDialogState.cs`
- Modify: `tests/PPDS.Cli.Tests/Tui/Dialogs/` (add or update EnvironmentConfigDialog state test)

**Step 1: Write the failing test**

Add a test file `tests/PPDS.Cli.Tests/Tui/Dialogs/EnvironmentConfigDialogStateTests.cs`:

```csharp
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Dialogs;

/// <summary>
/// Tests for EnvironmentConfigDialog state capture — verifies the Type field
/// is a constrained dropdown (not free text).
/// </summary>
[Trait("Category", "TuiUnit")]
public class EnvironmentConfigDialogStateTests
{
    [Fact]
    public void CaptureState_TypeIsEnum_NotFreeText()
    {
        // After the fix, EnvironmentConfigDialogState.Type should be EnvironmentType?
        // not a free-text string. Verify the type on the record.
        var state = new Testing.States.EnvironmentConfigDialogState(
            Title: "Configure Environment",
            Url: "https://org.crm.dynamics.com/",
            Label: "",
            Type: null,
            SelectedColorIndex: 0,
            SelectedColor: null,
            ConfigChanged: false,
            IsVisible: true);

        Assert.Null(state.Type); // Auto-detect
    }
}
```

Wait — this depends on `EnvironmentConfigDialogState.Type` being `EnvironmentType?` instead of `string`. Let's write it to match:

```csharp
[Fact]
public void CaptureState_Type_IsEnvironmentTypeNullable()
{
    // Verify the state record uses EnvironmentType? for Type (not string)
    var state = new PPDS.Cli.Tui.Testing.States.EnvironmentConfigDialogState(
        Title: "Test",
        Url: "https://org.crm.dynamics.com/",
        Label: "",
        Type: EnvironmentType.Sandbox,
        SelectedColorIndex: 0,
        SelectedColor: null,
        ConfigChanged: false,
        IsVisible: true);

    Assert.Equal(EnvironmentType.Sandbox, state.Type);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~EnvironmentConfigDialogStateTests" --no-restore -v minimal`

Expected: FAIL — `EnvironmentConfigDialogState.Type` is currently `string`.

**Step 3: Update the state record**

In `src/PPDS.Cli/Tui/Testing/States/EnvironmentConfigDialogState.cs`, change `Type` from `string` to `EnvironmentType?`:

```csharp
public sealed record EnvironmentConfigDialogState(
    string Title,
    string Url,
    string Label,
    EnvironmentType? Type,
    int SelectedColorIndex,
    EnvironmentColor? SelectedColor,
    bool ConfigChanged,
    bool IsVisible);
```

Add `using PPDS.Auth.Profiles;` at top.

**Step 4: Update EnvironmentConfigDialog to use ListView for Type**

In `src/PPDS.Cli/Tui/Dialogs/EnvironmentConfigDialog.cs`:

Replace `_typeField` declaration (currently `TextField`) with:

```csharp
private readonly ListView _typeList;
private readonly EnvironmentType?[] _typeValues;
```

Replace the type field creation (lines 75-94) with:

```csharp
// Type selection (constrained to known types)
var typeLabel = new Label("Type:")
{
    X = 2,
    Y = 5
};

_typeValues = new EnvironmentType?[] { null }
    .Concat(Enum.GetValues<EnvironmentType>()
        .Where(t => t != EnvironmentType.Unknown)
        .Cast<EnvironmentType?>())
    .ToArray();
var typeNames = _typeValues
    .Select(t => t?.ToString() ?? "(Auto-detect)")
    .ToList();

_typeList = new ListView(typeNames)
{
    X = 10,
    Y = 5,
    Width = Dim.Fill() - 3,
    Height = 4,
    AllowsMarking = false,
    AllowsMultipleSelection = false
};
```

Update `LoadExistingConfig()` to set selection from enum:

```csharp
if (config.Type != null)
{
    var idx = Array.IndexOf(_typeValues, config.Type);
    if (idx >= 0)
        _typeList.SelectedItem = idx;
}
```

When no config but `_suggestedType` is set, parse it:

```csharp
else if (_suggestedType != null)
{
    var parsed = EnvironmentConfigService.ParseDiscoveryType(_suggestedType);
    if (parsed != EnvironmentType.Unknown)
    {
        var idx = Array.IndexOf(_typeValues, (EnvironmentType?)parsed);
        if (idx >= 0)
            _typeList.SelectedItem = idx;
    }
}
```

Update `OnSaveClicked()` to read from the list:

```csharp
EnvironmentType? type = null;
if (_typeList.SelectedItem >= 0 && _typeList.SelectedItem < _typeValues.Length)
{
    type = _typeValues[_typeList.SelectedItem];
}
```

Update `CaptureState()`:

```csharp
var selectedType = _typeList.SelectedItem >= 0 && _typeList.SelectedItem < _typeValues.Length
    ? _typeValues[_typeList.SelectedItem]
    : null;

return new EnvironmentConfigDialogState(
    Title: Title?.ToString() ?? string.Empty,
    Url: _environmentUrl,
    Label: _labelField.Text?.ToString() ?? string.Empty,
    Type: selectedType,
    SelectedColorIndex: _colorList.SelectedItem,
    SelectedColor: selectedColor,
    ConfigChanged: ConfigChanged,
    IsVisible: Visible);
```

Update `Add()` call to use `_typeList` instead of `_typeField` and remove the `typeHint` label.

Adjust Y positions for color section since we removed the hint label and changed the type field height.

Remove the `NormalizeDiscoveryType` method (no longer needed — parsing is now in `EnvironmentConfigService.ParseDiscoveryType`).

Make `EnvironmentConfigService.ParseDiscoveryType` `public static` so the dialog can call it.

**Step 5: Run all tests**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "Category=TuiUnit" --no-restore -v minimal`

Expected: All pass.

**Step 6: Commit**

```bash
git add -A
git commit -m "feat(tui): replace Type TextField with constrained dropdown in EnvironmentConfigDialog"
```

---

### Task 7: Create DeviceCodeDialog with Selectable Code and Auto-Close

New dialog replaces `MessageBox.Query()` for device code display. Supports text selection and auto-close when auth succeeds.

**Files:**
- Create: `src/PPDS.Cli/Tui/Dialogs/DeviceCodeDialog.cs`
- Create: `src/PPDS.Cli/Tui/Testing/States/DeviceCodeDialogState.cs`
- Modify: `src/PPDS.Cli/Tui/Dialogs/ProfileCreationDialog.cs:497-512`
- Create: `tests/PPDS.Cli.Tests/Tui/Dialogs/DeviceCodeDialogTests.cs`

**Step 1: Write the failing test**

Create `tests/PPDS.Cli.Tests/Tui/Dialogs/DeviceCodeDialogTests.cs`:

```csharp
using PPDS.Cli.Tui.Dialogs;
using PPDS.Cli.Tui.Testing.States;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Dialogs;

[Trait("Category", "TuiUnit")]
public class DeviceCodeDialogTests
{
    [Fact]
    public void CaptureState_ContainsUserCode()
    {
        using var dialog = new DeviceCodeDialog("ABC-DEF", "https://microsoft.com/devicelogin");
        var state = dialog.CaptureState();

        Assert.Equal("ABC-DEF", state.UserCode);
    }

    [Fact]
    public void CaptureState_ContainsVerificationUrl()
    {
        using var dialog = new DeviceCodeDialog("ABC-DEF", "https://microsoft.com/devicelogin");
        var state = dialog.CaptureState();

        Assert.Equal("https://microsoft.com/devicelogin", state.VerificationUrl);
    }

    [Fact]
    public void CaptureState_ShowsClipboardStatus()
    {
        using var dialog = new DeviceCodeDialog("ABC-DEF", "https://microsoft.com/devicelogin", clipboardCopied: true);
        var state = dialog.CaptureState();

        Assert.True(state.ClipboardCopied);
    }

    [Fact]
    public void CaptureState_CodeFieldIsNotEmpty()
    {
        using var dialog = new DeviceCodeDialog("XYZ-123", "https://microsoft.com/devicelogin");
        var state = dialog.CaptureState();

        Assert.Equal("XYZ-123", state.UserCode);
        Assert.False(string.IsNullOrEmpty(state.UserCode));
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~DeviceCodeDialogTests" --no-restore -v minimal`

Expected: FAIL — `DeviceCodeDialog` class doesn't exist yet.

**Step 3: Create DeviceCodeDialogState**

Create `src/PPDS.Cli/Tui/Testing/States/DeviceCodeDialogState.cs`:

```csharp
namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Captured state of the DeviceCodeDialog for testing.
/// </summary>
public sealed record DeviceCodeDialogState(
    string Title,
    string UserCode,
    string VerificationUrl,
    bool ClipboardCopied,
    bool IsVisible);
```

**Step 4: Create DeviceCodeDialog**

Create `src/PPDS.Cli/Tui/Dialogs/DeviceCodeDialog.cs`:

```csharp
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog for displaying device code authentication info.
/// Shows the code in a selectable TextField and auto-closes when auth completes.
/// </summary>
internal sealed class DeviceCodeDialog : TuiDialog, ITuiStateCapture<DeviceCodeDialogState>
{
    private readonly string _userCode;
    private readonly string _verificationUrl;
    private readonly bool _clipboardCopied;
    private readonly TextField _codeField;
    private CancellationTokenRegistration? _autoCloseRegistration;

    /// <summary>
    /// Creates a device code dialog with selectable code display.
    /// </summary>
    /// <param name="userCode">The device code to display.</param>
    /// <param name="verificationUrl">The URL where the user enters the code.</param>
    /// <param name="clipboardCopied">Whether the code was auto-copied to clipboard.</param>
    /// <param name="authComplete">Optional token that fires when auth succeeds — auto-closes the dialog.</param>
    /// <param name="session">Optional session for hotkey registry integration.</param>
    public DeviceCodeDialog(
        string userCode,
        string verificationUrl,
        bool clipboardCopied = false,
        CancellationToken authComplete = default,
        InteractiveSession? session = null)
        : base("Authentication Required", session)
    {
        _userCode = userCode;
        _verificationUrl = verificationUrl;
        _clipboardCopied = clipboardCopied;

        Width = 60;
        Height = 12;

        var urlLabel = new Label($"Visit: {verificationUrl}")
        {
            X = Pos.Center(),
            Y = 1,
            TextAlignment = TextAlignment.Centered
        };

        var codeLabel = new Label("Enter this code:")
        {
            X = Pos.Center(),
            Y = 3,
            TextAlignment = TextAlignment.Centered
        };

        // Selectable TextField so user can select + Ctrl+C the code
        _codeField = new TextField(userCode)
        {
            X = Pos.Center(),
            Y = 5,
            Width = userCode.Length + 4,
            ReadOnly = true,
            ColorScheme = TuiColorPalette.Focused
        };

        var clipboardLabel = new Label(clipboardCopied ? "(copied to clipboard!)" : "(select code above and Ctrl+C to copy)")
        {
            X = Pos.Center(),
            Y = 7,
            TextAlignment = TextAlignment.Centered,
            ColorScheme = clipboardCopied ? TuiColorPalette.Success : TuiColorPalette.Default
        };

        var okButton = new Button("_OK")
        {
            X = Pos.Center(),
            Y = Pos.AnchorEnd(1),
            IsDefault = true
        };
        okButton.Clicked += () => Application.RequestStop();

        Add(urlLabel, codeLabel, _codeField, clipboardLabel, okButton);

        // Auto-close when authentication completes
        if (authComplete.CanBeCanceled)
        {
            _autoCloseRegistration = authComplete.Register(() =>
            {
                Application.MainLoop?.Invoke(() => Application.RequestStop());
            });
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autoCloseRegistration?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public DeviceCodeDialogState CaptureState() => new(
        Title: Title?.ToString() ?? string.Empty,
        UserCode: _userCode,
        VerificationUrl: _verificationUrl,
        ClipboardCopied: _clipboardCopied,
        IsVisible: Visible);
}
```

**Step 5: Run test to verify it passes**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "FullyQualifiedName~DeviceCodeDialogTests" --no-restore -v minimal`

Expected: PASS

**Step 6: Commit the dialog**

```bash
git add src/PPDS.Cli/Tui/Dialogs/DeviceCodeDialog.cs src/PPDS.Cli/Tui/Testing/States/DeviceCodeDialogState.cs tests/PPDS.Cli.Tests/Tui/Dialogs/DeviceCodeDialogTests.cs
git commit -m "feat(tui): add DeviceCodeDialog with selectable code and auto-close support"
```

---

### Task 8: Wire DeviceCodeDialog into ProfileCreationDialog

Replace the `MessageBox.Query()` in ProfileCreationDialog with the new DeviceCodeDialog, including auto-close wiring.

**Files:**
- Modify: `src/PPDS.Cli/Tui/Dialogs/ProfileCreationDialog.cs:497-512`

**Step 1: Understand the current callback structure**

The callback at line 497-512 is:

```csharp
var deviceCallback = _deviceCodeCallback ?? (info =>
{
    Application.MainLoop?.Invoke(() =>
    {
        var copied = ClipboardHelper.CopyToClipboard(info.UserCode) ? " (copied!)" : "";
        MessageBox.Query(
            "Authentication Required",
            $"Visit: {info.VerificationUrl}\n\nEnter code: {info.UserCode}{copied}\n\nComplete authentication in browser, then press OK.",
            "OK");
    });
});
```

**Step 2: Replace with DeviceCodeDialog**

The device code callback needs to show DeviceCodeDialog instead. For auto-close, we need a `CancellationTokenSource` that fires when auth completes. The tricky part: the callback is invoked from MSAL's background thread, and `CreateProfileAndHandleResultAsync` runs concurrently. We need a shared CTS.

Create the CTS before calling `CreateProfileAndHandleResultAsync`:

```csharp
// CancellationTokenSource that signals when auth completes (to auto-close device code dialog)
using var authCompleteCts = new CancellationTokenSource();

var deviceCallback = _deviceCodeCallback ?? (info =>
{
    Application.MainLoop?.Invoke(() =>
    {
        var copied = ClipboardHelper.CopyToClipboard(info.UserCode);

        using var dialog = new DeviceCodeDialog(
            info.UserCode,
            info.VerificationUrl,
            clipboardCopied: copied,
            authComplete: authCompleteCts.Token);
        Application.Run(dialog);
    });
});
```

Then in `CreateProfileAndHandleResultAsync`, after the profile service call returns successfully, cancel the CTS:

This is trickier because `CreateProfileAndHandleResultAsync` is called via fire-and-forget. The CTS needs to be accessible. The simplest approach: add a field `_authCompleteCts` on the dialog, set it before calling auth, and cancel it in the success path.

Add a field:

```csharp
private CancellationTokenSource? _authCompleteCts;
```

In `OnAuthenticateClicked()`, before building the callback:

```csharp
_authCompleteCts?.Dispose();
_authCompleteCts = new CancellationTokenSource();
```

In the callback:

```csharp
var deviceCallback = _deviceCodeCallback ?? (info =>
{
    Application.MainLoop?.Invoke(() =>
    {
        var copied = ClipboardHelper.CopyToClipboard(info.UserCode);

        using var dialog = new DeviceCodeDialog(
            info.UserCode,
            info.VerificationUrl,
            clipboardCopied: copied,
            authComplete: _authCompleteCts?.Token ?? CancellationToken.None);
        Application.Run(dialog);
    });
});
```

In `CreateProfileAndHandleResultAsync`, after success (around line 565 where `_createdProfile = profile`), add:

```csharp
_authCompleteCts?.Cancel();
```

Also cancel on failure/completion to ensure cleanup.

**Step 3: Run all TUI tests**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "Category=TuiUnit" --no-restore -v minimal`

Expected: All pass.

**Step 4: Commit**

```bash
git add src/PPDS.Cli/Tui/Dialogs/ProfileCreationDialog.cs
git commit -m "fix(tui): replace MessageBox with DeviceCodeDialog for selectable code and auto-close"
```

---

### Task 9: Final Verification

Run full test suite and verify everything works together.

**Step 1: Build everything**

Run: `dotnet build --no-restore -v minimal`

Expected: Build success with no errors or warnings related to our changes.

**Step 2: Run all unit tests**

Run: `dotnet test --filter "Category!=Integration" --no-restore -v minimal`

Expected: All pass.

**Step 3: Commit any remaining fixups**

If any tests needed adjustment, commit them.

**Step 4: Final commit message summary**

Verify git log shows clean commits for each fix.
