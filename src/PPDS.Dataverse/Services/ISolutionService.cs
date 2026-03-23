using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Models;

namespace PPDS.Dataverse.Services;

/// <summary>
/// Service for querying and managing Dataverse solutions.
/// </summary>
public interface ISolutionService
{
    /// <summary>
    /// Lists solutions in the environment.
    /// </summary>
    /// <param name="filter">Optional filter by unique name or friendly name.</param>
    /// <param name="includeManaged">Include managed solutions (default: false).</param>
    /// <param name="includeInternal">Include internal/invisible solutions like Default and Active (default: false).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ListResult<SolutionInfo>> ListAsync(
        string? filter = null,
        bool includeManaged = false,
        bool includeInternal = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a solution by unique name.
    /// </summary>
    /// <param name="uniqueName">The solution unique name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SolutionInfo?> GetAsync(string uniqueName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a solution by ID.
    /// </summary>
    /// <param name="solutionId">The solution ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SolutionInfo?> GetByIdAsync(Guid solutionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets components for a solution.
    /// </summary>
    /// <param name="solutionId">The solution ID.</param>
    /// <param name="componentType">Optional filter by component type.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<List<SolutionComponentInfo>> GetComponentsAsync(
        Guid solutionId,
        int? componentType = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports a solution to a ZIP file.
    /// </summary>
    /// <param name="uniqueName">The solution unique name.</param>
    /// <param name="managed">Export as managed solution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<byte[]> ExportAsync(string uniqueName, bool managed = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports a solution from a ZIP file.
    /// </summary>
    /// <param name="solutionZip">The solution ZIP file contents.</param>
    /// <param name="overwrite">Overwrite existing customizations.</param>
    /// <param name="publishWorkflows">Automatically publish workflows.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Guid> ImportAsync(
        byte[] solutionZip,
        bool overwrite = true,
        bool publishWorkflows = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes all customizations.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PublishAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Information about a solution.
/// </summary>
public record SolutionInfo(
    Guid Id,
    string UniqueName,
    string FriendlyName,
    string? Version,
    bool IsManaged,
    string? PublisherName,
    string? Description,
    DateTime? CreatedOn,
    DateTime? ModifiedOn,
    DateTime? InstalledOn,
    bool IsVisible = true,
    bool IsApiManaged = false);

/// <summary>
/// Information about a solution component.
/// </summary>
public record SolutionComponentInfo(
    Guid Id,
    Guid ObjectId,
    int ComponentType,
    string ComponentTypeName,
    int RootComponentBehavior,
    bool IsMetadata,
    string? DisplayName = null,
    string? LogicalName = null,
    string? SchemaName = null);
