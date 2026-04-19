namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Abstraction over the OS-level browser launch used by CLI/TUI features.
/// Enables tests to swap in a no-op implementation so test runs never spawn
/// a real browser window (see <see cref="NoOpBrowserLauncher"/>).
/// </summary>
public interface IBrowserLauncher
{
    /// <summary>
    /// Opens the specified URL in the system's default browser.
    /// Implementations must still validate the URL via <see cref="BrowserHelper.ValidateUrl"/>
    /// so scheme-guard behaviour is preserved under any launcher.
    /// </summary>
    /// <param name="url">Absolute http(s) URL to open.</param>
    /// <returns>True if the launch was initiated successfully, false otherwise.</returns>
    bool OpenUrl(string url);
}
