using System.CommandLine;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.PluginTraces;

/// <summary>
/// Display plugin trace execution timeline as a hierarchy.
/// </summary>
public static class TimelineCommand
{
    public static Command Create()
    {
        var traceIdArgument = new Argument<Guid?>("trace-id")
        {
            Description = "The plugin trace ID (gets correlation ID from this trace)",
            Arity = ArgumentArity.ZeroOrOne
        };

        var correlationIdOption = new Option<Guid?>("--correlation-id", "-c")
        {
            Description = "The correlation ID to look up (alternative to trace-id)"
        };

        var command = new Command("timeline", "Display plugin execution timeline as a hierarchy tree")
        {
            traceIdArgument,
            correlationIdOption,
            PluginTracesCommandGroup.ProfileOption,
            PluginTracesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var traceId = parseResult.GetValue(traceIdArgument);
            var correlationId = parseResult.GetValue(correlationIdOption);
            var profile = parseResult.GetValue(PluginTracesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginTracesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(traceId, correlationId, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        Guid? traceId,
        Guid? correlationId,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        // Validate that either traceId or correlationId is provided
        if (!traceId.HasValue && !correlationId.HasValue)
        {
            var message = "Either trace-id or --correlation-id must be provided.";
            var error = new StructuredError(
                ErrorCodes.Validation.InvalidArguments,
                message);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteError(error);
            }
            else
            {
                Console.Error.WriteLine($"Error: {message}");
            }
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

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            // If traceId provided, look up its correlation ID
            var lookupCorrelationId = correlationId;
            if (traceId.HasValue && !correlationId.HasValue)
            {
                var trace = await traceService.GetAsync(traceId.Value, cancellationToken);
                if (trace == null)
                {
                    var error = new StructuredError(
                        ErrorCodes.Operation.NotFound,
                        $"Plugin trace {traceId} not found.",
                        null,
                        traceId.ToString());

                    if (globalOptions.IsJsonMode)
                    {
                        writer.WriteError(error);
                    }
                    else
                    {
                        Console.Error.WriteLine($"Plugin trace {traceId} not found.");
                    }
                    return ExitCodes.NotFoundError;
                }

                lookupCorrelationId = trace.CorrelationId;
                if (!lookupCorrelationId.HasValue)
                {
                    var error = new StructuredError(
                        ErrorCodes.Operation.NotFound,
                        $"Plugin trace {traceId} has no correlation ID.",
                        null,
                        traceId.ToString());

                    if (globalOptions.IsJsonMode)
                    {
                        writer.WriteError(error);
                    }
                    else
                    {
                        Console.Error.WriteLine($"Plugin trace {traceId} has no correlation ID.");
                    }
                    return ExitCodes.NotFoundError;
                }
            }

            var timeline = await traceService.BuildTimelineAsync(lookupCorrelationId!.Value, cancellationToken);

            if (timeline.Count == 0)
            {
                if (globalOptions.IsJsonMode)
                {
                    writer.WriteSuccess(new TimelineOutput
                    {
                        CorrelationId = lookupCorrelationId.Value,
                        Nodes = [],
                        TotalDuration = 0,
                        TotalNodes = 0
                    });
                }
                else
                {
                    Console.Error.WriteLine($"No traces found for correlation ID: {lookupCorrelationId}");
                }
                return ExitCodes.Success;
            }

            // Calculate total duration
            var allTraces = FlattenNodes(timeline);
            var totalDuration = TimelineHierarchyBuilder.GetTotalDuration(allTraces);
            var totalNodes = TimelineHierarchyBuilder.CountTotalNodes(timeline);

            if (globalOptions.IsJsonMode)
            {
                var output = new TimelineOutput
                {
                    CorrelationId = lookupCorrelationId.Value,
                    Nodes = timeline.Select(MapToJsonNode).ToList(),
                    TotalDuration = totalDuration,
                    TotalNodes = totalNodes
                };
                writer.WriteSuccess(output);
            }
            else
            {
                // Display header
                var firstTrace = timeline.First().Trace;
                Console.Error.WriteLine($"Request: {lookupCorrelationId}  ({firstTrace.CreatedOn:G})");
                Console.Error.WriteLine($"Total Duration: {totalDuration}ms  |  {totalNodes} plugin executions");
                Console.Error.WriteLine();

                // Draw tree
                foreach (var node in timeline)
                {
                    DrawNode(node, "", node == timeline.Last());
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "building plugin timeline", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static void DrawNode(TimelineNode node, string prefix, bool isLast)
    {
        var trace = node.Trace;

        // Build connector
        var connector = isLast ? "\u2514\u2500\u2500 " : "\u251c\u2500\u2500 "; // "└── " or "├── "

        // Build status icon
        var statusIcon = trace.HasException ? "\u2717" : "\u2713"; // "✗" or "✓"

        // Build mode indicator
        var modeStr = trace.Mode == PluginTraceMode.Synchronous ? "Sync" : "Async";

        // Build duration
        var durationStr = trace.DurationMs.HasValue ? $"{trace.DurationMs}ms" : "?ms";

        // Build the line
        var sb = new StringBuilder();
        sb.Append(prefix);
        sb.Append(connector);
        sb.Append($"[{modeStr}] ");
        sb.Append(GetShortTypeName(trace.TypeName));
        if (!string.IsNullOrEmpty(trace.MessageName))
        {
            sb.Append($".{trace.MessageName}");
        }
        sb.Append($" ({durationStr}) {statusIcon}");

        Console.Error.WriteLine(sb.ToString());

        // If there's an exception, show a truncated message
        if (trace.HasException)
        {
            var exceptionPrefix = prefix + (isLast ? "    " : "\u2502   "); // "│   " or "    "
            Console.Error.WriteLine($"{exceptionPrefix}Exception: (use 'get {trace.Id}' for details)");
        }

        // Draw children
        var childPrefix = prefix + (isLast ? "    " : "\u2502   "); // "│   " or "    "
        var children = node.Children.ToList();
        for (int i = 0; i < children.Count; i++)
        {
            DrawNode(children[i], childPrefix, i == children.Count - 1);
        }
    }

    private static string GetShortTypeName(string fullTypeName)
    {
        // Extract just the class name from a fully qualified type name
        // e.g., "MyPlugin.Namespace.AccountPlugin" -> "AccountPlugin"
        var lastDot = fullTypeName.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < fullTypeName.Length - 1)
        {
            return fullTypeName[(lastDot + 1)..];
        }
        return fullTypeName;
    }

    private static List<PluginTraceInfo> FlattenNodes(IReadOnlyList<TimelineNode> nodes)
    {
        var result = new List<PluginTraceInfo>();
        foreach (var node in nodes)
        {
            result.Add(node.Trace);
            result.AddRange(FlattenNodes(node.Children));
        }
        return result;
    }

    private static TimelineNodeOutput MapToJsonNode(TimelineNode node)
    {
        return new TimelineNodeOutput
        {
            Trace = new TraceOutput
            {
                Id = node.Trace.Id,
                TypeName = node.Trace.TypeName,
                MessageName = node.Trace.MessageName,
                PrimaryEntity = node.Trace.PrimaryEntity,
                Mode = node.Trace.Mode.ToString(),
                Depth = node.Trace.Depth,
                CreatedOn = node.Trace.CreatedOn,
                DurationMs = node.Trace.DurationMs,
                HasException = node.Trace.HasException
            },
            HierarchyDepth = node.HierarchyDepth,
            OffsetPercent = node.OffsetPercent,
            WidthPercent = node.WidthPercent,
            Children = node.Children.Select(MapToJsonNode).ToList()
        };
    }

    #region Output Models

    private sealed class TimelineOutput
    {
        [JsonPropertyName("correlationId")]
        public Guid CorrelationId { get; set; }

        [JsonPropertyName("nodes")]
        public List<TimelineNodeOutput> Nodes { get; set; } = [];

        [JsonPropertyName("totalDuration")]
        public long TotalDuration { get; set; }

        [JsonPropertyName("totalNodes")]
        public int TotalNodes { get; set; }
    }

    private sealed class TimelineNodeOutput
    {
        [JsonPropertyName("trace")]
        public TraceOutput Trace { get; set; } = new();

        [JsonPropertyName("hierarchyDepth")]
        public int HierarchyDepth { get; set; }

        [JsonPropertyName("offsetPercent")]
        public double OffsetPercent { get; set; }

        [JsonPropertyName("widthPercent")]
        public double WidthPercent { get; set; }

        [JsonPropertyName("children")]
        public List<TimelineNodeOutput> Children { get; set; } = [];
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

        [JsonPropertyName("depth")]
        public int Depth { get; set; }

        [JsonPropertyName("createdOn")]
        public DateTime CreatedOn { get; set; }

        [JsonPropertyName("durationMs")]
        public int? DurationMs { get; set; }

        [JsonPropertyName("hasException")]
        public bool HasException { get; set; }
    }

    #endregion
}
