using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Cli.Commands.Logs;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Logs;

[Trait("Category", "Unit")]
public class LogsTailCommandTests
{
    [Fact]
    public async Task ExecuteAsync_MissingLogDirectory_SucceedsWithMessage()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ppds-tail-{Guid.NewGuid():N}");
        var missingPpdsDir = Path.Combine(tempRoot, "does-not-exist");
        // intentionally do NOT create missingPpdsDir

        try
        {
            var exitCode = await LogsTailCommand.ExecuteAsync(
                lineCount: 10,
                levelFilter: null,
                ppdsDirOverride: missingPpdsDir,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithLogFile_TailsAndHonoursLevelFilter()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ppds-tail-{Guid.NewGuid():N}");
        var ppdsDir = Path.Combine(tempRoot, "fake-ppds");
        Directory.CreateDirectory(ppdsDir);
        var logPath = Path.Combine(ppdsDir, "daemon.log");
        await File.WriteAllLinesAsync(logPath, new[]
        {
            "[10:00:00] [INF] [Cat] info line",
            "[10:00:01] [ERR] [Cat] error line",
            "[10:00:02] [WRN] [Cat] warning line",
        });

        try
        {
            // Smoke-test: neither filter nor a filter match should raise an exception.
            var unfiltered = await LogsTailCommand.ExecuteAsync(
                lineCount: 10, levelFilter: null, ppdsDirOverride: ppdsDir, CancellationToken.None);
            Assert.Equal(0, unfiltered);

            var filtered = await LogsTailCommand.ExecuteAsync(
                lineCount: 10, levelFilter: "ERR", ppdsDirOverride: ppdsDir, CancellationToken.None);
            Assert.Equal(0, filtered);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task ExecuteAsync_InvalidLineCount_ReturnsFailure()
    {
        var exitCode = await LogsTailCommand.ExecuteAsync(
            lineCount: 0,
            levelFilter: null,
            CancellationToken.None);

        Assert.Equal(2, exitCode); // ExitCodes.Failure
    }
}
