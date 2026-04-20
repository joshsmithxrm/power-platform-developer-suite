using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.Users;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Models;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Services.Roles;

/// <summary>
/// Service for role operations.
/// </summary>
public class RoleService : IRoleService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly IShakedownGuard _guard;
    private readonly ILogger<RoleService> _logger;

    private static readonly Dictionary<int, string> AccessModeNames = new()
    {
        { 0, "Read-Write" },
        { 1, "Administrative" },
        { 2, "Read" },
        { 3, "Support User" },
        { 4, "Non-interactive" },
        { 5, "Delegated Admin" }
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="RoleService"/> class.
    /// </summary>
    /// <param name="pool">The connection pool.</param>
    /// <param name="guard">The shakedown guard.</param>
    /// <param name="logger">The logger.</param>
    public RoleService(
        IDataverseConnectionPool pool,
        IShakedownGuard guard,
        ILogger<RoleService> logger)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ListResult<RoleInfo>> ListAsync(
        string? filter = null,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression(Role.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                Role.Fields.RoleId,
                Role.Fields.Name,
                Role.Fields.Description,
                Role.Fields.BusinessUnitId,
                Role.Fields.IsManaged,
                Role.Fields.IsCustomizable,
                Role.Fields.CreatedOn,
                Role.Fields.ModifiedOn),
            Orders = { new OrderExpression(Role.Fields.Name, OrderType.Ascending) }
        };

        // Only get root roles (not inherited business unit copies)
        query.Criteria.AddCondition(Role.Fields.ParentRoleId, ConditionOperator.Null);

        // Apply filter
        if (!string.IsNullOrEmpty(filter))
        {
            query.Criteria.AddCondition(Role.Fields.Name, ConditionOperator.Like, $"%{filter}%");
        }

        _logger.LogDebug("Querying roles with filter: {Filter}", filter);
        var result = await client.RetrieveMultipleAsync(query, cancellationToken);

        var items = result.Entities.Select(MapToRoleInfo).ToList();

        return new ListResult<RoleInfo>
        {
            Items = items,
            TotalCount = items.Count,
            FiltersApplied = ["root roles only"]
        };
    }

    /// <inheritdoc />
    public async Task<RoleInfo?> GetByIdAsync(
        Guid roleId,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        try
        {
            var role = await client.RetrieveAsync(
                Role.EntityLogicalName,
                roleId,
                new ColumnSet(true),
                cancellationToken);

            return MapToRoleInfo(role);
        }
        catch (Exception ex) when (ex.Message.Contains("does not exist"))
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<RoleInfo?> GetByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression(Role.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(true),
            TopCount = 1
        };
        query.Criteria.AddCondition(Role.Fields.Name, ConditionOperator.Equal, name);
        // Only get root roles (not inherited business unit copies)
        query.Criteria.AddCondition(Role.Fields.ParentRoleId, ConditionOperator.Null);

        var result = await client.RetrieveMultipleAsync(query, cancellationToken);
        return result.Entities.FirstOrDefault() is { } role ? MapToRoleInfo(role) : null;
    }

    /// <inheritdoc />
    public async Task<List<UserInfo>> GetRoleUsersAsync(
        Guid roleId,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        // Query users via the systemuserroles relationship
        var query = new QueryExpression(SystemUser.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                SystemUser.Fields.SystemUserId,
                SystemUser.Fields.FullName,
                SystemUser.Fields.FirstName,
                SystemUser.Fields.LastName,
                SystemUser.Fields.DomainName,
                SystemUser.Fields.InternalEMailAddress,
                SystemUser.Fields.JobTitle,
                SystemUser.Fields.IsDisabled,
                SystemUser.Fields.IsLicensed,
                SystemUser.Fields.AccessMode,
                SystemUser.Fields.AzureActiveDirectoryObjectId,
                SystemUser.Fields.BusinessUnitId,
                SystemUser.Fields.CreatedOn,
                SystemUser.Fields.ModifiedOn)
        };

        var link = query.AddLink(
            SystemUserRoles.EntityLogicalName,
            SystemUser.Fields.SystemUserId,
            SystemUserRoles.Fields.SystemUserId);
        link.LinkCriteria.AddCondition(SystemUserRoles.Fields.RoleId, ConditionOperator.Equal, roleId);

        query.Orders.Add(new OrderExpression(SystemUser.Fields.FullName, OrderType.Ascending));

        var result = await client.RetrieveMultipleAsync(query, cancellationToken);
        return result.Entities.Select(MapToUserInfo).ToList();
    }

    /// <inheritdoc />
    public async Task AssignRoleAsync(
        Guid userId,
        Guid roleId,
        CancellationToken cancellationToken = default)
    {
        _guard.EnsureCanMutate("roles.assignRole");

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        // Use Associate to add the role to the user
        var relationship = new Relationship("systemuserroles_association");
        var roleRefs = new EntityReferenceCollection
        {
            new EntityReference(Role.EntityLogicalName, roleId)
        };

        _logger.LogDebug("Assigning role {RoleId} to user {UserId}", roleId, userId);
        await client.AssociateAsync(
            SystemUser.EntityLogicalName,
            userId,
            relationship,
            roleRefs,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task RemoveRoleAsync(
        Guid userId,
        Guid roleId,
        CancellationToken cancellationToken = default)
    {
        _guard.EnsureCanMutate("roles.removeRole");

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        // Use Disassociate to remove the role from the user
        var relationship = new Relationship("systemuserroles_association");
        var roleRefs = new EntityReferenceCollection
        {
            new EntityReference(Role.EntityLogicalName, roleId)
        };

        _logger.LogDebug("Removing role {RoleId} from user {UserId}", roleId, userId);
        await client.DisassociateAsync(
            SystemUser.EntityLogicalName,
            userId,
            relationship,
            roleRefs,
            cancellationToken);
    }

    private static RoleInfo MapToRoleInfo(Entity role)
    {
        var businessUnit = role.GetAttributeValue<EntityReference>(Role.Fields.BusinessUnitId);
        var isCustomizable = role.GetAttributeValue<BooleanManagedProperty>(Role.Fields.IsCustomizable);

        return new RoleInfo
        {
            Id = role.Id,
            Name = role.GetAttributeValue<string>(Role.Fields.Name) ?? string.Empty,
            Description = role.GetAttributeValue<string>(Role.Fields.Description),
            BusinessUnitName = businessUnit?.Name,
            IsManaged = role.GetAttributeValue<bool>(Role.Fields.IsManaged),
            IsCustomizable = isCustomizable?.Value,
            CreatedOn = role.GetAttributeValue<DateTime?>(Role.Fields.CreatedOn),
            ModifiedOn = role.GetAttributeValue<DateTime?>(Role.Fields.ModifiedOn)
        };
    }

    private static UserInfo MapToUserInfo(Entity user)
    {
        var accessModeValue = user.GetAttributeValue<OptionSetValue>(SystemUser.Fields.AccessMode)?.Value;
        var businessUnit = user.GetAttributeValue<EntityReference>(SystemUser.Fields.BusinessUnitId);

        return new UserInfo
        {
            Id = user.Id,
            FullName = user.GetAttributeValue<string>(SystemUser.Fields.FullName),
            FirstName = user.GetAttributeValue<string>(SystemUser.Fields.FirstName),
            LastName = user.GetAttributeValue<string>(SystemUser.Fields.LastName),
            DomainName = user.GetAttributeValue<string>(SystemUser.Fields.DomainName),
            InternalEmailAddress = user.GetAttributeValue<string>(SystemUser.Fields.InternalEMailAddress),
            JobTitle = user.GetAttributeValue<string>(SystemUser.Fields.JobTitle),
            IsDisabled = user.GetAttributeValue<bool>(SystemUser.Fields.IsDisabled),
            IsLicensed = user.GetAttributeValue<bool?>(SystemUser.Fields.IsLicensed),
            AccessMode = accessModeValue.HasValue && AccessModeNames.TryGetValue(accessModeValue.Value, out var name)
                ? name
                : accessModeValue?.ToString(),
            AzureActiveDirectoryObjectId = user.GetAttributeValue<Guid?>(SystemUser.Fields.AzureActiveDirectoryObjectId),
            BusinessUnitName = businessUnit?.Name,
            CreatedOn = user.GetAttributeValue<DateTime?>(SystemUser.Fields.CreatedOn),
            ModifiedOn = user.GetAttributeValue<DateTime?>(SystemUser.Fields.ModifiedOn)
        };
    }
}
