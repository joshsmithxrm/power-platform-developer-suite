# ADR-0027: Operation Clock

**Status:** Accepted
**Date:** 2026-01-07
**Authors:** Josh, Claude

## Context

Long-running CLI operations (import, export) display elapsed time in two places:

1. **Progress reporter** (`ConsoleProgressReporter`) - shows `[+00:00:04.279]` prefix
2. **MEL logger** (`ElapsedTimeConsoleFormatter`) - shows `[+00:00:00.000]` prefix

Each component was creating its own `Stopwatch`, resulting in desynchronized timestamps:

```
[+00:00:04.279] Processing tier 0: queue
[+00:00:00.000] fail: PPDS.Dataverse...    ← time jumped backwards!
[+00:00:00.008] warn: PPDS.Migration...
[+00:00:10.703] Tier 0 completed...
```

This violates Single Responsibility Principle - "elapsed time since operation started" has no single owner.

### Gap in ADR-0025

ADR-0025 (UI-Agnostic Progress) defines `IProgressReporter` and includes `RecordsPerSecond` and `EstimatedRemaining` in `ProgressSnapshot`, implying timing exists. However, it doesn't define:

- Who owns the operation start time
- How other components (like MEL formatters) access elapsed time

## Decision

### Single Owner: `OperationClock`

Introduce a static `OperationClock` that owns the operation start time:

```csharp
namespace PPDS.Migration.Progress;

/// <summary>
/// Provides elapsed time since the current operation started.
/// Call Start() at the beginning of each CLI command.
/// </summary>
public static class OperationClock
{
    private static readonly Stopwatch Stopwatch = new();

    /// <summary>
    /// Gets the elapsed time since Start() was called.
    /// Returns TimeSpan.Zero if not started.
    /// </summary>
    public static TimeSpan Elapsed => Stopwatch.Elapsed;

    /// <summary>
    /// Starts or restarts the operation clock.
    /// Call at the beginning of each CLI command.
    /// </summary>
    public static void Start() => Stopwatch.Restart();
}
```

### Consumers Use OperationClock

Both progress reporter and MEL formatter read from the same source:

```csharp
// ElapsedTimeConsoleFormatter
var elapsed = OperationClock.Elapsed;
var timestamp = $"[+{elapsed:hh\\:mm\\:ss\\.fff}]";

// ConsoleProgressReporter
var prefix = $"[+{OperationClock.Elapsed:hh\\:mm\\:ss\\.fff}]";
```

### CLI Commands Start the Clock

Each command starts the clock before doing work:

```csharp
// ImportCommand.ExecuteAsync
OperationClock.Start();
var progressReporter = ServiceFactory.CreateProgressReporter(...);
// ... rest of command
```

## Consequences

### Positive

- **Single source of truth** - All elapsed times are consistent
- **Simple implementation** - Static class, no DI complexity
- **Matches user mental model** - Time flows forward in logs

### Negative

- **Static state** - Harder to test, not DI-friendly
- **Requires explicit start** - CLI commands must remember to call `Start()`

### Neutral

- **Manual start required** - Commands must call `Start()`, but this is explicit and clear

## Alternatives Considered

### 1. Inject `IOperationClock` via DI

More testable but adds complexity. The clock needs to be available to MEL formatters which are created early in the DI pipeline, making injection awkward.

### 2. Progress reporter owns clock, MEL reads from it

Creates coupling between logging and progress systems. MEL formatter would need to know about `IProgressReporter`.

### 3. Pass start time through `ProgressSnapshot`

Would require all components to thread the start time through, adding noise to every call site.

## References

- ADR-0025: UI-Agnostic Progress Reporting (gap this fills)
