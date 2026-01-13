using System;

namespace PPDS.Dataverse.Resilience;

/// <summary>
/// Exception thrown when a Dataverse operation fails due to authentication or authorization issues.
/// </summary>
/// <remarks>
/// <para>
/// This exception is thrown during operation execution (not connection establishment) when
/// authentication fails. Use <see cref="RequiresReauthentication"/> to determine if the user
/// needs to re-authenticate.
/// </para>
/// <para>
/// Token failures (expired token, invalid credentials) set <see cref="RequiresReauthentication"/> to true.
/// Permission failures (user lacks privilege) set it to false.
/// </para>
/// </remarks>
public class DataverseAuthenticationException : Exception
{
    /// <summary>
    /// Gets whether the user needs to re-authenticate to continue.
    /// </summary>
    /// <remarks>
    /// True for token failures (expired, invalid credentials).
    /// False for permission failures (user lacks required privileges).
    /// </remarks>
    public bool RequiresReauthentication { get; }

    /// <summary>
    /// Gets the name of the connection that experienced the auth failure.
    /// </summary>
    public string? ConnectionName { get; }

    /// <summary>
    /// Gets the operation that was being performed when auth failed.
    /// </summary>
    public string? FailedOperation { get; }

    /// <summary>
    /// Gets a message safe to display to end users.
    /// </summary>
    public string UserMessage { get; }

    /// <summary>
    /// Creates a new authentication exception.
    /// </summary>
    /// <param name="userMessage">A user-friendly error message.</param>
    /// <param name="requiresReauthentication">Whether re-authentication is required.</param>
    /// <param name="innerException">The original exception.</param>
    public DataverseAuthenticationException(
        string userMessage,
        bool requiresReauthentication,
        Exception? innerException = null)
        : base(userMessage, innerException)
    {
        UserMessage = userMessage;
        RequiresReauthentication = requiresReauthentication;
    }

    /// <summary>
    /// Creates a new authentication exception with context.
    /// </summary>
    /// <param name="userMessage">A user-friendly error message.</param>
    /// <param name="requiresReauthentication">Whether re-authentication is required.</param>
    /// <param name="connectionName">The connection that failed.</param>
    /// <param name="failedOperation">The operation that was being performed.</param>
    /// <param name="innerException">The original exception.</param>
    public DataverseAuthenticationException(
        string userMessage,
        bool requiresReauthentication,
        string? connectionName,
        string? failedOperation,
        Exception? innerException = null)
        : base(userMessage, innerException)
    {
        UserMessage = userMessage;
        RequiresReauthentication = requiresReauthentication;
        ConnectionName = connectionName;
        FailedOperation = failedOperation;
    }

    /// <summary>
    /// Creates an authentication exception from an existing exception.
    /// </summary>
    /// <param name="exception">The original exception.</param>
    /// <param name="connectionName">The connection that failed.</param>
    /// <param name="failedOperation">The operation that was being performed.</param>
    /// <returns>A new DataverseAuthenticationException.</returns>
    public static DataverseAuthenticationException FromException(
        Exception exception,
        string? connectionName = null,
        string? failedOperation = null)
    {
        var userMessage = AuthenticationErrorDetector.GetUserMessage(exception);
        var requiresReauth = AuthenticationErrorDetector.RequiresReauthentication(exception);

        return new DataverseAuthenticationException(
            userMessage,
            requiresReauth,
            connectionName,
            failedOperation,
            exception);
    }
}
