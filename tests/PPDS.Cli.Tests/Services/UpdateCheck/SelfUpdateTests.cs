using PPDS.Cli.Services.UpdateCheck;
using Xunit;

namespace PPDS.Cli.Tests.Services.UpdateCheck;

public sealed class SelfUpdateTests
{
    [Fact]
    public void ResolveDotnetPath_FindsDotnet()
    {
        var path = UpdateCheckService.ResolveDotnetPath();
        Assert.NotNull(path);
        Assert.True(File.Exists(path), $"Dotnet not found at: {path}");
        Assert.Contains("dotnet", Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveDotnetPath_DoesNotReturnAppHostShim()
    {
        var path = UpdateCheckService.ResolveDotnetPath();
        Assert.NotNull(path);
        // Must NOT return ppds.exe (the apphost shim)
        Assert.DoesNotContain("ppds", Path.GetFileName(path!), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsGlobalToolInstall_ReturnsBool()
    {
        // In test environment, not running as global tool
        var result = UpdateCheckService.IsGlobalToolInstall();
        Assert.IsType<bool>(result);
    }

    [Fact]
    public void UpdateScriptWriter_GeneratesScript()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ppds-test-{Guid.NewGuid():N}");
        var statusPath = Path.Combine(tempDir, "status.json");
        var lockPath = Path.Combine(tempDir, "update.lock");

        try
        {
            var scriptPath = UpdateScriptWriter.WriteScript(
                "dotnet", "tool update PPDS.Cli -g", "1.0.0",
                12345, statusPath, lockPath);

            Assert.True(File.Exists(scriptPath));
            var content = File.ReadAllText(scriptPath);
            Assert.Contains("dotnet", content);
            Assert.Contains("tool update PPDS.Cli -g", content);
            Assert.Contains("12345", content); // parent PID
            Assert.Contains(statusPath, content);

            if (OperatingSystem.IsWindows())
                Assert.EndsWith(".cmd", scriptPath);
            else
                Assert.EndsWith(".sh", scriptPath);

            // Cleanup
            File.Delete(scriptPath);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void UpdateScriptWriter_NullTargetVersion_UsesUnknown()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ppds-test-{Guid.NewGuid():N}");
        var statusPath = Path.Combine(tempDir, "status.json");
        var lockPath = Path.Combine(tempDir, "update.lock");

        try
        {
            var scriptPath = UpdateScriptWriter.WriteScript(
                "dotnet", "tool update PPDS.Cli -g", null,
                12345, statusPath, lockPath);

            var content = File.ReadAllText(scriptPath);
            Assert.Contains("unknown", content);

            // Cleanup
            File.Delete(scriptPath);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
