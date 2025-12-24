# Adaptive Rate Controller Tuning Status

**Date:** 2025-12-24
**Branch:** `feature/v2-alpha`
**Status:** Preset System Implemented - Awaiting Validation

---

## Executive Summary

Implemented **execution time-aware rate control with configurable presets**:
- **Creates:** Full speed preserved (~545/s, 0 throttles)
- **Updates/Deletes:** Tuned via presets to balance throughput vs throttle avoidance
- **Configuration:** Simple preset selection or fine-grained tuning via appsettings.json

---

## Preset System

Three presets provide sensible defaults for common scenarios:

| Preset | Factor | Threshold | Use Case |
|--------|--------|-----------|----------|
| **Conservative** | 180 | 7000ms | Production bulk jobs, overnight migrations |
| **Balanced** | 200 | 8000ms | General purpose, mixed workloads |
| **Aggressive** | 320 | 11000ms | Dev/test, time-critical with monitoring |

### Configuration Examples

**appsettings.json - Simple:**
```json
{
  "Dataverse": {
    "AdaptiveRate": {
      "Preset": "Conservative"
    }
  }
}
```

**appsettings.json - With override:**
```json
{
  "Dataverse": {
    "AdaptiveRate": {
      "Preset": "Balanced",
      "ExecutionTimeCeilingFactor": 180
    }
  }
}
```

---

## Tuning History

### Round 1: Initial Implementation (SlowBatchThresholdMs = 10000)

| Operation | Throughput | Throttles | Issue |
|-----------|------------|-----------|-------|
| Create | 542/s | 0 | ✅ Excellent |
| Update | 118/s | ~67 | ❌ Ceiling borderline |
| Delete | 78.8/s | ~103 | ❌ Ceiling escaped (9.1s < 10s) |

**Problem:** Delete batches at 9.1s were under the 10s threshold, allowing ramp to 30 parallelism before ceiling applied.

### Round 2: Threshold = 9000, Factor = 250

| Operation | Throughput | Throttles | Issue |
|-----------|------------|-----------|-------|
| Create | 545/s | 0 | ✅ Excellent |
| Update | 100/s | 39 | ❌ Worse - ceiling still too high |
| Delete | 67/s | 25 | ❌ Worse - batches at 6.6-8.5s escaped |

**Problem:** Delete batches started at 6.6s, ramped to 30 before hitting 9s threshold. Factor of 250 gave ceiling of 25-27, still too aggressive.

### Round 3: Threshold = 8000, Factor = 200 (Current)

**Expected improvements:**
- Delete at 8s: ceiling = 200/8 = **25** (catches before ramp to 30)
- Update at 10s: ceiling = 200/10 = **20** (prevents throttle at 22)
- Create: under 8s threshold, runs free at full parallelism

**Awaiting validation run.**

---

## Current Implementation

### Public Options (AdaptiveRateOptions)

| Option | Type | Description |
|--------|------|-------------|
| `Enabled` | bool | Master switch (default: true) |
| `Preset` | enum | Conservative, Balanced, Aggressive |
| `ExecutionTimeCeilingEnabled` | bool | Enable execution time ceiling |
| `ExecutionTimeCeilingFactor` | int | Ceiling = Factor / batchSeconds |
| `SlowBatchThresholdMs` | int | Threshold for ceiling activation |
| `DecreaseFactor` | double | Throttle backoff multiplier |
| `StabilizationBatches` | int | Successes before parallelism increase |
| `MinIncreaseInterval` | TimeSpan | Minimum time between increases |
| `MaxRetryAfterTolerance` | TimeSpan? | Fail-fast if Retry-After exceeds this |

### Internal Options (Fixed Constants)

| Option | Value | Rationale |
|--------|-------|-----------|
| `HardCeiling` | 52 | Microsoft's per-user limit |
| `MinParallelism` | 1 | Absolute minimum fallback |
| `MinBatchSamplesForCeiling` | 3 | Statistical minimum for EMA |
| `BatchDurationSmoothingFactor` | 0.3 | Standard EMA weight |

---

## Algorithm

```
1. Track batch durations via EMA
2. Calculate ceiling = Factor / avgBatchSeconds
3. Only apply ceiling if avgBatchMs >= SlowBatchThresholdMs
4. Effective ceiling = min(HardCeiling, ThrottleCeiling, ExecutionTimeCeiling)
5. On success: increase parallelism by floor (after stabilization)
6. On throttle: decrease by DecreaseFactor, set throttle ceiling
```

---

## Success Criteria

| Operation | Target Throughput | Target Throttles |
|-----------|-------------------|------------------|
| Create | >400/s | 0 |
| Update | >120/s | 0-few (short Retry-After OK) |
| Delete | >80/s | 0-few (short Retry-After OK) |

---

## Files Changed

| File | Changes |
|------|---------|
| `RateControlPreset.cs` | NEW - Preset enum |
| `AdaptiveRateOptions.cs` | Preset support, nullable backing fields, internal options |
| `AdaptiveRateController.cs` | Uses options (no changes needed) |
| `AdaptiveRateControllerTests.cs` | Updated for new API, preset tests |

---

## Next Steps

1. **Validate Round 3** - Run tests with Balanced preset (Factor=200, Threshold=8000)
2. **Adjust if needed** - May need to lower further for Conservative to be the safe choice
3. **Document** - Update README, CLAUDE.md with preset guidance
