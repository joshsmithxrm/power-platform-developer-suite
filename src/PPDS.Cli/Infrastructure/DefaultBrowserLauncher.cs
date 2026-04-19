using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Production <see cref="IBrowserLauncher"/> that spawns the OS default browser.
/// </summary>
public sealed class DefaultBrowserLauncher : IBrowserLauncher
{
    /// <inheritdoc />
    public bool OpenUrl(string url)
    {
        BrowserHelper.ValidateUrl(url);

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
