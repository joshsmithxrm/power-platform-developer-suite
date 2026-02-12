using System;
using System.Threading;

namespace PPDS.Dataverse.BulkOperations;

/// <summary>
/// Dynamically adjusts the degree of parallelism (DOP) for bulk DML operations
/// in response to Dataverse 429 (service protection limit) errors.
/// Complementary to <see cref="AdaptiveBatchSizer"/> which adjusts batch SIZE;
/// this class adjusts thread COUNT.
/// </summary>
/// <remarks>
/// <para>
/// When a 429 throttle is encountered, <see cref="RecordThrottle"/> halves the active
/// thread count (minimum 1) to reduce pressure on the service. After sustained success
/// (<see cref="SuccessThreshold"/> consecutive successes AND a <see cref="CooldownPeriod"/>
/// since the last throttle), <see cref="RecordSuccess"/> increments the active thread count
/// back toward <see cref="MaxDop"/> one at a time.
/// </para>
/// <para>
/// Thread Safety: All mutable state uses <see cref="Interlocked"/> operations.
/// The <see cref="_clock"/> delegate enables deterministic testing without real-time delays.
/// </para>
/// </remarks>
public sealed class AdaptiveThrottleManager
{
    /// <summary>Default number of consecutive successes required before increasing DOP.</summary>
    public const int DefaultSuccessThreshold = 5;

    /// <summary>Default cooldown period after a throttle before DOP can increase.</summary>
    public static readonly TimeSpan DefaultCooldownPeriod = TimeSpan.FromSeconds(30);

    private readonly int _maxDop;
    private readonly int _successThreshold;
    private readonly TimeSpan _cooldownPeriod;
    private readonly Func<DateTime> _clock;

    private int _activeDop;
    private int _consecutiveSuccesses;
    private long _lastThrottleTicks;

    /// <summary>
    /// Gets the current active degree of parallelism.
    /// </summary>
    public int ActiveDop => Volatile.Read(ref _activeDop);

    /// <summary>
    /// Gets the maximum degree of parallelism (upper bound for recovery).
    /// </summary>
    public int MaxDop => _maxDop;

    /// <summary>
    /// Gets the number of consecutive successes since the last throttle or DOP increase.
    /// </summary>
    public int ConsecutiveSuccesses => Volatile.Read(ref _consecutiveSuccesses);

    /// <summary>
    /// Gets the number of consecutive successes required before DOP can increase.
    /// </summary>
    public int SuccessThreshold => _successThreshold;

    /// <summary>
    /// Gets the cooldown period after a throttle before DOP can increase.
    /// </summary>
    public TimeSpan CooldownPeriod => _cooldownPeriod;

    /// <summary>
    /// Gets the timestamp of the last throttle event, or <see cref="DateTime.MinValue"/>
    /// if no throttle has occurred.
    /// </summary>
    public DateTime LastThrottleTime =>
        new DateTime(Interlocked.Read(ref _lastThrottleTicks), DateTimeKind.Utc);

    /// <summary>
    /// Initializes a new instance of the <see cref="AdaptiveThrottleManager"/> class.
    /// </summary>
    /// <param name="maxDop">Maximum degree of parallelism (upper bound). Must be at least 1.</param>
    /// <param name="initialDop">
    /// Initial active DOP. If zero or negative, defaults to <paramref name="maxDop"/>.
    /// Clamped to [1, <paramref name="maxDop"/>].
    /// </param>
    /// <param name="successThreshold">
    /// Number of consecutive successes required before DOP can increase. Default: 5.
    /// </param>
    /// <param name="cooldownPeriod">
    /// Minimum time after a throttle before DOP can increase. Default: 30 seconds.
    /// If null, uses <see cref="DefaultCooldownPeriod"/>.
    /// </param>
    /// <param name="clock">
    /// Optional clock function for testability. Returns UTC now.
    /// If null, uses <see cref="DateTime.UtcNow"/>.
    /// </param>
    public AdaptiveThrottleManager(
        int maxDop,
        int initialDop = 0,
        int successThreshold = DefaultSuccessThreshold,
        TimeSpan? cooldownPeriod = null,
        Func<DateTime>? clock = null)
    {
        if (maxDop < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDop), "Must be at least 1.");

        _maxDop = maxDop;
        _activeDop = initialDop > 0 ? Math.Min(initialDop, maxDop) : maxDop;
        _successThreshold = successThreshold > 0 ? successThreshold : DefaultSuccessThreshold;
        _cooldownPeriod = cooldownPeriod ?? DefaultCooldownPeriod;
        _clock = clock ?? (() => DateTime.UtcNow);
        _lastThrottleTicks = DateTime.MinValue.Ticks;
    }

    /// <summary>
    /// Records a throttle (429) event. Halves the active DOP (minimum 1),
    /// resets the consecutive success counter, and records the throttle timestamp.
    /// </summary>
    public void RecordThrottle()
    {
        // Atomically halve: read current, compute new, exchange.
        // In a tight race two threads may both halve, which is acceptable
        // (aggressive reduction under heavy throttling is the desired behavior).
        var current = Volatile.Read(ref _activeDop);
        var newDop = Math.Max(1, current / 2);
        Interlocked.Exchange(ref _activeDop, newDop);
        Interlocked.Exchange(ref _consecutiveSuccesses, 0);
        Interlocked.Exchange(ref _lastThrottleTicks, _clock().Ticks);
    }

    /// <summary>
    /// Records a successful batch execution. After <see cref="SuccessThreshold"/>
    /// consecutive successes AND <see cref="CooldownPeriod"/> since the last throttle,
    /// increments the active DOP by one (up to <see cref="MaxDop"/>).
    /// </summary>
    public void RecordSuccess()
    {
        var successes = Interlocked.Increment(ref _consecutiveSuccesses);

        if (successes < _successThreshold)
            return;

        var currentDop = Volatile.Read(ref _activeDop);
        if (currentDop >= _maxDop)
            return;

        var lastThrottle = new DateTime(Interlocked.Read(ref _lastThrottleTicks), DateTimeKind.Utc);
        if ((_clock() - lastThrottle) < _cooldownPeriod)
            return;

        // Try to increment DOP by 1. CompareExchange ensures only one thread wins.
        if (Interlocked.CompareExchange(ref _activeDop, currentDop + 1, currentDop) == currentDop)
        {
            // Successfully incremented - reset consecutive counter for next ramp-up step
            Interlocked.Exchange(ref _consecutiveSuccesses, 0);
        }
        // If CAS failed, another thread changed _activeDop. The counter stays high,
        // so the next RecordSuccess call will re-evaluate. This is safe and correct.
    }
}
