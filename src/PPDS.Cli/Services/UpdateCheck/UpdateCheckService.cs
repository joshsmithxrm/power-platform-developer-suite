using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using PPDS.Cli.Commands;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.UpdateCheck;

/// <summary>
/// Application service for checking available updates to the PPDS CLI via the NuGet flat-container API.
/// </summary>
/// <remarks>
/// <para>Results are cached for 24 hours at <c>~/.ppds/update-check.json</c>.</para>
/// <para>
/// The constructor accepts optional <paramref name="handler"/> and <paramref name="cachePath"/>
/// parameters to facilitate testing without live network or filesystem side-effects.
/// </para>
/// </remarks>
public sealed class UpdateCheckService : IUpdateCheckService
{
    private const string NuGetIndexUrl =
        "https://api.nuget.org/v3-flatcontainer/ppds.cli/index.json";

    private const string UpdateCommand = "dotnet tool update PPDS.Cli -g";
    private const string UpdateCommandPreRelease = "dotnet tool update PPDS.Cli -g --prerelease";

    /// <summary>How long a cached result is considered fresh. Shared with <see cref="Infrastructure.StartupUpdateNotifier"/>.</summary>
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpMessageHandler? _handler;
    private readonly string _cachePath;
    private readonly string _statusPath;
    private readonly string _lockPath;

    /// <summary>
    /// Initializes the service for production use with default HTTP and cache path.
    /// </summary>
    public UpdateCheckService()
        : this(handler: null, cachePath: null, statusPath: null)
    {
    }

    /// <summary>
    /// Initializes the service with optional overrides for testing.
    /// </summary>
    /// <param name="handler">
    /// Optional <see cref="HttpMessageHandler"/> for mocking HTTP calls.
    /// Pass <see langword="null"/> to use the default handler.
    /// </param>
    /// <param name="cachePath">
    /// Optional full path to the cache JSON file.
    /// Pass <see langword="null"/> to use the default <c>~/.ppds/update-check.json</c>.
    /// </param>
    /// <param name="statusPath">
    /// Optional full path to the update status JSON file.
    /// Pass <see langword="null"/> to use the default <c>~/.ppds/update-status.json</c>.
    /// </param>
    public UpdateCheckService(
        HttpMessageHandler? handler = null,
        string? cachePath = null,
        string? statusPath = null)
    {
        _handler = handler;
        var ppdsDir = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".ppds");
        _cachePath = cachePath ?? Path.Combine(ppdsDir, "update-check.json");
        _statusPath = statusPath ?? Path.Combine(ppdsDir, "update-status.json");
        _lockPath = Path.Combine(ppdsDir, "update.lock");
    }

    /// <inheritdoc/>
    public async Task<UpdateCheckResult?> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        List<string>? versions;

        try
        {
            versions = await FetchVersionsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Network or parse failures must not propagate to callers
            return null;
        }

        if (versions is null)
            return null;

        var current = TryParseVersion(currentVersion);

        // Always populate both versions regardless of user's track (AC-19).
        // Track-based filtering is a presentation concern (notifier/command).
        var latestStable = versions
            .Select(TryParseVersion)
            .Where(v => v is not null && !v.IsPreRelease)
            .OrderDescending()
            .FirstOrDefault();

        var latestPreRelease = versions
            .Select(TryParseVersion)
            .Where(v => v is not null && v.IsPreRelease)
            .OrderDescending()
            .FirstOrDefault();

        // Honestly computed — no track filtering at the data model level
        var stableUpdateAvailable = latestStable is not null
            && (current is null || latestStable > current);

        var preReleaseUpdateAvailable = latestPreRelease is not null
            && (current is null || latestPreRelease > current);

        // Primary command: stable if available, else pre-release
        string? command = null;
        if (stableUpdateAvailable)
            command = UpdateCommand;
        else if (preReleaseUpdateAvailable)
            command = UpdateCommandPreRelease;

        // Pre-release command: populated when a pre-release update exists
        string? preReleaseUpdateCommand = preReleaseUpdateAvailable
            ? UpdateCommandPreRelease
            : null;

        var result = new UpdateCheckResult
        {
            CurrentVersion = currentVersion,
            LatestStableVersion = latestStable?.ToString(),
            LatestPreReleaseVersion = latestPreRelease?.ToString(),
            StableUpdateAvailable = stableUpdateAvailable,
            PreReleaseUpdateAvailable = preReleaseUpdateAvailable,
            UpdateCommand = command,
            PreReleaseUpdateCommand = preReleaseUpdateCommand,
            CheckedAt = DateTimeOffset.UtcNow
        };

        await WriteCacheAsync(result, cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <inheritdoc/>
    public UpdateCheckResult? GetCachedResult()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return null;

            var json = File.ReadAllText(_cachePath);
            var result = JsonSerializer.Deserialize<UpdateCheckResult>(json, JsonOptions);

            if (result is null)
                return null;

            // Honour TTL
            if (DateTimeOffset.UtcNow - result.CheckedAt > CacheTtl)
                return null;

            return result;
        }
        catch
        {
            // Corrupt, missing, or inaccessible cache is not an error condition
            return null;
        }
    }

    /// <inheritdoc/>
    public void RefreshCacheInBackgroundIfStale(string currentVersion)
    {
        try
        {
            // Check cache freshness synchronously before spawning a task
            var cached = GetCachedResult();
            if (cached is not null)
                return; // Cache is fresh — no refresh needed

            // Fire-and-forget: no await, no CancellationToken (R2)
            _ = Task.Run(async () =>
            {
                try
                {
                    await CheckAsync(currentVersion, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort — never surface to caller
                }
            });
        }
        catch
        {
            // Swallow errors in freshness check
        }
    }

    /// <inheritdoc/>
    public async Task<UpdateResult> UpdateAsync(
        UpdateChannel channel,
        CancellationToken cancellationToken = default)
    {
        // Guard: check for existing update-in-progress (AC-52)
        if (File.Exists(_lockPath))
        {
            try
            {
                var pidStr = File.ReadAllText(_lockPath).Trim();
                if (int.TryParse(pidStr, out var pid))
                {
                    try
                    {
                        using var proc = Process.GetProcessById(pid);
                        // Process still running — update in progress
                        return new UpdateResult
                        {
                            Success = false,
                            ErrorMessage = "An update is already in progress. Try again later."
                        };
                    }
                    catch (ArgumentException)
                    {
                        // PID doesn't exist — stale lock, clean up
                        File.Delete(_lockPath);
                    }
                }
                else
                {
                    File.Delete(_lockPath); // corrupt lock
                }
            }
            catch
            {
                // Ignore lock file read errors
            }
        }

        // Step 1: Determine target version
        var cached = GetCachedResult();
        if (cached is null)
        {
            cached = await CheckAsync(
                ErrorOutput.Version, cancellationToken).ConfigureAwait(false);
        }

        if (cached is null)
        {
            throw new PpdsException(
                ErrorCodes.UpdateCheck.NetworkError,
                "Cannot determine latest version. Check your network connection.");
        }

        if (!cached.StableUpdateAvailable && !cached.PreReleaseUpdateAvailable)
        {
            return new UpdateResult
            {
                Success = true,
                ErrorMessage = "Already up to date.",
                InstalledVersion = cached.CurrentVersion
            };
        }

        // Step 2: Detect install type
        if (!IsGlobalToolInstall())
        {
            return new UpdateResult
            {
                IsNonGlobalInstall = true,
                ManualCommand = "dotnet tool update PPDS.Cli",
                ErrorMessage = "PPDS is installed as a local tool. Update your tool manifest manually."
            };
        }

        // Step 3: Determine command based on channel
        var usePreRelease = channel switch
        {
            UpdateChannel.Stable => false,
            UpdateChannel.PreRelease => true,
            UpdateChannel.Current => NuGetVersion.TryParse(
                cached.CurrentVersion, out var cv) && cv!.IsOddMinor,
            _ => false
        };

        var targetVersion = usePreRelease
            ? cached.LatestPreReleaseVersion
            : cached.LatestStableVersion;

        var updateArgs = usePreRelease
            ? "tool update PPDS.Cli -g --prerelease"
            : "tool update PPDS.Cli -g";

        // Step 4: Resolve dotnet path (S2: no shell: true on user input)
        var dotnetPath = ResolveDotnetPath();
        if (dotnetPath is null)
        {
            throw new PpdsException(
                ErrorCodes.UpdateCheck.DotnetNotFound,
                "Cannot locate the dotnet runtime. Run manually: dotnet tool update PPDS.Cli -g");
        }

        // Step 5: Spawn detached update via wrapper script
        SpawnDetachedUpdate(dotnetPath, updateArgs, targetVersion);

        return new UpdateResult
        {
            Success = true,
            InstalledVersion = targetVersion
        };
    }

    #region Private Helpers

    /// <summary>
    /// Fetches version strings from the NuGet flat-container index.
    /// Returns <see langword="null"/> on non-success HTTP status.
    /// Throws on network/parse errors (caller handles).
    /// </summary>
    private async Task<List<string>?> FetchVersionsAsync(CancellationToken cancellationToken)
    {
        // R1: dispose HttpClient after each call — do not hold as a field
        using var client = _handler is not null
            ? new HttpClient(_handler, disposeHandler: false)
            : new HttpClient();

        client.Timeout = HttpTimeout;

        using var response = await client
            .GetAsync(NuGetIndexUrl, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        var envelope = JsonSerializer.Deserialize<NuGetVersionsEnvelope>(json, JsonOptions);
        return envelope?.Versions;
    }

    /// <summary>
    /// Persists <paramref name="result"/> to the cache file using an atomic write.
    /// Failures are silently swallowed — cache write errors must not surface to callers.
    /// </summary>
    private async Task WriteCacheAsync(
        UpdateCheckResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(result, JsonOptions);
            var tempPath = _cachePath + ".tmp";

            await File.WriteAllTextAsync(tempPath, json, cancellationToken)
                .ConfigureAwait(false);

            // Atomic replace (prevents corrupt reads if the process exits mid-write)
            File.Move(tempPath, _cachePath, overwrite: true);
        }
        catch
        {
            // Cache write failure is not fatal
        }
    }

    private static NuGetVersion? TryParseVersion(string? v)
    {
        NuGetVersion.TryParse(v, out var parsed);
        return parsed;
    }

    /// <summary>
    /// Determines whether the CLI is running as a global dotnet tool by checking
    /// if <see cref="AppContext.BaseDirectory"/> resides under <c>~/.dotnet/tools</c>.
    /// </summary>
    internal static bool IsGlobalToolInstall()
    {
        var baseDir = AppContext.BaseDirectory;
        var dotnetToolsDir = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            ".dotnet", "tools");
        return baseDir.StartsWith(dotnetToolsDir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the full path to the <c>dotnet</c> executable.
    /// Uses <see cref="System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory"/>
    /// and navigates up 3 levels (avoiding <c>Process.MainModule</c> which returns the apphost shim).
    /// Falls back to <c>DOTNET_ROOT</c> and platform defaults.
    /// </summary>
    internal static string? ResolveDotnetPath()
    {
        var dotnetExeName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

        // Primary: navigate up from runtime directory
        try
        {
#pragma warning disable SYSLIB0019 // RuntimeEnvironment is obsolete but reliable for this purpose
            var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
#pragma warning restore SYSLIB0019
            var dotnetFromRuntime = Path.GetFullPath(
                Path.Combine(runtimeDir, "..", "..", "..", dotnetExeName));
            if (File.Exists(dotnetFromRuntime))
                return dotnetFromRuntime;
        }
        catch
        {
            // Fall through to alternatives
        }

        // Fallback: DOTNET_ROOT
        var dotnetRoot = System.Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot))
        {
            var dotnetExe = Path.Combine(dotnetRoot, dotnetExeName);
            if (File.Exists(dotnetExe))
                return dotnetExe;
        }

        // Platform defaults
        if (OperatingSystem.IsWindows())
        {
            var pf = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles),
                "dotnet", "dotnet.exe");
            if (File.Exists(pf)) return pf;
        }
        else
        {
            foreach (var p in new[] { "/usr/local/share/dotnet/dotnet", "/usr/share/dotnet/dotnet" })
                if (File.Exists(p)) return p;
        }

        return null;
    }

    /// <summary>
    /// Writes a platform-specific wrapper script and spawns it as a detached process.
    /// The script waits for the parent PID to exit, then runs dotnet tool update.
    /// </summary>
    /// <remarks>
    /// S2 justification: We use <c>cmd.exe /c</c> (Windows) or <c>/bin/sh</c> (Unix) to execute
    /// a script that we generate ourselves. The shell is not interpreting user input — the script
    /// content is fully controlled by this code, and the dotnet invocation uses a fully qualified path.
    /// </remarks>
    private void SpawnDetachedUpdate(string dotnetPath, string updateArgs, string? targetVersion)
    {
        var parentPid = System.Environment.ProcessId;
        var scriptPath = UpdateScriptWriter.WriteScript(
            dotnetPath, updateArgs, targetVersion, parentPid, _statusPath, _lockPath);

        var psi = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows()
                ? $"/c \"{scriptPath}\""
                : scriptPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };

        try
        {
            var process = Process.Start(psi);
            if (process is not null)
            {
                File.WriteAllText(_lockPath, process.Id.ToString());
                process.Dispose(); // R1: don't hold the process handle
            }
        }
        catch (Exception ex)
        {
            throw new PpdsException(
                ErrorCodes.UpdateCheck.UpdateFailed,
                $"Failed to start update process: {ex.Message}");
        }
    }

    #endregion

    #region API Response Model

    /// <summary>Response envelope from <c>https://api.nuget.org/v3-flatcontainer/{id}/index.json</c>.</summary>
    private sealed class NuGetVersionsEnvelope
    {
        [JsonPropertyName("versions")]
        public List<string>? Versions { get; init; }
    }

    #endregion
}
