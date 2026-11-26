using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;

namespace PPDS.Cli.Tests.Services.Query.StackDepth;

/// <summary>
/// Runs tests/PPDS.Cli.StackProbe as a child process (#1952): one job per process, so the JIT is cold as in a
/// real start, any thread stack can be chosen, and a stack overflow ends only the child. A child that dies
/// without printing its result fails the test.
/// </summary>
internal static class StackProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private static string ProbePath =>
        typeof(StackProbe).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "StackProbePath").Value!;

    public static JsonElement Run(params string[] args)
    {
        File.Exists(ProbePath).Should().BeTrue($"the stack probe is built at {ProbePath}");

        var start = new ProcessStartInfo(DotnetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(ProbePath);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(Timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"stack probe {string.Join(' ', args)} did not finish in {Timeout}");
        }

        var output = stdout.Result;
        var line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("RESULT ", StringComparison.Ordinal));
        line.Should().NotBeNull(
            $"the probe must finish: '{string.Join(' ', args)}' ended with exit code {process.ExitCode} and no result " +
            $"(a stack overflow ends the process). stderr: {Tail(stderr.Result)}");
        process.ExitCode.Should().Be(0);
        return JsonDocument.Parse(line!["RESULT ".Length..]).RootElement.Clone();
    }

    /// <summary>
    /// Writes a measurement to the test output and to the console: <c>dotnet test --verbosity normal</c> (as CI
    /// runs it) prints console lines of passing tests, so each platform's measurements can be read from its log.
    /// </summary>
    public static void Report(Xunit.Abstractions.ITestOutputHelper output, string line)
    {
        output.WriteLine(line);
        Console.WriteLine(line);
    }

    private static string DotnetHost()
    {
        var host = System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return !string.IsNullOrEmpty(host) && File.Exists(host) ? host : "dotnet";
    }

    private static string Tail(string text) => text.Length <= 1500 ? text : text[^1500..];
}
