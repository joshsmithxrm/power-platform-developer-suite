using PPDS.Auth.Profiles;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// The one place that resolves where the CLI keeps its own non-credential files:
/// query history, log files and update-check state.
/// The self-update lock (<see cref="UpdateLockFile"/>) is the one exception to the folder rule below.
/// </summary>
/// <remarks>
/// <para>
/// When <c>PPDS_CONFIG_DIR</c> (<see cref="ProfilePaths.ConfigDirEnvVar"/>) is set, every path here is
/// under that folder, the same folder <see cref="ProfilePaths.DataDirectory"/> uses for profiles.
/// </para>
/// <para>
/// When it is not set, the folder is <c>&lt;user profile&gt;/.ppds</c> on every platform. This is
/// deliberately not <see cref="ProfilePaths.DataDirectory"/>: on Windows that defaults to
/// <c>%LOCALAPPDATA%\PPDS</c>, while history, logs and update state have always lived in
/// <c>%USERPROFILE%\.ppds</c>. Keeping the default unchanged means no user's files move.
/// </para>
/// <para>
/// Every property is evaluated on each access, so a change to the variable is seen immediately.
/// </para>
/// </remarks>
internal static class CliDataPaths
{
    /// <summary>Name of the query-history folder under <see cref="DataDirectory"/>.</summary>
    public const string HistoryDirectoryName = "history";

    /// <summary>File name of the TUI debug log.</summary>
    public const string TuiDebugLogFileName = "tui-debug.log";

    /// <summary>File name of the update-check cache.</summary>
    public const string UpdateCheckFileName = "update-check.json";

    /// <summary>File name of the post-update status written by the detached updater.</summary>
    public const string UpdateStatusFileName = "update-status.json";

    /// <summary>File name of the self-update lock.</summary>
    public const string UpdateLockFileName = "update.lock";

    /// <summary>
    /// <c>PPDS_CONFIG_DIR</c> when set (not blank); otherwise <c>&lt;user profile&gt;/.ppds</c>.
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            var configDir = Environment.GetEnvironmentVariable(ProfilePaths.ConfigDirEnvVar);
            if (!string.IsNullOrWhiteSpace(configDir))
            {
                // Same rule as ProfilePaths.DataDirectory, so both resolve to the same folder.
                return ProfilePaths.DataDirectory;
            }

            return DefaultDataDirectory;
        }
    }

    /// <summary>
    /// <c>&lt;user profile&gt;/.ppds</c>, whatever <c>PPDS_CONFIG_DIR</c> says.
    /// </summary>
    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ppds");

    /// <summary>Folder holding one query-history file per environment.</summary>
    public static string HistoryDirectory => Path.Combine(DataDirectory, HistoryDirectoryName);

    /// <summary>Full path of the TUI debug log.</summary>
    public static string TuiDebugLogFile => Path.Combine(DataDirectory, TuiDebugLogFileName);

    /// <summary>Full path of the update-check cache.</summary>
    public static string UpdateCheckFile => Path.Combine(DataDirectory, UpdateCheckFileName);

    /// <summary>Full path of the post-update status file.</summary>
    public static string UpdateStatusFile => Path.Combine(DataDirectory, UpdateStatusFileName);

    /// <summary>
    /// Full path of the self-update lock file. Always under <see cref="DefaultDataDirectory"/>,
    /// not <c>PPDS_CONFIG_DIR</c>.
    /// </summary>
    /// <remarks>
    /// The lock guards <c>dotnet tool update -g</c>, which replaces the one global-tool install in
    /// the user profile folder. Every configuration folder shares that install, so they must share
    /// one lock; a lock per configuration folder would let two shells update it at the same time.
    /// </remarks>
    public static string UpdateLockFile => Path.Combine(DefaultDataDirectory, UpdateLockFileName);
}
