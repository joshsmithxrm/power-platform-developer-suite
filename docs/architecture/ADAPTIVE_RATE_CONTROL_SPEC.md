# Adaptive Rate Control - Design Specification

**Status:** Approved for Implementation
**Target:** Next release branch
**Author:** Claude Code
**Date:** 2025-12-23

---

## Problem Statement

After throttle recovery, the pool resumes at full parallelism, causing:
- Immediate re-throttling
- Progressively longer `Retry-After` durations (server extends penalties for aggressive clients)
- Suboptimal total throughput

Microsoft recommends: *"Start with a lower number of requests and gradually increase until you start hitting the service protection API limits. After that, let the server tell you how many requests it can handle."*

**Reference:** [Maximize API Throughput](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/data-entities/service-protection-maximizing-api-throughput)

---

## Solution: AIMD-based Adaptive Rate Control

Implement **Additive Increase, Multiplicative Decrease** (AIMD) - the algorithm that powers TCP congestion control, adapted for Dataverse API rate limiting.

### Core Principles

1. **Start conservative** - Begin at 50% of recommended parallelism
2. **Increase gradually** - Add parallelism only after sustained success
3. **Decrease aggressively** - Halve parallelism on throttle
4. **Fast recovery** - Return to last-known-good quickly, then probe cautiously
5. **Time-aware** - Respect Microsoft's 5-minute rolling window

---

## Algorithm

### State (Per Connection)

```
currentParallelism: int        # Current allowed concurrent requests
maxParallelism: int            # Ceiling from RecommendedDegreesOfParallelism
lastKnownGoodParallelism: int  # Level before last throttle
lastKnownGoodTimestamp: DateTime # When lastKnownGood was recorded
successesSinceThrottle: int    # Counter for stabilization
lastIncreaseTimestamp: DateTime # When we last increased (time-gating)
lastActivityTimestamp: DateTime # When we last had any activity (idle detection)
totalThrottleEvents: int       # Statistics
```

### Initialization

```
On first request for connection:
  maxParallelism = ServiceClient.RecommendedDegreesOfParallelism
  currentParallelism = floor(maxParallelism × InitialParallelismFactor)
  lastKnownGoodParallelism = currentParallelism
  lastKnownGoodTimestamp = now
  successesSinceThrottle = 0
  lastIncreaseTimestamp = now
  lastActivityTimestamp = now
```

### On Batch Success

```
lastActivityTimestamp = now
successesSinceThrottle++

# Check if lastKnownGood is stale (older than TTL)
if (now - lastKnownGoodTimestamp > LastKnownGoodTTL):
    lastKnownGoodParallelism = currentParallelism  # Treat current as baseline

# Check if we can increase (batch count AND time elapsed)
canIncrease = successesSinceThrottle >= StabilizationBatches
              AND (now - lastIncreaseTimestamp) >= MinIncreaseInterval

if canIncrease:
    if currentParallelism < lastKnownGoodParallelism:
        # Fast recovery phase - get back to known-good quickly
        increase = IncreaseRate × RecoveryMultiplier
    else:
        # Probing phase - cautiously explore above known-good
        increase = IncreaseRate

    currentParallelism = min(currentParallelism + increase, maxParallelism)
    successesSinceThrottle = 0
    lastIncreaseTimestamp = now
```

### On Throttle

```
lastActivityTimestamp = now
totalThrottleEvents++

# Remember current level as "almost good" (we were one step too high)
lastKnownGoodParallelism = max(currentParallelism - IncreaseRate, MinParallelism)
lastKnownGoodTimestamp = now

# Multiplicative decrease
currentParallelism = max(floor(currentParallelism × DecreaseFactor), MinParallelism)
successesSinceThrottle = 0
```

### On Get Parallelism (Before Each Chunk)

```
lastActivityTimestamp = now

# Check for idle reset
if (now - lastActivityTimestamp) > IdleResetPeriod:
    Reset()  # Start fresh

return currentParallelism
```

### Reset

```
currentParallelism = floor(maxParallelism × InitialParallelismFactor)
lastKnownGoodParallelism = currentParallelism
lastKnownGoodTimestamp = now
successesSinceThrottle = 0
lastIncreaseTimestamp = now
# Note: totalThrottleEvents is NOT reset (cumulative stat)
```

---

## Configuration

```csharp
public class AdaptiveRateOptions
{
    /// <summary>
    /// Enable/disable adaptive rate control. Default: true.
    /// When disabled, uses fixed parallelism from RecommendedDegreesOfParallelism.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Initial parallelism as factor of max (0.1-1.0). Default: 0.5.
    /// Starts at 50% of RecommendedDegreesOfParallelism.
    /// </summary>
    public double InitialParallelismFactor { get; set; } = 0.5;

    /// <summary>
    /// Minimum parallelism floor. Default: 1.
    /// Never goes below this regardless of throttling.
    /// </summary>
    public int MinParallelism { get; set; } = 1;

    /// <summary>
    /// Parallelism increase amount per stabilization period. Default: 2.
    /// </summary>
    public int IncreaseRate { get; set; } = 2;

    /// <summary>
    /// Multiplier applied on throttle (0.1-0.9). Default: 0.5.
    /// Halves parallelism on throttle.
    /// </summary>
    public double DecreaseFactor { get; set; } = 0.5;

    /// <summary>
    /// Successful batches required before considering increase. Default: 3.
    /// Must also satisfy MinIncreaseInterval.
    /// </summary>
    public int StabilizationBatches { get; set; } = 3;

    /// <summary>
    /// Minimum time between parallelism increases. Default: 5 seconds.
    /// Prevents rapid oscillation when batches complete quickly.
    /// </summary>
    public TimeSpan MinIncreaseInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Multiplier for recovery phase (getting back to last-known-good). Default: 2.0.
    /// Increases faster during recovery, slower when probing new territory.
    /// </summary>
    public double RecoveryMultiplier { get; set; } = 2.0;

    /// <summary>
    /// TTL for lastKnownGood value. Default: 5 minutes.
    /// Matches Microsoft's rolling window. Stale values are discarded.
    /// </summary>
    public TimeSpan LastKnownGoodTTL { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Idle period after which state resets. Default: 5 minutes.
    /// Long-running integrations with gaps get fresh starts.
    /// </summary>
    public TimeSpan IdleResetPeriod { get; set; } = TimeSpan.FromMinutes(5);
}
```

---

## Interface

```csharp
public interface IAdaptiveRateController
{
    /// <summary>
    /// Gets the current recommended parallelism for a connection.
    /// Also updates last activity timestamp and checks for idle reset.
    /// </summary>
    /// <param name="connectionName">The connection to get parallelism for.</param>
    /// <param name="maxParallelism">The ceiling (from RecommendedDegreesOfParallelism).</param>
    /// <returns>Current parallelism to use.</returns>
    int GetParallelism(string connectionName, int maxParallelism);

    /// <summary>
    /// Records successful batch completion. May increase parallelism if stable.
    /// </summary>
    /// <param name="connectionName">The connection that succeeded.</param>
    void RecordSuccess(string connectionName);

    /// <summary>
    /// Records throttle event. Reduces parallelism.
    /// </summary>
    /// <param name="connectionName">The connection that was throttled.</param>
    /// <param name="retryAfter">The Retry-After duration from server.</param>
    void RecordThrottle(string connectionName, TimeSpan retryAfter);

    /// <summary>
    /// Manually resets state for a connection.
    /// </summary>
    /// <param name="connectionName">The connection to reset.</param>
    void Reset(string connectionName);

    /// <summary>
    /// Gets current statistics for monitoring/logging.
    /// </summary>
    /// <param name="connectionName">The connection to get stats for.</param>
    /// <returns>Current statistics.</returns>
    AdaptiveRateStatistics GetStatistics(string connectionName);
}

public record AdaptiveRateStatistics
{
    public required string ConnectionName { get; init; }
    public required int CurrentParallelism { get; init; }
    public required int MaxParallelism { get; init; }
    public required int LastKnownGoodParallelism { get; init; }
    public required bool IsLastKnownGoodStale { get; init; }
    public required int SuccessesSinceThrottle { get; init; }
    public required int TotalThrottleEvents { get; init; }
    public required DateTime? LastThrottleTime { get; init; }
    public required DateTime? LastIncreaseTime { get; init; }
    public required DateTime LastActivityTime { get; init; }
}
```

---

## Integration Points

### BulkOperationExecutor Changes

Replace fixed parallelism with chunked adaptive execution:

```csharp
// Current (fixed parallelism)
var parallelism = await ResolveParallelismAsync(options.MaxParallelBatches, ct);
await Parallel.ForEachAsync(batches, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, ...);

// New (adaptive parallelism)
var maxParallelism = await GetMaxParallelismAsync(ct);
var batchQueue = new Queue<List<Entity>>(batches);

while (batchQueue.Count > 0)
{
    var connectionName = GetPrimaryConnectionName();
    var parallelism = _rateController.GetParallelism(connectionName, maxParallelism);

    // Dequeue up to 'parallelism' batches for this chunk
    var chunk = DequeueChunk(batchQueue, parallelism);

    // Process chunk with current parallelism
    var results = await ProcessChunkAsync(chunk, parallelism, ct);

    // Update rate controller based on results
    foreach (var result in results)
    {
        if (result.WasThrottled)
            _rateController.RecordThrottle(result.ConnectionName, result.RetryAfter);
        else
            _rateController.RecordSuccess(result.ConnectionName);
    }

    // Log current state
    var stats = _rateController.GetStatistics(connectionName);
    _logger.LogDebug(
        "Adaptive rate: {Current}/{Max} parallelism, {Successes} since throttle, {Total} total throttles",
        stats.CurrentParallelism, stats.MaxParallelism,
        stats.SuccessesSinceThrottle, stats.TotalThrottleEvents);
}
```

### Dependency Injection

```csharp
services.AddSingleton<IAdaptiveRateController, AdaptiveRateController>();
services.Configure<AdaptiveRateOptions>(configuration.GetSection("Dataverse:AdaptiveRate"));
```

### Coordination with ThrottleTracker

| Component | Responsibility | Scope |
|-----------|----------------|-------|
| `ThrottleTracker` | Binary: Is connection throttled? | Connection selection |
| `AdaptiveRateController` | Continuous: What parallelism? | Batch execution |

They work together but don't duplicate:
- ThrottleTracker prevents using throttled connections
- AdaptiveRateController optimizes parallelism to avoid throttling

---

## Example Scenario

```
Initial state:
  maxParallelism = 52 (from server)
  currentParallelism = 26 (50% of 52)

Time 0:00 - Batch 1-3 succeed
  successesSinceThrottle = 3
  MinIncreaseInterval not yet passed (< 5s)
  → No increase yet

Time 0:05 - Batch 4 succeeds
  successesSinceThrottle = 4, interval passed
  → Increase to 28 (probing: +2)

Time 0:10 - Batch 5-7 succeed
  successesSinceThrottle = 3, interval passed
  → Increase to 30

...continues ramping...

Time 1:00 - At parallelism 44, THROTTLE received
  lastKnownGoodParallelism = 42 (44 - 2)
  currentParallelism = 22 (44 × 0.5)
  successesSinceThrottle = 0

Time 1:05 - Throttle clears, Batch resumes, succeeds
  successesSinceThrottle = 1

Time 1:15 - 3 more successes, interval passed
  currentParallelism < lastKnownGoodParallelism (22 < 42)
  → Fast recovery: increase to 26 (+4, using 2× multiplier)

Time 1:20 - 3 more successes, interval passed
  → Fast recovery: increase to 30

...fast recovery continues...

Time 1:45 - Reached lastKnownGood (42)
  → Switch to probing: increase to 44 (+2)

Time 1:50 - 3 more successes
  → Probing: increase to 46

Time 6:00 - No activity for 5 minutes
  → Idle reset: currentParallelism = 26, start fresh
```

---

## File Changes

| File | Change |
|------|--------|
| `src/PPDS.Dataverse/Resilience/IAdaptiveRateController.cs` | New interface |
| `src/PPDS.Dataverse/Resilience/AdaptiveRateController.cs` | New implementation |
| `src/PPDS.Dataverse/Resilience/AdaptiveRateOptions.cs` | New configuration |
| `src/PPDS.Dataverse/Resilience/AdaptiveRateStatistics.cs` | New statistics record |
| `src/PPDS.Dataverse/DependencyInjection/DataverseOptions.cs` | Add `AdaptiveRate` section |
| `src/PPDS.Dataverse/DependencyInjection/ServiceCollectionExtensions.cs` | Register controller |
| `src/PPDS.Dataverse/BulkOperations/BulkOperationExecutor.cs` | Chunked adaptive execution |
| `tests/PPDS.Dataverse.Tests/Resilience/AdaptiveRateControllerTests.cs` | Unit tests |
| `docs/adr/0004_THROTTLE_RECOVERY_STRATEGY.md` | Update to reference this spec |

---

## Testing Strategy

### Unit Tests

1. **Initialization** - Verify initial parallelism is factor of max
2. **Increase logic** - Verify stabilization batches AND time interval required
3. **Decrease logic** - Verify multiplicative decrease on throttle
4. **Fast recovery** - Verify 2× increase rate when below lastKnownGood
5. **Probing** - Verify 1× increase rate when above lastKnownGood
6. **TTL expiry** - Verify stale lastKnownGood is ignored
7. **Idle reset** - Verify state resets after idle period
8. **Thread safety** - Verify concurrent access is safe
9. **Min/max bounds** - Verify parallelism stays within bounds

### Integration Tests

1. **Simulated throttle scenario** - Verify recovery behavior
2. **Long-running simulation** - Verify TTL and idle reset
3. **Multi-connection** - Verify per-connection state isolation

---

## Future Enhancements

1. **Per-entity-type tracking** - Some entities have heavier plugins
2. **Predictive adjustment** - Learn patterns over time
3. **Telemetry integration** - Expose metrics for monitoring dashboards
4. **Circuit breaker** - Stop entirely if too many throttles in window

---

## References

- [Service Protection API Limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits)
- [Maximize API Throughput](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/data-entities/service-protection-maximizing-api-throughput)
- [TCP Congestion Control (AIMD)](https://en.wikipedia.org/wiki/Additive_increase/multiplicative_decrease)
- ADR-0004: Throttle Recovery Strategy
