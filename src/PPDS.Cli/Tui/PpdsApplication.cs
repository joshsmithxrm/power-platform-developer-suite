using PPDS.Auth;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;

namespace PPDS.Cli.Tui;

/// <summary>
/// Entry point for the Terminal.Gui TUI application.
/// Provides the main menu and navigation between screens.
/// </summary>
internal sealed class PpdsApplication : IDisposable
{
    /// <summary>
    /// Timeout for session disposal to prevent hanging on exit.
    /// </summary>
    private static readonly TimeSpan SessionDisposeTimeout = TimeSpan.FromSeconds(3);

    private readonly string? _profileName;
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;
    private ProfileStore? _profileStore;
    private InteractiveSession? _session;
    private bool _disposed;

    public PpdsApplication(string? profileName, Action<DeviceCodeInfo>? deviceCodeCallback)
    {
        _profileName = profileName;
        _deviceCodeCallback = deviceCodeCallback;
    }

    /// <summary>
    /// Runs the TUI application. Blocks until the user exits.
    /// </summary>
    /// <param name="cancellationToken">Token to signal application shutdown.</param>
    /// <returns>Exit code (0 for success).</returns>
    public int Run(CancellationToken cancellationToken = default)
    {
        // Clear debug log for fresh session
        TuiDebugLog.Clear();
        TuiDebugLog.Log("TUI session starting");

        // Create shared ProfileStore singleton for all local services
        _profileStore = new ProfileStore();

        // Callback invoked before browser opens for interactive authentication.
        // Shows a dialog giving user control: Open Browser, Use Device Code, or Cancel.
        // Note: This callback is always invoked from a background thread (Task.Run in GetSeedClient),
        // so we always marshal to the UI thread via MainLoop.Invoke.
        Func<Action<DeviceCodeInfo>?, PreAuthDialogResult> beforeInteractiveAuth = (deviceCodeCallback) =>
        {
            // Terminal.Gui not yet initialized - default to browser auth
            if (Application.MainLoop == null)
            {
                TuiDebugLog.Log("Interactive auth triggered before TUI initialized - browser will open");
                return PreAuthDialogResult.OpenBrowser;
            }

            // Marshal to UI thread and wait for dialog result
            // Default to Cancel for fail-safe if dialog throws unexpectedly
            var result = PreAuthDialogResult.Cancel;
            using var waitHandle = new ManualResetEventSlim(false);
            Application.MainLoop.Invoke(() =>
            {
                try
                {
                    var dialog = new Dialogs.PreAuthenticationDialog(deviceCodeCallback);
                    Application.Run(dialog);
                    result = dialog.Result;
                    TuiDebugLog.Log($"Pre-auth dialog result: {result}");
                }
                finally
                {
                    waitHandle.Set();
                }
            });
            waitHandle.Wait();
            return result;
        };

        // Create session for connection pool reuse across screens
        _session = new InteractiveSession(_profileName, _profileStore, serviceProviderFactory: null, _deviceCodeCallback, beforeInteractiveAuth);

        // Start warming the connection pool in the background
        // This runs while Terminal.Gui initializes, so connection is ready faster
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling in InitializeAsync
        _ = _session.InitializeAsync(cancellationToken);
#pragma warning restore PPDS013

        // Register cancellation to request stop
        using var registration = cancellationToken.Register(() => Application.RequestStop());

        // Suppress auth status messages that would corrupt Terminal.Gui display
        // (AuthenticationOutput.Writer defaults to Console.WriteLine which Terminal.Gui captures)
        AuthenticationOutput.Writer = null;

        // Enable auth debug logging - redirect to TuiDebugLog for diagnostics
        AuthDebugLog.Writer = msg => TuiDebugLog.Log($"[Auth] {msg}");

        Application.Init();

        try
        {
            var mainWindow = new MainWindow(_profileName, _deviceCodeCallback, _session);
            Application.Top.Add(mainWindow);
            Application.Run();
            return 0;
        }
        finally
        {
            Application.Shutdown();
            AuthDebugLog.Reset();  // Clean up auth debug logging
            TuiDebugLog.Log("TUI shutdown, disposing session...");

            // Note: Sync-over-async is required here because Terminal.Gui's Application.Run()
            // is synchronous and we need to clean up the session before returning.
            // Use timeout to prevent hanging if connection pool is stuck.
#pragma warning disable PPDS012 // Terminal.Gui requires sync disposal
            if (_session != null)
            {
                var disposeTask = _session.DisposeAsync().AsTask();
                if (!disposeTask.Wait(SessionDisposeTimeout))
                {
                    TuiDebugLog.Log($"Session disposal timed out after {SessionDisposeTimeout.TotalSeconds}s - forcing exit");
                }
                else
                {
                    TuiDebugLog.Log("Session disposed successfully");
                }
            }
#pragma warning restore PPDS012
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
#pragma warning disable PPDS012 // IDisposable.Dispose must be synchronous
        if (_session != null)
        {
            var disposeTask = _session.DisposeAsync().AsTask();
            disposeTask.Wait(SessionDisposeTimeout); // Don't hang forever
        }
#pragma warning restore PPDS012
    }
}
