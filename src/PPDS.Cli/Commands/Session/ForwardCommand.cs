using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Session;

namespace PPDS.Cli.Commands.Session;

/// <summary>
/// Forward a message to a worker session.
/// </summary>
public static class ForwardCommand
{
    public static Command Create()
    {
        var sessionArg = new Argument<string>("session")
        {
            Description = "Session ID (issue number)"
        };

        var messageArg = new Argument<string>("message")
        {
            Description = "Message to forward to the worker"
        };

        var command = new Command("forward", "Forward a message to a worker session")
        {
            sessionArg,
            messageArg
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var sessionId = parseResult.GetValue(sessionArg)!;
            var message = parseResult.GetValue(messageArg)!;
            var globalOptions = GlobalOptions.GetValues(parseResult);
            return await ExecuteAsync(sessionId, message, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string sessionId,
        string message,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            var spawner = new WindowsTerminalWorkerSpawner();
            var logger = NullLogger<SessionService>.Instance;
            var service = new SessionService(spawner, logger);

            await service.ForwardAsync(sessionId, message, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                var output = new ForwardResult
                {
                    SessionId = sessionId,
                    Message = message,
                    Forwarded = true
                };

                writer.WriteSuccess(output);
            }
            else
            {
                Console.WriteLine($"Message forwarded to session #{sessionId}.");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"forwarding message to session '{sessionId}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class ForwardResult
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [JsonPropertyName("forwarded")]
        public bool Forwarded { get; set; }
    }

    #endregion
}
