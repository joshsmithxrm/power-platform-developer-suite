# ADR-0006: Execution Time Ceiling for Adaptive Rate Control

**Status:** Accepted
**Date:** 2025-12-24
**Applies to:** PPDS.Dataverse

## Context

Dataverse service protection limits include three dimensions:
1. **Request count**: 6,000 requests per 5-minute window per user
2. **Execution time**: 1,200 seconds (20 minutes) per 5-minute window per user
3. **Concurrent requests**: 52 simultaneous requests per user

The original AIMD adaptive rate controller (ADR-0004) focused on request count, adjusting parallelism based on throttle responses. However, **execution time exhaustion** was causing severe throttle cascades:

- Update/delete operations take 10-15 seconds per batch (server-side)
- At 25 parallelism with 12s batches: 25 × 12s = 300s execution time consumed per batch cycle
- The 1,200s budget (4s/second average) is quickly exhausted
- Result: 80-100 concurrent throttle responses, 1-8 minute Retry-After cascades

### The Problem: Fast vs Slow Operations

| Operation | Batch Time | At 30 Parallelism | Result |
|-----------|------------|-------------------|--------|
| Create | 7-8s | Well under budget | No throttle |
| Update | 10-15s | Exceeds budget | Throttle cascade |
| Delete | 8-12s | Borderline/exceeds | Throttle cascade |

A single parallelism ceiling doesn't work for all operation types.

## Decision

Implement an **execution time-aware ceiling** that dynamically adjusts based on observed batch durations.

### Algorithm

```
1. Track batch durations via exponential moving average (EMA)
   ema = α × newDuration + (1-α) × ema  // α = 0.3

2. Calculate ceiling based on batch time:
   ceiling = ExecutionTimeCeilingFactor / avgBatchSeconds

   Example: Factor=200, avgBatch=10s → ceiling = 20

3. Only apply ceiling for "slow" operations:
   if (avgBatchMs >= SlowBatchThresholdMs) {
     effectiveCeiling = min(hardCeiling, throttleCeiling, executionTimeCeiling)
   }

4. Fast operations (creates) run uncapped at full parallelism
```

### Configurable Presets

To simplify configuration, three presets provide sensible defaults:

| Preset | Factor | Threshold | Use Case |
|--------|--------|-----------|----------|
| **Conservative** | 140 | 6000ms | Production bulk jobs, deletes |
| **Balanced** | 200 | 8000ms | General purpose (default) |
| **Aggressive** | 320 | 11000ms | Dev/test with monitoring |

**Why Conservative uses Factor=140:**
- Creates ~20% headroom below the throttle limit
- At 8.5s batches: ceiling = 140/8.5 = **16** (vs 21 with Factor=180)
- Prevents throttle cascades that occur when running at 100% of ceiling capacity
- Lower threshold (6000ms) applies ceiling proactively for operations that slow down

### Configuration

**Simple (preset only):**
```json
{"Dataverse": {"AdaptiveRate": {"Preset": "Conservative"}}}
```

**Fine-tuned (preset + overrides):**
```json
{
  "Dataverse": {
    "AdaptiveRate": {
      "Preset": "Balanced",
      "ExecutionTimeCeilingFactor": 180,
      "SlowBatchThresholdMs": 7000
    }
  }
}
```

### Implementation Details

**Nullable backing fields** enable preset defaults with explicit overrides:

```csharp
private int? _executionTimeCeilingFactor;

public int ExecutionTimeCeilingFactor
{
    get => _executionTimeCeilingFactor ?? GetPresetDefaults(Preset).Factor;
    set => _executionTimeCeilingFactor = value;
}
```

This allows:
- `{"Preset": "Conservative"}` → Uses all Conservative values
- `{"Preset": "Conservative", "Factor": 200}` → Conservative base with Factor override

### Tuning History

Empirical testing with 42,366 records determined optimal preset values:

| Round | Factor | Threshold | Create | Update | Delete | Issue |
|-------|--------|-----------|--------|--------|--------|-------|
| 1 | 250 | 10000 | 542/s ✅ | 118/s ❌ 67 throttles | 78/s ❌ 103 throttles | Threshold too high; delete batches at 9.1s escaped ceiling |
| 2 | 200 | 8000 | 483/s ✅ | 153/s ✅ 0 throttles | 83/s ❌ 23 throttles | Balanced preset validated for creates/updates |
| 3 | 180 | 8000 | - | - | 175/s → 87/s ❌ cascade | Factor=180 gave zero headroom at ceiling |
| 4 | 140 | 6000 | - | - | ✅ 0 throttles | Conservative preset: 20% headroom below limit |

**Final validation:**
- **Balanced** (Factor=200, Threshold=8000): Creates 483/s, Updates 153/s, zero throttles
- **Conservative** (Factor=140, Threshold=6000): Recommended for deletes and production bulk jobs

**Why Conservative uses Factor=140 (not 180):**
At Factor=180 with 8.5s batches, ceiling = 180/8.5 = 21, and parallelism ran at 100% of ceiling (20 of 21). When server load spiked, immediate throttle cascade occurred (Retry-After escalating from 37s → 81s). Factor=140 creates ~20% headroom.

## Consequences

### Positive

- **Prevents throttle cascades**: Slow operations get lower ceilings automatically
- **Preserves fast operation throughput**: Creates run at full speed (under threshold)
- **Easy configuration**: Presets cover common scenarios
- **Fine-grained control**: Individual options for advanced tuning
- **appsettings.json support**: Full configuration binding compatibility

### Negative

- **EMA lag**: The moving average can lag behind sudden batch time changes
- **Per-connection tracking**: Each connection maintains separate state
- **No operation type awareness**: Relies on batch timing, not explicit operation type

### Trade-offs

| Approach | Pros | Cons |
|----------|------|------|
| Per-operation ceilings | Precise control | Requires API changes, complex config |
| Dynamic EMA (chosen) | Adapts automatically | Slight lag, simpler API |
| Fixed ceilings | Simple | One size doesn't fit all |

## References

- [Service protection API limits](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/api-limits) - Execution time budget details
- [Optimize performance for bulk operations](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/optimize-performance-create-update) - Microsoft's throughput guidance
- [ADR-0004: Throttle Recovery Strategy](0004_THROTTLE_RECOVERY_STRATEGY.md) - Base AIMD implementation
