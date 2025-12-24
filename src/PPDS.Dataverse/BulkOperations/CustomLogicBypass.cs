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
