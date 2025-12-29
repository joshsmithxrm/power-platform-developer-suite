using System;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Exception thrown when authentication fails.
/// </summary>
public class AuthenticationException : Exception
{
    /// <summary>
    /// Gets the error code, if available.
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// Creates a new authentication exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    public AuthenticationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates a new authentication exception with an inner exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public AuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Creates a new authentication exception with an error code.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="errorCode">The error code.</param>
    /// <param name="innerException">The inner exception.</param>
    public AuthenticationException(string message, string errorCode, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}
