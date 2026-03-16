using System.CommandLine;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.UpdateCheck;

namespace PPDS.Cli.Commands;

/// <summary>
/// Displays version information for the PPDS CLI and optionally checks for updates or self-updates.
/// </summary>
public static class VersionCommand
{
    public static Command Create()
    {
        var command = new Command("version", "Show version information for the PPDS CLI");

        var checkOption = new Option<bool>("--check")
        {
            Description = "Check NuGet for the latest available version"
        };

        var updateOption = new Option<bool>("--update")
        {
            Description = "Update PPDS CLI to the latest version"
        };

        var stableOption = new Option<bool>("--stable")
        {
            Description = "Force update to latest stable version"
        };

        var preReleaseOption = new Option<bool>("--prerelease")
        {
            Description = "Force update to latest pre-release version"
        };

        var yesOption = new Option<bool>("--yes", new[] { "-y" })
        {
            Description = "Skip confirmation prompt"
        };

        command.Options.Add(checkOption);
        command.Options.Add(updateOption);
        command.Options.Add(stableOption);
        command.Options.Add(preReleaseOption);
        command.Options.Add(yesOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var runtimeVersion = System.Environment.Version.ToString();
            var platform = RuntimeInformation.OSDescription;

            Console.Error.WriteLine($"PPDS CLI v{ErrorOutput.Version}");
            Console.Error.WriteLine($"SDK v{ErrorOutput.SdkVersion}");
            Console.Error.WriteLine($".NET {runtimeVersion}");
            Console.Error.WriteLine($"Platform: {platform}");

            var update = parseResult.GetValue(updateOption);
            var stable = parseResult.GetValue(stableOption);
            var preRelease = parseResult.GetValue(preReleaseOption);
            var yes = parseResult.GetValue(yesOption);
            var check = parseResult.GetValue(checkOption);

            // Validate mutual exclusivity (AC-49)
            if (stable && preRelease)
            {
                Console.Error.WriteLine("Error: --stable and --prerelease are mutually exclusive.");
                return ExitCodes.InvalidArguments;
            }

            if (update || stable || preRelease)
            {
                return await HandleUpdateAsync(stable, preRelease, yes, cancellationToken);
            }

            if (check)
            {
                return await HandleCheckAsync(cancellationToken);
            }

            return ExitCodes.Success;
        });

        return command;
    }

    private static async Task<int> HandleCheckAsync(CancellationToken cancellationToken)
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
            return ExitCodes.Success;
        }

        if (result.LatestStableVersion is not null)
            Console.Error.WriteLine($"Latest stable:      {result.LatestStableVersion}");

        if (result.LatestPreReleaseVersion is not null)
            Console.Error.WriteLine($"Latest pre-release: {result.LatestPreReleaseVersion}");

        if (result.UpdateAvailable && result.UpdateCommand is not null)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Update available! Run: {result.UpdateCommand}");
            if (result.PreReleaseUpdateCommand is not null)
                Console.Error.WriteLine($"Pre-release available! Run: {result.PreReleaseUpdateCommand}");
        }
        else
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("You are up to date.");
        }

        return ExitCodes.Success;
    }

    private static async Task<int> HandleUpdateAsync(
        bool forceStable,
        bool forcePreRelease,
        bool skipConfirmation,
        CancellationToken cancellationToken)
    {
        var channel = forceStable ? UpdateChannel.Stable
            : forcePreRelease ? UpdateChannel.PreRelease
            : UpdateChannel.Current;

        Console.Error.WriteLine();
        Console.Error.WriteLine("Checking for updates...");

        try
        {
            await using var localProvider = ProfileServiceFactory.CreateLocalProvider();
            var service = localProvider.GetRequiredService<IUpdateCheckService>();
            var result = await service.UpdateAsync(channel, cancellationToken)
                .ConfigureAwait(false);

            if (result.IsNonGlobalInstall)
            {
                Console.Error.WriteLine(result.ErrorMessage);
                if (result.ManualCommand is not null)
                    Console.Error.WriteLine($"  Run: {result.ManualCommand}");
                return ExitCodes.Success;
            }

            if (result.Success && result.ErrorMessage?.Contains("up to date",
                StringComparison.OrdinalIgnoreCase) == true)
            {
                Console.Error.WriteLine("You are already up to date.");
                return ExitCodes.Success;
            }

            if (!skipConfirmation && result.InstalledVersion is not null)
            {
                Console.Error.Write($"Update to {result.InstalledVersion}? [Y/n] ");
                var response = Console.ReadLine();
                if (!string.IsNullOrEmpty(response) &&
                    !response.StartsWith("y", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("Update cancelled.");
                    return ExitCodes.Success;
                }
            }

            Console.Error.WriteLine($"Updating to {result.InstalledVersion}...");
            Console.Error.WriteLine("The update will complete in the background.");
            return ExitCodes.Success;
        }
        catch (PpdsException ex)
        {
            Console.Error.WriteLine($"Error: {ex.UserMessage}");
            if (ex.Context?.TryGetValue("manualCommand", out var cmd) == true)
                Console.Error.WriteLine($"  Run manually: {cmd}");
            return ExitCodes.Failure;
        }
    }
}
