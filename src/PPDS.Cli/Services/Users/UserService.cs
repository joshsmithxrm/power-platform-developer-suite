using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Services.Roles;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Models;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Services.Users;

/// <summary>
/// Service for user operations.
/// </summary>
public class UserService : IUserService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly ILogger<UserService> _logger;

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
    /// Initializes a new instance of the <see cref="UserService"/> class.
    /// </summary>
    /// <param name="pool">The connection pool.</param>
    /// <param name="logger">The logger.</param>
    public UserService(
        IDataverseConnectionPool pool,
        ILogger<UserService> logger)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ListResult<UserInfo>> ListAsync(
        string? filter = null,
        bool includeDisabled = false,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

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
                SystemUser.Fields.ModifiedOn),
            Orders = { new OrderExpression(SystemUser.Fields.FullName, OrderType.Ascending) }
        };

        var filtersApplied = new List<string>();

        // Exclude disabled users unless requested
        if (!includeDisabled)
        {
            query.Criteria.AddCondition(SystemUser.Fields.IsDisabled, ConditionOperator.Equal, false);
            filtersApplied.Add("enabled only");
        }

        // Apply filter
        if (!string.IsNullOrEmpty(filter))
        {
            var filterGroup = new FilterExpression(LogicalOperator.Or);
            filterGroup.AddCondition(SystemUser.Fields.FullName, ConditionOperator.Like, $"%{filter}%");
            filterGroup.AddCondition(SystemUser.Fields.DomainName, ConditionOperator.Like, $"%{filter}%");
            filterGroup.AddCondition(SystemUser.Fields.InternalEMailAddress, ConditionOperator.Like, $"%{filter}%");
            query.Criteria.AddFilter(filterGroup);
        }

        _logger.LogDebug("Querying users with filter: {Filter}, includeDisabled: {IncludeDisabled}", filter, includeDisabled);

        // Progressive paging — load all records
        var allItems = new List<UserInfo>();
        string? pagingCookie = null;
        int pageNumber = 1;

        while (true)
        {
            query.PageInfo = new PagingInfo
            {
                PageNumber = pageNumber,
                Count = 250,
                PagingCookie = pagingCookie
            };

            var result = await client.RetrieveMultipleAsync(query, cancellationToken);
            allItems.AddRange(result.Entities.Select(MapToUserInfo));

            if (!result.MoreRecords) break;

            pagingCookie = result.PagingCookie;
            pageNumber++;
        }

        return new ListResult<UserInfo>
        {
            Items = allItems,
            TotalCount = allItems.Count,
            FiltersApplied = filtersApplied
        };
    }

    /// <inheritdoc />
    public async Task<UserInfo?> GetByIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        try
        {
            var user = await client.RetrieveAsync(
                SystemUser.EntityLogicalName,
                userId,
                new ColumnSet(true),
                cancellationToken);

            return MapToUserInfo(user);
        }
        catch (Exception ex) when (ex.Message.Contains("does not exist"))
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<UserInfo?> GetByDomainNameAsync(
        string domainName,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression(SystemUser.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(true),
            TopCount = 1
        };
        query.Criteria.AddCondition(SystemUser.Fields.DomainName, ConditionOperator.Equal, domainName);

        var result = await client.RetrieveMultipleAsync(query, cancellationToken);
        return result.Entities.FirstOrDefault() is { } user ? MapToUserInfo(user) : null;
    }

    /// <inheritdoc />
    public async Task<List<RoleInfo>> GetUserRolesAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        // Query roles via the systemuserroles relationship
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
                Role.Fields.ModifiedOn)
        };

        var link = query.AddLink(
            SystemUserRoles.EntityLogicalName,
            Role.Fields.RoleId,
            SystemUserRoles.Fields.RoleId);
        link.LinkCriteria.AddCondition(SystemUserRoles.Fields.SystemUserId, ConditionOperator.Equal, userId);

        query.Orders.Add(new OrderExpression(Role.Fields.Name, OrderType.Ascending));

        var result = await client.RetrieveMultipleAsync(query, cancellationToken);
        return result.Entities.Select(MapToRoleInfo).ToList();
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
}
