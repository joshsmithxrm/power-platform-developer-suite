using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.PluginTraces;

/// <summary>
/// List plugin trace logs with filtering.
/// </summary>
public static class ListCommand
{
    public static Command Create()
    {
        // Filter options
        var typeOption = new Option<string?>("--type", "-t")
        {
            Description = "Filter by plugin type name (contains)"
        };

        var messageOption = new Option<string?>("--message", "-m")
        {
            Description = "Filter by message name (Create, Update, etc.)"
        };

        var entityOption = new Option<string?>("--entity")
        {
            Description = "Filter by primary entity (contains)"
        };

        var modeOption = new Option<string?>("--mode")
        {
            Description = "Filter by execution mode: sync or async"
        };

        var errorsOnlyOption = new Option<bool>("--errors-only")
        {
            Description = "Show only traces with exceptions"
        };

        var successOnlyOption = new Option<bool>("--success-only")
        {
            Description = "Show only successful traces (no exceptions)"
        };

        // Time filters
        var sinceOption = new Option<DateTime?>("--since")
        {
            Description = "Show traces created after this time (ISO 8601)"
        };

        var untilOption = new Option<DateTime?>("--until")
        {
            Description = "Show traces created before this time (ISO 8601)"
        };

        // Performance filters
        var minDurationOption = new Option<int?>("--min-duration")
        {
            Description = "Minimum execution duration in milliseconds"
        };

        var maxDurationOption = new Option<int?>("--max-duration")
        {
            Description = "Maximum execution duration in milliseconds"
        };

        // Correlation filters
        var correlationIdOption = new Option<Guid?>("--correlation-id")
        {
            Description = "Filter by correlation ID"
        };

        var requestIdOption = new Option<Guid?>("--request-id")
        {
            Description = "Filter by request ID"
        };

        var stepIdOption = new Option<Guid?>("--step-id")
        {
            Description = "Filter by plugin step ID"
        };

        // Filter file support (#155)
        var filterFileOption = new Option<FileInfo?>("--filter")
        {
            Description = "JSON file with filter criteria"
        };

        // Pagination & output
        var topOption = new Option<int>("--top", "-n")
        {
            Description = "Maximum number of results to return",
            DefaultValueFactory = _ => 100
        };

        var orderByOption = new Option<string?>("--order-by")
        {
            Description = "Sort field (default: createdon desc)"
        };

        var command = new Command("list", "List plugin trace logs with optional filtering")
        {
            PluginTracesCommandGroup.ProfileOption,
            PluginTracesCommandGroup.EnvironmentOption,
            typeOption,
            messageOption,
            entityOption,
            modeOption,
            errorsOnlyOption,
            successOnlyOption,
            sinceOption,
            untilOption,
            minDurationOption,
            maxDurationOption,
            correlationIdOption,
            requestIdOption,
            stepIdOption,
            filterFileOption,
            topOption,
            orderByOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var profile = parseResult.GetValue(PluginTracesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginTracesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            var filter = new PluginTraceFilter
            {
                TypeName = parseResult.GetValue(typeOption),
                MessageName = parseResult.GetValue(messageOption),
                PrimaryEntity = parseResult.GetValue(entityOption),
                CreatedAfter = parseResult.GetValue(sinceOption),
                CreatedBefore = parseResult.GetValue(untilOption),
                MinDurationMs = parseResult.GetValue(minDurationOption),
                MaxDurationMs = parseResult.GetValue(maxDurationOption),
                CorrelationId = parseResult.GetValue(correlationIdOption),
                RequestId = parseResult.GetValue(requestIdOption),
                PluginStepId = parseResult.GetValue(stepIdOption),
                OrderBy = parseResult.GetValue(orderByOption)
            };

            // Parse mode option
            var modeStr = parseResult.GetValue(modeOption);
            if (!string.IsNullOrEmpty(modeStr))
            {
                filter = filter with
                {
                    Mode = modeStr.ToLowerInvariant() switch
                    {
                        "sync" or "synchronous" => PluginTraceMode.Synchronous,
                        "async" or "asynchronous" => PluginTraceMode.Asynchronous,
                        _ => null
                    }
                };
            }

            // Handle errors/success filters
            var errorsOnly = parseResult.GetValue(errorsOnlyOption);
            var successOnly = parseResult.GetValue(successOnlyOption);
            if (errorsOnly)
            {
                filter = filter with { HasException = true };
            }
            else if (successOnly)
            {
                filter = filter with { HasException = false };
            }

            // Load filter from file if specified (#155)
            var filterFile = parseResult.GetValue(filterFileOption);
            if (filterFile != null)
            {
                filter = await LoadFilterFromFileAsync(filterFile, filter, cancellationToken);
            }

            var top = parseResult.GetValue(topOption);

            return await ExecuteAsync(profile, environment, filter, top, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<PluginTraceFilter> LoadFilterFromFileAsync(
        FileInfo filterFile,
        PluginTraceFilter baseFilter,
        CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(filterFile.FullName, cancellationToken);
        var fileFilter = JsonSerializer.Deserialize<PluginTraceFilter>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (fileFilter == null)
        {
            return baseFilter;
        }

        // Merge file filter with command-line filter (command-line takes precedence)
        return new PluginTraceFilter
        {
            TypeName = baseFilter.TypeName ?? fileFilter.TypeName,
            MessageName = baseFilter.MessageName ?? fileFilter.MessageName,
            PrimaryEntity = baseFilter.PrimaryEntity ?? fileFilter.PrimaryEntity,
            Mode = baseFilter.Mode ?? fileFilter.Mode,
            OperationType = baseFilter.OperationType ?? fileFilter.OperationType,
            MinDepth = baseFilter.MinDepth ?? fileFilter.MinDepth,
            MaxDepth = baseFilter.MaxDepth ?? fileFilter.MaxDepth,
            CreatedAfter = baseFilter.CreatedAfter ?? fileFilter.CreatedAfter,
            CreatedBefore = baseFilter.CreatedBefore ?? fileFilter.CreatedBefore,
            MinDurationMs = baseFilter.MinDurationMs ?? fileFilter.MinDurationMs,
            MaxDurationMs = baseFilter.MaxDurationMs ?? fileFilter.MaxDurationMs,
            HasException = baseFilter.HasException ?? fileFilter.HasException,
            CorrelationId = baseFilter.CorrelationId ?? fileFilter.CorrelationId,
            RequestId = baseFilter.RequestId ?? fileFilter.RequestId,
            PluginStepId = baseFilter.PluginStepId ?? fileFilter.PluginStepId,
            OrderBy = baseFilter.OrderBy ?? fileFilter.OrderBy
        };
    }

    private static async Task<int> ExecuteAsync(
        string? profile,
        string? environment,
        PluginTraceFilter filter,
        int top,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        // Validate --top is within Dataverse paging limit
        const int maxTop = 5000;
        if (top > maxTop)
        {
            var error = new StructuredError(
                ErrorCodes.Validation.InvalidValue,
                $"--top value {top} exceeds maximum of {maxTop}. Use pagination for larger result sets.",
                null,
                "top");

            writer.WriteError(error);
            return ExitCodes.InvalidArguments;
        }

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var traceService = serviceProvider.GetRequiredService<IPluginTraceService>();

            if (!globalOptions.IsJsonMode && globalOptions.OutputFormat != OutputFormat.Csv)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var traces = await traceService.ListAsync(filter, top, cancellationToken);

            if (traces.Count == 0)
            {
                if (globalOptions.IsJsonMode)
                {
                    writer.WriteSuccess(new ListOutput { Traces = [] });
                }
                else if (globalOptions.OutputFormat == OutputFormat.Csv)
                {
                    // Empty CSV with just headers
                    Console.WriteLine(GetCsvHeader());
                }
                else
                {
                    Console.Error.WriteLine("No plugin traces found.");
                }
                return ExitCodes.Success;
            }

            if (globalOptions.OutputFormat == OutputFormat.Csv)
            {
                WriteCsvOutput(traces);
            }
            else if (globalOptions.IsJsonMode)
            {
                var output = new ListOutput
                {
                    Traces = traces.Select(t => new TraceOutput
                    {
                        Id = t.Id,
                        TypeName = t.TypeName,
                        MessageName = t.MessageName,
                        PrimaryEntity = t.PrimaryEntity,
                        Mode = t.Mode.ToString(),
                        OperationType = t.OperationType.ToString(),
                        Depth = t.Depth,
                        CreatedOn = t.CreatedOn,
                        DurationMs = t.DurationMs,
                        HasException = t.HasException,
                        CorrelationId = t.CorrelationId,
                        RequestId = t.RequestId,
                        PluginStepId = t.PluginStepId
                    }).ToList()
                };
                writer.WriteSuccess(output);
            }
            else
            {
                WriteTextOutput(traces);
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "listing plugin traces", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static void WriteTextOutput(List<PluginTraceInfo> traces)
    {
        Console.Error.WriteLine($"{"Type",-35} {"Message",-12} {"Entity",-20} {"Mode",-6} {"Duration",-10} {"Status",-6} {"Created",-20}");
        Console.Error.WriteLine(new string('-', 115));

        foreach (var trace in traces)
        {
            var type = Truncate(trace.TypeName, 35);
            var message = Truncate(trace.MessageName ?? "-", 12);
            var entity = Truncate(trace.PrimaryEntity ?? "-", 20);
            var mode = trace.Mode == PluginTraceMode.Synchronous ? "Sync" : "Async";
            var duration = trace.DurationMs.HasValue ? $"{trace.DurationMs}ms" : "-";
            var status = trace.HasException ? "Error" : "OK";
            var created = trace.CreatedOn.ToString("g");

            Console.Error.WriteLine($"{type,-35} {message,-12} {entity,-20} {mode,-6} {duration,-10} {status,-6} {created,-20}");
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine($"Total: {traces.Count} trace(s)");
    }

    private static string GetCsvHeader()
    {
        return "Id,TypeName,MessageName,PrimaryEntity,Mode,OperationType,Depth,CreatedOn,DurationMs,HasException,CorrelationId,RequestId,PluginStepId";
    }

    private static void WriteCsvOutput(List<PluginTraceInfo> traces)
    {
        Console.WriteLine(GetCsvHeader());

        var sb = new StringBuilder();
        foreach (var trace in traces)
        {
            sb.Clear();
            sb.Append(trace.Id).Append(',');
            sb.Append(EscapeCsv(trace.TypeName)).Append(',');
            sb.Append(EscapeCsv(trace.MessageName ?? "")).Append(',');
            sb.Append(EscapeCsv(trace.PrimaryEntity ?? "")).Append(',');
            sb.Append(trace.Mode).Append(',');
            sb.Append(trace.OperationType).Append(',');
            sb.Append(trace.Depth).Append(',');
            sb.Append(trace.CreatedOn.ToString("o")).Append(',');
            sb.Append(trace.DurationMs?.ToString() ?? "").Append(',');
            sb.Append(trace.HasException).Append(',');
            sb.Append(trace.CorrelationId?.ToString() ?? "").Append(',');
            sb.Append(trace.RequestId?.ToString() ?? "").Append(',');
            sb.Append(trace.PluginStepId?.ToString() ?? "");

            Console.WriteLine(sb);
        }
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }

    #region Output Models

    private sealed class ListOutput
    {
        [JsonPropertyName("traces")]
        public List<TraceOutput> Traces { get; set; } = [];
    }

    private sealed class TraceOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("typeName")]
        public string TypeName { get; set; } = "";

        [JsonPropertyName("messageName")]
        public string? MessageName { get; set; }

        [JsonPropertyName("primaryEntity")]
        public string? PrimaryEntity { get; set; }

        [JsonPropertyName("mode")]
        public string? Mode { get; set; }

        [JsonPropertyName("operationType")]
        public string? OperationType { get; set; }

        [JsonPropertyName("depth")]
        public int Depth { get; set; }

        [JsonPropertyName("createdOn")]
        public DateTime CreatedOn { get; set; }

        [JsonPropertyName("durationMs")]
        public int? DurationMs { get; set; }

        [JsonPropertyName("hasException")]
        public bool HasException { get; set; }

        [JsonPropertyName("correlationId")]
        public Guid? CorrelationId { get; set; }

        [JsonPropertyName("requestId")]
        public Guid? RequestId { get; set; }

        [JsonPropertyName("pluginStepId")]
        public Guid? PluginStepId { get; set; }
    }

    #endregion
}
