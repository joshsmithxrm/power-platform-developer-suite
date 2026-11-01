using System.Globalization;

namespace PPDS.LiveTests.Infrastructure;

/// <summary>Bounded propagation polling with deadline and caller cancellation kept distinct.</summary>
internal static class PropagationWait
{
    internal static TimeSpan TimeoutFromEnvironment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return TimeSpan.FromSeconds(90);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds is < 1 or > 900)
            throw new ArgumentException("PPDS_TEST_PROPAGATION_TIMEOUT_SECONDS must be an integer from 1 to 900.");
        return TimeSpan.FromSeconds(seconds);
    }

    internal static async Task<Result<T>> UntilAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<T, bool> succeeded,
        TimeSpan timeout,
        TimeSpan interval,
        CancellationToken cancellationToken = default,
        TimeProvider? clock = null) where T : class
    {
        if (timeout <= TimeSpan.Zero || interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        clock ??= TimeProvider.System;
        var started = clock.GetTimestamp();
        using var deadline = new CancellationTokenSource(timeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        T? last = null;
        var attempts = 0;
        string? lastError = null;
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempts++;
                last = await operation(linked.Token);
                cancellationToken.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested) break;
                if (succeeded(last))
                    return new(last, attempts, clock.GetElapsedTime(started), true, null);
                var remaining = timeout - clock.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining < interval ? remaining : interval, clock, linked.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            lastError = "Propagation deadline expired.";
        }
        catch (TimeoutException ex)
        {
            lastError = ex.Message;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(last, attempts, clock.GetElapsedTime(started), false, lastError);
    }

    internal sealed record Result<T>(T? Last, int Attempts, TimeSpan Elapsed, bool Succeeded, string? LastError);
}

