using System.CommandLine;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using PPDS.Cli.Commands;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Security;

namespace PPDS.Cli.Commands.Logs;

/// <summary>
/// <c>ppds logs dump</c> — bundles everything a user needs for a bug report into a
/// timestamped zip file in the current directory. Captures:
/// <list type="bullet">
///   <item>all <c>*.log</c> files under <c>~/.ppds/</c> (TUI debug + stderr captures)</item>
///   <item>environment info (<c>ppds --version</c>, <c>dotnet --info</c>, OS description)</item>
///   <item>redacted <c>PPDS_*</c> environment variables</item>
/// </list>
/// Secrets are redacted <b>before</b> the zip is written using
/// <see cref="ConnectionStringRedactor"/> so leaked client secrets and tokens never
/// reach the archive the user attaches to their issue.
/// </summary>
public static class LogsDumpCommand
{
    /// <summary>
    /// Environment variable name prefixes that are safe to include without redaction.
    /// </summary>
    private static readonly string[] PpdsEnvPrefixes = ["PPDS_"];

    /// <summary>
    /// Sensitive substrings in env-var names that trigger whole-value redaction.
    /// Keep lowercase for case-insensitive matching.
    /// </summary>
    private static readonly string[] SensitiveNameMarkers =
    [
        "secret", "password", "pwd", "token", "apikey", "key",
        "credential", "cert", "clientsecret"
    ];

    /// <summary>
    /// Creates the 'logs dump' subcommand with a --output option for targeting a
    /// specific directory (otherwise defaults to the current working directory).
    /// </summary>
    public static Command Create()
    {
        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = "Directory to write the zip into (default: current directory)"
        };

        var command = new Command(
            "dump",
            "Bundle PPDS logs and diagnostics into a zip file for bug reports")
        {
            outputOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputDir = parseResult.GetValue(outputOption);
            return await ExecuteAsync(outputDir, cancellationToken);
        });

        return command;
    }

    /// <summary>
    /// Creates the diagnostics zip and writes its path to stdout on success.
    /// </summary>
    /// <param name="outputDir">Optional destination directory; defaults to <c>Environment.CurrentDirectory</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Exit code.</returns>
    public static Task<int> ExecuteAsync(string? outputDir, CancellationToken cancellationToken)
        => ExecuteAsync(outputDir, ppdsDirOverride: null, cancellationToken);

    /// <summary>
    /// Testable overload that lets callers substitute a fake <c>~/.ppds</c> directory.
    /// Production code should call the single-argument overload.
    /// </summary>
    internal static async Task<int> ExecuteAsync(
        string? outputDir,
        string? ppdsDirOverride,
        CancellationToken cancellationToken)
    {
        try
        {
            outputDir ??= Environment.CurrentDirectory;
            if (!Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            // UTC so the filename is unambiguous across timezones and matches the
            // "Generated:" line in diagnostics.txt (DateTimeOffset.UtcNow, ISO-8601).
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var zipName = $"ppds-diagnostics-{timestamp}.zip";
            var zipPath = Path.Combine(outputDir, zipName);

            var ppdsDir = ppdsDirOverride ?? LogsTailCommand.GetPpdsDirectory();

            // Build the zip into a temp path, then move — prevents a partial archive
            // from being left behind if the process is killed mid-write.
            var tempPath = zipPath + ".tmp";
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            using (var fs = new FileStream(tempPath, FileMode.CreateNew))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                await AddLogFilesAsync(archive, ppdsDir, cancellationToken);
                await AddDiagnosticsReportAsync(archive, cancellationToken);
                await AddEnvironmentVariablesAsync(archive, cancellationToken);
            }

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
            File.Move(tempPath, zipPath);

            // Stdout because the zip path is data a script may consume (e.g.,
            // `PPDS_ZIP=$(ppds logs dump)` to attach to an issue).
            Console.WriteLine(zipPath);
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Wrote {zipPath}");
            Console.Error.WriteLine("Review the contents before sharing — PPDS redacts known secrets but");
            Console.Error.WriteLine("cannot guarantee customer-specific data in log messages is removed.");
            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: Failed to create diagnostics zip: {ex.Message}");
            return ExitCodes.Failure;
        }
    }

    /// <summary>
    /// Copies each <c>~/.ppds/*.log</c> into the archive under <c>logs/</c>. Each file
    /// is streamed through <see cref="ConnectionStringRedactor"/> line-by-line so any
    /// accidentally-logged secret values are scrubbed before landing in the zip.
    /// </summary>
    private static async Task AddLogFilesAsync(
        ZipArchive archive,
        string ppdsDir,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(ppdsDir))
        {
            var missing = archive.CreateEntry("logs/README.txt");
            await using var writer = new StreamWriter(missing.Open());
            await writer.WriteAsync($"No log directory at {ppdsDir} at time of dump.\n");
            return;
        }

        // Sort so the archive has deterministic file ordering — matches LogsTailCommand
        // and makes diffs between two user-submitted dumps easier to reason about.
        var logFiles = Directory.EnumerateFiles(ppdsDir, "*.log", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var path in logFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            var entry = archive.CreateEntry($"logs/{name}", CompressionLevel.Optimal);

            try
            {
                await using var source = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);
                using var reader = new StreamReader(source);
                await using var destStream = entry.Open();
                await using var writer = new StreamWriter(destStream);

                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
                {
                    await writer.WriteLineAsync(ConnectionStringRedactor.RedactExceptionMessage(line));
                }
            }
            catch (IOException ex)
            {
                // Soft-fail on a locked or unreadable file — keep bundling the rest.
                await using var errStream = entry.Open();
                await using var errWriter = new StreamWriter(errStream);
                await errWriter.WriteLineAsync($"(could not read source file: {ex.Message})");
            }
        }
    }

    /// <summary>
    /// Captures <c>ppds --version</c> (from the running assembly), <c>dotnet --info</c>,
    /// and OS description into <c>diagnostics.txt</c>.
    /// </summary>
    private static async Task AddDiagnosticsReportAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry("diagnostics.txt", CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream);

        await writer.WriteLineAsync($"PPDS CLI v{ErrorOutput.Version}");
        await writer.WriteLineAsync($"SDK v{ErrorOutput.SdkVersion}");
        await writer.WriteLineAsync($".NET runtime: {Environment.Version}");
        await writer.WriteLineAsync($"OS: {RuntimeInformation.OSDescription}");
        await writer.WriteLineAsync($"Architecture: {RuntimeInformation.ProcessArchitecture}");
        await writer.WriteLineAsync($"Generated: {DateTimeOffset.UtcNow:O}");
        await writer.WriteLineAsync();

        await writer.WriteLineAsync("=== dotnet --info ===");
        var dotnetInfo = await RunExternalToolAsync("dotnet", "--info", cancellationToken);
        await writer.WriteLineAsync(dotnetInfo);
    }

    /// <summary>
    /// Serializes <c>PPDS_*</c> environment variables into <c>environment.txt</c>.
    /// Any variable whose <i>name</i> contains a sensitive marker (e.g., <c>PPDS_CLIENT_SECRET</c>)
    /// has its value replaced with <see cref="ConnectionStringRedactor.RedactedPlaceholder"/>.
    /// Non-sensitive values still flow through <see cref="ConnectionStringRedactor.RedactExceptionMessage"/>
    /// in case a secret snuck into the value body.
    /// </summary>
    private static async Task AddEnvironmentVariablesAsync(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entry = archive.CreateEntry("environment.txt", CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream);

        await writer.WriteLineAsync("PPDS-related environment variables (values redacted where sensitive):");
        await writer.WriteLineAsync();

        foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
        {
            var name = kv.Key as string ?? string.Empty;
            if (!PpdsEnvPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var rawValue = kv.Value as string ?? string.Empty;
            var redactedValue = IsSensitiveName(name)
                ? ConnectionStringRedactor.RedactedPlaceholder
                : ConnectionStringRedactor.RedactExceptionMessage(rawValue);

            await writer.WriteLineAsync($"{name}={redactedValue}");
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when the variable name looks like it holds a secret.
    /// </summary>
    internal static bool IsSensitiveName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var lower = name.ToLowerInvariant();
        return SensitiveNameMarkers.Any(marker => lower.Contains(marker));
    }

    /// <summary>
    /// Runs an external process and captures stdout+stderr. Swallows failures
    /// (including the tool being absent) so the dump still succeeds when
    /// <c>dotnet</c> isn't on PATH — rare, but it's better to ship a partial
    /// bundle than no bundle.
    /// </summary>
    private static async Task<string> RunExternalToolAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return $"(could not start: {fileName} {arguments})";
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Kill the tree so we don't orphan the child (and any grandchildren
                // it spawned). WaitForExitAsync only signals the wait, it does not
                // terminate the process.
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(stdout)) sb.Append(stdout);
            if (!string.IsNullOrEmpty(stderr))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append("[stderr]").AppendLine();
                sb.Append(stderr);
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"(failed: {ex.GetType().Name}: {ex.Message})";
        }
    }
}
