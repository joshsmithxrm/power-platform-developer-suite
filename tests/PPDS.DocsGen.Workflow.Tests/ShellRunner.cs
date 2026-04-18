using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PPDS.DocsGen.Workflow.Tests;

/// <summary>
/// Helpers for invoking the Phase 5 shell scripts from xunit tests. On Windows
/// we route through <c>bash</c> (the MSYS/Git-for-Windows copy that ships with
/// <c>git</c>); on Linux/macOS the script is invoked directly.
/// </summary>
/// <remarks>
/// Dependency: the <c>bash</c> binary must be on <c>PATH</c>. All PPDS
/// contributor machines install Git for Windows, which provides
/// <c>C:\Program Files\Git\usr\bin\bash.exe</c>. CI runners
/// (<c>ubuntu-latest</c>) have <c>bash</c> natively.
/// </remarks>
internal static class ShellRunner
{
    public record Result(int ExitCode, string Stdout, string Stderr)
    {
        public string Combined => (Stdout + "\n" + Stderr).Trim();
    }

    public static string ScriptsDir =>
        Path.Combine(RepoRoot.Find(), "scripts", "docs-gen");

    /// <summary>
    /// Resolve a bash binary that understands Windows-style paths. Prefers
    /// Git-for-Windows bash (<c>C:\Program Files\Git\bin\bash.exe</c>) so the
    /// tests do not get routed into WSL — WSL interprets backslash paths
    /// incorrectly and mangles <c>C:\Users\...</c> to <c>C:Users...</c>.
    /// </summary>
    public static string BashPath { get; } = ResolveBash();

    private static string ResolveBash()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string[] candidates =
            {
                @"C:\Program Files\Git\bin\bash.exe",
                @"C:\Program Files\Git\usr\bin\bash.exe",
                @"C:\Program Files (x86)\Git\bin\bash.exe",
            };

            foreach (var c in candidates)
            {
                if (File.Exists(c))
                {
                    return c;
                }
            }
        }

        // On Linux/macOS (CI) plain "bash" resolves correctly; the Windows
        // fallback here is last-resort (hits whatever is on PATH, possibly
        // WSL, which may misparse Windows paths).
        return "bash";
    }

    public static Result RunScript(
        string scriptName,
        IEnumerable<string> args,
        string? workingDir = null,
        IDictionary<string, string?>? env = null)
    {
        var scriptPath = Path.Combine(ScriptsDir, scriptName);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException(
                $"script not found at {scriptPath}", scriptPath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = BashPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir ?? RepoRoot.Find(),
        };

        psi.ArgumentList.Add(scriptPath);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        if (env is not null)
        {
            foreach (var (k, v) in env)
            {
                if (v is null)
                {
                    psi.Environment.Remove(k);
                }
                else
                {
                    psi.Environment[k] = v;
                }
            }
        }

        return RunAndCollect(psi, 60_000);
    }

    public static Result RunNode(
        string scriptRelativeToRepo,
        IEnumerable<string> args,
        string? workingDir = null)
    {
        var scriptPath = Path.Combine(RepoRoot.Find(), scriptRelativeToRepo);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException(
                $"node script not found at {scriptPath}", scriptPath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir ?? RepoRoot.Find(),
        };

        psi.ArgumentList.Add(scriptPath);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        return RunAndCollect(psi, 60_000);
    }

    public static Result RunGit(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir,
        };

        // Force a deterministic identity + no GPG signing inside temp repos.
        psi.Environment["GIT_AUTHOR_NAME"] = "Fixture";
        psi.Environment["GIT_AUTHOR_EMAIL"] = "fixture@example.invalid";
        psi.Environment["GIT_COMMITTER_NAME"] = "Fixture";
        psi.Environment["GIT_COMMITTER_EMAIL"] = "fixture@example.invalid";

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        return RunAndCollect(psi, 30_000);
    }

    /// <summary>
    /// Spawn the process, drain stdout and stderr concurrently (avoids the
    /// classic Windows deadlock where the child blocks writing to a full
    /// stderr pipe while the parent is still synchronously reading stdout),
    /// and return a <see cref="Result"/>. If the process does not exit in
    /// <paramref name="timeoutMs"/> we kill it and fail loudly.
    /// </summary>
    private static Result RunAndCollect(ProcessStartInfo psi, int timeoutMs)
    {
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start process");

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
            throw new TimeoutException(
                $"process {psi.FileName} did not exit in {timeoutMs}ms");
        }

        // Now that the process has exited, the pipes have closed and the
        // async readers will complete promptly.
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        return new Result(p.ExitCode, stdout, stderr);
    }

    public static string MakeTempDir(string suffix)
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            $"ppds-docsgen-workflow-tests-{suffix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void DeleteQuietly(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return;
        }

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
