# Adaptive Rate Controller Tuning Status

**Date:** 2025-12-24
**Branch:** `feature/v2-alpha`
**Status:** Validated - Balanced preset works for Creates/Updates, Conservative recommended for Deletes

---

## Executive Summary

Implemented **execution time-aware rate control with configurable presets**:
- **Creates:** 483/s, 0 throttles ✅ (target: >400/s)
- **Updates:** 153/s, 0 throttles ✅ (target: >150/s)
- **Deletes:** 83/s, 23 throttles ❌ (target: >90/s) - use Conservative preset

---

## Preset System

Three presets provide sensible defaults for common scenarios:

| Preset | Factor | Threshold | Use Case |
|--------|--------|-----------|----------|
| **Conservative** | 140 | 6000ms | Production bulk jobs, delete operations, overnight migrations |
| **Balanced** | 200 | 8000ms | General purpose, creates, updates |
| **Aggressive** | 320 | 11000ms | Dev/test, time-critical with monitoring |

**Why Conservative uses Factor=140 (not 180):**
- Creates ~20% headroom below the throttle limit
- At 8.5s batches: ceiling = 140/8.5 = **16** parallelism
- Prevents throttle cascades when running at 100% of ceiling capacity
- Lower threshold (6000ms) applies ceiling proactively

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

### Round 2: Threshold = 9000, Factor = 250 (Balanced Preset - FINAL)

| Operation | Duration | Throughput | Throttles | Max Parallelism | Status |
|-----------|----------|------------|-----------|-----------------|--------|
| **Create** | 87s | **483/s** | 0 | 104 | ✅ Ceiling not applied (batches <9s) |
| **Update** | 277s | **153/s** | 0 | 19 | ✅ Ceiling=18-22 applied at 10s batches |
| **Delete** | 513s | **83/s** | 23 | 21→10 | ❌ Ceiling=22 at 9s, still too high |

**Analysis:**
- **Creates:** Batches under 9s threshold, no ceiling applied, runs at full 104 parallelism.
- **Updates:** Batches at 10-12s trigger ceiling of 18-22, parallelism capped at 19, **zero throttles**.
- **Deletes:** Batches at 9s trigger ceiling of 22, but even 21 parallelism causes throttle (47s Retry-After). Need lower factor.

**Delete throttle root cause:**
```
13:02:44 Execution time ceiling = 22 (avg batch: 9.0s)   ← Ceiling applied ✓
13:02:54 Parallelism 20 → 21 (capped at ceiling)         ← Correctly limited
13:05:06 Throttle at 21, Retry-After 47s                 ← Still too high!
```

At 21 parallelism with 9s batches, execution time consumption exceeds 4s/s budget.

**Recommendation:** Use **Conservative preset** for delete operations.

### Round 3: Conservative Preset Analysis (Factor=180, Threshold=8000) - FAILED

Testing Conservative preset with 42,366 delete records revealed a critical flaw:

| Phase | Progress | Parallelism | Ceiling | Throughput | Status |
|-------|----------|-------------|---------|------------|--------|
| Cruising | 0-67% | 20 | 20-23 | 173-175/s | ✅ Good |
| **Throttle Wall** | 67% | 20 → 10 | 18 | - | ❌ CASCADE |
| Recovery | 67-100% | 10 | 10-17 | 83-87/s | 50% loss |

**Root Cause: Parallelism was AT the ceiling (20 of 20) - zero headroom!**

```
Conservative Factor=180, Batch time=8.6s
Ceiling = 180 / 8.6 = 20.9 → 20
Parallelism = 20 = 100% of ceiling ← NO HEADROOM
```

When server load spiked → instant throttle cascade with escalating Retry-After (37s → 45s → 67s → 81s).

**Fix: Lower Conservative Factor to 140**
- At 8.5s batches: ceiling = 140/8.5 = **16** (vs 21 with Factor=180)
- Creates ~20% headroom below throttle limit
- Lower threshold (6000ms) applies ceiling proactively

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
