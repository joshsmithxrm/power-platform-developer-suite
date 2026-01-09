using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Session;

namespace PPDS.Cli.Commands.Session;

/// <summary>
/// Pause a worker session.
/// </summary>
public static class PauseCommand
{
    public static Command Create()
    {
        var sessionArg = new Argument<string>("session")
        {
            Description = "Session ID (issue number)"
        };

        var command = new Command("pause", "Pause a worker session")
        {
            sessionArg
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var sessionId = parseResult.GetValue(sessionArg)!;
            var globalOptions = GlobalOptions.GetValues(parseResult);
            return await ExecuteAsync(sessionId, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string sessionId,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            var spawner = new WindowsTerminalWorkerSpawner();
            var logger = NullLogger<SessionService>.Instance;
            var service = new SessionService(spawner, logger);

            await service.PauseAsync(sessionId, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                var output = new PauseResult
                {
                    SessionId = sessionId,
                    Paused = true
                };

                writer.WriteSuccess(output);
            }
            else
            {
                Console.WriteLine($"Session #{sessionId} paused.");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"pausing session '{sessionId}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class PauseResult
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("paused")]
        public bool Paused { get; set; }
    }

    #endregion
}
