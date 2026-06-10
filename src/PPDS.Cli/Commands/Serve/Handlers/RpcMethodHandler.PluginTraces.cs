using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Auth.Credentials;
using PPDS.Auth.Discovery;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.History;
using PPDS.Cli.Services.Profile;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Diagnostics;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using Authoring = PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Security;
using PPDS.Cli.Services.ConnectionReferences;
using PPDS.Cli.Services.DeploymentSettings;
using PPDS.Cli.Services.EnvironmentVariables;
using PPDS.Cli.Services.Flows;
using PPDS.Cli.Services.ImportJobs;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Services.PluginTraces;
using PPDS.Cli.Services.Solutions;
using PPDS.Cli.Services.WebResources;
using PPDS.Cli.Services;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Sql.Intellisense;
using PPDS.Query.Intellisense;
using PPDS.Query.Parsing;
using System.Threading;
using StreamJsonRpc;

// Aliases to disambiguate from local DTOs
using PluginTypeInfoModel = PPDS.Cli.Plugins.Registration.PluginTypeInfo;
using PluginImageInfoModel = PPDS.Cli.Plugins.Registration.PluginImageInfo;
using PluginAssemblyInfoModel = PPDS.Cli.Plugins.Registration.PluginAssemblyInfo;
using PluginPackageInfoModel = PPDS.Cli.Plugins.Registration.PluginPackageInfo;
using PluginStepInfoModel = PPDS.Cli.Plugins.Registration.PluginStepInfo;
using ConnRefRelationshipType = PPDS.Cli.Services.ConnectionReferences.RelationshipType;
using WebResourceInfoModel = PPDS.Cli.Services.WebResources.WebResourceInfo;

namespace PPDS.Cli.Commands.Serve.Handlers;

public partial class RpcMethodHandler
{
    #region Plugin Traces

    /// <summary>
    /// Lists plugin trace logs with optional filtering.
    /// Maps to: ppds plugintraces list --json
    /// </summary>
    [JsonRpcMethod("pluginTraces/list")]
    public async Task<PluginTracesListResponse> PluginTracesListAsync(
        TraceFilterDto? filter = null,
        int top = 100,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var traceService = sp.GetRequiredService<IPluginTraceService>();
            var serviceFilter = MapTraceFilterFromDto(filter);
            var result = await traceService.ListAsync(serviceFilter, top, ct);

            return new PluginTracesListResponse
            {
                TotalCount = result.TotalCount,
                Traces = result.Items.Select(MapTraceInfoToDto).ToList()
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets a single plugin trace with full details.
    /// Maps to: ppds plugintraces get --json
    /// </summary>
    [JsonRpcMethod("pluginTraces/get")]
    public async Task<PluginTracesGetResponse> PluginTracesGetAsync(
        string id,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var traceId))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'id' parameter must be a valid GUID");
        }

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var traceService = sp.GetRequiredService<IPluginTraceService>();
            var trace = await traceService.GetAsync(traceId, ct)
                ?? throw new RpcException(
                    ErrorCodes.Operation.NotFound,
                    $"Plugin trace '{id}' not found");

            return new PluginTracesGetResponse
            {
                Trace = MapTraceDetailToDto(trace)
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Builds a timeline hierarchy from traces with the given correlation ID.
    /// Maps to: ppds plugintraces timeline --json
    /// </summary>
    [JsonRpcMethod("pluginTraces/timeline")]
    public async Task<PluginTracesTimelineResponse> PluginTracesTimelineAsync(
        string correlationId,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(correlationId) || !Guid.TryParse(correlationId, out var corrId))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'correlationId' parameter must be a valid GUID");
        }

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var traceService = sp.GetRequiredService<IPluginTraceService>();
            var nodes = await traceService.BuildTimelineAsync(corrId, ct);

            return new PluginTracesTimelineResponse
            {
                Nodes = nodes.Select(MapTimelineNodeToDto).ToList()
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Deletes plugin traces by IDs, by age, or by filter.
    /// Maps to: ppds plugintraces delete --json
    /// </summary>
    [JsonRpcMethod("pluginTraces/delete")]
    public async Task<PluginTracesDeleteResponse> PluginTracesDeleteAsync(
        string[]? ids = null,
        int? olderThanDays = null,
        TraceFilterDto? filter = null,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        int modeCount = (ids != null && ids.Length > 0 ? 1 : 0)
            + (olderThanDays.HasValue ? 1 : 0)
            + (filter != null ? 1 : 0);
        if (modeCount == 0)
            throw new RpcException(ErrorCodes.Validation.RequiredField, "One of 'ids', 'olderThanDays', or 'filter' must be provided");
        if (modeCount > 1)
            throw new RpcException(ErrorCodes.Validation.RequiredField, "Only one of 'ids', 'olderThanDays', or 'filter' may be provided per call");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var traceService = sp.GetRequiredService<IPluginTraceService>();
            int deletedCount;

            if (ids != null && ids.Length > 0)
            {
                var guids = new List<Guid>(ids.Length);
                foreach (var idStr in ids)
                {
                    if (!Guid.TryParse(idStr, out var guid))
                    {
                        throw new RpcException(
                            ErrorCodes.Validation.InvalidValue,
                            $"The ID '{idStr}' is not a valid GUID");
                    }
                    guids.Add(guid);
                }

                deletedCount = await traceService.DeleteByIdsAsync(guids, cancellationToken: ct);
            }
            else if (olderThanDays != null)
            {
                var olderThan = TimeSpan.FromDays(olderThanDays.Value);
                deletedCount = await traceService.DeleteOlderThanAsync(olderThan, cancellationToken: ct);
            }
            else
            {
                var domainFilter = MapTraceFilterFromDto(filter) ?? new PluginTraceFilter();
                deletedCount = await traceService.DeleteByFilterAsync(domainFilter, cancellationToken: ct);
            }

            return new PluginTracesDeleteResponse
            {
                DeletedCount = deletedCount
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets the current plugin trace logging level.
    /// Maps to: ppds plugintraces tracelevel --json
    /// </summary>
    [JsonRpcMethod("pluginTraces/traceLevel")]
    public async Task<PluginTracesTraceLevelResponse> PluginTracesTraceLevelAsync(
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var traceService = sp.GetRequiredService<IPluginTraceService>();
            var settings = await traceService.GetSettingsAsync(ct);

            return new PluginTracesTraceLevelResponse
            {
                Level = settings.SettingName,
                LevelValue = (int)settings.Setting
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Sets the plugin trace logging level.
    /// Maps to: ppds plugintraces settracelevel --json
    /// </summary>
    [JsonRpcMethod("pluginTraces/setTraceLevel")]
    public async Task<PluginTracesSetTraceLevelResponse> PluginTracesSetTraceLevelAsync(
        string level,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'level' parameter is required");
        }

        if (!Enum.TryParse<PluginTraceLogSetting>(level, ignoreCase: true, out var setting))
        {
            throw new RpcException(
                ErrorCodes.Validation.InvalidValue,
                $"Invalid trace level '{level}'. Valid values are: Off, Exception, All");
        }

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var traceService = sp.GetRequiredService<IPluginTraceService>();
            await traceService.SetSettingsAsync(setting, ct);

            return new PluginTracesSetTraceLevelResponse
            {
                Success = true
            };
        }, cancellationToken);
    }

    // ── Plugin Traces mapper helpers ────────────────────────────────────────

    private static PluginTraceFilter? MapTraceFilterFromDto(TraceFilterDto? dto)
    {
        if (dto == null) return null;

        PluginTraceMode? mode = null;
        if (dto.Mode != null && Enum.TryParse<PluginTraceMode>(dto.Mode, ignoreCase: true, out var parsedMode))
        {
            mode = parsedMode;
        }

        return new PluginTraceFilter
        {
            TypeName = dto.TypeName,
            MessageName = dto.MessageName,
            PrimaryEntity = dto.PrimaryEntity,
            Mode = mode,
            HasException = dto.HasException,
            CorrelationId = dto.CorrelationId != null && Guid.TryParse(dto.CorrelationId, out var corrId) ? corrId : null,
            MinDurationMs = dto.MinDurationMs,
            CreatedAfter = dto.StartDate != null && DateTime.TryParse(dto.StartDate, null, System.Globalization.DateTimeStyles.RoundtripKind, out var startDate) ? startDate : null,
            CreatedBefore = dto.EndDate != null && DateTime.TryParse(dto.EndDate, null, System.Globalization.DateTimeStyles.RoundtripKind, out var endDate) ? endDate : null
        };
    }

    private static PluginTraceInfoDto MapTraceInfoToDto(PluginTraceInfo trace)
    {
        return new PluginTraceInfoDto
        {
            Id = trace.Id.ToString(),
            TypeName = trace.TypeName,
            MessageName = trace.MessageName,
            PrimaryEntity = trace.PrimaryEntity,
            Mode = trace.Mode == PluginTraceMode.Synchronous ? "Sync" : "Async",
            OperationType = trace.OperationType.ToString(),
            Depth = trace.Depth,
            CreatedOn = trace.CreatedOn.ToString("o"),
            DurationMs = trace.DurationMs,
            HasException = trace.HasException,
            CorrelationId = trace.CorrelationId?.ToString()
        };
    }

    private static PluginTraceDetailDto MapTraceDetailToDto(PluginTraceDetail detail)
    {
        return new PluginTraceDetailDto
        {
            Id = detail.Id.ToString(),
            TypeName = detail.TypeName,
            MessageName = detail.MessageName,
            PrimaryEntity = detail.PrimaryEntity,
            Mode = detail.Mode == PluginTraceMode.Synchronous ? "Sync" : "Async",
            OperationType = detail.OperationType.ToString(),
            Depth = detail.Depth,
            CreatedOn = detail.CreatedOn.ToString("o"),
            DurationMs = detail.DurationMs,
            HasException = detail.HasException,
            CorrelationId = detail.CorrelationId?.ToString(),
            ConstructorDurationMs = detail.ConstructorDurationMs,
            ExecutionStartTime = detail.ExecutionStartTime?.ToString("o"),
            ExceptionDetails = detail.ExceptionDetails,
            MessageBlock = detail.MessageBlock,
            Configuration = detail.Configuration,
            SecureConfiguration = detail.SecureConfiguration,
            RequestId = detail.RequestId?.ToString(),
            // Additional fields (PT-01 through PT-09)
            Stage = detail.OperationType.ToString(),
            ConstructorStartTime = detail.ConstructorStartTime?.ToString("o"),
            IsSystemCreated = detail.IsSystemCreated,
            CreatedById = detail.CreatedById?.ToString(),
            CreatedOnBehalfById = detail.CreatedOnBehalfById?.ToString(),
            PluginStepId = detail.PluginStepId?.ToString(),
            PersistenceKey = detail.PersistenceKey?.ToString(),
            OrganizationId = detail.OrganizationId?.ToString(),
            Profile = detail.Profile
        };
    }

    private static TimelineNodeDto MapTimelineNodeToDto(TimelineNode node)
    {
        return new TimelineNodeDto
        {
            TraceId = node.Trace.Id.ToString(),
            TypeName = node.Trace.TypeName,
            MessageName = node.Trace.MessageName,
            Depth = node.Trace.Depth,
            DurationMs = node.Trace.DurationMs,
            HasException = node.Trace.HasException,
            OffsetPercent = node.OffsetPercent,
            WidthPercent = node.WidthPercent,
            HierarchyDepth = node.HierarchyDepth,
            Children = node.Children.Select(MapTimelineNodeToDto).ToList()
        };
    }

    #endregion
}

public class PluginTracesListResponse
{
    [JsonPropertyName("traces")]
    public List<PluginTraceInfoDto> Traces { get; set; } = [];

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}

public class PluginTracesGetResponse
{
    [JsonPropertyName("trace")]
    public PluginTraceDetailDto Trace { get; set; } = null!;
}

public class PluginTracesTimelineResponse
{
    [JsonPropertyName("nodes")]
    public List<TimelineNodeDto> Nodes { get; set; } = [];
}

public class PluginTracesDeleteResponse
{
    [JsonPropertyName("deletedCount")]
    public int DeletedCount { get; set; }
}

public class PluginTracesTraceLevelResponse
{
    [JsonPropertyName("level")]
    public string Level { get; set; } = "";

    [JsonPropertyName("levelValue")]
    public int LevelValue { get; set; }
}

public class PluginTracesSetTraceLevelResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

public class PluginTraceInfoDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = "";

    [JsonPropertyName("messageName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageName { get; set; }

    [JsonPropertyName("primaryEntity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryEntity { get; set; }

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("operationType")]
    public string OperationType { get; set; } = "";

    [JsonPropertyName("depth")]
    public int Depth { get; set; }

    [JsonPropertyName("createdOn")]
    public string CreatedOn { get; set; } = "";

    [JsonPropertyName("durationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DurationMs { get; set; }

    [JsonPropertyName("hasException")]
    public bool HasException { get; set; }

    [JsonPropertyName("correlationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; set; }
}

public class PluginTraceDetailDto : PluginTraceInfoDto
{
    [JsonPropertyName("constructorDurationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ConstructorDurationMs { get; set; }

    [JsonPropertyName("executionStartTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutionStartTime { get; set; }

    [JsonPropertyName("exceptionDetails")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExceptionDetails { get; set; }

    [JsonPropertyName("messageBlock")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageBlock { get; set; }

    [JsonPropertyName("configuration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Configuration { get; set; }

    [JsonPropertyName("secureConfiguration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SecureConfiguration { get; set; }

    [JsonPropertyName("requestId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestId { get; set; }

    // Additional fields (PT-01 through PT-09)
    [JsonPropertyName("stage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stage { get; set; }

    [JsonPropertyName("constructorStartTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConstructorStartTime { get; set; }

    [JsonPropertyName("isSystemCreated")]
    public bool IsSystemCreated { get; set; }

    [JsonPropertyName("createdById")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedById { get; set; }

    [JsonPropertyName("createdOnBehalfById")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedOnBehalfById { get; set; }

    [JsonPropertyName("pluginStepId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PluginStepId { get; set; }

    [JsonPropertyName("persistenceKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PersistenceKey { get; set; }

    [JsonPropertyName("organizationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OrganizationId { get; set; }

    [JsonPropertyName("profile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Profile { get; set; }
}

public class TimelineNodeDto
{
    [JsonPropertyName("traceId")]
    public string TraceId { get; set; } = "";

    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = "";

    [JsonPropertyName("messageName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageName { get; set; }

    [JsonPropertyName("depth")]
    public int Depth { get; set; }

    [JsonPropertyName("durationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DurationMs { get; set; }

    [JsonPropertyName("hasException")]
    public bool HasException { get; set; }

    [JsonPropertyName("offsetPercent")]
    public double OffsetPercent { get; set; }

    [JsonPropertyName("widthPercent")]
    public double WidthPercent { get; set; }

    [JsonPropertyName("hierarchyDepth")]
    public int HierarchyDepth { get; set; }

    [JsonPropertyName("children")]
    public List<TimelineNodeDto> Children { get; set; } = [];
}

public class TraceFilterDto
{
    [JsonPropertyName("typeName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TypeName { get; set; }

    [JsonPropertyName("messageName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageName { get; set; }

    [JsonPropertyName("primaryEntity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryEntity { get; set; }

    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }

    [JsonPropertyName("hasException")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasException { get; set; }

    [JsonPropertyName("correlationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("minDurationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinDurationMs { get; set; }

    [JsonPropertyName("startDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StartDate { get; set; }

    [JsonPropertyName("endDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EndDate { get; set; }
}

// ── Metadata DTOs ────────────────────────────────────────────────────────
