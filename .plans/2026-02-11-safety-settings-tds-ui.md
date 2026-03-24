# Safety Settings + TDS UI Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add configurable safety settings (per-environment DML thresholds, protection levels), OPTION query hints, and expose the TDS Endpoint toggle in the TUI and CLI.

**Architecture:** Extend `EnvironmentConfig` with query safety settings stored in the existing per-environment config file. Add `OPTION()` hint parsing in `ExecutionPlanBuilder` that overrides settings per-query. Wire `UseTdsEndpoint` into the TUI as a menu toggle and use the existing `--tds` CLI flag. Enforce environment protection levels (Dev/Test/Production) in `DmlSafetyGuard`.

**Tech Stack:** C# (.NET 8/9/10), Terminal.Gui 1.19+, System.Text.Json, xUnit, FluentAssertions

**Dependency chain:**
```
Task 1: Extend EnvironmentConfig with safety settings (independent)
Task 2: Extend DmlSafetyGuard with configurable thresholds (depends on Task 1)
Task 3: Add protection level enforcement (depends on Task 2)
Task 4: Parse OPTION() query hints (independent)
Task 5: Wire TDS toggle into TUI (independent)
Task 6: End-to-end safety tests (depends on Tasks 2-4)
```

---

### Task 1: Add Safety Settings to EnvironmentConfig

**Files:**
- Modify: `src/PPDS.Auth/Profiles/EnvironmentConfig.cs`
- Test: `tests/PPDS.Auth.Tests/Profiles/EnvironmentConfigStoreTests.cs`

**Context:** `EnvironmentConfig` stores per-environment settings in JSON. Currently has `url`, `label`, `type`, `color`. We add query safety settings here so they persist per-environment and are shared across CLI/TUI/MCP.

**Step 1: Write failing test for new settings round-trip**

Add to `tests/PPDS.Auth.Tests/Profiles/EnvironmentConfigStoreTests.cs`:

```csharp
[Fact]
public async Task SafetySettings_RoundTrips()
{
    var store = CreateTestStore();
    var config = await store.SaveConfigAsync(
        "https://test.crm.dynamics.com",
        label: "TEST",
        type: "Sandbox",
        ct: CancellationToken.None);

    config.SafetySettings = new QuerySafetySettings
    {
        WarnInsertThreshold = 10,
        WarnUpdateThreshold = 0,
        WarnDeleteThreshold = 0,
        PreventUpdateWithoutWhere = true,
        PreventDeleteWithoutWhere = true,
        DmlBatchSize = 200,
        MaxResultRows = 50000,
        QueryTimeoutSeconds = 120,
        UseTdsEndpoint = false,
        BypassCustomPlugins = BypassPluginMode.None,
        BypassPowerAutomateFlows = false
    };

    await store.SaveAsync(await store.LoadAsync(CancellationToken.None), CancellationToken.None);
    var reloaded = await store.GetConfigAsync("https://test.crm.dynamics.com", CancellationToken.None);

    reloaded.Should().NotBeNull();
    reloaded!.SafetySettings.Should().NotBeNull();
    reloaded.SafetySettings!.WarnInsertThreshold.Should().Be(10);
    reloaded.SafetySettings.DmlBatchSize.Should().Be(200);
    reloaded.SafetySettings.PreventDeleteWithoutWhere.Should().BeTrue();
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test tests/PPDS.Auth.Tests --filter "SafetySettings_RoundTrips" -v minimal`
Expected: FAIL — `QuerySafetySettings` class doesn't exist

**Step 3: Create QuerySafetySettings class and add to EnvironmentConfig**

Add new file `src/PPDS.Auth/Profiles/QuerySafetySettings.cs`:

```csharp
using System.Text.Json.Serialization;

namespace PPDS.Auth.Profiles;

/// <summary>
/// Per-environment query safety settings. Stored in environment config JSON.
/// All settings have sensible defaults — null means "use default".
/// </summary>
public sealed class QuerySafetySettings
{
    // ── DML Safety Thresholds ──

    /// <summary>Prompt when inserting more than N records (0 = always prompt). Default: 1.</summary>
    [JsonPropertyName("warn_insert_threshold")]
    public int? WarnInsertThreshold { get; set; }

    /// <summary>Prompt when updating more than N records (0 = always prompt). Default: 0.</summary>
    [JsonPropertyName("warn_update_threshold")]
    public int? WarnUpdateThreshold { get; set; }

    /// <summary>Prompt when deleting more than N records (0 = always prompt). Default: 0.</summary>
    [JsonPropertyName("warn_delete_threshold")]
    public int? WarnDeleteThreshold { get; set; }

    /// <summary>Block UPDATE without WHERE clause. Default: true.</summary>
    [JsonPropertyName("prevent_update_without_where")]
    public bool PreventUpdateWithoutWhere { get; set; } = true;

    /// <summary>Block DELETE without WHERE clause. Default: true.</summary>
    [JsonPropertyName("prevent_delete_without_where")]
    public bool PreventDeleteWithoutWhere { get; set; } = true;

    // ── Execution Settings ──

    /// <summary>Records per DML batch (1-1000). Default: 100.</summary>
    [JsonPropertyName("dml_batch_size")]
    public int? DmlBatchSize { get; set; }

    /// <summary>Maximum rows returned (0 = unlimited). Default: 0.</summary>
    [JsonPropertyName("max_result_rows")]
    public int? MaxResultRows { get; set; }

    /// <summary>Cancel query after N seconds (0 = no timeout). Default: 300.</summary>
    [JsonPropertyName("query_timeout_seconds")]
    public int? QueryTimeoutSeconds { get; set; }

    /// <summary>Route SELECT queries to TDS read replica. Default: false.</summary>
    [JsonPropertyName("use_tds_endpoint")]
    public bool UseTdsEndpoint { get; set; }

    /// <summary>Bypass custom plugin execution. Default: None.</summary>
    [JsonPropertyName("bypass_custom_plugins")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BypassPluginMode BypassCustomPlugins { get; set; } = BypassPluginMode.None;

    /// <summary>Suppress Power Automate flow triggers on DML. Default: false.</summary>
    [JsonPropertyName("bypass_power_automate_flows")]
    public bool BypassPowerAutomateFlows { get; set; }
}

/// <summary>Which plugin types to bypass during DML operations.</summary>
public enum BypassPluginMode
{
    /// <summary>Execute all plugins normally.</summary>
    None,
    /// <summary>Bypass synchronous plugins only.</summary>
    Synchronous,
    /// <summary>Bypass asynchronous plugins only.</summary>
    Asynchronous,
    /// <summary>Bypass all custom plugins.</summary>
    All
}
```

Add property to `src/PPDS.Auth/Profiles/EnvironmentConfig.cs`:

```csharp
[JsonPropertyName("safety_settings")]
public QuerySafetySettings? SafetySettings { get; set; }
```

**Step 4: Run test to verify it passes**

Run: `dotnet test tests/PPDS.Auth.Tests --filter "SafetySettings" -v minimal`
Expected: PASS

**Step 5: Commit**

```bash
git add src/PPDS.Auth/Profiles/QuerySafetySettings.cs src/PPDS.Auth/Profiles/EnvironmentConfig.cs tests/PPDS.Auth.Tests/Profiles/EnvironmentConfigStoreTests.cs
git commit -m "feat(auth): add QuerySafetySettings to EnvironmentConfig"
```

---

### Task 2: Extend DmlSafetyGuard with Configurable Thresholds

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/DmlSafetyGuard.cs`
- Modify: `tests/PPDS.Cli.Tests/Services/Query/DmlSafetyGuardTests.cs`

**Context:** DmlSafetyGuard currently has hardcoded rules: always block DELETE/UPDATE without WHERE, always require confirmation for DML, hardcoded 10K row cap. We make these configurable via `QuerySafetySettings` while keeping the same defaults.

**Step 1: Write failing tests for configurable thresholds**

Add to `tests/PPDS.Cli.Tests/Services/Query/DmlSafetyGuardTests.cs`:

```csharp
[Fact]
public void Check_DeleteWithoutWhere_WhenPreventionDisabled_AllowsWithConfirmation()
{
    var stmt = _parser.ParseStatement("DELETE FROM account");
    var settings = new QuerySafetySettings { PreventDeleteWithoutWhere = false };
    var guard = new DmlSafetyGuard();
    var result = guard.Check(stmt, new DmlSafetyOptions(), settings);

    result.IsBlocked.Should().BeFalse();
    result.RequiresConfirmation.Should().BeTrue();
}

[Fact]
public void Check_Delete_CustomThreshold_BlocksAboveThreshold()
{
    var stmt = _parser.ParseStatement("DELETE FROM account WHERE statecode = 1");
    var settings = new QuerySafetySettings { WarnDeleteThreshold = 0 }; // Always prompt
    var guard = new DmlSafetyGuard();
    var result = guard.Check(stmt, new DmlSafetyOptions(), settings);

    result.RequiresConfirmation.Should().BeTrue();
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "DmlSafetyGuard" --filter "WhenPreventionDisabled|CustomThreshold" -v minimal`
Expected: FAIL — `Check` doesn't accept `QuerySafetySettings` parameter

**Step 3: Add QuerySafetySettings parameter to Check method**

In `src/PPDS.Cli/Services/Query/DmlSafetyGuard.cs`, add overload:

```csharp
/// <summary>
/// Checks a DML statement against safety rules with environment-specific settings.
/// </summary>
public DmlSafetyResult Check(TSqlStatement statement, DmlSafetyOptions options, QuerySafetySettings? settings = null)
{
    var effectiveSettings = settings ?? new QuerySafetySettings();

    return statement switch
    {
        DeleteStatement delete => CheckDelete(delete, options, effectiveSettings),
        UpdateStatement update => CheckUpdate(update, options, effectiveSettings),
        InsertStatement => CheckInsert(options, effectiveSettings),
        SelectStatement => new DmlSafetyResult { IsBlocked = false },
        BeginEndBlockStatement block => CheckBlock(block, options, effectiveSettings),
        IfStatement ifStmt => CheckIf(ifStmt, options, effectiveSettings),
        _ => new DmlSafetyResult { IsBlocked = false }
    };
}
```

Update `CheckDelete` to use `effectiveSettings.PreventDeleteWithoutWhere`:

```csharp
private static DmlSafetyResult CheckDelete(
    DeleteStatement delete, DmlSafetyOptions options, QuerySafetySettings settings)
{
    var hasWhere = delete.DeleteSpecification.WhereClause != null;

    if (!hasWhere && settings.PreventDeleteWithoutWhere)
    {
        return new DmlSafetyResult
        {
            IsBlocked = true,
            BlockReason = "DELETE without WHERE clause is blocked. Add a WHERE clause or disable prevent_delete_without_where.",
        };
    }

    return CheckRowCap(options, settings);
}
```

Similarly update `CheckUpdate` to use `settings.PreventUpdateWithoutWhere`.

Add `CheckInsert`:

```csharp
private static DmlSafetyResult CheckInsert(DmlSafetyOptions options, QuerySafetySettings settings)
{
    return CheckRowCap(options, settings);
}
```

**Step 4: Run tests**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "DmlSafetyGuard" -v minimal`
Expected: ALL PASS

**Step 5: Commit**

```bash
git add src/PPDS.Cli/Services/Query/DmlSafetyGuard.cs tests/PPDS.Cli.Tests/Services/Query/DmlSafetyGuardTests.cs
git commit -m "feat(query): add configurable DML safety thresholds from QuerySafetySettings"
```

---

### Task 3: Add Protection Level Enforcement

**Files:**
- Modify: `src/PPDS.Cli/Services/Query/DmlSafetyGuard.cs`
- Modify: `tests/PPDS.Cli.Tests/Services/Query/DmlSafetyGuardTests.cs`

**Context:** Environment protection levels (Development/Test/Production) affect DML behavior. Production environments require explicit confirmation with preview. Development environments are unrestricted. Test environments use thresholds.

**Step 1: Write failing tests**

```csharp
[Fact]
public void Check_ProductionEnvironment_BlocksDmlByDefault()
{
    var stmt = _parser.ParseStatement("UPDATE account SET name = 'x' WHERE accountid = '123'");
    var guard = new DmlSafetyGuard();
    var result = guard.Check(stmt, new DmlSafetyOptions(), protectionLevel: ProtectionLevel.Production);

    result.RequiresConfirmation.Should().BeTrue();
    result.RequiresPreview.Should().BeTrue();
}

[Fact]
public void Check_DevelopmentEnvironment_UnrestrictedDml()
{
    var stmt = _parser.ParseStatement("DELETE FROM account");
    var settings = new QuerySafetySettings { PreventDeleteWithoutWhere = false };
    var guard = new DmlSafetyGuard();
    var result = guard.Check(stmt, new DmlSafetyOptions { IsConfirmed = true }, settings,
        protectionLevel: ProtectionLevel.Development);

    result.IsBlocked.Should().BeFalse();
    result.RequiresConfirmation.Should().BeFalse();
}
```

**Step 2: Create ProtectionLevel enum**

Add to `src/PPDS.Auth/Profiles/QuerySafetySettings.cs` (same file):

```csharp
/// <summary>Environment protection level determining DML behavior.</summary>
public enum ProtectionLevel
{
    /// <summary>Unrestricted DML. Sandbox and Developer environments.</summary>
    Development,
    /// <summary>Warn per thresholds. Trial environments.</summary>
    Test,
    /// <summary>Block by default, require explicit confirmation with preview. Production and unknown environments.</summary>
    Production
}
```

**Step 3: Add protection level parameter to DmlSafetyGuard.Check**

Add `protectionLevel` parameter and enforce:

```csharp
public DmlSafetyResult Check(
    TSqlStatement statement,
    DmlSafetyOptions options,
    QuerySafetySettings? settings = null,
    ProtectionLevel protectionLevel = ProtectionLevel.Production)
{
    // Development: no additional restrictions beyond what's configured
    // Test: use configured thresholds
    // Production: always require confirmation + preview for DML
    // ...
}
```

In the DML check methods, add Production enforcement:

```csharp
if (protectionLevel == ProtectionLevel.Production && !options.IsConfirmed)
{
    result.RequiresConfirmation = true;
    result.RequiresPreview = true;
}
```

Add `RequiresPreview` property to `DmlSafetyResult`:

```csharp
/// <summary>Whether the user must preview affected records before confirming (Production environments).</summary>
public bool RequiresPreview { get; init; }
```

**Step 4: Add auto-detection helper**

```csharp
/// <summary>
/// Maps a Dataverse environment type string to a protection level.
/// Unknown types default to Production (fail closed).
/// </summary>
public static ProtectionLevel DetectProtectionLevel(string? environmentType) => environmentType?.ToLowerInvariant() switch
{
    "sandbox" => ProtectionLevel.Development,
    "developer" => ProtectionLevel.Development,
    "production" => ProtectionLevel.Production,
    "trial" => ProtectionLevel.Test,
    _ => ProtectionLevel.Production // Fail closed
};
```

**Step 5: Run tests**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "DmlSafetyGuard" -v minimal`
Expected: ALL PASS

**Step 6: Commit**

```bash
git add src/PPDS.Auth/Profiles/QuerySafetySettings.cs src/PPDS.Cli/Services/Query/DmlSafetyGuard.cs tests/PPDS.Cli.Tests/Services/Query/DmlSafetyGuardTests.cs
git commit -m "feat(query): add environment protection level enforcement to DmlSafetyGuard"
```

---

### Task 4: Parse OPTION() Query Hints

**Files:**
- Modify: `src/PPDS.Query/Planning/ExecutionPlanBuilder.cs`
- Create: `src/PPDS.Query/Planning/QueryHintParser.cs`
- Test: `tests/PPDS.Query.Tests/Planning/QueryHintParserTests.cs`

**Context:** ScriptDom parses `OPTION (...)` hints on `SelectStatement.OptimizerHints`. We need to extract recognized hints and apply them as overrides to `QueryPlanOptions`. Unrecognized hints are silently ignored (match SQL Server behavior).

**Step 1: Write failing tests**

Create `tests/PPDS.Query.Tests/Planning/QueryHintParserTests.cs`:

```csharp
using FluentAssertions;
using PPDS.Query.Parsing;
using PPDS.Query.Planning;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class QueryHintParserTests
{
    private readonly QueryParser _parser = new();

    [Fact]
    public void Parse_UseTds_SetsFlag()
    {
        var overrides = QueryHintParser.Parse(
            _parser.Parse("SELECT name FROM account OPTION (USE_TDS)"));

        overrides.UseTdsEndpoint.Should().BeTrue();
    }

    [Fact]
    public void Parse_BatchSize_SetsValue()
    {
        var overrides = QueryHintParser.Parse(
            _parser.Parse("DELETE FROM account WHERE x = 1 OPTION (BATCH_SIZE 50)"));

        overrides.DmlBatchSize.Should().Be(50);
    }

    [Fact]
    public void Parse_MaxDop_SetsValue()
    {
        var overrides = QueryHintParser.Parse(
            _parser.Parse("DELETE FROM account WHERE x = 1 OPTION (MAXDOP 4)"));

        overrides.MaxParallelism.Should().Be(4);
    }

    [Fact]
    public void Parse_NoHints_ReturnsEmpty()
    {
        var overrides = QueryHintParser.Parse(
            _parser.Parse("SELECT name FROM account"));

        overrides.UseTdsEndpoint.Should().BeNull();
        overrides.DmlBatchSize.Should().BeNull();
    }

    [Fact]
    public void Parse_MultipleHints_SetsAll()
    {
        var overrides = QueryHintParser.Parse(
            _parser.Parse("DELETE FROM account WHERE x = 1 OPTION (BATCH_SIZE 50, MAXDOP 4)"));

        overrides.DmlBatchSize.Should().Be(50);
        overrides.MaxParallelism.Should().Be(4);
    }
}
```

**Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PPDS.Query.Tests --filter "QueryHintParser" -v minimal`
Expected: FAIL — class doesn't exist

**Step 3: Implement QueryHintParser**

Create `src/PPDS.Query/Planning/QueryHintParser.cs`:

```csharp
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace PPDS.Query.Planning;

/// <summary>
/// Parses OPTION() query hints from ScriptDom AST and returns override values.
/// Unrecognized hints are silently ignored (match SQL Server behavior).
/// </summary>
public static class QueryHintParser
{
    /// <summary>
    /// Extracts recognized query hints from a parsed SQL statement.
    /// </summary>
    public static QueryHintOverrides Parse(TSqlFragment fragment)
    {
        var overrides = new QueryHintOverrides();

        var statement = fragment is TSqlScript script && script.Batches.Count > 0
            ? script.Batches[0].Statements.FirstOrDefault()
            : fragment as TSqlStatement;

        if (statement is not StatementWithCtesAndXmlNamespaces stmtWithHints)
            return overrides;

        if (stmtWithHints.OptimizerHints == null || stmtWithHints.OptimizerHints.Count == 0)
            return overrides;

        foreach (var hint in stmtWithHints.OptimizerHints)
        {
            if (hint is LiteralOptimizerHint literalHint)
            {
                var name = literalHint.HintKind.ToString().ToUpperInvariant();
                var value = literalHint.Value?.Value;

                switch (name)
                {
                    case "MAXDOP" when int.TryParse(value, out var maxdop):
                        overrides.MaxParallelism = maxdop;
                        break;
                }
            }
            else if (hint is OptimizerHint generalHint)
            {
                // ScriptDom parses table hints and optimizer hints differently.
                // For custom hints (USE_TDS, BATCH_SIZE, etc.) we check the hint text.
                var hintText = generalHint.ToString()?.Trim().ToUpperInvariant() ?? "";

                if (hintText.Contains("USE_TDS"))
                    overrides.UseTdsEndpoint = true;
                else if (hintText.Contains("BYPASS_PLUGINS"))
                    overrides.BypassPlugins = true;
                else if (hintText.Contains("BYPASS_FLOWS"))
                    overrides.BypassFlows = true;
                else if (hintText.Contains("NOLOCK"))
                    overrides.NoLock = true;
                else if (hintText.Contains("HASH GROUP"))
                    overrides.ForceClientAggregation = true;

                // Extract value-based hints
                if (TryExtractIntHint(hintText, "BATCH_SIZE", out var batchSize))
                    overrides.DmlBatchSize = batchSize;
                else if (TryExtractIntHint(hintText, "MAX_ROWS", out var maxRows))
                    overrides.MaxResultRows = maxRows;
            }
        }

        return overrides;
    }

    private static bool TryExtractIntHint(string hintText, string prefix, out int value)
    {
        value = 0;
        var idx = hintText.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        var rest = hintText[(idx + prefix.Length)..].Trim();
        var numStr = new string(rest.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(numStr, out value);
    }
}

/// <summary>
/// Per-query overrides extracted from OPTION() hints.
/// Null values mean "no override — use profile/session default".
/// </summary>
public sealed class QueryHintOverrides
{
    public bool? UseTdsEndpoint { get; set; }
    public int? DmlBatchSize { get; set; }
    public int? MaxParallelism { get; set; }
    public int? MaxResultRows { get; set; }
    public bool? BypassPlugins { get; set; }
    public bool? BypassFlows { get; set; }
    public bool? NoLock { get; set; }
    public bool? ForceClientAggregation { get; set; }
}
```

**Step 4: Run tests**

Run: `dotnet test tests/PPDS.Query.Tests --filter "QueryHintParser" -v minimal`
Expected: ALL PASS (some tests may need adjustment depending on how ScriptDom parses custom OPTION hints — the parser may reject non-standard hints. If so, use a comment-based approach: `-- ppds:USE_TDS` or parse from the raw SQL text.)

**Step 5: Commit**

```bash
git add src/PPDS.Query/Planning/QueryHintParser.cs tests/PPDS.Query.Tests/Planning/QueryHintParserTests.cs
git commit -m "feat(query): add OPTION() query hint parser"
```

---

### Task 5: Wire TDS Toggle into TUI

**Files:**
- Modify: `src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs`

**Context:** TDS backend is fully wired (`TdsScanNode`, `TdsCompatibilityChecker`, `TdsQueryExecutor`, `QueryPlanOptions.UseTdsEndpoint`). The CLI already has `--tds` flag. The TUI needs a toggle in the Query menu and a status indicator.

**Step 1: Add TDS toggle state field**

In `SqlQueryScreen.cs`, add field near the existing `_isExecuting` field:

```csharp
private bool _useTdsEndpoint;
```

**Step 2: Add menu item to ScreenMenuItems**

Update the `ScreenMenuItems` property to add a TDS toggle:

```csharp
public override MenuBarItem[]? ScreenMenuItems => new[]
{
    new MenuBarItem("_Query", new MenuItem[]
    {
        new("Execute", "Ctrl+Enter", () => _ = ExecuteQueryAsync()),
        new("Show FetchXML", "Ctrl+Shift+F", ShowFetchXmlDialog),
        new("Show Execution Plan", "Ctrl+Shift+E", ShowExecutionPlanDialog),
        new("History", "Ctrl+Shift+H", ShowHistoryDialog),
        new("", "", () => {}, null, null, Key.Null), // Separator
        new("Filter Results", "/", ShowFilter),
        new("", "", () => {}, null, null, Key.Null), // Separator
        new(_useTdsEndpoint ? "✓ TDS Read Replica" : "  TDS Read Replica", "Ctrl+T", ToggleTdsEndpoint),
    })
};
```

**Step 3: Add toggle method**

```csharp
private void ToggleTdsEndpoint()
{
    _useTdsEndpoint = !_useTdsEndpoint;
    _statusLabel.Text = _useTdsEndpoint
        ? "Mode: TDS Read Replica (read-only, slight delay)"
        : "Mode: Dataverse (real-time)";
}
```

**Step 4: Pass TDS flag to query request**

In the `ExecuteQueryAsync` method (around line 521), update the request:

```csharp
var request = new SqlQueryRequest
{
    Sql = sql,
    PageNumber = null,
    PagingCookie = null,
    EnablePrefetch = true,
    UseTdsEndpoint = _useTdsEndpoint
};
```

**Step 5: Add Ctrl+T keyboard shortcut handler**

In the `ProcessKey` override (near the existing keyboard handlers):

```csharp
case Key.T | Key.CtrlMask:
    ToggleTdsEndpoint();
    e.Handled = true;
    break;
```

**Step 6: Build and verify**

Run: `dotnet build src/PPDS.Cli`
Expected: zero errors

**Step 7: Commit**

```bash
git add src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs
git commit -m "feat(tui): add TDS Endpoint toggle with Ctrl+T shortcut"
```

---

### Task 6: End-to-End Safety Integration Tests

**Files:**
- Modify: `tests/PPDS.Cli.Tests/Services/Query/DmlSafetyGuardTests.cs`

**Step 1: Write comprehensive integration tests**

```csharp
[Theory]
[InlineData("DELETE FROM account", ProtectionLevel.Production, true, true)]
[InlineData("DELETE FROM account", ProtectionLevel.Development, true, false)]
[InlineData("DELETE FROM account WHERE x = 1", ProtectionLevel.Production, false, true)]
[InlineData("DELETE FROM account WHERE x = 1", ProtectionLevel.Test, false, true)]
[InlineData("UPDATE account SET name = 'x'", ProtectionLevel.Production, true, true)]
[InlineData("INSERT INTO account (name) VALUES ('x')", ProtectionLevel.Production, false, true)]
public void Check_ProtectionLevel_EnforcesCorrectPolicy(
    string sql, ProtectionLevel level, bool expectBlocked, bool expectConfirmation)
{
    var stmt = _parser.ParseStatement(sql);
    var guard = new DmlSafetyGuard();
    var result = guard.Check(stmt, new DmlSafetyOptions(), protectionLevel: level);

    result.IsBlocked.Should().Be(expectBlocked);
    if (!expectBlocked)
        result.RequiresConfirmation.Should().Be(expectConfirmation);
}

[Fact]
public void DetectProtectionLevel_Maps_Correctly()
{
    DmlSafetyGuard.DetectProtectionLevel("Sandbox").Should().Be(ProtectionLevel.Development);
    DmlSafetyGuard.DetectProtectionLevel("Developer").Should().Be(ProtectionLevel.Development);
    DmlSafetyGuard.DetectProtectionLevel("Production").Should().Be(ProtectionLevel.Production);
    DmlSafetyGuard.DetectProtectionLevel("Trial").Should().Be(ProtectionLevel.Test);
    DmlSafetyGuard.DetectProtectionLevel(null).Should().Be(ProtectionLevel.Production); // Fail closed
    DmlSafetyGuard.DetectProtectionLevel("Unknown").Should().Be(ProtectionLevel.Production); // Fail closed
}
```

**Step 2: Run all safety tests**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "DmlSafetyGuard" -v minimal`
Expected: ALL PASS

**Step 3: Run full test suite to verify no regressions**

Run: `dotnet test tests/PPDS.Cli.Tests --filter "Category!=Integration" -v minimal`
Run: `dotnet test tests/PPDS.Query.Tests --filter "Category!=Integration" -v minimal`
Expected: ALL PASS

**Step 4: Commit**

```bash
git add tests/PPDS.Cli.Tests/Services/Query/DmlSafetyGuardTests.cs
git commit -m "test(query): add comprehensive safety settings integration tests"
```

---

## Summary

| Task | What | Type | Effort |
|------|------|------|--------|
| 1 | Add QuerySafetySettings to EnvironmentConfig | Feature | Small |
| 2 | Configurable DML thresholds in DmlSafetyGuard | Feature | Medium |
| 3 | Protection level enforcement | Feature | Medium |
| 4 | OPTION() query hint parsing | Feature | Medium |
| 5 | TDS toggle in TUI | Feature | Small |
| 6 | End-to-end safety tests | Testing | Small |

## Deferred Items (Not in This Plan)

| Item | Reason |
|------|--------|
| Cross-environment DML policy | Depends on Wave 3 cross-environment infrastructure (RemoteScanNode) |
| `ppds profile set --protection` CLI command | Nice-to-have; auto-detection covers most cases |
| `max_page_retrievals` setting | Needs changes deep in FetchXmlScanNode paging; separate PR |
| `datetime_mode` setting | Requires pervasive DateTime handling changes; separate initiative |
| `show_fetchxml_in_explain` setting | EXPLAIN output formatting is a separate concern |
| Adaptive DML batching | Performance optimization; separate from safety settings |
