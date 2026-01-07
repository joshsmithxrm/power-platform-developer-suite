using System.CommandLine;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Tui;
using Spectre.Console;

namespace PPDS.Cli.Commands;

/// <summary>
/// Launches interactive TUI mode for profile/environment selection and SQL querying.
/// </summary>
/// <remarks>
/// Also accessible via 'ppds -i' or 'ppds --interactive' (handled in Program.cs).
/// </remarks>
public static class InteractiveCommand
{
    /// <summary>
    /// Creates the 'interactive' command.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("interactive", "Launch interactive TUI mode for profile selection and SQL queries");

        command.SetAction((parseResult, cancellationToken) =>
        {
            // Check if we're in a TTY environment
            if (!AnsiConsole.Profile.Capabilities.Interactive)
            {
                Console.Error.WriteLine("Error: Interactive mode requires a terminal (TTY).");
                Console.Error.WriteLine("This may occur in CI/CD pipelines, redirected input, or non-interactive shells.");
                Console.Error.WriteLine();
                Console.Error.WriteLine("Use standard CLI commands instead:");
                Console.Error.WriteLine("  ppds auth list       - List profiles");
                Console.Error.WriteLine("  ppds auth select     - Select a profile");
                Console.Error.WriteLine("  ppds env list        - List environments");
                Console.Error.WriteLine("  ppds env select      - Select an environment");
                return Task.FromResult(ExitCodes.Failure);
            }

            try
            {
                var app = new PpdsApplication(
                    profileName: null, // Uses active profile
                    deviceCodeCallback: ProfileServiceFactory.DefaultDeviceCodeCallback);

                return Task.FromResult(app.Run());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Interactive mode error: {ex}");
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
                return Task.FromResult(ExitCodes.Failure);
            }
        });

        return command;
    }
}
