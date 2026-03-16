using System.CommandLine;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.UpdateCheck;

namespace PPDS.Cli.Commands;

/// <summary>
/// Displays version information for the PPDS CLI and optionally checks for updates.
/// </summary>
public static class VersionCommand
{
    /// <summary>
    /// Creates the 'version' command.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("version", "Show version information for the PPDS CLI");

        var checkOption = new Option<bool>("--check")
        {
            Description = "Check NuGet for the latest available version"
        };

        command.Options.Add(checkOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var runtimeVersion = Environment.Version.ToString();
            var platform = RuntimeInformation.OSDescription;

            Console.Error.WriteLine($"PPDS CLI v{ErrorOutput.Version}");
            Console.Error.WriteLine($"SDK v{ErrorOutput.SdkVersion}");
            Console.Error.WriteLine($".NET {runtimeVersion}");
            Console.Error.WriteLine($"Platform: {platform}");

            var check = parseResult.GetValue(checkOption);
            if (check)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("Checking for updates...");

                await using var localProvider = ProfileServiceFactory.CreateLocalProvider();
                var service = localProvider.GetRequiredService<IUpdateCheckService>();
                var result = await service.CheckAsync(ErrorOutput.Version, cancellationToken)
                    .ConfigureAwait(false);

                if (result is null)
                {
                    Console.Error.WriteLine("Unable to check for updates (network unavailable or check failed).");
                }
                else
                {
                    if (result.LatestStableVersion is not null)
                        Console.Error.WriteLine($"Latest stable:      {result.LatestStableVersion}");

                    if (result.LatestPreReleaseVersion is not null)
                        Console.Error.WriteLine($"Latest pre-release: {result.LatestPreReleaseVersion}");

                    if (result.UpdateAvailable && result.UpdateCommand is not null)
                    {
                        Console.Error.WriteLine();
                        Console.Error.WriteLine($"Update available! Run: {result.UpdateCommand}");
                    }
                    else
                    {
                        Console.Error.WriteLine();
                        Console.Error.WriteLine("You are up to date.");
                    }
                }
            }

            return ExitCodes.Success;
        });

        return command;
    }
}
