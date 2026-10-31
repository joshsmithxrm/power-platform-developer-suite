using System.Diagnostics;
using FluentAssertions;
using PPDS.DocsGen.Cli;
using PPDS.DocsGen.Common;
using Xunit;

namespace PPDS.DocsGen.Cli.Tests;

public sealed class CliReflectProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ppds-docs-process-" + Guid.NewGuid().ToString("N"));

    public CliReflectProcessTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ValidCommandAssembly_WritesReferenceAndSucceeds()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "PPDS.DocsGen.Cli.Tests.FixtureCli.dll");
        var result = await RunAsync(fixture);

        result.ExitCode.Should().Be(0, result.Error);
        File.Exists(Path.Combine(_root, "output", "query", "run.md")).Should().BeTrue();
        result.Output.Should().Contain("query/run.md");
    }

    [Fact]
    public async Task UnloadableAssembly_FailsWithoutPublishingEmptyOutput()
    {
        var invalid = Path.Combine(_root, "invalid.dll");
        await File.WriteAllTextAsync(invalid, "not a managed assembly");

        var result = await RunAsync(invalid);

        result.ExitCode.Should().Be(1);
        result.Error.Should().Contain("load:");
        result.Output.Should().BeEmpty();
        Directory.Exists(Path.Combine(_root, "output")).Should().BeFalse();
    }

    [Fact]
    public async Task AssemblyWithoutCommands_FailsWithoutPublishingEmptyOutput()
    {
        var result = await RunAsync(typeof(GenerationInput).Assembly.Location);

        result.ExitCode.Should().Be(1);
        result.Error.Should().Contain("complete command surface");
        result.Output.Should().BeEmpty();
        Directory.Exists(Path.Combine(_root, "output")).Should().BeFalse();
    }

    [Fact]
    public async Task FailedCommandFactory_FailsWithoutPublishingPartialOutput()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "PPDS.DocsGen.Cli.Tests.FixtureCli.dll");
        var result = await RunAsync(fixture, failFactory: true);

        result.ExitCode.Should().Be(1);
        result.Error.Should().Contain("Expected fixture factory failure");
        result.Output.Should().BeEmpty();
        Directory.Exists(Path.Combine(_root, "output")).Should().BeFalse();
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(string assembly, bool failFactory = false)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.Environment["PPDS_DOCS_FIXTURE_FAIL_FACTORY"] = failFactory ? "1" : "0";
        foreach (var argument in new[]
        {
            typeof(CliReferenceGenerator).Assembly.Location,
            "--assembly", assembly, "--output", Path.Combine(_root, "output"),
        })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        return (process.ExitCode, await output, await error);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
