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
    #region Query Methods

    /// <summary>
    /// Gets completion items at a given cursor position for SQL or FetchXML.
    /// Used by VS Code extension for IntelliSense in the query editor.
    /// </summary>
    [JsonRpcMethod("query/complete")]
    public async Task<QueryCompleteResponse> QueryCompleteAsync(
        QueryCompleteRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(request.Sql))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'sql' parameter is required");
        }
        if (request.CursorOffset < 0 || request.CursorOffset > request.Sql.Length)
        {
            throw new RpcException(
                ErrorCodes.Validation.InvalidArguments,
                $"cursorOffset must be between 0 and {request.Sql.Length}");
        }

        return await WithProfileAndEnvironmentAsync(request.ProfileName, request.EnvironmentUrl, async (sp, ct) =>
        {
            var metadataProvider = sp.GetRequiredService<ICachedMetadataProvider>();

            IReadOnlyList<PPDS.Dataverse.Sql.Intellisense.SqlCompletion> completions;

            if (string.Equals(request.Language, "fetchxml", StringComparison.OrdinalIgnoreCase))
            {
                var engine = new FetchXmlCompletionEngine(metadataProvider);
                completions = await engine.GetCompletionsAsync(request.Sql, request.CursorOffset, ct);
            }
            else
            {
                var engine = new SqlCompletionEngine(metadataProvider);
                completions = await engine.GetCompletionsAsync(request.Sql, request.CursorOffset, ct);
            }

            return new QueryCompleteResponse
            {
                Items = completions.Select(c => new CompletionItemDto
                {
                    Label = c.Label,
                    InsertText = c.InsertText,
                    Kind = c.Kind.ToString().ToLowerInvariant(),
                    Detail = c.Detail,
                    Description = c.Description,
                    SortOrder = c.SortOrder
                }).ToList()
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Validates SQL text and returns structured diagnostics for editor squiggles.
    /// Parse-only mode (no metadata required) — does not connect to a profile/environment.
    /// FetchXML validation is a non-goal: returns empty diagnostics for language=="xml".
    /// </summary>
    [JsonRpcMethod("query/validate")]
    public async Task<QueryValidateResponse> QueryValidateAsync(
        QueryValidateRequest request,
        CancellationToken cancellationToken = default)
    {
        // FetchXML validation is a non-goal — return empty diagnostics.
        if (string.Equals(request.Language, "xml", StringComparison.OrdinalIgnoreCase))
        {
            return new QueryValidateResponse();
        }

        // Short-circuit on empty/whitespace or trivially short input.
        if (string.IsNullOrWhiteSpace(request.Sql) || request.Sql.Trim().Length < 3)
        {
            return new QueryValidateResponse();
        }

        // Parse-only mode: no metadata provider, no environment connection required.
        var validator = new SqlValidator(metadataProvider: null);
        var diagnostics = await validator.ValidateAsync(request.Sql, cancellationToken);

        return new QueryValidateResponse
        {
            Diagnostics = diagnostics.Select(d => new DiagnosticDto
            {
                Start = d.Start,
                Length = d.Length,
                Severity = d.Severity.ToString().ToLowerInvariant(),
                Message = d.Message,
            }).ToList(),
        };
    }

    /// <summary>
    /// Executes a FetchXML query against Dataverse.
    /// Maps to: ppds query fetch --json
    /// </summary>
    [JsonRpcMethod("query/fetch")]
    public async Task<QueryResultResponse> QueryFetchAsync(
        QueryFetchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.FetchXml))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'fetchXml' parameter is required");
        }

        // Inject top attribute if specified
        var query = request.FetchXml;
        if (request.Top.HasValue)
        {
            query = InjectTopAttribute(query, request.Top.Value);
        }

        var response = await WithProfileAndEnvironmentAsync(request.ProfileName, request.EnvironmentUrl, async (sp, ct) =>
        {
            var queryExecutor = sp.GetRequiredService<IQueryExecutor>();
            var result = await queryExecutor.ExecuteFetchXmlAsync(
                query,
                request.Page,
                request.PagingCookie,
                request.Count,
                ct);

            var mapped = MapToResponse(result, query);
            mapped.QueryMode = "dataverse";
            return mapped;
        }, cancellationToken);

        // Auto-save to history (fire-and-forget)
        FireAndForgetHistorySave(request.FetchXml, response);

        return response;
    }

    /// <summary>
    /// Executes a SQL query against Dataverse by transpiling to FetchXML.
    /// Delegates to <see cref="SqlQueryService"/> for the shared transpile-and-execute pipeline.
    /// Maps to: ppds query sql --json
    /// </summary>
    [JsonRpcMethod("query/sql")]
    public async Task<QueryResultResponse> QuerySqlAsync(
        QuerySqlRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Sql))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'sql' parameter is required");
        }

        // Execute (or transpile-only) via SqlQueryService from the environment's service provider
        var response = await WithProfileAndEnvironmentAsync(request.ProfileName, request.EnvironmentUrl, async (sp, profile, env, ct) =>
        {
            var service = sp.GetRequiredService<ISqlQueryService>();

            // ShowFetchXml mode: transpile only, no execution needed
            if (request.ShowFetchXml)
            {
                try
                {
                    var fetchXml = service.TranspileSql(request.Sql, request.Top);
                    return new QueryResultResponse
                    {
                        Success = true,
                        ExecutedFetchXml = fetchXml,
                        QueryMode = "dataverse"
                    };
                }
                catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Query.ParseError)
                {
                    var diagnostics = ExtractParseDiagnostics(ex, request.Sql);
                    if (diagnostics is { Count: > 0 })
                    {
                        throw new RpcException(
                            ErrorCodes.Query.ParseError,
                            ex.UserMessage,
                            new RpcErrorData
                            {
                                Code = ErrorCodes.Query.ParseError,
                                Message = ex.UserMessage,
                                Diagnostics = diagnostics,
                            });
                    }
                    throw new RpcException(ErrorCodes.Query.ParseError, ex.UserMessage);
                }
            }

            // Wire cross-environment support and DML safety (mirrors InteractiveSession pattern)
            if (service is SqlQueryService concrete)
            {
                var configStore = _authServices.GetRequiredService<EnvironmentConfigStore>();
                configStore.ClearCache();
                var configCollection = await configStore.LoadAsync(ct);

                // ProfileResolutionService rejects duplicate labels — gracefully degrade
                // to no cross-env support if the user's config has duplicates
                try
                {
                    var profileResolution = new ProfileResolutionService(configCollection.Environments);

                    concrete.RemoteExecutorFactory = label =>
                    {
                        var config = profileResolution.ResolveByLabel(label);
                        if (config?.Url == null) return null;
#pragma warning disable PPDS012
                        // Planner calls this factory synchronously; on cache hit the task is
                        // already completed so .GetResult() is free.  On first cache miss the
                        // pool creation performs async I/O (auth / device-code).  Wrapping in
                        // Task.Run avoids blocking the RPC handler's async context thread,
                        // which would be a thread-pool starvation risk under concurrent requests.
                        var remoteProfileName = profile.Name ?? profile.DisplayIdentifier;
                        var remoteProvider = Task.Run(() => _poolManager.GetOrCreateServiceProviderAsync(
                            new[] { remoteProfileName },
                            config.Url,
                            deviceCodeCallback: DaemonDeviceCodeHandler.CreateCallback(_rpc, remoteProfileName),
                            cancellationToken: ct)).GetAwaiter().GetResult();
#pragma warning restore PPDS012
                        return remoteProvider.GetRequiredService<IQueryExecutor>();
                    };

                    concrete.ProfileResolver = profileResolution;
                }
                catch (ArgumentException)
                {
                    // Duplicate labels or reserved label — cross-env queries won't work
                    // but single-env queries proceed normally
                    _logger.LogWarning("Environment config has duplicate or reserved labels — cross-environment queries disabled");
                }

                var envConfig = await configStore.GetConfigAsync(env.Url, ct);
                if (envConfig != null)
                {
                    concrete.EnvironmentSafetySettings = envConfig.SafetySettings;
                    var envType = envConfig.Type ?? PPDS.Auth.Profiles.EnvironmentType.Unknown;
                    concrete.EnvironmentProtectionLevel = envConfig.Protection
                        ?? DmlSafetyGuard.DetectProtectionLevel(envType);
                }
            }

            // Build the shared request
            var sqlRequest = new SqlQueryRequest
            {
                Sql = request.Sql,
                TopOverride = request.Top,
                PageNumber = request.Page,
                PagingCookie = request.PagingCookie,
                IncludeCount = request.Count,
                UseTdsEndpoint = request.UseTds,
                DmlSafety = request.DmlSafety != null
                    ? new DmlSafetyOptions
                    {
                        IsConfirmed = request.DmlSafety.IsConfirmed,
                        IsDryRun = request.DmlSafety.IsDryRun,
                        NoLimit = request.DmlSafety.NoLimit,
                        RowCap = request.DmlSafety.RowCap,
                    }
                    : null,
            };

            try
            {
                var result = await service.ExecuteAsync(sqlRequest, ct);
                var mapped = MapToResponse(result.Result, result.TranspiledFetchXml);
                mapped.QueryMode = result.ExecutionMode switch
                {
                    QueryExecutionMode.Tds => "tds",
                    _ => "dataverse"
                };

                if (result.DataSources is { Count: > 1 })
                {
                    mapped.DataSources = result.DataSources
                        .Select(ds => new QueryDataSourceDto { Label = ds.Label, IsRemote = ds.IsRemote })
                        .ToList();
                }

                if (result.AppliedHints is { Count: > 0 })
                {
                    mapped.AppliedHints = result.AppliedHints.ToList();
                }

                return mapped;
            }
            catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Query.ParseError)
            {
                var diagnostics = ExtractParseDiagnostics(ex, request.Sql);
                if (diagnostics is { Count: > 0 })
                {
                    throw new RpcException(
                        ErrorCodes.Query.ParseError,
                        ex.UserMessage,
                        new RpcErrorData
                        {
                            Code = ErrorCodes.Query.ParseError,
                            Message = ex.UserMessage,
                            Diagnostics = diagnostics,
                        });
                }
                throw new RpcException(ErrorCodes.Query.ParseError, ex.UserMessage);
            }
            catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Query.DmlConfirmationRequired)
            {
                throw new RpcException(
                    ErrorCodes.Query.DmlConfirmationRequired,
                    ex.UserMessage,
                    new DmlSafetyErrorData
                    {
                        Code = ErrorCodes.Query.DmlConfirmationRequired,
                        Message = ex.UserMessage,
                        DmlConfirmationRequired = true,
                    });
            }
            catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Query.DmlBlocked)
            {
                throw new RpcException(
                    ErrorCodes.Query.DmlBlocked,
                    ex.UserMessage,
                    new DmlSafetyErrorData
                    {
                        Code = ErrorCodes.Query.DmlBlocked,
                        Message = ex.UserMessage,
                        DmlBlocked = true,
                    });
            }
            catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Query.TdsIncompatible)
            {
                throw new RpcException(ErrorCodes.Query.TdsIncompatible, ex.UserMessage);
            }
            catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Query.TdsConnectionFailed)
            {
                throw new RpcException(ErrorCodes.Query.TdsConnectionFailed, ex.UserMessage);
            }
            catch (PpdsException ex)
            {
                throw MapPpdsToRpcException(ex);
            }
        }, cancellationToken);

        // Auto-save to history (fire-and-forget)
        FireAndForgetHistorySave(request.Sql, response);

        return response;
    }

    /// <summary>
    /// Lists query history entries for the active environment.
    /// Maps to: ppds query history list --json
    /// </summary>
    [JsonRpcMethod("query/history/list")]
    public async Task<QueryHistoryListResponse> QueryHistoryListAsync(
        QueryHistoryListRequest request,
        CancellationToken cancellationToken = default)
    {
        var limit = Math.Min(request.Limit, 1000);
        var store = _authServices.GetRequiredService<ProfileStore>();
        var collection = await store.LoadAsync(cancellationToken);

        var profile = collection.ActiveProfile
            ?? throw new RpcException(ErrorCodes.Auth.NoActiveProfile, "No active profile configured");

        var environment = profile.Environment
            ?? throw new RpcException(
                ErrorCodes.Connection.EnvironmentNotFound,
                "No environment selected. Use env/select first.");

        var historyService = _authServices.GetRequiredService<IQueryHistoryService>();

        IReadOnlyList<QueryHistoryEntry> entries;
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            entries = await historyService.SearchHistoryAsync(environment.Url, request.Search, limit, cancellationToken);
        }
        else
        {
            entries = await historyService.GetHistoryAsync(environment.Url, limit, cancellationToken);
        }

        return new QueryHistoryListResponse
        {
            Entries = entries.Select(e => new QueryHistoryEntryDto
            {
                Id = e.Id,
                Sql = e.Sql,
                RowCount = e.RowCount,
                ExecutionTimeMs = e.ExecutionTimeMs,
                EnvironmentUrl = environment.Url,
                ExecutedAt = e.ExecutedAt
            }).ToList()
        };
    }

    /// <summary>
    /// Deletes a query history entry by ID.
    /// Maps to: ppds query history delete --json
    /// </summary>
    [JsonRpcMethod("query/history/delete")]
    public async Task<QueryHistoryDeleteResponse> QueryHistoryDeleteAsync(
        QueryHistoryDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'id' parameter is required");
        }

        var store = _authServices.GetRequiredService<ProfileStore>();
        var collection = await store.LoadAsync(cancellationToken);

        var profile = collection.ActiveProfile
            ?? throw new RpcException(ErrorCodes.Auth.NoActiveProfile, "No active profile configured");

        var environment = profile.Environment
            ?? throw new RpcException(
                ErrorCodes.Connection.EnvironmentNotFound,
                "No environment selected. Use env/select first.");

        var historyService = _authServices.GetRequiredService<IQueryHistoryService>();
        var deleted = await historyService.DeleteEntryAsync(environment.Url, request.Id, cancellationToken);

        return new QueryHistoryDeleteResponse
        {
            Deleted = deleted
        };
    }

    /// <summary>
    /// Exports query results in the specified format (CSV, TSV, or JSON).
    /// Reuses the same SQL-to-FetchXML transpilation and execution pipeline as QuerySqlAsync.
    /// </summary>
    [JsonRpcMethod("query/export")]
    public async Task<QueryExportResponse> QueryExportAsync(
        QueryExportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Sql) && string.IsNullOrWhiteSpace(request.FetchXml))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "Either 'sql' or 'fetchXml' parameter is required");
        }

        var format = request.Format.ToLowerInvariant();
        if (format is not ("csv" or "tsv" or "json"))
        {
            throw new RpcException(
                ErrorCodes.Validation.InvalidArguments,
                $"Invalid format '{format}'. Valid values: csv, tsv, json");
        }

        // Execute the query
        const int MaxExportRecords = 100_000;

        var queryResponse = await WithProfileAndEnvironmentAsync(request.ProfileName, request.EnvironmentUrl, async (sp, ct) =>
        {
            // Use FetchXML directly if provided, otherwise transpile SQL via SqlQueryService
            string fetchXml;
            if (!string.IsNullOrWhiteSpace(request.FetchXml))
            {
                fetchXml = request.FetchXml;
            }
            else
            {
                var sqlService = sp.GetRequiredService<ISqlQueryService>();
                try
                {
                    fetchXml = sqlService.TranspileSql(request.Sql, request.Top);
                }
                catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Query.ParseError)
                {
                    throw new RpcException(ErrorCodes.Query.ParseError, ex.UserMessage);
                }
            }

            var queryExecutor = sp.GetRequiredService<IQueryExecutor>();

            // Fetch all pages to export complete results
            var allRecords = new List<Dictionary<string, object?>>();
            List<QueryColumnInfo>? columns = null;
            string? pagingCookie = null;
            int? currentPage = null;
            bool moreRecords;

            do
            {
                var result = await queryExecutor.ExecuteFetchXmlAsync(
                    fetchXml,
                    currentPage,
                    pagingCookie,
                    false,
                    ct);

                var mapped = MapToResponse(result, fetchXml);
                columns ??= mapped.Columns;

                allRecords.AddRange(mapped.Records);
                pagingCookie = mapped.PagingCookie;
                moreRecords = mapped.MoreRecords;
                currentPage = (currentPage ?? 1) + 1;

                if (allRecords.Count >= MaxExportRecords)
                {
                    break; // Safety cap to prevent OOM on very large exports
                }
            } while (moreRecords);

            return (columns: columns ?? [], records: allRecords);
        }, cancellationToken);

        // Format the results
        var content = FormatExportContent(
            queryResponse.columns,
            queryResponse.records,
            format,
            request.IncludeHeaders);

        return new QueryExportResponse
        {
            Content = content,
            Format = format,
            RowCount = queryResponse.records.Count
        };
    }

    /// <summary>
    /// Returns the execution plan for a SQL query.
    /// Builds a full plan tree showing node types, descriptions, and estimated row counts.
    /// Falls back to transpiled FetchXML if plan building fails.
    /// </summary>
    [JsonRpcMethod("query/explain")]
    public async Task<QueryExplainResponse> QueryExplainAsync(
        QueryExplainRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Sql))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'sql' parameter is required");
        }

        return await WithProfileAndEnvironmentAsync(request.ProfileName, request.EnvironmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<ISqlQueryService>();

            try
            {
                var description = await service.ExplainAsync(request.Sql, ct);
                var formatted = Tui.Components.QueryPlanView.FormatPlanTree(description);
                var fetchXml = service.TranspileSql(request.Sql);

                return new QueryExplainResponse
                {
                    Plan = formatted + "\n\n--- FetchXML ---\n" + fetchXml,
                    Format = "text",
                    FetchXml = fetchXml
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is PpdsException or QueryExecutionException or QueryParseException)
            {
                _logger.LogWarning(ex, "Query plan building failed, falling back to FetchXML");
                // Fall back to just the transpiled FetchXML if plan building fails
                try
                {
                    var fetchXml = service.TranspileSql(request.Sql);
                    return new QueryExplainResponse
                    {
                        Plan = fetchXml,
                        Format = "fetchxml",
                        FetchXml = fetchXml
                    };
                }
                catch (PpdsException parseEx) when (parseEx.ErrorCode == ErrorCodes.Query.ParseError)
                {
                    throw new RpcException(ErrorCodes.Query.ParseError, parseEx.UserMessage);
                }
            }
        }, cancellationToken);
    }

    #endregion
}

/// <summary>
/// Response for query/fetch and query/sql methods.
/// </summary>
public class QueryResultResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("entityName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntityName { get; set; }

    [JsonPropertyName("columns")]
    public List<QueryColumnInfo> Columns { get; set; } = [];

    [JsonPropertyName("records")]
    public List<Dictionary<string, object?>> Records { get; set; } = [];

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("totalCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TotalCount { get; set; }

    [JsonPropertyName("moreRecords")]
    public bool MoreRecords { get; set; }

    [JsonPropertyName("pagingCookie")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PagingCookie { get; set; }

    [JsonPropertyName("pageNumber")]
    public int PageNumber { get; set; }

    [JsonPropertyName("isAggregate")]
    public bool IsAggregate { get; set; }

    [JsonPropertyName("executedFetchXml")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutedFetchXml { get; set; }

    [JsonPropertyName("executionTimeMs")]
    public long ExecutionTimeMs { get; set; }

    [JsonPropertyName("queryMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QueryMode { get; set; }

    [JsonPropertyName("dataSources")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<QueryDataSourceDto>? DataSources { get; set; }

    [JsonPropertyName("appliedHints")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AppliedHints { get; set; }
}

/// <summary>
/// Data source information in query results (for cross-env queries).
/// </summary>
public class QueryDataSourceDto
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("isRemote")] public bool IsRemote { get; set; }
}

/// <summary>
/// Column information in query results.
/// </summary>
public class QueryColumnInfo
{
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    [JsonPropertyName("alias")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Alias { get; set; }

    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonPropertyName("dataType")]
    public string DataType { get; set; } = "";

    [JsonPropertyName("linkedEntityAlias")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LinkedEntityAlias { get; set; }
}

/// <summary>
/// Response for query/complete method.
/// </summary>
public class QueryCompleteResponse
{
    [JsonPropertyName("items")] public List<CompletionItemDto> Items { get; set; } = [];
}

/// <summary>
/// Completion item for query/complete response.
/// </summary>
public class CompletionItemDto
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("insertText")] public string InsertText { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("sortOrder")] public int SortOrder { get; set; }
}

/// <summary>
/// Response for query/history/list method.
/// </summary>
public class QueryHistoryListResponse
{
    [JsonPropertyName("entries")] public List<QueryHistoryEntryDto> Entries { get; set; } = [];
}

/// <summary>
/// A single query history entry DTO for RPC responses.
/// </summary>
public class QueryHistoryEntryDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("sql")] public string Sql { get; set; } = "";
    [JsonPropertyName("rowCount")] public int? RowCount { get; set; }
    [JsonPropertyName("executionTimeMs")] public long? ExecutionTimeMs { get; set; }
    [JsonPropertyName("environmentUrl")] public string? EnvironmentUrl { get; set; }
    [JsonPropertyName("executedAt")] public DateTimeOffset ExecutedAt { get; set; }
}

/// <summary>
/// Response for query/history/delete method.
/// </summary>
public class QueryHistoryDeleteResponse
{
    [JsonPropertyName("deleted")] public bool Deleted { get; set; }
}

/// <summary>
/// Response for query/export method.
/// </summary>
public class QueryExportResponse
{
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("format")] public string Format { get; set; } = "";
    [JsonPropertyName("rowCount")] public int RowCount { get; set; }
}

/// <summary>
/// Response for query/explain method.
/// </summary>
public class QueryExplainResponse
{
    [JsonPropertyName("plan")] public string Plan { get; set; } = "";
    [JsonPropertyName("format")] public string Format { get; set; } = "fetchxml";
    [JsonPropertyName("fetchXml")] public string? FetchXml { get; set; }
}

// ── Query Request DTOs ──────────────────────────────────────────────────────
// These DTOs accept named JSON-RPC parameters from the TypeScript client
// (e.g. { sql: "...", top: 100 }) instead of positional parameters.

/// <summary>
/// Request DTO for query/sql method.
/// </summary>
public class QuerySqlRequest
{
    [JsonPropertyName("sql")] public string Sql { get; set; } = "";
    [JsonPropertyName("top")] public int? Top { get; set; }
    [JsonPropertyName("page")] public int? Page { get; set; }
    [JsonPropertyName("pagingCookie")] public string? PagingCookie { get; set; }
    [JsonPropertyName("count")] public bool Count { get; set; }
    [JsonPropertyName("showFetchXml")] public bool ShowFetchXml { get; set; }
    [JsonPropertyName("useTds")] public bool UseTds { get; set; }
    [JsonPropertyName("environmentUrl")] public string? EnvironmentUrl { get; set; }
    [JsonPropertyName("profileName")] public string? ProfileName { get; set; }
    [JsonPropertyName("dmlSafety")] public DmlSafetyRpcOptions? DmlSafety { get; set; }
}

/// <summary>
/// Options for DML safety checking passed from the TypeScript client.
/// When present, the SQL statement is parsed and checked via <see cref="Services.Query.DmlSafetyGuard"/>
/// before transpilation/execution.
/// </summary>
public sealed class DmlSafetyRpcOptions
{
    [JsonPropertyName("isConfirmed")] public bool IsConfirmed { get; set; }
    [JsonPropertyName("isDryRun")] public bool IsDryRun { get; set; }
    [JsonPropertyName("noLimit")] public bool NoLimit { get; set; }
    [JsonPropertyName("rowCap")] public int? RowCap { get; set; }
}

/// <summary>
/// Request DTO for query/fetch method.
/// </summary>
public class QueryFetchRequest
{
    [JsonPropertyName("fetchXml")] public string FetchXml { get; set; } = "";
    [JsonPropertyName("top")] public int? Top { get; set; }
    [JsonPropertyName("page")] public int? Page { get; set; }
    [JsonPropertyName("pagingCookie")] public string? PagingCookie { get; set; }
    [JsonPropertyName("count")] public bool Count { get; set; }
    [JsonPropertyName("environmentUrl")] public string? EnvironmentUrl { get; set; }
    [JsonPropertyName("profileName")] public string? ProfileName { get; set; }
}

/// <summary>
/// Request DTO for query/complete method.
/// </summary>
public class QueryCompleteRequest
{
    [JsonPropertyName("sql")] public string Sql { get; set; } = "";
    [JsonPropertyName("cursorOffset")] public int CursorOffset { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("environmentUrl")] public string? EnvironmentUrl { get; set; }
    [JsonPropertyName("profileName")] public string? ProfileName { get; set; }
}

/// <summary>
/// Request DTO for query/validate method.
/// </summary>
public class QueryValidateRequest
{
    [JsonPropertyName("sql")] public string Sql { get; set; } = "";
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("environmentUrl")] public string? EnvironmentUrl { get; set; }
    [JsonPropertyName("profileName")] public string? ProfileName { get; set; }
}

/// <summary>
/// Response DTO for query/validate method.
/// </summary>
public class QueryValidateResponse
{
    [JsonPropertyName("diagnostics")] public List<DiagnosticDto> Diagnostics { get; set; } = [];
}

/// <summary>
/// A single diagnostic (error/warning/info) on query text.
/// Maps to Monaco IMarkerData on the webview side.
/// </summary>
public class DiagnosticDto
{
    [JsonPropertyName("start")] public int Start { get; set; }
    [JsonPropertyName("length")] public int Length { get; set; }
    [JsonPropertyName("severity")] public string Severity { get; set; } = "error";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

/// <summary>
/// Request DTO for query/export method.
/// </summary>
public class QueryExportRequest
{
    [JsonPropertyName("sql")] public string Sql { get; set; } = "";
    [JsonPropertyName("fetchXml")] public string? FetchXml { get; set; }
    [JsonPropertyName("format")] public string Format { get; set; } = "csv";
    [JsonPropertyName("includeHeaders")] public bool IncludeHeaders { get; set; } = true;
    [JsonPropertyName("top")] public int? Top { get; set; }
    [JsonPropertyName("environmentUrl")] public string? EnvironmentUrl { get; set; }
    [JsonPropertyName("profileName")] public string? ProfileName { get; set; }
}

/// <summary>
/// Request DTO for query/explain method.
/// </summary>
public class QueryExplainRequest
{
    [JsonPropertyName("sql")] public string Sql { get; set; } = "";
    [JsonPropertyName("environmentUrl")] public string? EnvironmentUrl { get; set; }
    [JsonPropertyName("profileName")] public string? ProfileName { get; set; }
}

/// <summary>
/// Request DTO for query/history/list method.
/// </summary>
public class QueryHistoryListRequest
{
    [JsonPropertyName("search")] public string? Search { get; set; }
    [JsonPropertyName("limit")] public int Limit { get; set; } = 50;
}

/// <summary>
/// Request DTO for query/history/delete method.
/// </summary>
public class QueryHistoryDeleteRequest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
}

// ── Import Jobs DTOs ────────────────────────────────────────────────────────
