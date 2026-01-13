using System;
using System.ServiceModel;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;

namespace PPDS.Dataverse.Resilience;

/// <summary>
/// Detects Dataverse service protection throttle events and authentication errors.
/// Extracted from PooledClient for testability and SRP.
/// </summary>
/// <remarks>
/// <para>
/// This class wraps Dataverse operations to detect and handle two types of errors:
/// </para>
/// <list type="bullet">
/// <item>
/// <term>Throttle errors</term>
/// <description>
/// Service protection (429) errors are recorded via callback and re-thrown.
/// </description>
/// </item>
/// <item>
/// <term>Authentication errors</term>
/// <description>
/// 401/403 and token expiry errors are wrapped in <see cref="DataverseAuthenticationException"/>.
/// </description>
/// </item>
/// </list>
/// </remarks>
internal sealed class ThrottleDetector
{
    private static readonly TimeSpan FallbackRetryAfter = TimeSpan.FromSeconds(30);

    private readonly string _connectionName;
    private readonly Action<string, TimeSpan>? _onThrottle;
    private readonly Action<string>? _onAuthFailure;

    /// <summary>
    /// Creates a new throttle detector.
    /// </summary>
    /// <param name="connectionName">The connection name for reporting.</param>
    /// <param name="onThrottle">Callback when throttle is detected (connectionName, retryAfter).</param>
    /// <param name="onAuthFailure">Optional callback when auth failure is detected (connectionName).</param>
    public ThrottleDetector(
        string connectionName,
        Action<string, TimeSpan>? onThrottle,
        Action<string>? onAuthFailure = null)
    {
        _connectionName = connectionName ?? throw new ArgumentNullException(nameof(connectionName));
        _onThrottle = onThrottle;
        _onAuthFailure = onAuthFailure;
    }

    /// <summary>
    /// Checks if an exception is a service protection error and extracts the RetryAfter.
    /// </summary>
    /// <param name="ex">The exception to check.</param>
    /// <returns>True if the exception was a throttle and was handled.</returns>
    public bool TryHandleThrottle(Exception ex)
    {
        if (ex is not FaultException<OrganizationServiceFault> faultEx)
        {
            return false;
        }

        var fault = faultEx.Detail;
        if (!ServiceProtectionException.IsServiceProtectionError(fault.ErrorCode))
        {
            return false;
        }

        var retryAfter = ExtractRetryAfter(fault);
        _onThrottle?.Invoke(_connectionName, retryAfter);
        return true;
    }

    /// <summary>
    /// Extracts the Retry-After duration from a fault.
    /// </summary>
    private static TimeSpan ExtractRetryAfter(OrganizationServiceFault fault)
    {
        if (fault.ErrorDetails != null &&
            fault.ErrorDetails.TryGetValue("Retry-After", out var retryAfterObj))
        {
            return retryAfterObj switch
            {
                TimeSpan ts => ts,
                int seconds => TimeSpan.FromSeconds(seconds),
                double seconds => TimeSpan.FromSeconds(seconds),
                _ => FallbackRetryAfter
            };
        }

        return FallbackRetryAfter;
    }

    /// <summary>
    /// Checks if an exception is an authentication error and handles it.
    /// </summary>
    /// <param name="ex">The exception to check.</param>
    /// <param name="operationName">The name of the operation that failed.</param>
    /// <returns>True if the exception was an auth error (to match in exception filter).</returns>
    private bool TryHandleAuthError(Exception ex, string? operationName = null)
    {
        if (!AuthenticationErrorDetector.IsAuthenticationFailure(ex))
        {
            return false;
        }

        // Notify callback if registered
        _onAuthFailure?.Invoke(_connectionName);

        return true;
    }

    /// <summary>
    /// Wraps an exception in DataverseAuthenticationException if it's an auth error.
    /// </summary>
    private DataverseAuthenticationException WrapAuthError(Exception ex, string? operationName = null)
    {
        return DataverseAuthenticationException.FromException(ex, _connectionName, operationName);
    }

    /// <summary>
    /// Wraps a synchronous operation with throttle and auth detection.
    /// </summary>
    public T Execute<T>(Func<T> operation, string? operationName = null)
    {
        try
        {
            return operation();
        }
        catch (Exception ex) when (TryHandleThrottle(ex))
        {
            throw; // Re-throw after recording throttle
        }
        catch (Exception ex) when (TryHandleAuthError(ex, operationName))
        {
            throw WrapAuthError(ex, operationName);
        }
    }

    /// <summary>
    /// Wraps a synchronous void operation with throttle and auth detection.
    /// </summary>
    public void Execute(Action operation, string? operationName = null)
    {
        try
        {
            operation();
        }
        catch (Exception ex) when (TryHandleThrottle(ex))
        {
            throw; // Re-throw after recording throttle
        }
        catch (Exception ex) when (TryHandleAuthError(ex, operationName))
        {
            throw WrapAuthError(ex, operationName);
        }
    }

    /// <summary>
    /// Wraps an async operation with throttle and auth detection.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, string? operationName = null)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (TryHandleThrottle(ex))
        {
            throw; // Re-throw after recording throttle
        }
        catch (Exception ex) when (TryHandleAuthError(ex, operationName))
        {
            throw WrapAuthError(ex, operationName);
        }
    }

    /// <summary>
    /// Wraps an async void operation with throttle and auth detection.
    /// </summary>
    public async Task ExecuteAsync(Func<Task> operation, string? operationName = null)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception ex) when (TryHandleThrottle(ex))
        {
            throw; // Re-throw after recording throttle
        }
        catch (Exception ex) when (TryHandleAuthError(ex, operationName))
        {
            throw WrapAuthError(ex, operationName);
        }
    }
}
