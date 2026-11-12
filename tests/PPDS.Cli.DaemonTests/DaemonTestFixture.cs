using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Nerdbank.Streams;
using PPDS.LiveTests.Infrastructure;
using StreamJsonRpc;
using Xunit;

namespace PPDS.Cli.DaemonTests;

/// <summary>
/// Manages daemon process lifecycle for integration tests.
/// Starts the daemon, provides a JSON-RPC client, and handles cleanup.
/// </summary>
public sealed class DaemonTestFixture : IAsyncLifetime, IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisposalTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeProvider _clock;
    private readonly Func<string, IDaemonTestProcess> _start;
    private readonly Func<JsonRpc, CancellationToken, Task> _ready;
    private readonly Func<Stream, Stream, Stream> _splice;
    private readonly FixtureProcessInterruption _stop;
    private readonly CancellationTokenSource _startupCancellation = new();
    private readonly CancellationTokenSource _drainCancellation = new();
    private readonly object _gate = new();
    private readonly List<string> _errors = [];
    private IDaemonTestProcess? _daemonProcess;
    private JsonRpc? _rpc;
    private Stream? _duplexStream;
    private Task? _stderrDrain;
    private Task? _initializeTask;
    private Task? _disposeTask;
    private Task? _resourceCleanupTask;
    private string _resourceCleanupPhase = "resource-dispose";
    private ExceptionDispatchInfo? _primaryCause;
    private DaemonFixtureException? _failure;
    private long _stderrCharacters;
    private bool _initialized;
    private bool _disposing;
    private bool _resourceCleanupIncomplete;

    public DaemonTestFixture() : this(TimeProvider.System, StartDaemon, WaitForHeartbeatAsync) { }

    // Only offline tests replace these boundaries; production CLI/authentication is never needed.
    internal DaemonTestFixture(TimeProvider clock, Func<string, IDaemonTestProcess> start,
        Func<JsonRpc, CancellationToken, Task>? ready = null,
        Func<Stream, Stream, Stream>? splice = null)
    {
        _clock = clock;
        _start = start;
        _ready = ready ?? WaitForHeartbeatAsync;
        _splice = splice ?? FullDuplexStream.Splice;
        _stop = new FixtureProcessInterruption(clock);
    }

    internal string EvidencePath => Path.Combine(IsolatedConfigDir, "daemon-interruption.json");
    internal Exception? PrimaryCause => _primaryCause?.SourceException;
    internal bool RequiresManualReview { get { lock (_gate) return _failure != null; } }
    internal FixtureProcessStopEvidence? StopEvidence => _stop.Evidence;
    internal long StderrCharactersDiscarded => Interlocked.Read(ref _stderrCharacters);
    internal Task StderrCompletion => _stderrDrain ?? Task.CompletedTask;
    internal Task ResourceCleanupCompletion => _resourceCleanupTask ?? Task.CompletedTask;

    /// <summary>
    /// Gets the isolated config directory for this test instance.
    /// </summary>
    public string IsolatedConfigDir { get; private set; } = "";

    /// <summary>
    /// Gets the JSON-RPC connection to the daemon.
    /// </summary>
    public JsonRpc Rpc
    {
        get
        {
            lock (_gate)
            {
                if (_failure != null) throw _failure;
                if (_disposing || !_initialized || _rpc == null)
                    throw new InvalidOperationException("Daemon fixture is not available.");
                if (_stderrDrain?.IsFaulted == true)
                {
                    RecordFailure("stderr", _stderrDrain.Exception!);
                    throw _failure!;
                }
                try
                {
                    if (_daemonProcess!.HasExited) RecordFailure("exit", new IOException("Daemon exited unexpectedly."));
                }
                catch (Exception exception) { RecordFailure("probe", exception); }
                if (_failure != null) throw _failure;
                return _rpc;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the daemon process is running.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                if (_disposing || _failure != null || _daemonProcess == null) return false;
                try
                {
                    if (!_daemonProcess.HasExited) return true;
                    RecordFailure("exit", new IOException("Daemon exited unexpectedly."));
                    return false;
                }
                catch (Exception exception) { RecordFailure("probe", exception); return false; }
            }
        }
    }

    /// <inheritdoc/>
    public Task InitializeAsync()
    {
        lock (_gate)
        {
            if (_disposing) throw new InvalidOperationException("Daemon fixture is disposed.");
            return _initializeTask ??= InitializeCoreAsync();
        }
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            IsolatedConfigDir = Path.Combine(Path.GetTempPath(), $"ppds-daemon-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(IsolatedConfigDir);
            _daemonProcess = _start(IsolatedConfigDir);
            _stderrDrain = DrainStderrAsync(_daemonProcess.StandardError, _drainCancellation.Token);
            ObserveLateFault(_stderrDrain);
            _duplexStream = _splice(_daemonProcess.StandardOutput, _daemonProcess.StandardInput);
            // The stream constructor registers an asynchronous stream-disposal callback.
            // Explicit leave-open pipes keep stream ownership with our one cleanup worker;
            // Rpc.Completion alone does not join that constructor's disposal callback.
            var reader = PipeReader.Create(_duplexStream, new StreamPipeReaderOptions(leaveOpen: true));
            var writer = PipeWriter.Create(_duplexStream, new StreamPipeWriterOptions(leaveOpen: true));
            var handler = new HeaderDelimitedMessageHandler(writer, reader, new SystemTextJsonFormatter());
            _rpc = new JsonRpc(handler);
            _rpc.StartListening();

            using var deadline = new CancellationTokenSource(StartupTimeout, _clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, _startupCancellation.Token);
            var ready = _ready(_rpc, linked.Token);
            ObserveLateFault(ready);
            // A stderr read fault must not leave readiness waiting for its full timeout.
            var first = await Task.WhenAny(ready, _stderrDrain).WaitAsync(linked.Token);
            if (first == _stderrDrain) await _stderrDrain;
            await ready.WaitAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (_daemonProcess.HasExited) throw new IOException("Daemon exited before readiness.");
            lock (_gate)
            {
                if (_disposing) throw new OperationCanceledException(_startupCancellation.Token);
                _initialized = true;
            }
        }
        catch (Exception exception)
        {
            RecordFailure("startup", exception);
            // Teardown errors add safe evidence; the first failure remains the reported cause.
            await DisposeAsync();
            throw _failure!;
        }
    }

    private static async Task WaitForHeartbeatAsync(JsonRpc rpc, CancellationToken token)
    {
        var response = await rpc.InvokeWithCancellationAsync<JsonElement>("_heartbeat", cancellationToken: token);
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("Daemon readiness response is invalid.");
    }

    private async Task DrainStderrAsync(TextReader reader, CancellationToken token)
    {
        // Start() must be able to install its readiness deadline even when reads complete
        // synchronously forever. Periodic yielding also prevents a hot stderr producer
        // from starving cancellation/RPC continuations on a shared synchronization context.
        await Task.Yield();
        var buffer = new char[1024];
        var synchronousReadBudget = 64;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var count = await reader.ReadAsync(buffer.AsMemory(), token);
                if (count == 0) break;
                Interlocked.Add(ref _stderrCharacters, count);
                if (--synchronousReadBudget == 0)
                {
                    synchronousReadBudget = 64;
                    await Task.Yield();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Fixture-owned cancellation completes the drain normally; other read faults propagate.
            return;
        }
        // No stderr content is accumulated, printed, or written to evidence.
    }

    private static IDaemonTestProcess StartDaemon(string configDirectory)
    {
        var cliProjectPath = GetCliProjectPath();

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(cliProjectPath)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Set isolated config directory to avoid affecting user's real config
        startInfo.Environment["PPDS_CONFIG_DIR"] = configDirectory;

        // Build argument list for running the daemon
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(cliProjectPath);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--framework");
        startInfo.ArgumentList.Add("net8.0");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("serve");

        return NativeDaemonTestProcess.Start(startInfo);
    }

    /// <inheritdoc/>
    public Task DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_disposeTask != null) return _disposeTask;
            if (_initializeTask != null && !_initialized && _failure == null)
                RecordFailure("startup-cancelled", new OperationCanceledException());
            _disposing = true; // Refuse further use before starting termination or disposal.
            // Cancellation may inline the startup catch, which calls DisposeAsync again.
            // Publish its result before any callbacks or teardown code can run.
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }
        _ = FinishDisposalAsync(completion);
        return completion.Task;
    }

    private async Task FinishDisposalAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeAsyncCore();
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            // Observe every runner failure; even an unexpected teardown failure must use
            // the safe first diagnostic rather than exposing a raw adapter exception.
            if (!ReferenceEquals(exception, _failure)) RecordFailure("dispose", exception);
            RetainEvidence();
            completion.TrySetException(_failure!);
        }
    }

    /// <inheritdoc/>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await DisposeAsync();
    }

    private async Task DisposeAsyncCore()
    {
        TryDispose("cancel-startup", _startupCancellation.Cancel);
        if (_daemonProcess != null)
        {
            // An already-dead daemon before teardown is not a successful normal shutdown.
            try
            {
                if (_initialized && _daemonProcess.HasExited)
                    RecordFailure("exit", new IOException("Daemon exited before teardown."));
            }
            catch (Exception exception) { RecordFailure("probe", exception); }
            // This is the same independent30second kill/confirm implementation used by #1505.
            await _stop.StopAsync(_daemonProcess);
            if (_stop.Evidence is not { ExitConfirmed: true, Errors.Count: 0 })
                RecordFailure("stop", new IOException("Daemon termination was not cleanly confirmed."));
        }

        using var disposalDeadline = new CancellationTokenSource(DisposalTimeout, _clock);
        var disposalToken = disposalDeadline.Token;
        var rpc = _rpc;
        var stream = _duplexStream;
        var process = _daemonProcess;
        var drain = _stderrDrain;
        // One worker owns all resource cleanup, including synchronous disposal prefixes.
        // The fixture may stop waiting, but the worker continues serially if a late operation
        // settles. No second disposer touches its resources, and it never deletes local state.
        _resourceCleanupTask = Task.Run(() => DisposeResourcesAsync(rpc, stream, process, drain));
        ObserveLateFault(_resourceCleanupTask);
        try
        {
            await _resourceCleanupTask.WaitAsync(disposalToken);
            disposalToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception)
        {
            lock (_gate) RecordFailure(_resourceCleanupPhase, exception);
            _resourceCleanupIncomplete = !_resourceCleanupTask.IsCompleted;
        }
        _rpc = null;
        _duplexStream = null;
        _daemonProcess = null;
        _startupCancellation.Dispose();

        if (!RequiresManualReview && Directory.Exists(IsolatedConfigDir))
        {
            TryDispose("directory-delete", () => Directory.Delete(IsolatedConfigDir, recursive: true));
        }
        if (RequiresManualReview) RetainEvidence();
        if (_failure != null) throw _failure;
    }

    private async Task DisposeResourcesAsync(JsonRpc? rpc, Stream? stream, IDaemonTestProcess? process, Task? drain)
    {
        // Stop stderr first so an endless synchronous producer cannot outlive a blocked
        // stream disposer. Joining an uncooperative reader remains owned by this worker.
        await CompleteResourceAsync("cancel-drain", () => { _drainCancellation.Cancel(); return Task.CompletedTask; });
        if (drain != null) await CompleteResourceAsync("stderr-drain", () => drain);
        if (rpc != null) await CompleteResourceAsync("rpc-dispose", () => { rpc.Dispose(); return Task.CompletedTask; });
        if (stream != null) await CompleteResourceAsync("stream-dispose", () => stream.DisposeAsync().AsTask());
        if (rpc != null) await CompleteResourceAsync("rpc-completion", () => rpc.Completion);
        if (process != null) await CompleteResourceAsync("process-dispose", () => { process.Dispose(); return Task.CompletedTask; });
        TryDispose("dispose-drain-token", _drainCancellation.Dispose);
    }

    private async Task CompleteResourceAsync(string phase, Func<Task> operation)
    {
        lock (_gate) _resourceCleanupPhase = phase;
        try
        {
            // The single worker bounds invocation from the fixture's perspective; within
            // that worker, finish this resource before advancing even after outer timeout.
            await operation();
        }
        catch (Exception exception) { RecordFailure(phase, exception); }
    }

    private void TryDispose(string phase, Action action)
    {
        try { action(); }
        catch (Exception exception) { RecordFailure(phase, exception); }
    }

    private void RecordFailure(string phase, Exception exception)
    {
        lock (_gate)
        {
            _primaryCause ??= ExceptionDispatchInfo.Capture(exception);
            _failure ??= new DaemonFixtureException(phase, exception.GetType().Name);
            _errors.Add(phase + ": " + exception.GetType().Name);
        }
    }

    private void RetainEvidence()
    {
        try
        {
            // Never fall back to the working directory if creating the owned temp directory failed.
            if (string.IsNullOrEmpty(IsolatedConfigDir) || !Directory.Exists(IsolatedConfigDir)) return;
            string json;
            lock (_gate)
            {
                var evidence = new
                {
                    requiresManualReview = true,
                    phase = _failure!.Phase,
                    failureType = _failure.FailureType,
                    parentExitConfirmed = _stop.Evidence?.ExitConfirmed,
                    remoteOutcome = "unknown",
                    resourceCleanupIncomplete = _resourceCleanupIncomplete,
                    stderrCharactersDiscarded = Interlocked.Read(ref _stderrCharacters),
                    configDirectory = IsolatedConfigDir,
                    errors = _errors.ToArray(),
                    stopErrors = _stop.Evidence?.Errors
                };
                json = JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true });
            }
            File.WriteAllText(EvidencePath, json);
        }
        catch (Exception) { /* Evidence failure cannot replace the first failure or authorize deletion. */ }
    }

    // An adapter that ignores cancellation may settle later; observe faults without logging messages.
    private static void ObserveLateFault(Task task) => _ = task.ContinueWith(
        completed => _ = completed.Exception, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>
    /// Gets the path to the CLI project.
    /// </summary>
    private static string GetCliProjectPath()
    {
        var testDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(testDir);

        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PPDS.sln")))
            {
                var cliPath = Path.Combine(dir.FullName, "src", "PPDS.Cli", "PPDS.Cli.csproj");
                if (File.Exists(cliPath))
                {
                    return cliPath;
                }
            }
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find PPDS.sln starting from: {testDir}");
    }
}

/// <summary>Safe outer diagnostic; the original cause is retained privately, never as a logged inner exception.</summary>
internal sealed class DaemonFixtureException(string phase, string failureType)
    : Exception($"Daemon fixture failed during {phase} ({failureType}); retained local state requires manual review.")
{
    public string Phase { get; } = phase;
    public string FailureType { get; } = failureType;
}

internal interface IDaemonTestProcess : IFixtureProcess, IDisposable
{
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    TextReader StandardError { get; }
}

internal sealed class NativeDaemonTestProcess : IDaemonTestProcess
{
    private readonly Process _process;
    private readonly FixtureProcessInterruption.NativeFixtureProcess _lifetime;
    private NativeDaemonTestProcess(Process process) { _process = process; _lifetime = new(process); }
    public static NativeDaemonTestProcess Start(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Daemon test process did not start.");
            return new(process);
        }
        catch { process.Dispose(); throw; }
    }
    public Stream StandardInput => _process.StandardInput.BaseStream;
    public Stream StandardOutput => _process.StandardOutput.BaseStream;
    public TextReader StandardError => _process.StandardError;
    public bool HasExited => _lifetime.HasExited;
    public void Kill() => _lifetime.Kill();
    public Task WaitForExitAsync(CancellationToken token) => _lifetime.WaitForExitAsync(token);
    public void Dispose() => _process.Dispose();
}

/// <summary>
/// Collection definition for daemon integration tests.
/// Tests in this collection share a single daemon instance.
/// </summary>
[CollectionDefinition("Daemon")]
public class DaemonCollection : ICollectionFixture<DaemonTestFixture>
{
}
