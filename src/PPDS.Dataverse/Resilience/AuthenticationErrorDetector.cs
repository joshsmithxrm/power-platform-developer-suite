using System;
using System.Net.Http;
using System.ServiceModel;
using System.ServiceModel.Security;
using Microsoft.Xrm.Sdk;

namespace PPDS.Dataverse.Resilience;

/// <summary>
/// Detects authentication and authorization failures from Dataverse operations.
/// </summary>
/// <remarks>
/// <para>
/// This class distinguishes between two types of auth failures:
/// </para>
/// <list type="bullet">
/// <item>
/// <term>Token failures</term>
/// <description>
/// The authentication context itself is broken (token expired, credential invalid).
/// Requires re-authentication and seed invalidation.
/// </description>
/// </item>
/// <item>
/// <term>Permission failures</term>
/// <description>
/// Authentication is valid but user lacks required privileges.
/// Does not require re-authentication.
/// </description>
/// </item>
/// </list>
/// </remarks>
public static class AuthenticationErrorDetector
{
    // Common Dataverse error codes for auth failures
    private static readonly int[] PermissionErrorCodes =
    [
        -2147180286, // Caller does not have privilege
        -2147204720, // User is disabled
        -2147180285, // Access denied
    ];

    /// <summary>
    /// Checks if an exception indicates an authentication or authorization failure.
    /// </summary>
    /// <param name="exception">The exception to check.</param>
    /// <returns>True if this is an authentication or authorization failure.</returns>
    public static bool IsAuthenticationFailure(Exception exception)
    {
        // MessageSecurityException indicates the token wasn't sent or was rejected.
        // This can occur when the OAuth token expires and refresh fails.
        if (exception is MessageSecurityException)
        {
            return true;
        }

        // Check for common auth failure patterns in FaultException
        if (exception is FaultException<OrganizationServiceFault> faultEx)
        {
            var fault = faultEx.Detail;

            // Check error codes
            if (Array.IndexOf(PermissionErrorCodes, fault.ErrorCode) >= 0)
            {
                return true;
            }

            // Check message for auth-related keywords
            var message = fault.Message?.ToLowerInvariant() ?? "";
            if (message.Contains("authentication") ||
                message.Contains("authorization") ||
                message.Contains("token") ||
                message.Contains("expired") ||
                message.Contains("credential"))
            {
                return true;
            }
        }

        // Check for HTTP 401/403 in inner exceptions
        if (exception.InnerException is HttpRequestException httpEx)
        {
            var message = httpEx.Message?.ToLowerInvariant() ?? "";
            if (message.Contains("401") || message.Contains("403") ||
                message.Contains("unauthorized") || message.Contains("forbidden"))
            {
                return true;
            }
        }

        // Check HttpRequestException directly (for newer .NET versions)
        if (exception is HttpRequestException directHttpEx)
        {
            var message = directHttpEx.Message?.ToLowerInvariant() ?? "";
            if (message.Contains("401") || message.Contains("403") ||
                message.Contains("unauthorized") || message.Contains("forbidden"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if an exception indicates a token/credential failure that requires re-authentication.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Token failures require invalidating the cached authentication and re-authenticating.
    /// Permission failures (user lacks privilege, user disabled) don't require re-authentication
    /// because the authentication is valid - the user just doesn't have access.
    /// </para>
    /// </remarks>
    /// <param name="exception">The exception to check.</param>
    /// <returns>True if this is a token failure requiring re-authentication.</returns>
    public static bool RequiresReauthentication(Exception exception)
    {
        // MessageSecurityException with "Anonymous" means the token wasn't sent at all.
        // This is the clearest indicator that the token expired and MSAL refresh failed.
        if (exception is MessageSecurityException)
        {
            return true;
        }

        // HTTP 401 Unauthorized means the token was rejected by the server.
        // This is different from 403 Forbidden which is typically a permission issue.
        if (exception.InnerException is HttpRequestException httpEx)
        {
            var message = httpEx.Message?.ToLowerInvariant() ?? "";
            if (message.Contains("401") || message.Contains("unauthorized"))
            {
                return true;
            }
        }

        // Check HttpRequestException directly
        if (exception is HttpRequestException directHttpEx)
        {
            var message = directHttpEx.Message?.ToLowerInvariant() ?? "";
            if (message.Contains("401") || message.Contains("unauthorized"))
            {
                return true;
            }
        }

        // Check for explicit token expiration in FaultException messages
        if (exception is FaultException<OrganizationServiceFault> faultEx)
        {
            var message = faultEx.Detail.Message?.ToLowerInvariant() ?? "";

            // Token expiration messages
            if (message.Contains("token") && message.Contains("expired"))
            {
                return true;
            }

            // Credential issues
            if (message.Contains("credential") &&
                (message.Contains("invalid") || message.Contains("expired")))
            {
                return true;
            }

            // AADSTS errors are Azure AD token errors
            if (message.Contains("aadsts"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets a user-friendly message for an authentication failure.
    /// </summary>
    /// <param name="exception">The exception to describe.</param>
    /// <returns>A user-friendly error message.</returns>
    public static string GetUserMessage(Exception exception)
    {
        if (RequiresReauthentication(exception))
        {
            return "Your session has expired. Please re-authenticate to continue.";
        }

        // Permission failure
        if (exception is FaultException<OrganizationServiceFault> faultEx)
        {
            return faultEx.Detail.ErrorCode switch
            {
                -2147180286 => "You don't have the required privileges for this operation.",
                -2147204720 => "Your user account has been disabled.",
                -2147180285 => "Access denied. Check your security role permissions.",
                _ => "Authentication failed. Please check your credentials and permissions."
            };
        }

        return "Authentication failed. Please check your credentials and permissions.";
    }
}
