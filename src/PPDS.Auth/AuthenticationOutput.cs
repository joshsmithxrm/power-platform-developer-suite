using System;

namespace PPDS.Auth;

/// <summary>
/// Controls authentication status output for the auth library.
/// </summary>
/// <remarks>
/// By default, authentication status messages are written to the console.
/// Library consumers who don't want console output can set <see cref="Writer"/> to null
/// or provide a custom action to redirect output.
/// </remarks>
public static class AuthenticationOutput
{
    // Lambda (not method-group) so Console.Error is re-evaluated on every call — if a test or
    // host redirects Console.Error after startup the new stream is used automatically.
    private static volatile Action<string>? _writer = static msg => Console.Error.WriteLine(msg);

    /// <summary>
    /// Gets or sets the action used to write authentication status messages.
    /// Set to null to suppress all output, or provide a custom action to redirect.
    /// Default: writes to Console.Error (stderr — stdout is reserved for data per PPDS I1/NEVER rule)
    /// </summary>
    /// <example>
    /// <code>
    /// // Suppress all auth output
    /// AuthenticationOutput.Writer = null;
    ///
    /// // Redirect to a logger
    /// AuthenticationOutput.Writer = message => logger.LogInformation(message);
    /// </code>
    /// </example>
    public static Action<string>? Writer
    {
        get => _writer;
        set => _writer = value;
    }

    /// <summary>
    /// Writes a status message using the configured writer.
    /// Does nothing if Writer is null.
    /// </summary>
    /// <param name="message">The message to write.</param>
    internal static void WriteLine(string message = "")
    {
        _writer?.Invoke(message);
    }

    /// <summary>
    /// Resets the writer to the default (Console.Error / stderr).
    /// </summary>
    public static void Reset()
    {
        _writer = static msg => Console.Error.WriteLine(msg);
    }
}
