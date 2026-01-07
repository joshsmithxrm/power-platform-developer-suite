# ADR-0025: UI-Agnostic Progress Reporting

**Status:** Accepted
**Date:** 2026-01-06
**Authors:** Josh, Claude

## Context

Long-running operations (data migration, bulk import, solution export) need progress feedback. Different UIs display progress differently:

| UI | Progress Display |
|----|-----------------|
| CLI | Spectre.Console `Progress` with tasks and spinners |
| TUI | Terminal.Gui `ProgressBar` in status panels |
| VS Code | Notification with percentage, status bar item |
| Daemon Logs | Structured log events |

Current state: Some operations write directly to console, making them unusable in TUI/RPC contexts. Others accept `IProgress<T>` but with inconsistent snapshot types.

## Decision

### UI-Agnostic Interface

Services accept `IProgressReporter` for operations expected to take more than ~1 second:

```csharp
public interface IProgressReporter
{
    /// <summary>Reports progress snapshot with current/total counts.</summary>
    void ReportProgress(ProgressSnapshot snapshot);

    /// <summary>Reports phase change (e.g., "Exporting", "Importing").</summary>
    void ReportPhase(string phase, string? detail = null);

    /// <summary>Reports non-fatal warning during operation.</summary>
    void ReportWarning(string message);

    /// <summary>Reports informational message.</summary>
    void ReportInfo(string message);
}

public record ProgressSnapshot
{
    public required int CurrentItem { get; init; }
    public required int TotalItems { get; init; }
    public string? CurrentEntity { get; init; }
    public double? RecordsPerSecond { get; init; }
    public TimeSpan? EstimatedRemaining { get; init; }
    public string? StatusMessage { get; init; }
}
```

### Each UI Implements Adapter

```csharp
// CLI adapter - uses Spectre.Console
internal class CliProgressReporter : IProgressReporter
{
    private readonly ProgressContext _ctx;
    private readonly ProgressTask _task;

    public void ReportProgress(ProgressSnapshot snapshot)
    {
        _task.Value = snapshot.CurrentItem;
        _task.MaxValue = snapshot.TotalItems;
        _task.Description = snapshot.StatusMessage ?? $"{snapshot.CurrentEntity}...";
    }
}

// TUI adapter - uses Terminal.Gui ProgressBar
internal class TuiProgressReporter : IProgressReporter
{
    private readonly ProgressBar _progressBar;
    private readonly Label _statusLabel;

    public void ReportProgress(ProgressSnapshot snapshot)
    {
        _progressBar.Fraction = (float)snapshot.CurrentItem / snapshot.TotalItems;
        _statusLabel.Text = snapshot.StatusMessage ?? "";
    }
}

// RPC adapter - streams JSON-RPC notifications to VS Code
internal class RpcProgressReporter : IProgressReporter
{
    private readonly JsonRpcConnection _connection;

    public void ReportProgress(ProgressSnapshot snapshot)
    {
        _connection.NotifyAsync("progress", snapshot);
    }
}

// Null adapter - for non-interactive/silent operations
internal class NullProgressReporter : IProgressReporter
{
    public static readonly IProgressReporter Instance = new NullProgressReporter();
    public void ReportProgress(ProgressSnapshot snapshot) { }
    public void ReportPhase(string phase, string? detail = null) { }
    public void ReportWarning(string message) { }
    public void ReportInfo(string message) { }
}
```

### Service Implementation Pattern

```csharp
public class DataMigrationService : IDataMigrationService
{
    public async Task<ImportResult> ImportAsync(
        ImportRequest request,
        IProgressReporter progress,  // Required for long operations
        CancellationToken cancellationToken)
    {
        progress.ReportPhase("Analyzing", "Building dependency graph...");

        var plan = await BuildExecutionPlanAsync(request, cancellationToken);

        progress.ReportPhase("Importing", $"{plan.TotalRecords} records in {plan.TierCount} tiers");

        for (int i = 0; i < plan.TotalRecords; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entity = plan.GetEntity(i);
            progress.ReportProgress(new ProgressSnapshot
            {
                CurrentItem = i,
                TotalItems = plan.TotalRecords,
                CurrentEntity = entity.LogicalName,
                RecordsPerSecond = _rateCalculator.CurrentRate,
                EstimatedRemaining = _rateCalculator.EstimateRemaining(plan.TotalRecords - i)
            });

            await ImportEntityAsync(entity, cancellationToken);
        }

        return new ImportResult { ... };
    }
}
```

## Consequences

### Positive

- **Services don't know about UI frameworks** - No Spectre.Console/Terminal.Gui dependencies in service layer
- **Same service code works everywhere** - CLI, TUI, RPC, tests
- **Rich progress info** - Rate, ETA, entity-level detail available to all UIs
- **Testable** - Can verify progress reports in unit tests

### Negative

- **Additional parameter** - Services must accept `IProgressReporter`
- **Adapter boilerplate** - Each UI needs an adapter implementation

### Neutral

- **Gradual migration** - Existing services can add `IProgressReporter` incrementally
- **Optional for quick operations** - Use `NullProgressReporter.Instance` when progress not needed

## Implementation Guidelines

### When to Accept IProgressReporter

| Operation Duration | Recommendation |
|-------------------|----------------|
| < 100ms | Don't need progress |
| 100ms - 1s | Optional, phase reporting only |
| > 1s | Required with item-level progress |
| > 10s | Required with rate/ETA |

### ProgressSnapshot Best Practices

```csharp
// Include rate/ETA for long operations
new ProgressSnapshot
{
    CurrentItem = processed,
    TotalItems = total,
    CurrentEntity = "account",
    RecordsPerSecond = 1234.5,
    EstimatedRemaining = TimeSpan.FromMinutes(2),
    StatusMessage = "Creating 500 accounts..."  // Human-readable
}

// Minimal for quick operations
new ProgressSnapshot
{
    CurrentItem = i,
    TotalItems = count
}
```

## References

- ADR-0015: Application Service Layer
- ADR-0024: Shared Local State Architecture
- Existing pattern: `IProgress<ImportProgress>` in `TieredImporter`
