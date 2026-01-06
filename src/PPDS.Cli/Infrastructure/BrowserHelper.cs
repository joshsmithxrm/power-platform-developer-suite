using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Cross-platform helper for opening URLs in the default browser.
/// </summary>
public static class BrowserHelper
{
    /// <summary>
    /// Opens the specified URL in the system's default browser.
    /// </summary>
    /// <param name="url">The URL to open.</param>
    /// <returns>True if the browser was opened successfully, false otherwise.</returns>
    public static bool OpenUrl(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else
            {
                // Linux and other Unix-like systems
                Process.Start("xdg-open", url);
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not open browser: {ex.Message}");
            Console.Error.WriteLine($"Please visit: {url}");
            return false;
        }
    }
}
