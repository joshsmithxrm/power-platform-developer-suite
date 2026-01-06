using System.CommandLine;
using PPDS.Cli.Infrastructure;

namespace PPDS.Cli.Commands;

/// <summary>
/// Opens CLI documentation in the default browser.
/// </summary>
public static class DocsCommand
{
    /// <summary>
    /// URL to the CLI documentation.
    /// </summary>
    public const string DocsUrl = "https://github.com/joshsmithxrm/ppds-sdk/blob/main/src/PPDS.Cli/README.md";

    /// <summary>
    /// Creates the 'docs' command.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("docs", "Open CLI documentation in browser");

        command.SetAction((parseResult, cancellationToken) =>
        {
            Console.Error.WriteLine($"Opening documentation: {DocsUrl}");
            BrowserHelper.OpenUrl(DocsUrl);
            return Task.FromResult(0);
        });

        return command;
    }
}
