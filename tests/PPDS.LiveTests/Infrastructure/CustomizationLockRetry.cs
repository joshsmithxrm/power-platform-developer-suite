using System.ServiceModel;
using Microsoft.Xrm.Sdk;

namespace PPDS.LiveTests.Infrastructure;

/// <summary>
/// Retries a Dataverse request that was refused because another customisation or solution operation holds the
/// organisation-wide customisation lock (other live suites creating tables or publishing in the same environment).
/// Recognition is by fault code only, never by message text. A refused request did not run, so re-sending it is safe.
/// </summary>
/// <remarks>
/// Codes, from Microsoft's Dataverse web service error code reference (learn.microsoft.com/power-apps/developer/data-platform/reference/web-service-error-codes):
/// CustomizationLockEx_BothUnknown 0x8004F061, _BlockedUnknown 0x8004F062, _BothKnownSame 0x8004F063 ("Cannot start
/// another [{0}] because there is a previous [{1}] running at this moment", the fault observed 2026-11-17),
/// _BothKnownDifferent 0x8004F064, _BlockingUnknown 0x8004F067; EntityCustomizationLockException 0x80090412;
/// RACCannotAcquireCustomizationLock 0x8004F077; SolutionConcurrencyFailure 0x80071151 ("The solution installation or
/// removal failed due to the installation or removal of another solution at the same time").
/// The approach is the one on branch test/metadata-second-language-live.
/// </remarks>
public static class CustomizationLockRetry
{
    private static readonly HashSet<int> LockCodes =
    [
        unchecked((int)0x8004F061), unchecked((int)0x8004F062), unchecked((int)0x8004F063), unchecked((int)0x8004F064),
        unchecked((int)0x8004F067), unchecked((int)0x80090412), unchecked((int)0x8004F077), unchecked((int)0x80071151)
    ];

    /// <summary>How long one request keeps retrying before the lock fault is rethrown.</summary>
    public static TimeSpan Budget { get; } = TimeSpan.FromMinutes(10);

    /// <summary>The lock fault code carried by <paramref name="exception"/> (or an inner exception or inner fault), if any.</summary>
    public static int? LockCode(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is FaultException<OrganizationServiceFault> { Detail: { } detail })
                for (var fault = detail; fault is not null; fault = fault.InnerFault)
                    if (LockCodes.Contains(fault.ErrorCode))
                        return fault.ErrorCode;
            if (LockCodes.Contains(current.HResult))
                return current.HResult;
        }

        return null;
    }

    /// <summary>
    /// Runs <paramref name="operation"/>, retrying with backoff (10 s, 20 s, 40 s, then every 60 s) for up to
    /// <see cref="Budget"/> while it fails with a customisation-lock fault. Any other failure propagates at once.
    /// </summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> operation, string what, Action<string>? log)
    {
        var started = DateTimeOffset.UtcNow;
        var delay = TimeSpan.FromSeconds(10);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex) when (LockCode(ex) is { } code && DateTimeOffset.UtcNow - started + delay <= Budget)
            {
                log?.Invoke($"Customisation lock (0x{unchecked((uint)code):x8}) on {what}, attempt {attempt}; retrying in {delay.TotalSeconds:F0}s. {ex.Message}");
                await Task.Delay(delay);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
        }
    }

    /// <summary>As <see cref="RunAsync{T}"/>, for an operation without a result.</summary>
    public static Task RunAsync(Func<Task> operation, string what, Action<string>? log)
        => RunAsync(async () => { await operation(); return true; }, what, log);
}
