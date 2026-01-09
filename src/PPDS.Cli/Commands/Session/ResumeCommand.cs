using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Session;

namespace PPDS.Cli.Commands.Session;

/// <summary>
/// Resume a paused worker session.
/// </summary>
public static class ResumeCommand
{
    public static Command Create()
    {
        var sessionArg = new Argument<string>("session")
        {
            Description = "Session ID (issue number)"
        };

        var command = new Command("resume", "Resume a paused worker session")
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

            await service.ResumeAsync(sessionId, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                var output = new ResumeResult
                {
                    SessionId = sessionId,
                    Resumed = true
                };

                writer.WriteSuccess(output);
            }
            else
            {
                Console.WriteLine($"Session #{sessionId} resumed.");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"resuming session '{sessionId}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class ResumeResult
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("resumed")]
        public bool Resumed { get; set; }
    }

    #endregion
}
