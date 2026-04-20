using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Models;

namespace PPDS.Cli.Services.EnvironmentVariables;

/// <summary>
/// Service for environment variable operations.
/// </summary>
public interface IEnvironmentVariableService
{
    /// <summary>
    /// Lists all environment variable definitions.
    /// </summary>
    /// <param name="solutionName">Optional solution filter (unique name).</param>
    /// <param name="includeInactive">When true, includes inactive variables. Defaults to false (active only).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of environment variable definitions.</returns>
    Task<ListResult<EnvironmentVariableInfo>> ListAsync(
        string? solutionName = null,
        bool includeInactive = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific environment variable by schema name.
    /// </summary>
    /// <param name="schemaName">The schema name of the environment variable.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The environment variable info, or null if not found.</returns>
    Task<EnvironmentVariableInfo?> GetAsync(
        string schemaName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific environment variable by ID.
    /// </summary>
    /// <param name="id">The environment variable definition ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The environment variable info, or null if not found.</returns>
    Task<EnvironmentVariableInfo?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the value of an environment variable.
    /// </summary>
    /// <param name="schemaName">The schema name of the environment variable.</param>
    /// <param name="value">The value to set.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if successful, false if the variable doesn't exist.</returns>
    Task<bool> SetValueAsync(
        string schemaName,
        string value,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports environment variable definitions and current values for deployment settings.
    /// </summary>
    /// <param name="solutionName">Optional solution filter (unique name).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Export data containing environment variable definitions and values.</returns>
    Task<EnvironmentVariableExport> ExportAsync(
        string? solutionName = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Environment variable definition with current value.
/// </summary>
public sealed record EnvironmentVariableInfo
{
    /// <summary>Gets the environment variable definition ID.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the schema name.</summary>
    public required string SchemaName { get; init; }

    /// <summary>Gets the display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Gets the description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the variable type (String, Number, Boolean, JSON, DataSource, Secret).</summary>
    public required string Type { get; init; }

    /// <summary>Gets the default value.</summary>
    public string? DefaultValue { get; init; }

    /// <summary>Gets the current value (from EnvironmentVariableValue).</summary>
    public string? CurrentValue { get; init; }

    /// <summary>Gets the current value record ID (if value exists).</summary>
    public Guid? CurrentValueId { get; init; }

    /// <summary>Gets whether the variable is required.</summary>
    public bool IsRequired { get; init; }

    /// <summary>Gets whether this is a managed variable.</summary>
    public bool IsManaged { get; init; }

    /// <summary>Gets the secret store for secret-type variables.</summary>
    public string? SecretStore { get; init; }

    /// <summary>Gets the created date.</summary>
    public DateTime? CreatedOn { get; init; }

    /// <summary>Gets the modified date.</summary>
    public DateTime? ModifiedOn { get; init; }
}

/// <summary>
/// Environment variable export data for deployment settings.
/// </summary>
public sealed record EnvironmentVariableExport
{
    /// <summary>Gets the environment variables.</summary>
    public required List<EnvironmentVariableExportItem> EnvironmentVariables { get; init; }
}

/// <summary>
/// A single environment variable for export.
/// </summary>
public sealed record EnvironmentVariableExportItem
{
    /// <summary>Gets the schema name.</summary>
    public required string SchemaName { get; init; }

    /// <summary>Gets the current value.</summary>
    public string? Value { get; init; }
}
