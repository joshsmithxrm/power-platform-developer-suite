using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Pooling;

namespace PPDS.Dataverse.Services;

/// <summary>
/// Service for querying and managing Dataverse solutions.
/// </summary>
public class SolutionService : ISolutionService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly ILogger<SolutionService> _logger;

    /// <summary>
    /// Component type names for common component types.
    /// </summary>
    private static readonly Dictionary<int, string> ComponentTypeNames = new()
    {
        { 1, "Entity" },
        { 2, "Attribute" },
        { 3, "Relationship" },
        { 4, "AttributePicklistValue" },
        { 5, "AttributeLookupValue" },
        { 6, "ViewAttribute" },
        { 7, "LocalizedLabel" },
        { 8, "RelationshipExtraCondition" },
        { 9, "OptionSet" },
        { 10, "EntityRelationship" },
        { 11, "EntityRelationshipRole" },
        { 12, "EntityRelationshipRelationships" },
        { 13, "ManagedProperty" },
        { 14, "EntityKey" },
        { 16, "Privilege" },
        { 17, "PrivilegeObjectTypeCode" },
        { 18, "Index" },
        { 20, "Role" },
        { 21, "RolePrivilege" },
        { 22, "DisplayString" },
        { 23, "DisplayStringMap" },
        { 24, "Form" },
        { 25, "Organization" },
        { 26, "SavedQuery" },
        { 29, "Workflow" },
        { 31, "Report" },
        { 32, "ReportEntity" },
        { 33, "ReportCategory" },
        { 34, "ReportVisibility" },
        { 35, "Attachment" },
        { 36, "EmailTemplate" },
        { 37, "ContractTemplate" },
        { 38, "KBArticleTemplate" },
        { 39, "MailMergeTemplate" },
        { 44, "DuplicateRule" },
        { 45, "DuplicateRuleCondition" },
        { 46, "EntityMap" },
        { 47, "AttributeMap" },
        { 48, "RibbonCommand" },
        { 49, "RibbonContextGroup" },
        { 50, "RibbonCustomization" },
        { 52, "RibbonRule" },
        { 53, "RibbonTabToCommandMap" },
        { 55, "RibbonDiff" },
        { 59, "SavedQueryVisualization" },
        { 60, "SystemForm" },
        { 61, "WebResource" },
        { 62, "SiteMap" },
        { 63, "ConnectionRole" },
        { 64, "ComplexControl" },
        { 65, "HierarchyRule" },
        { 66, "CustomControl" },
        { 68, "CustomControlDefaultConfig" },
        { 70, "FieldSecurityProfile" },
        { 71, "FieldPermission" },
        { 90, "PluginType" },
        { 91, "PluginAssembly" },
        { 92, "SDKMessageProcessingStep" },
        { 93, "SDKMessageProcessingStepImage" },
        { 95, "ServiceEndpoint" },
        { 150, "RoutingRule" },
        { 151, "RoutingRuleItem" },
        { 152, "SLA" },
        { 153, "SLAItem" },
        { 154, "ConvertRule" },
        { 155, "ConvertRuleItem" },
        { 161, "MobileOfflineProfile" },
        { 162, "MobileOfflineProfileItem" },
        { 165, "SimilarityRule" },
        { 166, "DataSourceMapping" },
        { 201, "SDKMessage" },
        { 202, "SDKMessageFilter" },
        { 203, "SdkMessagePair" },
        { 204, "SdkMessageRequest" },
        { 205, "SdkMessageRequestField" },
        { 206, "SdkMessageResponse" },
        { 207, "SdkMessageResponseField" },
        { 208, "ImportMap" },
        { 210, "WebWizard" },
        { 300, "CanvasApp" },
        { 371, "Connector" },
        { 372, "Connector" },
        { 380, "EnvironmentVariableDefinition" },
        { 381, "EnvironmentVariableValue" },
        { 400, "AIProjectType" },
        { 401, "AIProject" },
        { 402, "AIConfiguration" },
        { 430, "EntityAnalyticsConfiguration" },
        { 431, "AttributeImageConfiguration" },
        { 432, "EntityImageConfiguration" }
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="SolutionService"/> class.
    /// </summary>
    /// <param name="pool">The connection pool.</param>
    /// <param name="logger">The logger.</param>
    public SolutionService(IDataverseConnectionPool pool, ILogger<SolutionService> logger)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<List<SolutionInfo>> ListAsync(
        string? filter = null,
        bool includeManaged = false,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression(Solution.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                Solution.Fields.SolutionId,
                Solution.Fields.UniqueName,
                Solution.Fields.FriendlyName,
                Solution.Fields.Version,
                Solution.Fields.IsManaged,
                Solution.Fields.PublisherId,
                Solution.Fields.Description,
                Solution.Fields.CreatedOn,
                Solution.Fields.ModifiedOn,
                Solution.Fields.InstalledOn),
            Orders = { new OrderExpression(Solution.Fields.FriendlyName, OrderType.Ascending) }
        };

        // Exclude managed unless requested
        if (!includeManaged)
        {
            query.Criteria.AddCondition(Solution.Fields.IsManaged, ConditionOperator.Equal, false);
        }

        // Exclude internal solutions (Default, Active, Basic)
        query.Criteria.AddCondition(Solution.Fields.IsVisible, ConditionOperator.Equal, true);

        // Apply filter if provided
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var filterCondition = new FilterExpression(LogicalOperator.Or);
            filterCondition.AddCondition(Solution.Fields.UniqueName, ConditionOperator.Contains, filter);
            filterCondition.AddCondition(Solution.Fields.FriendlyName, ConditionOperator.Contains, filter);
            query.Criteria.AddFilter(filterCondition);
        }

        // Add link to publisher for name
        var publisherLink = query.AddLink(
            Publisher.EntityLogicalName,
            Solution.Fields.PublisherId,
            Publisher.Fields.PublisherId,
            JoinOperator.LeftOuter);
        publisherLink.EntityAlias = "pub";
        publisherLink.Columns.AddColumn(Publisher.Fields.FriendlyName);

        _logger.LogDebug("Querying solutions with filter: {Filter}, includeManaged: {IncludeManaged}", filter, includeManaged);

        var results = await client.RetrieveMultipleAsync(query, cancellationToken);

        return results.Entities.Select(e => MapToSolutionInfo(e)).ToList();
    }

    /// <inheritdoc />
    public async Task<SolutionInfo?> GetAsync(string uniqueName, CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression(Solution.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                Solution.Fields.SolutionId,
                Solution.Fields.UniqueName,
                Solution.Fields.FriendlyName,
                Solution.Fields.Version,
                Solution.Fields.IsManaged,
                Solution.Fields.PublisherId,
                Solution.Fields.Description,
                Solution.Fields.CreatedOn,
                Solution.Fields.ModifiedOn,
                Solution.Fields.InstalledOn),
            TopCount = 1
        };

        query.Criteria.AddCondition(Solution.Fields.UniqueName, ConditionOperator.Equal, uniqueName);

        // Add link to publisher for name
        var publisherLink = query.AddLink(
            Publisher.EntityLogicalName,
            Solution.Fields.PublisherId,
            Publisher.Fields.PublisherId,
            JoinOperator.LeftOuter);
        publisherLink.EntityAlias = "pub";
        publisherLink.Columns.AddColumn(Publisher.Fields.FriendlyName);

        _logger.LogDebug("Getting solution: {UniqueName}", uniqueName);

        var results = await client.RetrieveMultipleAsync(query, cancellationToken);

        return results.Entities.FirstOrDefault() is { } entity ? MapToSolutionInfo(entity) : null;
    }

    /// <inheritdoc />
    public async Task<SolutionInfo?> GetByIdAsync(Guid solutionId, CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression(Solution.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                Solution.Fields.SolutionId,
                Solution.Fields.UniqueName,
                Solution.Fields.FriendlyName,
                Solution.Fields.Version,
                Solution.Fields.IsManaged,
                Solution.Fields.PublisherId,
                Solution.Fields.Description,
                Solution.Fields.CreatedOn,
                Solution.Fields.ModifiedOn,
                Solution.Fields.InstalledOn),
            TopCount = 1
        };

        query.Criteria.AddCondition(Solution.Fields.SolutionId, ConditionOperator.Equal, solutionId);

        // Add link to publisher for name
        var publisherLink = query.AddLink(
            Publisher.EntityLogicalName,
            Solution.Fields.PublisherId,
            Publisher.Fields.PublisherId,
            JoinOperator.LeftOuter);
        publisherLink.EntityAlias = "pub";
        publisherLink.Columns.AddColumn(Publisher.Fields.FriendlyName);

        _logger.LogDebug("Getting solution by ID: {SolutionId}", solutionId);

        var results = await client.RetrieveMultipleAsync(query, cancellationToken);

        return results.Entities.FirstOrDefault() is { } entity ? MapToSolutionInfo(entity) : null;
    }

    /// <inheritdoc />
    public async Task<List<SolutionComponentInfo>> GetComponentsAsync(
        Guid solutionId,
        int? componentType = null,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression(SolutionComponent.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                SolutionComponent.Fields.SolutionComponentId,
                SolutionComponent.Fields.ObjectId,
                SolutionComponent.Fields.ComponentType,
                SolutionComponent.Fields.RootComponentBehavior,
                SolutionComponent.Fields.IsMetadata),
            Orders = { new OrderExpression(SolutionComponent.Fields.ComponentType, OrderType.Ascending) }
        };

        query.Criteria.AddCondition(SolutionComponent.Fields.SolutionId, ConditionOperator.Equal, solutionId);

        if (componentType.HasValue)
        {
            query.Criteria.AddCondition(SolutionComponent.Fields.ComponentType, ConditionOperator.Equal, componentType.Value);
        }

        _logger.LogDebug("Getting components for solution: {SolutionId}, componentType: {ComponentType}", solutionId, componentType);

        var results = await client.RetrieveMultipleAsync(query, cancellationToken);

        return results.Entities.Select(e =>
        {
            var type = e.GetAttributeValue<OptionSetValue>(SolutionComponent.Fields.ComponentType)?.Value ?? 0;
            var typeName = ComponentTypeNames.TryGetValue(type, out var name) ? name : $"Unknown ({type})";

            return new SolutionComponentInfo(
                e.Id,
                e.GetAttributeValue<Guid>(SolutionComponent.Fields.ObjectId),
                type,
                typeName,
                e.GetAttributeValue<OptionSetValue>(SolutionComponent.Fields.RootComponentBehavior)?.Value ?? 0,
                e.GetAttributeValue<bool?>(SolutionComponent.Fields.IsMetadata) ?? false);
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<byte[]> ExportAsync(string uniqueName, bool managed = false, CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        _logger.LogInformation("Exporting solution: {UniqueName}, managed: {Managed}", uniqueName, managed);

        var request = new ExportSolutionRequest
        {
            SolutionName = uniqueName,
            Managed = managed
        };

        var response = (ExportSolutionResponse)await client.ExecuteAsync(request, cancellationToken);

        return response.ExportSolutionFile;
    }

    /// <inheritdoc />
    public async Task<Guid> ImportAsync(
        byte[] solutionZip,
        bool overwrite = true,
        bool publishWorkflows = true,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var importJobId = Guid.NewGuid();

        _logger.LogInformation("Importing solution, importJobId: {ImportJobId}, overwrite: {Overwrite}", importJobId, overwrite);

        var request = new ImportSolutionRequest
        {
            CustomizationFile = solutionZip,
            ImportJobId = importJobId,
            OverwriteUnmanagedCustomizations = overwrite,
            PublishWorkflows = publishWorkflows
        };

        await client.ExecuteAsync(request, cancellationToken);

        return importJobId;
    }

    /// <inheritdoc />
    public async Task PublishAllAsync(CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        _logger.LogInformation("Publishing all customizations");

        var request = new PublishAllXmlRequest();
        await client.ExecuteAsync(request, cancellationToken);
    }

    private static SolutionInfo MapToSolutionInfo(Entity entity)
    {
        var publisherName = entity.GetAttributeValue<AliasedValue>("pub." + Publisher.Fields.FriendlyName)?.Value as string;

        return new SolutionInfo(
            entity.Id,
            entity.GetAttributeValue<string>(Solution.Fields.UniqueName) ?? string.Empty,
            entity.GetAttributeValue<string>(Solution.Fields.FriendlyName) ?? string.Empty,
            entity.GetAttributeValue<string>(Solution.Fields.Version),
            entity.GetAttributeValue<bool?>(Solution.Fields.IsManaged) ?? false,
            publisherName,
            entity.GetAttributeValue<string>(Solution.Fields.Description),
            entity.GetAttributeValue<DateTime?>(Solution.Fields.CreatedOn),
            entity.GetAttributeValue<DateTime?>(Solution.Fields.ModifiedOn),
            entity.GetAttributeValue<DateTime?>(Solution.Fields.InstalledOn));
    }
}
