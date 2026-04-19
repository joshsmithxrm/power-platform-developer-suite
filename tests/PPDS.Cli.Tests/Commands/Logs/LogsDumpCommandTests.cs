using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Cli.Commands.Logs;
using PPDS.Dataverse.Security;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Logs;

[Trait("Category", "Unit")]
public class LogsDumpCommandTests
{
    [Theory]
    [InlineData("PPDS_CLIENT_SECRET", true)]
    [InlineData("PPDS_MY_PASSWORD", true)]
    [InlineData("PPDS_AUTH_TOKEN", true)]
    [InlineData("PPDS_API_KEY", true)]
    [InlineData("PPDS_CERT_PATH", true)] // "cert" marker
    [InlineData("PPDS_LOG_LEVEL", false)]
    [InlineData("PPDS_PROFILE", false)]
    [InlineData("", false)]
    public void IsSensitiveName_ClassifiesNamesCorrectly(string name, bool expected)
    {
        Assert.Equal(expected, LogsDumpCommand.IsSensitiveName(name));
    }

    [Fact]
    public async Task ExecuteAsync_WritesZipWithRedactedLogsAndEnvironment()
    {
        // Arrange: point the dump at a temp output dir + fake ~/.ppds directory via the
        // internal override (overriding USERPROFILE in-process doesn't affect GetFolderPath
        // on .NET for Windows because the COM cache wins).
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ppds-dump-{Guid.NewGuid():N}");
        var ppdsDir = Path.Combine(tempRoot, "fake-ppds");
        var outputDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(ppdsDir);
        Directory.CreateDirectory(outputDir);

        var logPath = Path.Combine(ppdsDir, "tui-debug.log");
        // Note: ConnectionStringRedactor matches 'ClientSecret=...' — ensure our secret
        // uses that exact key so the redactor picks it up.
        await File.WriteAllTextAsync(logPath,
            "2026-04-17 sample line\n" +
            "ClientSecret=supersecret-should-be-scrubbed;OtherField=keep\n");

        // Also seed an env var with a sensitive-looking name so we can assert the
        // redaction path covers both file content and environment variables.
        Environment.SetEnvironmentVariable("PPDS_TEST_CLIENT_SECRET", "supersecret");
        Environment.SetEnvironmentVariable("PPDS_TEST_HARMLESS", "visible-value");

        try
        {
            var exitCode = await LogsDumpCommand.ExecuteAsync(
                outputDir,
                ppdsDirOverride: ppdsDir,
                CancellationToken.None);
            Assert.Equal(0, exitCode);

            var zips = Directory.GetFiles(outputDir, "ppds-diagnostics-*.zip");
            Assert.Single(zips);

            using var archive = ZipFile.OpenRead(zips[0]);

            var logEntry = archive.Entries.FirstOrDefault(e => e.FullName == "logs/tui-debug.log");
            Assert.NotNull(logEntry);
            using (var reader = new StreamReader(logEntry!.Open()))
            {
                var contents = await reader.ReadToEndAsync();
                Assert.DoesNotContain("supersecret-should-be-scrubbed", contents);
                Assert.Contains(ConnectionStringRedactor.RedactedPlaceholder, contents);
                Assert.Contains("OtherField=keep", contents);
            }

            var envEntry = archive.Entries.FirstOrDefault(e => e.FullName == "environment.txt");
            Assert.NotNull(envEntry);
            using (var reader = new StreamReader(envEntry!.Open()))
            {
                var contents = await reader.ReadToEndAsync();
                Assert.Contains("PPDS_TEST_CLIENT_SECRET=", contents);
                Assert.DoesNotContain("PPDS_TEST_CLIENT_SECRET=supersecret", contents);
                Assert.Contains("PPDS_TEST_HARMLESS=visible-value", contents);
            }

            var diagEntry = archive.Entries.FirstOrDefault(e => e.FullName == "diagnostics.txt");
            Assert.NotNull(diagEntry);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PPDS_TEST_CLIENT_SECRET", null);
            Environment.SetEnvironmentVariable("PPDS_TEST_HARMLESS", null);

            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
