using PPDS.Auth.Credentials;
using PPDS.Cli.Interactive;
using Terminal.Gui;

namespace PPDS.Cli.Tui;

/// <summary>
/// Entry point for the Terminal.Gui TUI application.
/// Provides the main menu and navigation between screens.
/// </summary>
internal sealed class PpdsApplication : IDisposable
{
    private readonly string? _profileName;
    private readonly Action<DeviceCodeInfo>? _deviceCodeCallback;
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
        // Create session for connection pool reuse across screens
        _session = new InteractiveSession(_profileName, _deviceCodeCallback);

        // Register cancellation to request stop
        using var registration = cancellationToken.Register(() => Application.RequestStop());

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
            // Note: Sync-over-async is required here because Terminal.Gui's Application.Run()
            // is synchronous and we need to clean up the session before returning.
            // The session disposal is fast (just releases pooled connections).
#pragma warning disable PPDS012 // Terminal.Gui requires sync disposal
            _session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore PPDS012
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
#pragma warning disable PPDS012 // IDisposable.Dispose must be synchronous
        _session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore PPDS012
    }
}
