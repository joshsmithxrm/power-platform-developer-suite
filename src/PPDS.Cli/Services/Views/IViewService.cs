using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Dataverse.Models;

namespace PPDS.Cli.Services.Views;

/// <summary>
/// Service for managing Dataverse savedqueries views.
/// </summary>
public interface IViewService
{
    Task<ListResult<ViewInfo>> ListAsync(
        string entityLogicalName,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);

    /// <summary>Throws View.NotFound if the view does not exist; throws View.Ambiguous if name matches multiple records.</summary>
    Task<ViewDetail> GetAsync(
        string entityLogicalName,
        string viewName,
        bool unpublished = false,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);

    Task AddColumnAsync(
        string entityLogicalName, string viewName,
        IReadOnlyList<ColumnSpec> columns,
        string? viaRelationship = null,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);

    Task RemoveColumnAsync(
        string entityLogicalName, string viewName,
        string attributeName,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);

    Task UpdateColumnAsync(
        string entityLogicalName, string viewName,
        string attributeName, int width,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);

    Task ReorderColumnsAsync(
        string entityLogicalName, string viewName,
        IReadOnlyList<string> orderedAttributes,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);

    Task SetSortAsync(
        string entityLogicalName, string viewName,
        IReadOnlyList<ViewSortOrder> sorts,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);

    Task ClearSortAsync(
        string entityLogicalName, string viewName,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);

    Task SetFilterAsync(
        string entityLogicalName, string viewName,
        string filterXmlFragment,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);

    Task ClearFilterAsync(
        string entityLogicalName, string viewName,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);

    Task SetFetchXmlAsync(
        string entityLogicalName, string viewName,
        string fetchXml,
        bool publish = false, string? solution = null,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Summary information for a view in list views.
/// </summary>
public record ViewInfo(
    Guid Id,
    string Name,
    int QueryType,
    string QueryTypeLabel,
    bool IsManaged,
    DateTime? ModifiedOn);

/// <summary>
/// Full view details including columns, sort orders, and active filter.
/// </summary>
public record ViewDetail(
    Guid Id,
    string Name,
    int QueryType,
    string QueryTypeLabel,
    string EntityLogicalName,
    IReadOnlyList<ViewColumn> Columns,
    IReadOnlyList<ViewSortOrder> SortOrders,
    ViewFilter? ActiveFilter,
    string? FetchXml = null,
    string? LayoutXml = null);

/// <summary>
/// A column in a view's layoutxml.
/// </summary>
public record ViewColumn(
    string AttributeName,
    int Width,
    bool IsRelated = false,
    string? RelationshipAttribute = null,
    string? RelatedEntityName = null,
    string? RelatedEntityPrimaryKeyName = null);

/// <summary>
/// A sort order entry in a view's fetchxml.
/// </summary>
public record ViewSortOrder(string AttributeName, bool Descending);

/// <summary>
/// The active filter expression from a view's fetchxml.
/// </summary>
public record ViewFilter(string FetchXmlFragment);

/// <summary>
/// Specifies a column to add: attribute name and optional width.
/// </summary>
public record ColumnSpec(string AttributeName, int Width = 150);
