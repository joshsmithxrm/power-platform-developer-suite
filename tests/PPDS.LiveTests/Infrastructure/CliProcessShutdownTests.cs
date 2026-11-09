using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using PPDS.LiveTests.Cli;
using Xunit;

namespace PPDS.LiveTests.Infrastructure;

/// <summary>Offline process/cleanup regressions. No PPDS command or SDK connection is executed.</summary>
public sealed class CliProcessShutdownTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutKeepsPrimaryAndBoundsUnconfirmedStop(bool parentAlreadyExited)
    {
        var clock = new ManualClock();
        using var fixture = new Harness(clock);
        using var caller = new CancellationTokenSource();
        var operation = new TaskCompletionSource<CliResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FakeProcess
        {
            Exited = parentAlreadyExited,
            OnKill = () => throw new System.ComponentModel.Win32Exception("controlled kill failure"),
            OnWait = _ => drain.Task // Models a descendant keeping redirected pipes open.
        };
        var pending = fixture.RunCommandAsync(() => process, _ => operation.Task, caller.Token);
        clock.Fire(0, TimeSpan.FromMinutes(2));
        await process.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fixture.Unsafe);
        Assert.False(process.StopToken.IsCancellationRequested);
        caller.Cancel(); // Late cancellation must not reclassify the already-established timeout.
        clock.Fire(1, TimeSpan.FromSeconds(30));
        try
        {
            var failure = await Assert.ThrowsAsync<TimeoutException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
            Assert.Contains("ObserveDispatchedProcessAsync", failure.StackTrace);
            Assert.False(fixture.StopEvidence!.ExitConfirmed);
            Assert.Equal("unknown", fixture.StopEvidence.RemoteOutcome);
            Assert.Contains(fixture.StopEvidence.Errors, error => error.StartsWith("confirm-exit:", StringComparison.Ordinal));
            Assert.Equal(parentAlreadyExited ? 0 : 1, process.Kills);
        }
        finally { operation.TrySetResult(new()); drain.TrySetResult(); }
    }

    [Fact]
    public async Task CallerCancellationPreservesExceptionAndStack()
    {
        using var fixture = new Harness();
        using var caller = new CancellationTokenSource();
        var primary = new OperationCanceledException("original cancellation", caller.Token);
        var process = new FakeProcess { OnKill = () => throw new InvalidOperationException("kill fault") };
        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.RunCommandAsync(() => process, _ =>
            {
                caller.Cancel();
                return ThrowOriginal(primary);
            }, caller.Token));
        Assert.Same(primary, actual);
        Assert.Equal(caller.Token, actual.CancellationToken);
        Assert.Contains(nameof(ThrowOriginal), actual.StackTrace);
        Assert.True(fixture.Unsafe);
        Assert.Contains("terminate: InvalidOperationException", fixture.StopEvidence!.Errors);
    }

    [Fact]
    public async Task QuarantinePrecedesStopAndBlocksFollowup()
    {
        var clock = new ManualClock();
        using var fixture = new Harness(clock);
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FakeProcess { OnWait = _ => stop.Task };
        var primary = new IOException("reader failed after dispatch");
        var pending = fixture.RunCommandAsync(() => process, _ => ThrowOriginal(primary), default);
        await process.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fixture.Unsafe);
        var started = false;
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.RunCommandAsync(() => { started = true; return new FakeProcess(); }, _ => Task.FromResult(new CliResult()), default));
        Assert.False(started);
        Assert.Contains("refusing another", refused.Message);
        Assert.False(await fixture.Cleanup(() => throw new InvalidOperationException("must never execute cleanup")));
        clock.Fire(1, TimeSpan.FromSeconds(30));
        try { Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)))); }
        finally { stop.TrySetResult(); }
        using var independent = new Harness();
        Assert.Equal(7, (await independent.RunCommandAsync(() => new FakeProcess(),
            _ => Task.FromResult(new CliResult { ExitCode = 7 }), default)).ExitCode);
        Assert.False(independent.Unsafe);
    }

    [Fact]
    public async Task ConcurrentAdmissionIsRefusedBeforeDispatch()
    {
        using var fixture = new Harness();
        var result = new TaskCompletionSource<CliResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = fixture.RunCommandAsync(() => new FakeProcess(), _ => result.Task, default);
        var started = false;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunCommandAsync(
                () => { started = true; return new FakeProcess(); }, _ => Task.FromResult(new CliResult()), default));
            Assert.False(started);
            Assert.False(fixture.Unsafe);
        }
        finally { result.TrySetResult(new()); }
        await first;
        await fixture.RunCommandAsync(() => new FakeProcess(), _ => Task.FromResult(new CliResult()), default);
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("wait")]
    [InlineData("kill-race")]
    public async Task PostStartFailureIsPreserved(string stopFailure)
    {
        using var fixture = new Harness();
        var primary = new IOException("output reader initialization");
        var process = new FakeProcess();
        if (stopFailure == "probe") process.OnHasExited = () => throw new InvalidOperationException("probe fault");
        if (stopFailure == "wait") process.OnWait = _ => throw new IOException("wait fault");
        if (stopFailure == "kill-race") process.OnKill = () => { process.Exited = true; throw new InvalidOperationException("already exited"); };
        var actual = await Assert.ThrowsAsync<IOException>(() =>
            fixture.RunCommandAsync(() => process, _ => ThrowOriginal(primary), default));
        Assert.Same(primary, actual);
        Assert.Contains(nameof(ThrowOriginal), actual.StackTrace);
        Assert.True(fixture.Unsafe);
        Assert.Equal(stopFailure == "kill-race", fixture.StopEvidence!.ExitConfirmed);
    }

    [Fact]
    public async Task CleanupGateRetainsOwnedArtifactsAndIds()
    {
        using var fixture = new Harness();
        var file = fixture.OwnedFile();
        await File.WriteAllTextAsync(file, "owned output");
        var profile = fixture.ProfilePendingDeletion();
        fixture.TrackResource("relationship", "fixture_relationship");
        await Interrupt(fixture);
        var ids = new[] { Guid.NewGuid() };
        Assert.False(await fixture.Cleanup(() => throw new InvalidOperationException("must not delete"), "account", ids));
        await fixture.DisposeAsync();
        Assert.True(File.Exists(file));
        Assert.True(Directory.Exists(fixture.Config));
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.InterruptionEvidencePath));
        Assert.Contains(file, evidence.RootElement.GetProperty("ownedFiles").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains(profile, evidence.RootElement.GetProperty("ownedProfiles").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(ids[0], evidence.RootElement.GetProperty("ownedRecords").GetProperty("account")[0].GetGuid());
        Assert.Equal("fixture_relationship", evidence.RootElement.GetProperty("possibleOwnedResources").GetProperty("relationship")[0].GetString());
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("import")]
    [InlineData("migration")]
    [InlineData("update")]
    public async Task ExistingAccountDisposersSkipInjectedSdkCleanup(string suite)
    {
        CliE2ETestBase fixture = suite switch
        {
            "delete" => new DataDeleteCommandE2ETests(),
            "import" => new DataImportCommandE2ETests(),
            "migration" => new DataMigrationWorkflowE2ETests(),
            _ => new DataUpdateCommandE2ETests()
        };
        var sdkCalls = 0;
        fixture.DeleteAccountsAsync = (_, _) => { sdkCalls++; throw new InvalidOperationException("SDK must not be called"); };
        var ownedIds = (List<Guid>)fixture.GetType().GetField("_createdAccountIds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture)!;
        var id = Guid.NewGuid();
        ownedIds.Add(id);
        var config = Path.GetDirectoryName(fixture.InterruptionEvidencePath)!;
        try
        {
            await Interrupt(fixture);
            await fixture.DisposeAsync();
            Assert.Equal(0, sdkCalls);
            using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.InterruptionEvidencePath));
            Assert.Equal(id, evidence.RootElement.GetProperty("ownedRecords").GetProperty("account")[0].GetGuid());
        }
        finally { Directory.Delete(config, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PluginCleanupSkipsAfterInterruptedBody(bool interruptPreflight)
    {
        using var fixture = new Harness();
        var config = fixture.OwnedFile();
        var calls = 0;
        var messages = new List<string>();
        var primary = new IOException("original interrupted command");
        var cleanup = new PluginFixtureCleanup(async (_, _) =>
        {
            calls++;
            if (interruptPreflight)
                return await fixture.RunCommandAsync(() => new FakeProcess(), _ => ThrowOriginal(primary), default);
            return new CliResult { StdOut = """{"success":true,"data":{"assemblies":[],"packages":[]}}""" };
        }, config, "test-profile", "https://fixture.example.test", messages.Add, () => fixture.Unsafe);
        var actual = await Assert.ThrowsAsync<IOException>(() => cleanup.RunAsync(async () =>
        {
            await File.WriteAllTextAsync(config, "retained-at-interruption");
            await fixture.RunCommandAsync(() => new FakeProcess(), _ => ThrowOriginal(primary), default);
        }));
        Assert.Same(primary, actual);
        Assert.Equal(1, calls);
        var retained = await File.ReadAllTextAsync(config);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.CleanupAsync());
        Assert.Equal(retained, await File.ReadAllTextAsync(config));
        Assert.Equal(1, calls);
        if (!interruptPreflight) Assert.Contains(messages, value => value.Contains("cleanup skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ModelDrivenAppDisposerSkipsCleanup()
    {
        var fixture = new ModelDrivenAppCommandE2ETests();
        var config = Path.GetDirectoryName(fixture.InterruptionEvidencePath)!;
        try
        {
            await Interrupt(fixture);
            // The runner is already quarantined: an ungated attempt would fail before any CLI launch.
            await fixture.DisposeAsync();
            Assert.True(File.Exists(fixture.InterruptionEvidencePath));
        }
        finally { Directory.Delete(config, recursive: true); }
    }

    [Fact]
    public async Task FinallyCleanupPreservesPrimaryAndRetainsCustomArtifact()
    {
        using var fixture = new Harness();
        var customDir = Path.Combine(fixture.Config, "custom-extraction");
        Directory.CreateDirectory(customDir);
        var output = Path.Combine(customDir, "output.json");
        fixture.TrackFile(output);
        await File.WriteAllTextAsync(output, "owned output");
        var primary = new IOException("original failure");
        async Task TestWithFinally()
        {
            try { await fixture.RunCommandAsync(() => new FakeProcess(), _ => ThrowOriginal(primary), default); }
            finally
            {
                Assert.False(await fixture.Cleanup(() => throw new InvalidOperationException("must not delete or publish")));
            }
        }
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(TestWithFinally));
        await fixture.DisposeAsync();
        Assert.True(File.Exists(output));
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.InterruptionEvidencePath));
        Assert.Contains(output, evidence.RootElement.GetProperty("ownedFiles").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task PreCancelledCommandDoesNotLaunch()
    {
        using var fixture = new Harness();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var started = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunCommandAsync(
            () => { started = true; return new FakeProcess(); }, _ => Task.FromResult(new CliResult()), cancellation.Token));
        Assert.False(started);
        Assert.False(fixture.Unsafe);
        Assert.True(await fixture.Cleanup(() => Task.CompletedTask));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.RunProcessAsync(new ProcessStartInfo("ppds-nonexistent-test-executable"), default));
        Assert.False(fixture.Unsafe);
    }

    [Fact]
    public async Task DiagnosticsOmitArgumentsAndEvidenceFailurePreservesPrimary()
    {
        const string secret = "dummy-secret-should-not-be-recorded";
        using var fixture = new Harness();
        // Force evidence I/O failure without changing config ACLs or obscuring the primary failure.
        Directory.CreateDirectory(fixture.InterruptionEvidencePath);
        var primary = new IOException(secret);
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() =>
            fixture.RunCommandAsync(() => new FakeProcess(), _ => ThrowOriginal(primary), default)));
        Assert.True(fixture.Unsafe);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.RunProcessAsync(new ProcessStartInfo(secret), default));
        Assert.DoesNotContain(secret, refused.Message);
        Assert.False(await fixture.Cleanup(() => throw new InvalidOperationException("must not cleanup")));
    }

    [Fact]
    public async Task NativeChildTimeoutIsStopped()
    {
        using var fixture = new Harness(timeout: TimeSpan.FromSeconds(2));
        var start = NativeStart(wait: true);
        start.ArgumentList.RemoveAt(start.ArgumentList.Count - 1);
        start.ArgumentList.Add(OperatingSystem.IsWindows()
            ? "echo dummy-secret-command-argument& more"
            : "printf 'dummy-secret-command-argument'; exec cat");
        start.Environment["PPDS_TEST_DUMMY_SECRET"] = "dummy-secret-never-copy-environment";
        var failure = await Assert.ThrowsAsync<TimeoutException>(() => fixture.RunProcessAsync(start, default));
        Assert.DoesNotContain("dummy-secret", failure.ToString());
        Assert.True(fixture.StopEvidence!.ExitConfirmed);
        Assert.True(fixture.Unsafe);
        Assert.Equal("unknown", fixture.StopEvidence.RemoteOutcome);
        Assert.DoesNotContain("dummy-secret", await File.ReadAllTextAsync(fixture.InterruptionEvidencePath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task NativeChildNormalExitCapturesOutput(int exit)
    {
        using var fixture = new Harness();
        var result = await fixture.RunProcessAsync(NativeStart(wait: false, exit), default);
        Assert.Equal(exit, result.ExitCode);
        Assert.Contains("final-out", result.StdOut);
        Assert.Contains("final-error", result.StdErr);
        Assert.False(fixture.Unsafe);
        Assert.Null(fixture.StopEvidence);
        var file = fixture.OwnedFile();
        await File.WriteAllTextAsync(file, "normal cleanup");
        await fixture.DisposeAsync();
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(fixture.Config));
    }

    [Fact]
    public async Task NativeReaderInitializationFailureStopsDispatchedChild()
    {
        using var fixture = new Harness();
        var start = NativeStart(wait: true);
        start.RedirectStandardOutput = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunProcessAsync(start, default));
        Assert.True(fixture.Unsafe);
        Assert.True(fixture.StopEvidence!.ExitConfirmed);
    }

    private static ProcessStartInfo NativeStart(bool wait, int exit = 0)
    {
        // Fixed harmless OS commands only; shell execution API is disabled. No PPDS/auth/network.
        var start = new ProcessStartInfo(OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") : "/bin/sh")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c");
            start.ArgumentList.Add(wait ? "more" : $"echo final-out& echo final-error 1>&2& exit /b {exit}");
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(wait ? "exec cat" : $"printf 'final-out\\n'; printf 'final-error\\n' >&2; exit {exit}");
        }
        return start;
    }

    private static async Task Interrupt(CliE2ETestBase fixture)
    {
        await Assert.ThrowsAsync<IOException>(() =>
            fixture.RunCommandAsync(() => new FakeProcess(), _ => ThrowOriginal(new IOException("controlled interruption")), default));
    }
    private static Task<CliResult> ThrowOriginal(Exception exception) => throw exception;

    private sealed class Harness(TimeProvider? clock = null, TimeSpan? timeout = null)
        : CliE2ETestBase(clock ?? TimeProvider.System), IDisposable
    {
        protected override TimeSpan CommandTimeout => timeout ?? TimeSpan.FromMinutes(2);
        public bool Unsafe => RequiresManualReview;
        public string Config => IsolatedConfigDir;
        public string OwnedFile() => GenerateTempFilePath(".json");
        public void TrackFile(string path) => TrackOwnedFile(path);
        public string ProfilePendingDeletion() { var name = GenerateTestProfileName(); CreatedProfiles.Remove(name); return name; }
        public void TrackResource(string kind, string value) => TrackPossibleOwnedResource(kind, value);
        public Task<bool> Cleanup(Func<Task> action, string? table = null, IEnumerable<Guid>? ids = null) => RunCleanupIfSafeAsync(action, table, ids);
        public void Dispose()
        {
            // Test-only disposal after all fake tasks/native children have settled; no remote cleanup.
            foreach (var file in CreatedFiles) if (File.Exists(file)) File.Delete(file);
            if (Directory.Exists(Config)) Directory.Delete(Config, recursive: true);
            Configuration.Dispose();
        }
    }

    private sealed class FakeProcess : IFixtureProcess
    {
        public bool Exited { get; set; }
        public Func<bool>? OnHasExited { get; set; }
        public bool HasExited => OnHasExited?.Invoke() ?? Exited;
        public int Kills { get; private set; }
        public Action? OnKill { get; set; }
        public Func<CancellationToken, Task>? OnWait { get; set; }
        public CancellationToken StopToken { get; private set; }
        public TaskCompletionSource StopStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Kill() { Kills++; if (OnKill != null) OnKill(); else Exited = true; }
        public Task WaitForExitAsync(CancellationToken token)
        {
            StopToken = token;
            StopStarted.TrySetResult();
            return OnWait?.Invoke(token) ?? Task.CompletedTask;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            lock (_timers) _timers.Add(timer);
            return timer;
        }
        public void Fire(int index, TimeSpan expected)
        {
            ManualTimer timer;
            lock (_timers) timer = _timers[index];
            Assert.Equal(expected, timer.Due);
            timer.Fire();
        }
        private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            private bool _disposed;
            public TimeSpan Due => due;
            public void Fire() { Assert.False(_disposed); callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
