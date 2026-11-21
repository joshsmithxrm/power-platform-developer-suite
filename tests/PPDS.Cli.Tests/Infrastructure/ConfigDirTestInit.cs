using System.Runtime.CompilerServices;
using PPDS.Auth.Profiles;

namespace PPDS.Cli.Tests.Infrastructure;

/// <summary>
/// Points <c>PPDS_CONFIG_DIR</c> at a temporary folder owned by this test run before any test
/// executes, so no unit test reads or writes the developer's real configuration folder: profiles,
/// query history, the TUI debug log and update-check files all land in the temporary folder.
/// Tests that change the variable restore this value when they finish. See issue #1850.
/// </summary>
internal static class ConfigDirTestInit
{
    /// <summary>The temporary configuration folder for this test run.</summary>
    internal static string ConfigDirectory { get; private set; } = string.Empty;

    [ModuleInitializer]
    internal static void Install()
    {
        ConfigDirectory = Path.Combine(
            Path.GetTempPath(),
            $"ppds-cli-tests-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ConfigDirectory);
        Environment.SetEnvironmentVariable(ProfilePaths.ConfigDirEnvVar, ConfigDirectory);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(ConfigDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of the temporary folder.
            }
        };
    }
}
