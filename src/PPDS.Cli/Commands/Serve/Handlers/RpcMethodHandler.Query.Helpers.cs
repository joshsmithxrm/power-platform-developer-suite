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
    /// <summary>
    /// Saves a query to history in a fire-and-forget fashion so callers are not
    /// blocked. History saves are best-effort: cancellation (daemon shutdown) is silently
    /// ignored, and all other exceptions are logged at Debug level to aid diagnostics
    /// without surfacing failures to the caller.
    /// </summary>
    private void FireAndForgetHistorySave(string queryText, QueryResultResponse response)
    {
        var daemonToken = _daemonCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                daemonToken.ThrowIfCancellationRequested();

                var historyService = _authServices.GetService<IQueryHistoryService>();
                if (historyService != null)
                {
                    var store = _authServices.GetRequiredService<ProfileStore>();
                    var collection = await store.LoadAsync(daemonToken);
                    var envUrl = collection.ActiveProfile?.Environment?.Url;
                    if (envUrl != null)
                    {
                        await historyService.AddQueryAsync(
                            envUrl, queryText,
                            rowCount: response.Count,
                            executionTimeMs: response.ExecutionTimeMs,
                            cancellationToken: daemonToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Daemon is shutting down — silently discard
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to save query history entry");
            }
        }, daemonToken);
    }

    private static string FormatExportContent(
        List<QueryColumnInfo> columns,
        List<Dictionary<string, object?>> records,
        string format,
        bool includeHeaders)
    {
        if (format == "json")
        {
            // JSON array of objects
            var jsonArray = records.Select(record =>
            {
                var obj = new Dictionary<string, object?>();
                foreach (var col in columns)
                {
                    var key = col.Alias ?? col.LogicalName;
                    record.TryGetValue(key, out var val);
                    obj[key] = ExtractDisplayValue(val);
                }
                return obj;
            }).ToList();

            return System.Text.Json.JsonSerializer.Serialize(jsonArray,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        // CSV or TSV
        var separator = format == "tsv" ? '\t' : ',';
        var sb = new System.Text.StringBuilder();

        if (includeHeaders)
        {
            var headers = columns.Select(c => c.Alias ?? c.LogicalName);
            if (format == "csv")
            {
                sb.AppendLine(string.Join(separator, headers.Select(h => CsvEscape(h, separator))));
            }
            else
            {
                sb.AppendLine(string.Join(separator, headers));
            }
        }

        foreach (var record in records)
        {
            var values = columns.Select(col =>
            {
                var key = col.Alias ?? col.LogicalName;
                record.TryGetValue(key, out var val);
                var display = ExtractDisplayValue(val)?.ToString() ?? "";
                return format == "csv" ? CsvEscape(display, separator) : display.Replace("\t", " ").Replace("\n", " ").Replace("\r", "");
            });
            sb.AppendLine(string.Join(separator, values));
        }

        return sb.ToString();
    }

    private static object? ExtractDisplayValue(object? val)
    {
        if (val is Dictionary<string, object?> dict)
        {
            // Return formatted value if available, otherwise raw value
            if (dict.TryGetValue("formatted", out var formatted) && formatted != null)
                return formatted;
            if (dict.TryGetValue("value", out var value))
                return value;
        }
        return val;
    }

    private static string CsvEscape(string value, char separator)
    {
        if (value.Contains(separator) || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    /// <summary>
    /// Injects a top="N" attribute into generated FetchXML.
    /// Uses string manipulation (not XML parsing) because the FetchXML
    /// comes from our generator and always has a predictable format.
    /// </summary>
    private static string InjectTopAttribute(string fetchXml, int top)
    {
        var fetchIndex = fetchXml.IndexOf("<fetch", StringComparison.OrdinalIgnoreCase);
        if (fetchIndex < 0) return fetchXml;

        var endOfFetch = fetchXml.IndexOf('>', fetchIndex);
        if (endOfFetch < 0) return fetchXml;

        var fetchElement = fetchXml.Substring(fetchIndex, endOfFetch - fetchIndex);

        if (fetchElement.Contains("top=", StringComparison.OrdinalIgnoreCase))
        {
            return fetchXml; // Already has top, don't override
        }

        var insertPoint = fetchIndex + "<fetch".Length;
        return fetchXml.Substring(0, insertPoint) + $" top=\"{top}\"" + fetchXml.Substring(insertPoint);
    }

    private static QueryResultResponse MapToResponse(QueryResult result, string? fetchXml)
    {
        return new QueryResultResponse
        {
            Success = true,
            EntityName = result.EntityLogicalName,
            Columns = result.Columns.Select(c => new QueryColumnInfo
            {
                LogicalName = c.LogicalName,
                Alias = c.Alias,
                DisplayName = c.DisplayName,
                DataType = c.DataType.ToString(),
                LinkedEntityAlias = c.LinkedEntityAlias
            }).ToList(),
            Records = result.Records.Select(r =>
                r.ToDictionary(
                    kvp => kvp.Key,
                    kvp => MapQueryValue(kvp.Value))).ToList(),
            Count = result.Count,
            TotalCount = result.TotalCount,
            MoreRecords = result.MoreRecords,
            PagingCookie = result.PagingCookie,
            PageNumber = result.PageNumber,
            IsAggregate = result.IsAggregate,
            ExecutedFetchXml = fetchXml,
            ExecutionTimeMs = result.ExecutionTimeMs
        };
    }

    private static object? MapQueryValue(QueryValue? value)
    {
        if (value == null) return null;

        // For lookups, return structured object
        if (value.LookupEntityId.HasValue)
        {
            return new Dictionary<string, object?>
            {
                ["value"] = value.Value,
                ["formatted"] = value.FormattedValue,
                ["entityType"] = value.LookupEntityType,
                ["entityId"] = value.LookupEntityId
            };
        }

        // For values with formatting, return structured object
        if (value.FormattedValue != null)
        {
            return new Dictionary<string, object?>
            {
                ["value"] = value.Value,
                ["formatted"] = value.FormattedValue
            };
        }

        // Simple value
        return value.Value;
    }

    /// <summary>
    /// Extracts <see cref="DiagnosticDto"/> items from a <see cref="QueryParseException"/>
    /// chained inside a <see cref="PpdsException"/>. Returns null if no parse exception
    /// is found in the chain.
    /// </summary>
    private static List<DiagnosticDto>? ExtractParseDiagnostics(Exception ex, string sql)
    {
        QueryParseException? parseEx = null;
        Exception? current = ex;
        while (current != null)
        {
            if (current is QueryParseException qpe)
            {
                parseEx = qpe;
                break;
            }
            current = current.InnerException;
        }
        if (parseEx == null || parseEx.Errors.Count == 0) return null;

        var result = new List<DiagnosticDto>(parseEx.Errors.Count);
        foreach (var pe in parseEx.Errors)
        {
            var offset = CalculateOffset(sql, pe.Line, pe.Column);
            var length = Math.Max(1, Math.Min(10, sql.Length - offset));
            result.Add(new DiagnosticDto
            {
                Start = offset,
                Length = length,
                Severity = "error",
                Message = pe.Message,
            });
        }
        return result;
    }

    /// <summary>
    /// Converts 1-based line/column to a 0-based character offset in the SQL text.
    /// Mirrors <see cref="PPDS.Query.Intellisense.SqlValidator.CalculateOffset"/>.
    /// </summary>
    private static int CalculateOffset(string sql, int line, int column)
    {
        var offset = 0;
        var currentLine = 1;
        for (var i = 0; i < sql.Length && currentLine < line; i++)
        {
            if (sql[i] == '\n') currentLine++;
            offset = i + 1;
        }
        offset += Math.Max(0, column - 1);
        return Math.Min(offset, sql.Length);
    }
}
