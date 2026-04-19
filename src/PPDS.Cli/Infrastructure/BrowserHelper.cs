using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Cross-platform helper for opening URLs in the default browser.
/// </summary>
public static class BrowserHelper
{
    /// <summary>
    /// Opens the specified URL in the system's default browser.
    /// </summary>
    /// <param name="url">The URL to open. Must use the <c>http</c> or <c>https</c> scheme.</param>
    /// <returns>True if the browser was opened successfully, false otherwise.</returns>
    /// <exception cref="PpdsException">
    /// Thrown with <see cref="ErrorCodes.Validation.InvalidUrlScheme"/> when the URL is null,
    /// empty, malformed, or uses a scheme other than <c>http</c>/<c>https</c>. URLs are frequently
    /// derived from Dataverse record values, so this guard blocks attempts to launch
    /// <c>file:</c>, <c>ftp:</c>, or custom protocol handlers through shell execution.
    /// </exception>
    public static bool OpenUrl(string url)
    {
        ValidateUrl(url);

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

    /// <summary>
    /// Validates that a URL is safe to pass to the OS shell for browser launch.
    /// Only <c>http</c> and <c>https</c> are allowed.
    /// </summary>
    /// <param name="url">The URL to validate.</param>
    /// <exception cref="PpdsException">Thrown when the URL is invalid or uses a disallowed scheme.</exception>
    internal static void ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new PpdsException(
                ErrorCodes.Validation.InvalidUrlScheme,
                "Cannot open browser: URL is empty.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            throw new PpdsException(
                ErrorCodes.Validation.InvalidUrlScheme,
                $"Cannot open browser: '{url}' is not a valid absolute URL.");
        }

        // Explicit allowlist — never pass file:, ftp:, javascript:, ms-*:,
        // or any custom protocol handler to the OS shell. URLs may originate
        // from untrusted Dataverse record values.
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            && !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new PpdsException(
                ErrorCodes.Validation.InvalidUrlScheme,
                $"Cannot open browser: URL scheme '{parsed.Scheme}' is not allowed. Only http and https are permitted.");
        }
    }
}
