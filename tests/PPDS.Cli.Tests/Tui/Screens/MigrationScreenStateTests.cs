using PPDS.Cli.Tui.Testing.States;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Screens;

[Trait("Category", "TuiUnit")]
public sealed class MigrationScreenStateTests
{
    [Fact]
    public void CapturesExportModeDefaults()
    {
        var state = new MigrationScreenState(
            Mode: "Export",
            OperationState: "Idle",
            SchemaPath: null,
            OutputPath: null,
            DataPath: null,
            CurrentPhase: null,
            CurrentEntity: null,
            RecordCount: 0,
            SuccessCount: 0,
            FailureCount: 0,
            WarningCount: 0,
            SkipCount: 0,
            RecordsPerSecond: null,
            EstimatedRemaining: null,
            Elapsed: null,
            ErrorMessage: null,
            IsLoading: false,
            StatusMessage: null);

        Assert.Equal("Export", state.Mode);
        Assert.Equal("Idle", state.OperationState);
        Assert.False(state.IsLoading);
        Assert.Equal(0, state.RecordCount);
    }

    [Fact]
    public void CapturesImportModeRunningState()
    {
        var state = new MigrationScreenState(
            Mode: "Import",
            OperationState: "Running",
            SchemaPath: null,
            OutputPath: null,
            DataPath: "/path/to/data.zip",
            CurrentPhase: "Importing records...",
            CurrentEntity: "account",
            RecordCount: 150,
            SuccessCount: 145,
            FailureCount: 5,
            WarningCount: 2,
            SkipCount: 0,
            RecordsPerSecond: 42.5,
            EstimatedRemaining: "1m 30s",
            Elapsed: "2m 15s",
            ErrorMessage: null,
            IsLoading: true,
            StatusMessage: "Import in progress...");

        Assert.Equal("Import", state.Mode);
        Assert.Equal("Running", state.OperationState);
        Assert.True(state.IsLoading);
        Assert.Equal(150, state.RecordCount);
        Assert.Equal(145, state.SuccessCount);
        Assert.Equal(5, state.FailureCount);
        Assert.Equal(42.5, state.RecordsPerSecond);
        Assert.Equal("/path/to/data.zip", state.DataPath);
    }

    [Fact]
    public void CapturesCompletedState()
    {
        var state = new MigrationScreenState(
            Mode: "Export",
            OperationState: "Completed",
            SchemaPath: "/schema.xml",
            OutputPath: "/output.zip",
            DataPath: null,
            CurrentPhase: "Complete",
            CurrentEntity: null,
            RecordCount: 500,
            SuccessCount: 500,
            FailureCount: 0,
            WarningCount: 0,
            SkipCount: 0,
            RecordsPerSecond: 100.0,
            EstimatedRemaining: null,
            Elapsed: "5s",
            ErrorMessage: null,
            IsLoading: false,
            StatusMessage: "Export completed successfully in 5s.");

        Assert.Equal("Completed", state.OperationState);
        Assert.False(state.IsLoading);
        Assert.Equal(500, state.SuccessCount);
        Assert.Null(state.ErrorMessage);
    }

    [Fact]
    public void CapturesFailedState()
    {
        var state = new MigrationScreenState(
            Mode: "Import",
            OperationState: "Failed",
            SchemaPath: null,
            OutputPath: null,
            DataPath: "/data.zip",
            CurrentPhase: "Error",
            CurrentEntity: null,
            RecordCount: 50,
            SuccessCount: 45,
            FailureCount: 5,
            WarningCount: 0,
            SkipCount: 0,
            RecordsPerSecond: null,
            EstimatedRemaining: null,
            Elapsed: "10s",
            ErrorMessage: "Connection lost",
            IsLoading: false,
            StatusMessage: "Import failed: Connection lost");

        Assert.Equal("Failed", state.OperationState);
        Assert.Equal("Connection lost", state.ErrorMessage);
        Assert.False(state.IsLoading);
    }
}
