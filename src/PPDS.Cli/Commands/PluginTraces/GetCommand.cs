using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.PluginTraces;

namespace PPDS.Cli.Commands.PluginTraces;

/// <summary>
/// Get detailed information about a specific plugin trace.
/// </summary>
public static class GetCommand
{
    public static Command Create()
    {
        var traceIdArgument = new Argument<Guid>("trace-id")
        {
            Description = "The plugin trace ID"
        };

        var command = new Command("get", "Get detailed information about a plugin trace")
        {
            traceIdArgument,
            PluginTracesCommandGroup.ProfileOption,
            PluginTracesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var traceId = parseResult.GetValue(traceIdArgument);
            var profile = parseResult.GetValue(PluginTracesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginTracesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(traceId, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        Guid traceId,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

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

            var trace = await traceService.GetAsync(traceId, cancellationToken);

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

            if (globalOptions.IsJsonMode)
            {
                var output = new TraceDetailOutput
                {
                    Id = trace.Id,
                    TypeName = trace.TypeName,
                    MessageName = trace.MessageName,
                    PrimaryEntity = trace.PrimaryEntity,
                    Mode = trace.Mode.ToString(),
                    OperationType = trace.OperationType.ToString(),
                    Depth = trace.Depth,
                    CreatedOn = trace.CreatedOn,
                    DurationMs = trace.DurationMs,
                    ConstructorDurationMs = trace.ConstructorDurationMs,
                    ExecutionStartTime = trace.ExecutionStartTime,
                    ConstructorStartTime = trace.ConstructorStartTime,
                    HasException = trace.HasException,
                    ExceptionDetails = trace.ExceptionDetails,
                    MessageBlock = trace.MessageBlock,
                    Configuration = trace.Configuration,
                    SecureConfiguration = trace.SecureConfiguration,
                    Profile = trace.Profile,
                    CorrelationId = trace.CorrelationId,
                    RequestId = trace.RequestId,
                    PluginStepId = trace.PluginStepId,
                    OrganizationId = trace.OrganizationId,
                    PersistenceKey = trace.PersistenceKey,
                    IsSystemCreated = trace.IsSystemCreated,
                    CreatedById = trace.CreatedById,
                    CreatedOnBehalfById = trace.CreatedOnBehalfById
                };
                writer.WriteSuccess(output);
            }
            else
            {
                WriteTextOutput(trace);
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "getting plugin trace", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static void WriteTextOutput(PluginTraceDetail trace)
    {
        Console.WriteLine($"Plugin Trace: {trace.Id}");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine();

        // Basic info
        Console.WriteLine($"  Type:          {trace.TypeName}");
        Console.WriteLine($"  Message:       {trace.MessageName ?? "-"}");
        Console.WriteLine($"  Entity:        {trace.PrimaryEntity ?? "-"}");
        Console.WriteLine($"  Mode:          {trace.Mode}");
        Console.WriteLine($"  Operation:     {trace.OperationType}");
        Console.WriteLine($"  Depth:         {trace.Depth}");
        Console.WriteLine();

        // Timing
        Console.WriteLine("Timing:");
        Console.WriteLine($"  Created:              {trace.CreatedOn:G}");
        if (trace.ExecutionStartTime.HasValue)
        {
            Console.WriteLine($"  Execution Start:      {trace.ExecutionStartTime:G}");
        }
        if (trace.ConstructorStartTime.HasValue)
        {
            Console.WriteLine($"  Constructor Start:    {trace.ConstructorStartTime:G}");
        }
        Console.WriteLine($"  Execution Duration:   {trace.DurationMs?.ToString() ?? "-"} ms");
        Console.WriteLine($"  Constructor Duration: {trace.ConstructorDurationMs?.ToString() ?? "-"} ms");
        Console.WriteLine();

        // Correlation
        Console.WriteLine("Correlation:");
        Console.WriteLine($"  Correlation ID: {trace.CorrelationId?.ToString() ?? "-"}");
        Console.WriteLine($"  Request ID:     {trace.RequestId?.ToString() ?? "-"}");
        Console.WriteLine($"  Plugin Step ID: {trace.PluginStepId?.ToString() ?? "-"}");
        Console.WriteLine();

        // Exception details
        if (trace.HasException && !string.IsNullOrEmpty(trace.ExceptionDetails))
        {
            Console.WriteLine("Exception:");
            Console.WriteLine(new string('-', 60));
            Console.WriteLine(trace.ExceptionDetails);
            Console.WriteLine(new string('-', 60));
            Console.WriteLine();
        }

        // Message block (trace output)
        if (!string.IsNullOrEmpty(trace.MessageBlock))
        {
            Console.WriteLine("Trace Output:");
            Console.WriteLine(new string('-', 60));
            Console.WriteLine(trace.MessageBlock);
            Console.WriteLine(new string('-', 60));
            Console.WriteLine();
        }

        // Configuration
        if (!string.IsNullOrEmpty(trace.Configuration))
        {
            Console.WriteLine("Configuration:");
            Console.WriteLine(new string('-', 60));
            Console.WriteLine(trace.Configuration);
            Console.WriteLine(new string('-', 60));
            Console.WriteLine();
        }
    }

    #region Output Models

    private sealed class TraceDetailOutput
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

        [JsonPropertyName("constructorDurationMs")]
        public int? ConstructorDurationMs { get; set; }

        [JsonPropertyName("executionStartTime")]
        public DateTime? ExecutionStartTime { get; set; }

        [JsonPropertyName("constructorStartTime")]
        public DateTime? ConstructorStartTime { get; set; }

        [JsonPropertyName("hasException")]
        public bool HasException { get; set; }

        [JsonPropertyName("exceptionDetails")]
        public string? ExceptionDetails { get; set; }

        [JsonPropertyName("messageBlock")]
        public string? MessageBlock { get; set; }

        [JsonPropertyName("configuration")]
        public string? Configuration { get; set; }

        [JsonPropertyName("secureConfiguration")]
        public string? SecureConfiguration { get; set; }

        [JsonPropertyName("profile")]
        public string? Profile { get; set; }

        [JsonPropertyName("correlationId")]
        public Guid? CorrelationId { get; set; }

        [JsonPropertyName("requestId")]
        public Guid? RequestId { get; set; }

        [JsonPropertyName("pluginStepId")]
        public Guid? PluginStepId { get; set; }

        [JsonPropertyName("organizationId")]
        public Guid? OrganizationId { get; set; }

        [JsonPropertyName("persistenceKey")]
        public Guid? PersistenceKey { get; set; }

        [JsonPropertyName("isSystemCreated")]
        public bool IsSystemCreated { get; set; }

        [JsonPropertyName("createdById")]
        public Guid? CreatedById { get; set; }

        [JsonPropertyName("createdOnBehalfById")]
        public Guid? CreatedOnBehalfById { get; set; }
    }

    #endregion
}
