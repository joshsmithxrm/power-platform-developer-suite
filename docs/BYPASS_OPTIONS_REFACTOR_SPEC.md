# Bypass Options Refactor Specification

## Overview

Refactor `BulkOperationOptions` to provide type-safe, discoverable configuration for bypassing custom business logic during bulk operations.

## Problem Statement

The current implementation exposes `BypassBusinessLogicExecution` as a raw `string?`, requiring consumers to know magic strings:

```csharp
// Current - poor DX
options.BypassBusinessLogicExecution = "CustomSync";
options.BypassBusinessLogicExecution = "CustomAsync";
options.BypassBusinessLogicExecution = "CustomSync,CustomAsync";
```

**Issues:**
- No IntelliSense discoverability
- Easy to typo (case-sensitive)
- Unclear what valid values are
- Legacy `BypassCustomPluginExecution` bool creates confusion

## Solution

### 1. Add Flags Enum

**File:** `src/PPDS.Dataverse/BulkOperations/CustomLogicBypass.cs`

```csharp
using System;

namespace PPDS.Dataverse.BulkOperations;

/// <summary>
/// Specifies which custom business logic to bypass during bulk operations.
/// </summary>
/// <remarks>
/// <para>
/// Requires the <c>prvBypassCustomBusinessLogic</c> privilege.
/// By default, only users with the System Administrator security role have this privilege.
/// </para>
/// <para>
/// This bypasses custom plugins and workflows only. Microsoft's core system plugins
/// and workflows included in Microsoft-published solutions are NOT bypassed.
/// </para>
/// <para>
/// Does not affect Power Automate flows. Use <see cref="BulkOperationOptions.BypassPowerAutomateFlows"/>
/// to bypass flows.
/// </para>
/// </remarks>
[Flags]
public enum CustomLogicBypass
{
    /// <summary>
    /// No bypass - execute all custom business logic (default).
    /// </summary>
    None = 0,

    /// <summary>
    /// Bypass synchronous custom plugins and workflows.
    /// Maps to Dataverse parameter: <c>BypassBusinessLogicExecution: "CustomSync"</c>
    /// </summary>
    Synchronous = 1,

    /// <summary>
    /// Bypass asynchronous custom plugins and workflows.
    /// Does not affect Power Automate flows.
    /// Maps to Dataverse parameter: <c>BypassBusinessLogicExecution: "CustomAsync"</c>
    /// </summary>
    Asynchronous = 2,

    /// <summary>
    /// Bypass all custom plugins and workflows (both sync and async).
    /// Does not affect Power Automate flows.
    /// Maps to Dataverse parameter: <c>BypassBusinessLogicExecution: "CustomSync,CustomAsync"</c>
    /// </summary>
    All = Synchronous | Asynchronous
}
```

### 2. Update BulkOperationOptions

**File:** `src/PPDS.Dataverse/BulkOperations/BulkOperationOptions.cs`

Remove:
- `BypassBusinessLogicExecution` (string)
- `BypassCustomPluginExecution` (bool)

Add:

```csharp
/// <summary>
/// Gets or sets which custom business logic to bypass during execution.
/// </summary>
/// <remarks>
/// <para>
/// Requires the <c>prvBypassCustomBusinessLogic</c> privilege.
/// By default, only System Administrators have this privilege.
/// </para>
/// <para>
/// This bypasses custom plugins and workflows only. Microsoft's core system plugins
/// and solution workflows are NOT bypassed.
/// </para>
/// <para>
/// Does not affect Power Automate flows - use <see cref="BypassPowerAutomateFlows"/> for that.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Bypass sync plugins only (for performance during bulk loads)
/// options.BypassCustomLogic = CustomLogicBypass.Synchronous;
///
/// // Bypass async plugins only (prevent system job backlog)
/// options.BypassCustomLogic = CustomLogicBypass.Asynchronous;
///
/// // Bypass all custom logic
/// options.BypassCustomLogic = CustomLogicBypass.All;
///
/// // Combine with Power Automate bypass
/// options.BypassCustomLogic = CustomLogicBypass.All;
/// options.BypassPowerAutomateFlows = true;
/// </code>
/// </example>
public CustomLogicBypass BypassCustomLogic { get; set; } = CustomLogicBypass.None;

/// <summary>
/// Gets or sets a tag value passed to plugin execution context.
/// </summary>
/// <remarks>
/// <para>
/// Plugins can access this value via <c>context.SharedVariables["tag"]</c>.
/// </para>
/// <para>
/// Useful for:
/// <list type="bullet">
/// <item>Identifying records created by bulk operations in plugin logic</item>
/// <item>Audit trails (e.g., "Migration-2025-Q4", "ETL-Job-123")</item>
/// <item>Conditional plugin behavior based on data source</item>
/// </list>
/// </para>
/// <para>
/// No special privileges required.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// options.Tag = "BulkImport-2025-12-24";
///
/// // In a plugin:
/// if (context.SharedVariables.TryGetValue("tag", out var tag)
///     &amp;&amp; tag?.ToString()?.StartsWith("BulkImport") == true)
/// {
///     // Skip audit logging for bulk imports
///     return;
/// }
/// </code>
/// </example>
public string? Tag { get; set; }
```

### 3. Update ApplyBypassOptions

**File:** `src/PPDS.Dataverse/BulkOperations/BulkOperationExecutor.cs`

```csharp
private static void ApplyBypassOptions(OrganizationRequest request, BulkOperationOptions options)
{
    // Custom business logic bypass
    if (options.BypassCustomLogic != CustomLogicBypass.None)
    {
        var parts = new List<string>(2);
        if (options.BypassCustomLogic.HasFlag(CustomLogicBypass.Synchronous))
            parts.Add("CustomSync");
        if (options.BypassCustomLogic.HasFlag(CustomLogicBypass.Asynchronous))
            parts.Add("CustomAsync");
        request.Parameters["BypassBusinessLogicExecution"] = string.Join(",", parts);
    }

    // Power Automate flows bypass
    if (options.BypassPowerAutomateFlows)
    {
        request.Parameters["SuppressCallbackRegistrationExpanderJob"] = true;
    }

    // Duplicate detection
    if (options.SuppressDuplicateDetection)
    {
        request.Parameters["SuppressDuplicateDetection"] = true;
    }

    // Tag for plugin context
    if (!string.IsNullOrEmpty(options.Tag))
    {
        request.Parameters["tag"] = options.Tag;
    }
}
```

## Final BulkOperationOptions API

```csharp
public class BulkOperationOptions
{
    public int BatchSize { get; set; } = 100;
    public bool ElasticTable { get; set; } = false;
    public bool ContinueOnError { get; set; } = true;
    public CustomLogicBypass BypassCustomLogic { get; set; } = CustomLogicBypass.None;
    public bool BypassPowerAutomateFlows { get; set; } = false;
    public bool SuppressDuplicateDetection { get; set; } = false;
    public string? Tag { get; set; }
    public int? MaxParallelBatches { get; set; } = null;
}
```

## Files to Modify

| File | Change |
|------|--------|
| `src/PPDS.Dataverse/BulkOperations/CustomLogicBypass.cs` | **NEW** - Flags enum |
| `src/PPDS.Dataverse/BulkOperations/BulkOperationOptions.cs` | Replace string/bool with enum, add Tag |
| `src/PPDS.Dataverse/BulkOperations/BulkOperationExecutor.cs` | Simplify `ApplyBypassOptions` |

## Consumer Usage

```csharp
var options = new BulkOperationOptions
{
    BypassCustomLogic = CustomLogicBypass.All,
    BypassPowerAutomateFlows = true,
    Tag = "Migration-2025"
};

await executor.UpsertMultipleAsync("account", accounts, options);
```

## Testing Requirements

1. Verify `CustomLogicBypass.Synchronous` maps to `"CustomSync"`
2. Verify `CustomLogicBypass.Asynchronous` maps to `"CustomAsync"`
3. Verify `CustomLogicBypass.All` maps to `"CustomSync,CustomAsync"`
4. Verify `CustomLogicBypass.Synchronous | CustomLogicBypass.Asynchronous` equals `All`
5. Verify `CustomLogicBypass.None` adds no parameter
6. Verify `Tag` is passed to request parameters
7. Verify `BypassPowerAutomateFlows` maps to `SuppressCallbackRegistrationExpanderJob`

## References

- [Bypass custom business logic](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/bypass-custom-business-logic)
- [Bypass Power Automate flows](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/bypass-power-automate-flows)
- [Optional parameters](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/optional-parameters)
