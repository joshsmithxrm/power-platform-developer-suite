using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Models;

namespace PPDS.Dataverse.Services;

/// <summary>
/// Service for role operations.
/// </summary>
public interface IRoleService
{
    /// <summary>
    /// Lists all security roles.
    /// </summary>
    /// <param name="filter">Optional filter string (matches name).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of role info.</returns>
    Task<ListResult<RoleInfo>> ListAsync(
        string? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific role by ID.
    /// </summary>
    /// <param name="roleId">The role ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The role info, or null if not found.</returns>
    Task<RoleInfo?> GetByIdAsync(
        Guid roleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific role by name.
    /// </summary>
    /// <param name="name">The role name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The role info, or null if not found.</returns>
    Task<RoleInfo?> GetByNameAsync(
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets users assigned to a role.
    /// </summary>
    /// <param name="roleId">The role ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of user info.</returns>
    Task<List<UserInfo>> GetRoleUsersAsync(
        Guid roleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Assigns a role to a user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="roleId">The role ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AssignRoleAsync(
        Guid userId,
        Guid roleId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a role from a user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="roleId">The role ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RemoveRoleAsync(
        Guid userId,
        Guid roleId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Security role information.
/// </summary>
public sealed record RoleInfo
{
    /// <summary>Gets the role ID.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the role name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the role description.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the business unit name.</summary>
    public string? BusinessUnitName { get; init; }

    /// <summary>Gets whether the role is managed.</summary>
    public bool IsManaged { get; init; }

    /// <summary>Gets whether the role is customizable.</summary>
    public bool? IsCustomizable { get; init; }

    /// <summary>Gets the created date.</summary>
    public DateTime? CreatedOn { get; init; }

    /// <summary>Gets the modified date.</summary>
    public DateTime? ModifiedOn { get; init; }
}
