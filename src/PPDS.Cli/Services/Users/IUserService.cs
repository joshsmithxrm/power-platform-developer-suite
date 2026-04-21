using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Cli.Services.Roles;
using PPDS.Dataverse.Models;

namespace PPDS.Cli.Services.Users;

/// <summary>
/// Service for user operations.
/// </summary>
public interface IUserService
{
    /// <summary>
    /// Lists all users (system users).
    /// </summary>
    /// <param name="filter">Optional filter string (matches name, email, domain).</param>
    /// <param name="includeDisabled">Include disabled users.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of user info.</returns>
    Task<ListResult<UserInfo>> ListAsync(
        string? filter = null,
        bool includeDisabled = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific user by ID.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The user info, or null if not found.</returns>
    Task<UserInfo?> GetByIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific user by domain name (e.g., user@domain.com).
    /// </summary>
    /// <param name="domainName">The domain name of the user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The user info, or null if not found.</returns>
    Task<UserInfo?> GetByDomainNameAsync(
        string domainName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the roles assigned to a user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of role info.</returns>
    Task<List<RoleInfo>> GetUserRolesAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// User (system user) information.
/// </summary>
public sealed record UserInfo
{
    /// <summary>Gets the user ID.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the full name.</summary>
    public string? FullName { get; init; }

    /// <summary>Gets the first name.</summary>
    public string? FirstName { get; init; }

    /// <summary>Gets the last name.</summary>
    public string? LastName { get; init; }

    /// <summary>Gets the domain name (e.g., user@domain.com).</summary>
    public string? DomainName { get; init; }

    /// <summary>Gets the internal email address.</summary>
    public string? InternalEmailAddress { get; init; }

    /// <summary>Gets the job title.</summary>
    public string? JobTitle { get; init; }

    /// <summary>Gets whether the user is disabled.</summary>
    public bool IsDisabled { get; init; }

    /// <summary>Gets whether the user is licensed.</summary>
    public bool? IsLicensed { get; init; }

    /// <summary>Gets the access mode (ReadWrite, Administrative, Read, etc.).</summary>
    public string? AccessMode { get; init; }

    /// <summary>Gets the Azure AD object ID.</summary>
    public Guid? AzureActiveDirectoryObjectId { get; init; }

    /// <summary>Gets the business unit name.</summary>
    public string? BusinessUnitName { get; init; }

    /// <summary>Gets the created date.</summary>
    public DateTime? CreatedOn { get; init; }

    /// <summary>Gets the modified date.</summary>
    public DateTime? ModifiedOn { get; init; }
}
