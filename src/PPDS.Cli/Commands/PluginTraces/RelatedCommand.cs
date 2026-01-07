using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.PluginTraces;

/// <summary>
/// Get plugin traces related by correlation ID.
/// </summary>
public static class RelatedCommand
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

        var topOption = new Option<int>("--top", "-n")
        {
            Description = "Maximum number of results to return",
            DefaultValueFactory = _ => 1000
        };

        var command = new Command("related", "Get plugin traces related by correlation ID")
        {
            traceIdArgument,
            correlationIdOption,
            topOption,
            PluginTracesCommandGroup.ProfileOption,
            PluginTracesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var traceId = parseResult.GetValue(traceIdArgument);
            var correlationId = parseResult.GetValue(correlationIdOption);
            var top = parseResult.GetValue(topOption);
            var profile = parseResult.GetValue(PluginTracesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginTracesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(traceId, correlationId, top, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        Guid? traceId,
        Guid? correlationId,
        int top,
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

            var traces = await traceService.GetRelatedAsync(lookupCorrelationId!.Value, top, cancellationToken);

            if (traces.Count == 0)
            {
                if (globalOptions.IsJsonMode)
                {
                    writer.WriteSuccess(new RelatedOutput
                    {
                        CorrelationId = lookupCorrelationId.Value,
                        Traces = []
                    });
                }
                else
                {
                    Console.Error.WriteLine($"No related traces found for correlation ID: {lookupCorrelationId}");
                }
                return ExitCodes.Success;
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new RelatedOutput
                {
                    CorrelationId = lookupCorrelationId.Value,
                    Traces = traces.Select(t => new TraceOutput
                    {
                        Id = t.Id,
                        TypeName = t.TypeName,
                        MessageName = t.MessageName,
                        PrimaryEntity = t.PrimaryEntity,
                        Mode = t.Mode.ToString(),
                        Depth = t.Depth,
                        CreatedOn = t.CreatedOn,
                        DurationMs = t.DurationMs,
                        HasException = t.HasException
                    }).ToList()
                };
                writer.WriteSuccess(output);
            }
            else
            {
                Console.Error.WriteLine($"Related traces for correlation ID: {lookupCorrelationId}");
                Console.Error.WriteLine();
                Console.Error.WriteLine($"{"#",-3} {"Depth",-5} {"Type",-35} {"Message",-12} {"Duration",-10} {"Status",-6}");
                Console.Error.WriteLine(new string('-', 75));

                int idx = 1;
                foreach (var trace in traces)
                {
                    var type = Truncate(trace.TypeName, 35);
                    var message = Truncate(trace.MessageName ?? "-", 12);
                    var duration = trace.DurationMs.HasValue ? $"{trace.DurationMs}ms" : "-";
                    var status = trace.HasException ? "Error" : "OK";

                    Console.Error.WriteLine($"{idx,-3} {trace.Depth,-5} {type,-35} {message,-12} {duration,-10} {status,-6}");
                    idx++;
                }

                Console.Error.WriteLine();
                Console.Error.WriteLine($"Total: {traces.Count} related trace(s)");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "getting related plugin traces", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }

    #region Output Models

    private sealed class RelatedOutput
    {
        [JsonPropertyName("correlationId")]
        public Guid CorrelationId { get; set; }

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
