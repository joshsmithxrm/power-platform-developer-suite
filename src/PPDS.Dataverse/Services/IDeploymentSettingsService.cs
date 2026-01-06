using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Dataverse.Services;

/// <summary>
/// Service for deployment settings file operations.
/// </summary>
/// <remarks>
/// Deployment settings files configure environment-specific values (environment variables,
/// connection references) for solution deployment. This service generates, syncs, and validates
/// these files in the PAC-compatible format.
/// </remarks>
public interface IDeploymentSettingsService
{
    /// <summary>
    /// Generates a new deployment settings file from the current environment.
    /// </summary>
    /// <param name="solutionName">The solution unique name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The generated deployment settings.</returns>
    Task<DeploymentSettingsFile> GenerateAsync(
        string solutionName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Syncs an existing deployment settings file with the current solution.
    /// Preserves existing values, adds new entries, removes obsolete entries.
    /// </summary>
    /// <param name="solutionName">The solution unique name.</param>
    /// <param name="existingSettings">The existing settings to sync (null if new file).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The synced settings and statistics.</returns>
    Task<DeploymentSettingsSyncResult> SyncAsync(
        string solutionName,
        DeploymentSettingsFile? existingSettings,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a deployment settings file against the current solution.
    /// </summary>
    /// <param name="solutionName">The solution unique name.</param>
    /// <param name="settings">The settings file to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation result with any issues found.</returns>
    Task<DeploymentSettingsValidation> ValidateAsync(
        string solutionName,
        DeploymentSettingsFile settings,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// PAC-compatible deployment settings file format.
/// </summary>
/// <remarks>
/// This format is compatible with `pac solution import --settings-file`.
/// Entries are sorted by schema name (StringComparison.Ordinal) for deterministic output.
/// </remarks>
public sealed class DeploymentSettingsFile
{
    /// <summary>Gets or sets the environment variables.</summary>
    [JsonPropertyName("EnvironmentVariables")]
    public List<EnvironmentVariableEntry> EnvironmentVariables { get; set; } = new();

    /// <summary>Gets or sets the connection references.</summary>
    [JsonPropertyName("ConnectionReferences")]
    public List<ConnectionReferenceEntry> ConnectionReferences { get; set; } = new();
}

/// <summary>
/// Environment variable entry for deployment settings.
/// </summary>
public sealed class EnvironmentVariableEntry
{
    /// <summary>Gets or sets the schema name.</summary>
    [JsonPropertyName("SchemaName")]
    public string SchemaName { get; set; } = string.Empty;

    /// <summary>Gets or sets the value.</summary>
    [JsonPropertyName("Value")]
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Connection reference entry for deployment settings.
/// </summary>
public sealed class ConnectionReferenceEntry
{
    /// <summary>Gets or sets the logical name.</summary>
    [JsonPropertyName("LogicalName")]
    public string LogicalName { get; set; } = string.Empty;

    /// <summary>Gets or sets the connection ID.</summary>
    [JsonPropertyName("ConnectionId")]
    public string ConnectionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the connector ID.</summary>
    [JsonPropertyName("ConnectorId")]
    public string ConnectorId { get; set; } = string.Empty;
}

/// <summary>
/// Result of syncing deployment settings.
/// </summary>
public sealed class DeploymentSettingsSyncResult
{
    /// <summary>Gets the synced settings file.</summary>
    public required DeploymentSettingsFile Settings { get; init; }

    /// <summary>Gets the environment variable sync statistics.</summary>
    public required SyncStatistics EnvironmentVariables { get; init; }

    /// <summary>Gets the connection reference sync statistics.</summary>
    public required SyncStatistics ConnectionReferences { get; init; }
}

/// <summary>
/// Statistics for a sync operation.
/// </summary>
public sealed class SyncStatistics
{
    /// <summary>Gets the number of entries added.</summary>
    public int Added { get; init; }

    /// <summary>Gets the number of entries removed.</summary>
    public int Removed { get; init; }

    /// <summary>Gets the number of entries preserved (values kept from existing file).</summary>
    public int Preserved { get; init; }
}

/// <summary>
/// Result of validating deployment settings.
/// </summary>
public sealed class DeploymentSettingsValidation
{
    /// <summary>Gets whether the settings are valid.</summary>
    public bool IsValid => Issues.Count == 0;

    /// <summary>Gets the validation issues.</summary>
    public List<ValidationIssue> Issues { get; init; } = new();
}

/// <summary>
/// A single validation issue.
/// </summary>
public sealed class ValidationIssue
{
    /// <summary>Gets the issue severity.</summary>
    public required ValidationSeverity Severity { get; init; }

    /// <summary>Gets the entry type (EnvironmentVariable or ConnectionReference).</summary>
    public required string EntryType { get; init; }

    /// <summary>Gets the schema/logical name of the entry.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the issue message.</summary>
    public required string Message { get; init; }
}

/// <summary>
/// Validation issue severity.
/// </summary>
public enum ValidationSeverity
{
    /// <summary>Warning - deployment may work but review recommended.</summary>
    Warning,

    /// <summary>Error - deployment will likely fail.</summary>
    Error
}
