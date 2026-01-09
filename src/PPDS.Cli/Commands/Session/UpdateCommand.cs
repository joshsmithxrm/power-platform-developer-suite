using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Session;

namespace PPDS.Cli.Commands.Session;

/// <summary>
/// Update session status (called by workers).
/// </summary>
public static class UpdateCommand
{
    public static Command Create()
    {
        var sessionOption = new Option<string>("--id", "-i")
        {
            Description = "Session ID (issue number)",
            Required = true
        };

        var statusOption = new Option<string>("--status", "-s")
        {
            Description = "New status: working, stuck, paused, complete",
            Required = true
        };

        var reasonOption = new Option<string?>("--reason", "-r")
        {
            Description = "Reason for status change (required for 'stuck')"
        };

        var prOption = new Option<string?>("--pr")
        {
            Description = "Pull request URL (for 'complete' status)"
        };

        var command = new Command("update", "Update session status (called by workers)")
        {
            sessionOption,
            statusOption,
            reasonOption,
            prOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var sessionId = parseResult.GetValue(sessionOption)!;
            var status = parseResult.GetValue(statusOption)!;
            var reason = parseResult.GetValue(reasonOption);
            var prUrl = parseResult.GetValue(prOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            return await ExecuteAsync(sessionId, status, reason, prUrl, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string sessionId,
        string statusStr,
        string? reason,
        string? prUrl,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            if (!Enum.TryParse<SessionStatus>(statusStr, true, out var status))
            {
                throw new ArgumentException($"Invalid status '{statusStr}'. Valid values: working, stuck, paused, complete, cancelled");
            }

            if (status == SessionStatus.Stuck && string.IsNullOrEmpty(reason))
            {
                throw new ArgumentException("Reason is required when setting status to 'stuck'");
            }

            var spawner = new WindowsTerminalWorkerSpawner();
            var logger = NullLogger<SessionService>.Instance;
            var service = new SessionService(spawner, logger);

            await service.UpdateAsync(sessionId, status, reason, prUrl, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                var output = new UpdateResult
                {
                    SessionId = sessionId,
                    Status = status.ToString().ToLowerInvariant(),
                    Updated = true
                };

                writer.WriteSuccess(output);
            }
            else
            {
                Console.WriteLine($"Session #{sessionId} updated to {status}.");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"updating session '{sessionId}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class UpdateResult
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("updated")]
        public bool Updated { get; set; }
    }

    #endregion
}
