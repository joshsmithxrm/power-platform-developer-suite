using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.PluginTraces;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.PluginTraces;
using PPDS.Cli.Tests.Services.PluginTraces;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

namespace PPDS.Cli.Tests.Commands.PluginTraces;

/// <summary>
/// Issue 1920: <c>plugintraces delete --older-than</c> through the command's own execute path, with a
/// stand-in connection, an injected clock and the typed answer injected. No connection is made and no
/// sign-in can start. The console is captured, so these run in the console-capture collection.
/// </summary>
[Collection(nameof(ConsoleCaptureCollection))]
[Trait("Category", "Unit")]
public sealed class DeleteCommandOlderThanTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 5, 38, 27, DateTimeKind.Utc);

    private static readonly TimeZoneInfo PlusTen =
        TimeZoneInfo.CreateCustomTimeZone("test+10", TimeSpan.FromHours(10), "test+10", "test+10");

    // ─── Refused before connecting ──────────────────────────────────────────────

    [Theory]
    [InlineData(true, false)]   // JSON output, no --force
    [InlineData(false, true)]   // redirected input, no --force
    public async Task WithoutAWayToConfirm_IsRefusedBeforeConnecting(bool json, bool redirected)
    {
        var run = await RunAsync("7d", dryRun: false, force: false, json, redirected, traces: null, new TestClock(Now));

        run.Exit.Should().Be(ExitCodes.InvalidArguments);
        run.Connects.Should().Be(0);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-7d")]
    [InlineData("7")]
    [InlineData("99999999d")]
    public async Task InvalidAge_IsRefusedBeforeConnecting(string age)
    {
        var run = await RunAsync(age, dryRun: false, force: true, json: true, redirected: true, traces: null, new TestClock(Now));

        run.Exit.Should().Be(ExitCodes.InvalidArguments);
        run.Connects.Should().Be(0);
    }

    // ─── Dry run ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DryRun_CountsOnceAndDeletesNothing_WithOrWithoutForce(bool force)
    {
        var traces = new FlowFake(count: 4);

        var run = await RunAsync("7d", dryRun: true, force, json: false, redirected: false, traces, new TestClock(Now),
            readLine: () => throw new InvalidOperationException("a dry run does not ask"));

        run.Exit.Should().Be(ExitCodes.Success);
        traces.CountFilters.Should().ContainSingle();
        traces.DeleteCutoffs.Should().BeEmpty();
    }

    // ─── The typed answer ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("n")]
    [InlineData("no")]
    [InlineData("N")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]          // end of input
    [InlineData("maybe")]
    [InlineData("yess")]
    [InlineData("y es")]
    public async Task Terminal_WithoutForce_AnAnswerThatIsNotYes_DeletesNothing(string? answer)
    {
        var traces = new FlowFake(count: 4);
        var asked = 0;

        var run = await RunAsync("7d", dryRun: false, force: false, json: false, redirected: false, traces, new TestClock(Now),
            readLine: () => { asked++; return answer; });

        run.Exit.Should().Be(ExitCodes.Failure);
        asked.Should().Be(1);
        traces.CountFilters.Should().ContainSingle();
        traces.DeleteCutoffs.Should().BeEmpty();
    }

    [Theory]
    [InlineData("y")]
    [InlineData("yes")]
    [InlineData("Y")]
    [InlineData(" yes ")]
    public async Task Terminal_WithoutForce_Yes_DeletesOnceWithTheCountsInstant(string answer)
    {
        var traces = new FlowFake(count: 4);

        var run = await RunAsync("7d", dryRun: false, force: false, json: false, redirected: false, traces,
            new TestClock(Now, step: TimeSpan.FromSeconds(1)), readLine: () => answer);

        run.Exit.Should().Be(ExitCodes.Success);
        var counted = traces.CountFilters.Single().CreatedBefore!.Value;
        counted.Should().Be(Now.AddDays(-7));
        traces.DeleteCutoffs.Should().ContainSingle().Which.Should().Be(counted);
    }

    [Fact]
    public async Task ForcedRun_DeletesWithTheInjectedClocksCutoff_WithoutAsking()
    {
        var traces = new FlowFake(count: 3);

        var run = await RunAsync("7d", dryRun: false, force: true, json: true, redirected: true, traces,
            new TestClock(Now, step: TimeSpan.FromSeconds(1), zone: PlusTen),
            readLine: () => throw new InvalidOperationException("a forced run does not ask"));

        run.Exit.Should().Be(ExitCodes.Success);
        run.Connects.Should().Be(1);
        traces.CountFilters.Should().ContainSingle().Which.CreatedBefore.Should().Be(Now.AddDays(-7));
        traces.DeleteCutoffs.Should().ContainSingle().Which.Should().Be(Now.AddDays(-7));
    }

    // ─── What is shown is what is sent (a clock on a fraction of a second) ──────

    [Fact]
    public async Task SubSecondClock_ThePromptAndTheDeleteNameTheInstantSent()
    {
        var traces = new FlowFake(count: 4);

        var run = await RunAsync("30m", dryRun: false, force: false, json: false, redirected: false, traces,
            new TestClock(Now.AddMilliseconds(900)), readLine: () => "y");

        var sent = traces.DeleteCutoffs.Single();
        sent.Should().Be(new DateTime(2026, 11, 25, 5, 8, 27, DateTimeKind.Utc));
        traces.CountFilters.Single().CreatedBefore.Should().Be(sent);
        run.Stderr.Should().Contain($"Delete 4 trace(s) created before {PluginTraceAge.Format(sent)} (older than 30m)? [y/N]");
        run.Stderr.Should().Contain($"Deleting 4 trace(s) created before {PluginTraceAge.Format(sent)} (older than 30m;");
        PluginTraceAge.Format(sent).Should().Be("2026-11-25T05:08:27Z");
    }

    [Fact]
    public async Task SubSecondClock_TheDryRunLineNamesTheInstantCounted()
    {
        var traces = new FlowFake(count: 4);

        var run = await RunAsync("36h", dryRun: true, force: false, json: false, redirected: false, traces,
            new TestClock(Now.AddMilliseconds(900)));

        var counted = traces.CountFilters.Single().CreatedBefore!.Value;
        counted.Should().Be(new DateTime(2026, 11, 23, 17, 38, 27, DateTimeKind.Utc));
        run.Stderr.Should().Contain($"Would delete 4 trace(s) created before {PluginTraceAge.Format(counted)} (older than 36h).");
    }

    [Fact]
    public async Task SubSecondClock_TheJsonCutoffIsTheInstantCounted()
    {
        var traces = new FlowFake(count: 4);

        var run = await RunAsync("30m", dryRun: true, force: false, json: true, redirected: true, traces,
            new TestClock(Now.AddMilliseconds(900)));

        var counted = traces.CountFilters.Single().CreatedBefore!.Value;
        var shown = Regex.Match(run.Stdout, "\"cutoff\"\\s*:\\s*\"([^\"]+)\"");
        shown.Success.Should().BeTrue(run.Stdout);
        DateTime.Parse(shown.Groups[1].Value, null, System.Globalization.DateTimeStyles.RoundtripKind)
            .Should().Be(counted);
        counted.Ticks.Should().Be(counted.Ticks - counted.Ticks % TimeSpan.TicksPerSecond);
    }

    // ─── Running the command ────────────────────────────────────────────────────

    private sealed record Run(int Exit, int Connects, string Stdout, string Stderr);

    private static async Task<Run> RunAsync(
        string age, bool dryRun, bool force, bool json, bool redirected, FlowFake? traces, TimeProvider clock,
        Func<string?>? readLine = null)
    {
        var connects = 0;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exit = await DeleteCommand.ExecuteAsync(
                traceId: null, ids: null, olderThan: age, all: false, dryRun: dryRun, force: force,
                profile: null, environment: null,
                new GlobalOptionValues { OutputFormat = json ? OutputFormat.Json : OutputFormat.Text },
                CancellationToken.None,
                connect: _ =>
                {
                    connects++;
                    if (traces is null)
                    {
                        throw new InvalidOperationException("must not connect");
                    }

                    var services = new ServiceCollection()
                        .AddSingleton(traces.Object)
                        .AddSingleton(new ResolvedConnectionInfo
                        {
                            Profile = new AuthProfile { Name = "test" },
                            EnvironmentUrl = "https://example.invalid",
                        });
                    return Task.FromResult(services.BuildServiceProvider());
                },
                clock: clock,
                inputRedirected: redirected,
                readLine: readLine ?? (() => throw new InvalidOperationException("no answer expected")));
            return new Run(exit, connects, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }
}
