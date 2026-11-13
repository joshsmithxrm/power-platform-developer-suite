using System.Diagnostics;
using System.Text.Json;
using Nerdbank.Streams;
using StreamJsonRpc;
using Xunit;

namespace PPDS.Cli.DaemonTests;

// Offline tests: no Integration trait/fixture collection and no CLI/authentication launch.
public sealed class DaemonTestFixtureTests
{
    private const string Secret = "dummy-secret-in-daemon-fixture";

    [Fact]
    public async Task ReadinessUsesRealLocalRpcAndNormalDisposalDeletesOnlyOwnedDirectory()
    {
        using var process = new FakeProcess();
        var fixture = new DaemonTestFixture(TimeProvider.System, _ => process);
        await fixture.InitializeAsync();
        Assert.True(fixture.IsRunning);
        var response = await fixture.Rpc.InvokeWithCancellationAsync<JsonElement>("_heartbeat");
        Assert.True(response.GetProperty("ok").GetBoolean());
        var owned = Path.Combine(fixture.IsolatedConfigDir, "owned.json");
        await File.WriteAllTextAsync(owned, "test-owned");
        var dispose = fixture.DisposeAsync();
        Assert.Same(dispose, fixture.DisposeAsync());
        await dispose;
        Assert.Equal(1, process.KillCalls);
        Assert.Equal(1, process.DisposeCalls);
        Assert.False(Directory.Exists(fixture.IsolatedConfigDir));
        Assert.False(fixture.RequiresManualReview);
        Assert.Throws<InvalidOperationException>(() => fixture.Rpc);
    }

    [Fact]
    public async Task IgnoredReadinessCancellationIsBoundedAndRetainsEvidence()
    {
        var clock = new ManualClock();
        var pendingReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new FakeProcess();
        var fixture = new DaemonTestFixture(clock, _ => process, (_, _) => pendingReady.Task);
        try
        {
            var initialize = fixture.InitializeAsync();
            clock.Fire(0);
            var error = await Assert.ThrowsAsync<DaemonFixtureException>(() => initialize.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("startup", error.Phase);
            Assert.IsAssignableFrom<OperationCanceledException>(fixture.PrimaryCause);
            Assert.True(fixture.RequiresManualReview);
            Assert.Equal(1, process.KillCalls);
            Assert.True(fixture.StopEvidence!.ExitConfirmed);
            Assert.Throws<DaemonFixtureException>(() => fixture.Rpc);
            Assert.True(File.Exists(fixture.EvidencePath));
            Assert.Contains("unknown", await File.ReadAllTextAsync(fixture.EvidencePath));
        }
        finally { pendingReady.TrySetResult(); RemoveOwnedDirectory(fixture); }
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("kill")]
    [InlineData("wait")]
    [InlineData("dispose")]
    public async Task StopOrDisposalFaultsRetainStateAndDoNotSkipResourceDisposal(string failure)
    {
        using var process = new FakeProcess();
        var fixture = new DaemonTestFixture(TimeProvider.System, _ => process);
        try
        {
            await fixture.InitializeAsync();
            if (failure == "probe") process.OnProbe = () => throw new IOException(Secret);
            if (failure == "kill") process.OnKill = () => throw new IOException(Secret);
            if (failure == "wait") process.OnWait = _ => throw new IOException(Secret);
            if (failure == "dispose") process.OnDispose = () => throw new IOException(Secret);
            var error = await Assert.ThrowsAsync<DaemonFixtureException>(() => fixture.DisposeAsync());
            Assert.True(fixture.RequiresManualReview);
            Assert.Equal(1, process.DisposeCalls);
            Assert.True(Directory.Exists(fixture.IsolatedConfigDir));
            Assert.DoesNotContain(Secret, error.ToString());
            Assert.DoesNotContain(Secret, await File.ReadAllTextAsync(fixture.EvidencePath));
            Assert.Null(error.InnerException);
        }
        finally { process.OnDispose = null; RemoveOwnedDirectory(fixture); }
    }

    [Fact]
    public async Task IgnoredStopCancellationUsesSharedDeadlineAndRefusesReuseDuringStop()
    {
        var clock = new ManualClock();
        var pendingWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new FakeProcess();
        var fixture = new DaemonTestFixture(clock, _ => process);
        try
        {
            await fixture.InitializeAsync();
            process.OnWait = _ => pendingWait.Task;
            var dispose = fixture.DisposeAsync();
            Assert.Throws<InvalidOperationException>(() => fixture.Rpc);
            Assert.False(fixture.IsRunning);
            Assert.False(process.WaitToken.IsCancellationRequested);
            clock.Fire(1); // startup deadline was timer0; shared stop is timer1.
            await Assert.ThrowsAsync<DaemonFixtureException>(() => dispose.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(fixture.StopEvidence!.ExitConfirmed);
            Assert.Equal(1, process.DisposeCalls);
            Assert.True(Directory.Exists(fixture.IsolatedConfigDir));
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InitializeAsync());
        }
        finally { pendingWait.TrySetResult(); RemoveOwnedDirectory(fixture); }
    }

    [Fact]
    public async Task ExitDuringKillStillConfirmsExitAndAllowsNormalCleanup()
    {
        using var process = new FakeProcess();
        var fixture = new DaemonTestFixture(TimeProvider.System, _ => process);
        process.OnKill = () => { process.Exited = true; throw new InvalidOperationException(Secret); };
        await fixture.InitializeAsync();
        await fixture.DisposeAsync();
        Assert.True(fixture.StopEvidence!.ExitConfirmed);
        Assert.Empty(fixture.StopEvidence.Errors);
        Assert.False(fixture.RequiresManualReview);
        Assert.False(Directory.Exists(fixture.IsolatedConfigDir));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncStreamDisposalThatFailsOrIgnoresCancellationCannotSkipStop(bool neverCompletes)
    {
        var clock = new ManualClock();
        var pendingDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new FakeProcess();
        DisposalStream? disposalStream = null;
        var fixture = new DaemonTestFixture(clock, _ => process, splice: (output, input) =>
            disposalStream = new DisposalStream(FullDuplexStream.Splice(output, input))
            {
                AsyncDispose = () => neverCompletes ? pendingDispose.Task : Task.FromException(new IOException(Secret))
            });
        try
        {
            await fixture.InitializeAsync();
            var dispose = fixture.DisposeAsync();
            await disposalStream!.AsyncDisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (neverCompletes) clock.Fire(2);
            var error = await Assert.ThrowsAsync<DaemonFixtureException>(() => dispose.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("stream-dispose", error.Phase);
            Assert.Equal(1, process.KillCalls);
            Assert.Equal(neverCompletes ? 0 : 1, process.DisposeCalls);
            Assert.True(fixture.StopEvidence!.ExitConfirmed);
            Assert.True(Directory.Exists(fixture.IsolatedConfigDir));
            Assert.DoesNotContain(Secret, error.ToString());
            pendingDispose.TrySetResult();
            await fixture.ResourceCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, process.DisposeCalls);
        }
        finally
        {
            pendingDispose.TrySetResult();
            await fixture.ResourceCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            disposalStream?.CloseInner();
            RemoveOwnedDirectory(fixture);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockingDisposalInvocationIsBoundedAndCannotReenterSharedResources(bool asyncPrefix)
    {
        var clock = new ManualClock();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new FakeProcess();
        void BlockDisposal()
        {
            started.TrySetResult();
            try
            {
                if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException(Secret);
                throw new IOException(Secret); // A late fault must not replace the timeout or expose text.
            }
            finally { returned.TrySetResult(); }
        }
        if (!asyncPrefix) process.ClientStream.SyncDispose = BlockDisposal;
        var fixture = asyncPrefix
            ? new DaemonTestFixture(clock, _ => process, splice: (output, input) =>
                new DisposalStream(FullDuplexStream.Splice(output, input))
                {
                    AsyncDispose = () => { BlockDisposal(); return Task.CompletedTask; }
                })
            : new DaemonTestFixture(clock, _ => process); // Actual production FullDuplexStream.Splice.
        try
        {
            await fixture.InitializeAsync();
            var dispose = fixture.DisposeAsync();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Fire(2);
            var error = await Assert.ThrowsAsync<DaemonFixtureException>(() => dispose.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("stream-dispose", error.Phase);
            Assert.IsAssignableFrom<OperationCanceledException>(fixture.PrimaryCause);
            Assert.False(returned.Task.IsCompleted); // The bound works before the synchronous call returns.
            Assert.Equal(0, process.DisposeCalls); // Process.Dispose would touch those same owned streams.
            Assert.True(fixture.StopEvidence!.ExitConfirmed);
            Assert.True(Directory.Exists(fixture.IsolatedConfigDir));
            using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.EvidencePath));
            Assert.True(evidence.RootElement.GetProperty("resourceCleanupIncomplete").GetBoolean());
            Assert.DoesNotContain(Secret, error.ToString());
            Assert.DoesNotContain(Secret, evidence.RootElement.GetRawText());
            Assert.Throws<DaemonFixtureException>(() => fixture.Rpc);
            release.Set();
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.ResourceCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(dispose, fixture.DisposeAsync());
            Assert.Same(error, await Assert.ThrowsAsync<DaemonFixtureException>(() => fixture.DisposeAsync()));
            Assert.Equal(1, process.DisposeCalls); // The one worker resumes serial cleanup after the late fault.
            Assert.True(Directory.Exists(fixture.IsolatedConfigDir)); // Late work cannot authorize deletion.
        }
        finally
        {
            release.Set();
            if (started.Task.IsCompleted) await returned.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.ResourceCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            process.ClientStream.SyncDispose = null;
            RemoveOwnedDirectory(fixture);
        }
    }

    [Fact]
    public async Task RpcLeaveOpenPipesKeepExactlyOneStreamDisposerUntilLateCleanupFinishes()
    {
        var clock = new ManualClock();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var active = 0;
        var maximumActive = 0;
        using var process = new FakeProcess();
        // Distinct component wrappers exercise the real production splice; only output
        // disposal is held. Both wrappers still use the real in-process RPC transport.
        process.InputOverride = new DisposalStream(process.ClientStream);
        process.ClientStream.SyncDispose = () =>
        {
            Interlocked.Increment(ref calls);
            var current = Interlocked.Increment(ref active);
            int previous;
            do { previous = Volatile.Read(ref maximumActive); }
            while (current > previous && Interlocked.CompareExchange(ref maximumActive, current, previous) != previous);
            entered.TrySetResult();
            try
            {
                if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException(Secret);
            }
            finally { Interlocked.Decrement(ref active); }
        };
        process.OnDispose = () => Assert.Equal(0, Volatile.Read(ref active));
        var fixture = new DaemonTestFixture(clock, _ => process);
        try
        {
            await fixture.InitializeAsync();
            var rpcCompletion = fixture.Rpc.Completion;
            var dispose = fixture.DisposeAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await rpcCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Fire(2);
            var error = await Assert.ThrowsAsync<DaemonFixtureException>(() => dispose.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("stream-dispose", error.Phase);
            Assert.Equal(1, Volatile.Read(ref active));
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.Equal(1, Volatile.Read(ref maximumActive));
            Assert.Equal(0, process.DisposeCalls);
            Assert.False(fixture.ResourceCleanupCompletion.IsCompleted);
            release.Set();
            await fixture.ResourceCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, Volatile.Read(ref active));
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.Equal(1, Volatile.Read(ref maximumActive));
            Assert.Equal(1, process.DisposeCalls);
            Assert.Same(error, await Assert.ThrowsAsync<DaemonFixtureException>(() => fixture.DisposeAsync()));
            Assert.True(File.Exists(fixture.EvidencePath));
        }
        finally
        {
            release.Set();
            await fixture.ResourceCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            process.ClientStream.SyncDispose = null;
            RemoveOwnedDirectory(fixture);
        }
    }

    [Fact]
    public async Task StderrDrainIgnoringCancellationIsBoundedAfterParentExit()
    {
        var clock = new ManualClock();
        var reader = new ControlledReader();
        using var process = new FakeProcess { Error = reader };
        var fixture = new DaemonTestFixture(clock, _ => process);
        try
        {
            await fixture.InitializeAsync();
            var dispose = fixture.DisposeAsync();
            Assert.False(dispose.IsCompleted);
            await reader.CancellationRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Fire(2);
            var error = await Assert.ThrowsAsync<DaemonFixtureException>(() => dispose.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("stderr-drain", error.Phase);
            Assert.True(fixture.StopEvidence!.ExitConfirmed);
            Assert.True(fixture.RequiresManualReview);
            Assert.True(Directory.Exists(fixture.IsolatedConfigDir));
        }
        finally
        {
            reader.Complete();
            await fixture.ResourceCleanupCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            RemoveOwnedDirectory(fixture);
        }
    }

    [Fact]
    public async Task ContinuousSynchronousStderrCannotMonopolizeStartupOrIgnoreDisposalCancellation()
    {
        var reader = new SynchronousEndlessReader();
        using var process = new FakeProcess { Error = reader };
        var fixture = new DaemonTestFixture(TimeProvider.System, _ => process);
        try
        {
            // Task.Run also bounds this regression against the former synchronous infinite loop.
            await Task.Run(fixture.InitializeAsync).WaitAsync(TimeSpan.FromSeconds(10));
            await reader.BurstObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(fixture.IsRunning);
            await fixture.DisposeAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(fixture.StderrCompletion.IsCompletedSuccessfully);
            Assert.True(fixture.StderrCharactersDiscarded >= 128 * Secret.Length);
            Assert.False(fixture.RequiresManualReview);
            Assert.False(Directory.Exists(fixture.IsolatedConfigDir));
        }
        finally
        {
            reader.Stop(); // Only test teardown can end a broken implementation's endless fake.
            try { await fixture.DisposeAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (DaemonFixtureException)
            {
                // Preserve a body failure while verifying teardown recorded its own failure.
                Assert.True(fixture.RequiresManualReview);
                Assert.NotNull(fixture.PrimaryCause);
            }
            RemoveOwnedDirectory(fixture);
        }
    }

    [Fact]
    public async Task FirstFailureAndStackSurviveCleanupAndEvidenceFailuresWithoutSecretDisclosure()
    {
        var primary = new IOException(Secret);
        using var process = new FakeProcess();
        process.OnDispose = () => throw new InvalidOperationException(Secret);
        var fixture = new DaemonTestFixture(TimeProvider.System, config =>
        {
            // Force evidence writing to fail. The directory remains available for manual inspection.
            Directory.CreateDirectory(Path.Combine(config, "daemon-interruption.json"));
            return process;
        }, (_, _) => ThrowPrimary(primary));
        try
        {
            var error = await Assert.ThrowsAsync<DaemonFixtureException>(() => fixture.InitializeAsync());
            Assert.Same(primary, fixture.PrimaryCause);
            Assert.Contains(nameof(ThrowPrimary), fixture.PrimaryCause!.StackTrace);
            Assert.Equal("startup", error.Phase);
            Assert.Equal(nameof(IOException), error.FailureType);
            Assert.DoesNotContain(Secret, error.ToString());
            Assert.Null(error.InnerException);
            Assert.Equal(1, process.KillCalls);
            Assert.Equal(1, process.DisposeCalls);
            Assert.True(Directory.Exists(fixture.IsolatedConfigDir));
            var repeated = await Assert.ThrowsAsync<DaemonFixtureException>(() => fixture.DisposeAsync());
            Assert.Same(error, repeated);
        }
        finally { process.OnDispose = null; RemoveOwnedDirectory(fixture); }
    }

    [Fact]
    public async Task PostStartStreamFailureStopsChildAndRetainsSafeEvidence()
    {
        using var process = new FakeProcess { OutputFailure = new IOException(Secret) };
        var fixture = new DaemonTestFixture(TimeProvider.System, _ => process);
        try
        {
            var error = await Assert.ThrowsAsync<DaemonFixtureException>(() => fixture.InitializeAsync());
            Assert.Equal(1, process.KillCalls);
            Assert.Same(process.OutputFailure, fixture.PrimaryCause);
            Assert.DoesNotContain(Secret, error.ToString());
            Assert.DoesNotContain(Secret, await File.ReadAllTextAsync(fixture.EvidencePath));
        }
        finally { RemoveOwnedDirectory(fixture); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StderrReadFailureIsSecretSafeAndPreventsFurtherUse(bool duringStartup)
    {
        var reader = new ControlledReader();
        if (duringStartup) reader.Fail(new IOException(Secret));
        using var process = new FakeProcess { Error = reader };
        var fixture = new DaemonTestFixture(TimeProvider.System, _ => process);
        try
        {
            DaemonFixtureException error;
            if (duringStartup) error = await Assert.ThrowsAsync<DaemonFixtureException>(() => fixture.InitializeAsync());
            else
            {
                await fixture.InitializeAsync();
                reader.Fail(new IOException(Secret));
                await Assert.ThrowsAsync<IOException>(() => fixture.StderrCompletion);
                Assert.Throws<DaemonFixtureException>(() => fixture.Rpc);
                error = await Assert.ThrowsAsync<DaemonFixtureException>(() => fixture.DisposeAsync());
            }
            Assert.DoesNotContain(Secret, error.ToString());
            Assert.DoesNotContain(Secret, await File.ReadAllTextAsync(fixture.EvidencePath));
            Assert.Throws<DaemonFixtureException>(() => fixture.Rpc);
            Assert.Equal(1, process.KillCalls);
        }
        finally { RemoveOwnedDirectory(fixture); }
    }

    [Fact]
    public async Task DisposalDuringStartupRetainsStateBeforeCancellationContinuationRuns()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var process = new FakeProcess();
        var fixture = new DaemonTestFixture(TimeProvider.System, _ => process, (_, _) => pending.Task);
        try
        {
            var initialize = fixture.InitializeAsync();
            var disposalError = await Assert.ThrowsAsync<DaemonFixtureException>(() => fixture.DisposeAsync());
            var startupError = await Assert.ThrowsAsync<DaemonFixtureException>(() => initialize);
            Assert.Same(disposalError, startupError);
            Assert.Equal("startup-cancelled", startupError.Phase);
            Assert.True(Directory.Exists(fixture.IsolatedConfigDir));
            Assert.Equal(1, process.KillCalls);
        }
        finally { pending.TrySetResult(); RemoveOwnedDirectory(fixture); }
    }

    [Fact]
    public async Task InlineStartupCancellationPublishesOneDisposalBeforeReentry()
    {
        // No test synchronization context and deliberately inline TCS continuations: cancellation
        // resumes InitializeCoreAsync's catch while the original DisposeAsync is still entering.
        await Task.Run(async () =>
        {
            var pendingReady = new TaskCompletionSource();
            var pendingExit = new TaskCompletionSource();
            using var process = new FakeProcess { OnWait = _ => pendingExit.Task };
            CancellationTokenRegistration registration = default;
            var fixture = new DaemonTestFixture(TimeProvider.System, _ => process, (_, token) =>
            {
                registration = token.Register(() => pendingReady.TrySetCanceled(token));
                return pendingReady.Task;
            });
            try
            {
                var initialize = fixture.InitializeAsync();
                var dispose = fixture.DisposeAsync();
                Assert.Equal(1, process.WaitCalls);
                Assert.Same(dispose, fixture.DisposeAsync());
                Assert.False(dispose.IsCompleted);
                pendingExit.SetResult();
                var disposalError = await Assert.ThrowsAsync<DaemonFixtureException>(() => dispose.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Same(disposalError, await Assert.ThrowsAsync<DaemonFixtureException>(() => initialize));
                Assert.Equal("startup-cancelled", disposalError.Phase);
                Assert.Equal(1, process.WaitCalls);
                Assert.Equal(1, process.KillCalls);
                Assert.Equal(1, process.DisposeCalls);
                Assert.Same(dispose, fixture.DisposeAsync());
                Assert.True(File.Exists(fixture.EvidencePath));
            }
            finally
            {
                // Capture the registration assigned by readiness, not its initial default value.
                // Release the controlled operations before disposing it, as in the original order.
                using (registration)
                {
                    pendingExit.TrySetResult();
                    pendingReady.TrySetResult();
                }
                RemoveOwnedDirectory(fixture);
            }
        }).WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task UnexpectedExitRetainsStateEvenWhenParentExitIsConfirmed()
    {
        using var process = new FakeProcess();
        var fixture = new DaemonTestFixture(TimeProvider.System, _ => process);
        try
        {
            await fixture.InitializeAsync();
            process.Exited = true;
            Assert.Throws<DaemonFixtureException>(() => fixture.Rpc);
            var error = await Assert.ThrowsAsync<DaemonFixtureException>(() => fixture.DisposeAsync());
            Assert.Equal("exit", error.Phase);
            Assert.True(fixture.StopEvidence!.ExitConfirmed);
            Assert.Equal("unknown", fixture.StopEvidence.RemoteOutcome);
            Assert.True(Directory.Exists(fixture.IsolatedConfigDir));
        }
        finally { RemoveOwnedDirectory(fixture); }
    }

    [Fact]
    public async Task NativeChildStderrIsDrainedWithoutCapturingSecretOutput()
    {
        // Fixed harmless shell command, shell API disabled; no PPDS/auth/network. The output exceeds
        // a pipe buffer, then waits on redirected stdin. Draining prevents the child from wedging.
        var start = new ProcessStartInfo(OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") : "/bin/sh")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c");
            start.ArgumentList.Add("(for /l %i in (1,1,5000) do @echo dummy-secret-in-daemon-fixture 1>&2) & more");
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("i=0; while [ $i -lt 5000 ]; do printf 'dummy-secret-in-daemon-fixture\\n' >&2; i=$((i+1)); done; exec cat");
        }
        start.Environment["DUMMY_FIXTURE_SECRET"] = Secret;
        var fixture = new DaemonTestFixture(TimeProvider.System, _ => NativeDaemonTestProcess.Start(start), (_, _) => Task.CompletedTask);
        try
        {
            await fixture.InitializeAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (fixture.StderrCharactersDiscarded < 5000 * Secret.Length)
                await Task.Delay(10, deadline.Token);
            await fixture.DisposeAsync();
            Assert.True(fixture.StopEvidence!.ExitConfirmed);
            Assert.False(fixture.RequiresManualReview);
            Assert.False(Directory.Exists(fixture.IsolatedConfigDir));
        }
        finally
        {
            try { await fixture.DisposeAsync(); }
            catch (DaemonFixtureException)
            {
                // Preserve a body failure while verifying teardown recorded its own failure.
                Assert.True(fixture.RequiresManualReview);
                Assert.NotNull(fixture.PrimaryCause);
            }
            if (fixture.StopEvidence?.ExitConfirmed == true) RemoveOwnedDirectory(fixture);
        }
    }

    private static Task ThrowPrimary(Exception exception) => throw exception;
    private static void RemoveOwnedDirectory(DaemonTestFixture fixture)
    {
        // Test-created paths only, after fake operations are released/native process stopped.
        if (Directory.Exists(fixture.IsolatedConfigDir)) Directory.Delete(fixture.IsolatedConfigDir, recursive: true);
    }

    private sealed class FakeProcess : IDaemonTestProcess
    {
        private readonly Stream _serverStream;
        private readonly JsonRpc _server;
        public FakeProcess()
        {
            var pair = FullDuplexStream.CreatePair();
            ClientStream = new DisposalStream(pair.Item1);
            _serverStream = pair.Item2;
            _server = new JsonRpc(new HeaderDelimitedMessageHandler(_serverStream, _serverStream, new SystemTextJsonFormatter()), new HeartbeatTarget());
            _server.StartListening();
        }
        public DisposalStream ClientStream { get; }
        public TextReader Error { get; init; } = new StringReader(Secret);
        public Exception? OutputFailure { get; init; }
        public Stream? InputOverride { get; set; }
        public Stream StandardInput => InputOverride ?? ClientStream;
        public Stream StandardOutput => OutputFailure is null ? ClientStream : throw OutputFailure;
        public TextReader StandardError => Error;
        public bool Exited { get; set; }
        public bool HasExited => OnProbe?.Invoke() ?? Exited;
        public Func<bool>? OnProbe { get; set; }
        public Action? OnKill { get; set; }
        public Action? OnDispose { get; set; }
        public Func<CancellationToken, Task>? OnWait { get; set; }
        public CancellationToken WaitToken { get; private set; }
        public int KillCalls { get; private set; }
        public int WaitCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public void Kill() { KillCalls++; if (OnKill != null) OnKill(); else Exited = true; }
        public Task WaitForExitAsync(CancellationToken token) { WaitCalls++; WaitToken = token; return OnWait?.Invoke(token) ?? Task.CompletedTask; }
        public void Dispose()
        {
            DisposeCalls++;
            OnDispose?.Invoke();
            _server.Dispose(); _serverStream.Dispose(); ClientStream.CloseInner(); Error.Dispose();
        }
    }

    public sealed class HeartbeatTarget
    {
        [JsonRpcMethod("_heartbeat")]
        public object Heartbeat() => new { ok = true };
    }

    private sealed class DisposalStream(Stream inner) : Stream
    {
        public Func<Task>? AsyncDispose { get; set; }
        public Action? SyncDispose { get; set; }
        public TaskCompletionSource AsyncDisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => inner.WriteAsync(buffer, token);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) SyncDispose?.Invoke(); /* Test owns inner pipe. */ }
        public override ValueTask DisposeAsync()
        {
            AsyncDisposeStarted.TrySetResult();
            return new ValueTask(AsyncDispose?.Invoke() ?? Task.CompletedTask);
        }
        public void CloseInner() => inner.Dispose();
    }

    private sealed class ControlledReader : TextReader
    {
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() => CancellationRequested.TrySetResult());
            return await _read.Task;
        }
        public void Complete() => _read.TrySetResult(0);
        public void Fail(Exception exception) => _read.TrySetException(exception);
    }

    private sealed class SynchronousEndlessReader : TextReader
    {
        private int _stop;
        private int _reads;
        public TaskCompletionSource BurstObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            // Deliberately ignores cancellation and completes synchronously with nonempty data.
            if (Volatile.Read(ref _stop) != 0) return ValueTask.FromResult(0);
            Secret.AsSpan().CopyTo(buffer.Span);
            if (Interlocked.Increment(ref _reads) == 128) BurstObserved.TrySetResult();
            return ValueTask.FromResult(Secret.Length);
        }
        public void Stop() => Interlocked.Exchange(ref _stop, 1);
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
        public void Fire(int index)
        {
            ManualTimer timer;
            lock (_timers) timer = _timers[index];
            Assert.Equal(TimeSpan.FromSeconds(30), timer.Due);
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
