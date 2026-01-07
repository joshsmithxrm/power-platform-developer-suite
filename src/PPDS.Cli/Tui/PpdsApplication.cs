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
    /// <returns>Exit code (0 for success).</returns>
    public int Run()
    {
        // Create session for connection pool reuse across screens
        _session = new InteractiveSession(_profileName, _deviceCodeCallback);

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
            _session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
