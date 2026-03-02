using Microsoft.Extensions.DependencyInjection;
using PPDS.Auth;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;
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
    private bool _sessionDisposed;

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

        // Create shared auth service provider for ProfileStore and EnvironmentConfigStore
        using var authProvider = ProfileServiceFactory.CreateLocalProvider();
        _profileStore = authProvider.GetRequiredService<ProfileStore>();

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
                    // Force full screen redraw before showing dialog.
                    // Without this, Terminal.Gui's ConsoleDriver state may be unstable
                    // when Application.Run() is called from an async callback, causing
                    // the dialog border to not render properly.
                    Application.Refresh();
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
        _session = new InteractiveSession(
            _profileName,
            _profileStore,
            authProvider.GetRequiredService<EnvironmentConfigStore>(),
            serviceProviderFactory: null,
            _deviceCodeCallback,
            beforeInteractiveAuth);

        // Start warming the connection pool in the background
        // This runs while Terminal.Gui initializes, so connection is ready faster
        var initTask = _session.InitializeAsync(cancellationToken);
        _session.GetErrorService().FireAndForget(initTask, "SessionInit");

        // Register cancellation to request stop
        using var registration = cancellationToken.Register(() => Application.RequestStop());

        // Suppress auth status messages that would corrupt Terminal.Gui display
        // (AuthenticationOutput.Writer defaults to Console.WriteLine which Terminal.Gui captures)
        AuthenticationOutput.Writer = null;

        // Enable auth debug logging - redirect to TuiDebugLog for diagnostics
        AuthDebugLog.Writer = msg => TuiDebugLog.Log($"[Auth] {msg}");

        Application.Init();

        // Replace Terminal.Gui's clipboard with ours (supports OSC 52 fallback for SSH/WSL).
        // Must happen after Init() when the driver is available.
        PpdsClipboardInstaller.Install();

        // Override terminal's 16-color palette AFTER Init so OSC 4 sequences
        // apply to the screen buffer Terminal.Gui is actually rendering on.
        TuiTerminalPalette.Apply();

        // Set block cursor for better visibility in text fields (DECSCUSR)
        // \x1b[2 q = steady block cursor
        // \x1b]12;black\x07 = set cursor color to black (OSC 12)
        // Note: OSC 12 is terminal-dependent; unsupported terminals ignore it
        Console.Out.Write("\x1b[2 q\x1b]12;black\x07");
        Console.Out.Flush();

        // Override Terminal.Gui's global color defaults with our palette.
        // Without this, views that fall back to Colors.Base/TopLevel/etc.
        // use Terminal.Gui's built-in theme instead of our dark theme.
        Colors.Base = TuiColorPalette.Default;
        Colors.TopLevel = TuiColorPalette.Default;
        Colors.Menu = TuiColorPalette.MenuBar;
        Colors.Dialog = TuiColorPalette.Default;
        Colors.Error = TuiColorPalette.Error;

        // Application.Top was created by Init() with the old Colors.TopLevel.
        // Changing Colors.TopLevel doesn't retroactively update Top's scheme.
        Application.Top.ColorScheme = TuiColorPalette.Default;

        // Wire up global key interception via HotkeyRegistry
        // This intercepts ALL keys before any view processes them
        Application.RootKeyEvent += (keyEvent) =>
        {
            return _session?.GetHotkeyRegistry().TryHandle(keyEvent) == true;
        };

        try
        {
            var shell = new TuiShell(_profileName, _deviceCodeCallback, _session, initTask);
            Application.Top.Add(shell);

            // PALETTE RACE FIX: TuiTerminalPalette.Apply() emits OSC 4 sequences that remap
            // the terminal's 16 ANSI colors. The terminal processes these asynchronously — the
            // bytes are flushed but the terminal emulator hasn't finished remapping by the time
            // Application.Run() draws the first frame. This causes the first render to use the
            // terminal theme's default color mapping (e.g. Cyan renders as green in many themes).
            // Scheduling a refresh after a short delay ensures a full redraw occurs after the
            // terminal has processed the palette override.
            // DO NOT REMOVE — this is the fix for "colors wrong on first load" (#520).
            Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(100), _ =>
            {
                Application.Refresh();
                return false; // One-shot, don't repeat
            });

            // Global exception handler - catches exceptions from MainLoop.Invoke callbacks
            // that would otherwise crash the TUI. Reports to error service and continues.
            var errorService = _session.GetErrorService();
            Application.Run(errorHandler: ex =>
            {
                errorService.ReportError("Unexpected error", ex, "Application");
                TuiDebugLog.Log($"Global exception caught: {ex}");
                return true;  // true = handled, continue running
            });
            return 0;
        }
        finally
        {
            // Restore default cursor and color before Terminal.Gui cleanup
            // \x1b[0 q = restore default cursor shape
            // \x1b]112\x07 = restore default cursor color (OSC 112)
            Console.Out.Write("\x1b[0 q\x1b]112\x07");
            Console.Out.Flush();

            // Restore terminal's default 16-color palette
            TuiTerminalPalette.Restore();

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
                    _sessionDisposed = true;
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
        if (_session != null && !_sessionDisposed)
        {
            var disposeTask = _session.DisposeAsync().AsTask();
            disposeTask.Wait(SessionDisposeTimeout); // Don't hang forever
        }
#pragma warning restore PPDS012
    }
}
