using System.CommandLine;

namespace PPDS.Cli.Commands.Session;

/// <summary>
/// Command group for session orchestration operations.
/// </summary>
public static class SessionCommandGroup
{
    /// <summary>
    /// Creates the session command group.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("session", "Manage parallel worker sessions")
        {
            ListCommand.Create(),
            SpawnCommand.Create(),
            GetCommand.Create(),
            PauseCommand.Create(),
            ResumeCommand.Create(),
            CancelCommand.Create(),
            CancelAllCommand.Create(),
            ForwardCommand.Create(),
            UpdateCommand.Create()
        };

        return command;
    }
}
