using System;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Cross-platform helper for opening URLs in the default browser.
/// Delegates the actual launch to a swappable <see cref="IBrowserLauncher"/> —
/// production uses <see cref="DefaultBrowserLauncher"/>, tests install a
/// <see cref="NoOpBrowserLauncher"/> so real browser windows never spawn.
/// </summary>
public static class BrowserHelper
{
    private static IBrowserLauncher _launcher = new DefaultBrowserLauncher();

    /// <summary>
    /// The launcher used by <see cref="OpenUrl"/>. Tests replace this with a
    /// <see cref="NoOpBrowserLauncher"/> to suppress real browser launches.
    /// </summary>
    public static IBrowserLauncher Launcher
    {
        get => _launcher;
        set => _launcher = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Opens the specified URL in the system's default browser via <see cref="Launcher"/>.
    /// </summary>
    /// <param name="url">The URL to open. Must use the <c>http</c> or <c>https</c> scheme.</param>
    /// <returns>True if the browser was opened successfully, false otherwise.</returns>
    /// <exception cref="PpdsException">
    /// Thrown with <see cref="ErrorCodes.Validation.InvalidUrlScheme"/> when the URL is null,
    /// empty, malformed, or uses a scheme other than <c>http</c>/<c>https</c>.
    /// </exception>
    public static bool OpenUrl(string url) => _launcher.OpenUrl(url);

    /// <summary>
    /// Validates that a URL is safe to pass to the OS shell for browser launch.
    /// Only <c>http</c> and <c>https</c> are allowed. URLs may originate from
    /// untrusted Dataverse record values, so <c>file:</c>, <c>ftp:</c>,
    /// <c>javascript:</c>, <c>ms-*:</c>, and other custom handlers are rejected.
    /// </summary>
    /// <param name="url">The URL to validate.</param>
    /// <exception cref="PpdsException">Thrown when the URL is invalid or uses a disallowed scheme.</exception>
    public static void ValidateUrl(string? url)
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

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            && !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            throw new PpdsException(
                ErrorCodes.Validation.InvalidUrlScheme,
                $"Cannot open browser: URL scheme '{parsed.Scheme}' is not allowed. Only http and https are permitted.");
        }
    }
}
