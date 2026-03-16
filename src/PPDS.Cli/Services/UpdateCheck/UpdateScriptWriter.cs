namespace PPDS.Cli.Services.UpdateCheck;

/// <summary>
/// Generates platform-specific wrapper scripts for detached self-update.
/// The script waits for the parent ppds process to exit (unlocking .store DLLs),
/// then runs dotnet tool update, captures the result, and writes a status file.
/// </summary>
internal static class UpdateScriptWriter
{
    public static string WriteScript(
        string dotnetPath, string updateArgs, string? targetVersion,
        int parentPid, string statusPath, string lockPath)
    {
        var scriptDir = Path.GetTempPath();
        var scriptPath = OperatingSystem.IsWindows()
            ? Path.Combine(scriptDir, $"ppds-update-{Guid.NewGuid():N}.cmd")
            : Path.Combine(scriptDir, $"ppds-update-{Guid.NewGuid():N}.sh");

        var content = OperatingSystem.IsWindows()
            ? GenerateWindowsScript(dotnetPath, updateArgs, targetVersion, parentPid, statusPath, lockPath, scriptPath)
            : GenerateUnixScript(dotnetPath, updateArgs, targetVersion, parentPid, statusPath, lockPath, scriptPath);

        File.WriteAllText(scriptPath, content);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return scriptPath;
    }

    private static string GenerateWindowsScript(
        string dotnetPath, string updateArgs, string? targetVersion,
        int parentPid, string statusPath, string lockPath, string scriptPath)
    {
        var safeTarget = targetVersion ?? "unknown";
        // Use tasklist to poll for parent PID exit, then run update.
        // $$""" means {{ }} is interpolation, single { } are literal.
        return $$"""
            @echo off
            :wait
            tasklist /FI "PID eq {{parentPid}}" 2>NUL | find /I "{{parentPid}}" >NUL
            if not errorlevel 1 (
                timeout /t 1 /nobreak >NUL
                goto wait
            )
            "{{dotnetPath}}" {{updateArgs}}
            set EXIT_CODE=%ERRORLEVEL%
            if %EXIT_CODE% EQU 0 (
                echo {"success":true,"exitCode":0,"targetVersion":"{{safeTarget}}","timestamp":"%DATE%T%TIME%"} > "{{statusPath}}"
            ) else (
                echo {"success":false,"exitCode":%EXIT_CODE%,"targetVersion":"{{safeTarget}}","timestamp":"%DATE%T%TIME%"} > "{{statusPath}}"
            )
            del "{{lockPath}}" 2>NUL
            del "%~f0" 2>NUL
            """;
    }

    private static string GenerateUnixScript(
        string dotnetPath, string updateArgs, string? targetVersion,
        int parentPid, string statusPath, string lockPath, string scriptPath)
    {
        var safeTarget = targetVersion ?? "unknown";
        // $$""" means {{ }} is interpolation, single { } are literal.
        return $$"""
            #!/bin/sh
            while kill -0 {{parentPid}} 2>/dev/null; do sleep 1; done
            "{{dotnetPath}}" {{updateArgs}}
            EXIT_CODE=$?
            if [ $EXIT_CODE -eq 0 ]; then
                printf '{"success":true,"exitCode":0,"targetVersion":"{{safeTarget}}","timestamp":"%s"}' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "{{statusPath}}"
            else
                printf '{"success":false,"exitCode":%d,"targetVersion":"{{safeTarget}}","timestamp":"%s"}' "$EXIT_CODE" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "{{statusPath}}"
            fi
            rm -f "{{lockPath}}" "{{scriptPath}}"
            """;
    }
}
