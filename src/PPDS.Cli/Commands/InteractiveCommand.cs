using System.CommandLine;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Tui;
using PPDS.Cli.Tui.Infrastructure;
using Spectre.Console;
using TuiApp = Terminal.Gui.Application;
using MessageBox = Terminal.Gui.MessageBox;

namespace PPDS.Cli.Commands;

/// <summary>
/// Launches interactive TUI mode for profile/environment selection and SQL querying.
/// </summary>
/// <remarks>
/// Also accessible via 'ppds' (no args) which calls <see cref="LaunchTui"/>.
/// </remarks>
public static class InteractiveCommand
{
    /// <summary>
    /// Creates the 'interactive' command.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("interactive", "Launch interactive TUI mode for profile selection and SQL queries");

        command.SetAction((parseResult, cancellationToken) => Task.FromResult(LaunchTui(cancellationToken)));

        return command;
    }

    /// <summary>
    /// Launches the Terminal.Gui TUI application.
    /// </summary>
    /// <remarks>
    /// Called by both 'ppds' (no args) and 'ppds interactive' command.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Exit code (0 for success).</returns>
    public static int LaunchTui(CancellationToken cancellationToken = default)
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
            return ExitCodes.Failure;
        }

        try
        {
            // TUI-aware device code callback - shows MessageBox instead of console output
            // NOTE: Cannot use nested Application.Run() as Terminal.Gui event loops aren't reentrant
            using var app = new PpdsApplication(
                profileName: null, // Uses active profile
                deviceCodeCallback: info => TuiApp.MainLoop?.Invoke(() =>
                {
                    TuiDebugLog.Log($"Device code auth requested: {info.UserCode}");

                    // Auto-copy code to clipboard for convenience
                    var copied = ClipboardHelper.CopyToClipboard(info.UserCode) ? " (copied!)" : "";

                    // MessageBox is safe from MainLoop.Invoke - doesn't start nested event loop
                    MessageBox.Query(
                        "Authentication Required",
                        $"Visit: {info.VerificationUrl}\n\n" +
                        $"Enter code: {info.UserCode}{copied}\n\n" +
                        "Complete authentication in browser, then press OK.",
                        "OK");

                    TuiDebugLog.Log("Device code dialog closed");
                }));

            return app.Run(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // User cancelled - not an error
            return ExitCodes.Success;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Interactive mode error: {ex.Message}");
            return ExitCodes.Failure;
        }
    }
}
