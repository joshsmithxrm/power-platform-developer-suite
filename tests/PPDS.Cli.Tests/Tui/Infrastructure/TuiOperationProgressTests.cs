using PPDS.Cli.Infrastructure;
using PPDS.Cli.Tui.Infrastructure;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Infrastructure;

/// <summary>
/// Unit tests for <see cref="TuiOperationProgress"/>.
/// </summary>
/// <remarks>
/// These tests verify the progress reporter behavior without Terminal.Gui context.
/// The component gracefully handles null controls and missing Application.MainLoop.
/// </remarks>
public class TuiOperationProgressTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_WithNullControls_DoesNotThrow()
    {
        // Should not throw when both controls are null
        var progress = new TuiOperationProgress(progressBar: null, statusLabel: null);

        Assert.NotNull(progress);
    }

    #endregion

    #region ReportStatus Tests

    [Fact]
    public void ReportStatus_WithNullControls_DoesNotThrow()
    {
        var progress = new TuiOperationProgress(progressBar: null, statusLabel: null);

        // Should not throw
        progress.ReportStatus("Test message");
    }

    [Fact]
    public void ReportStatus_ImplementsInterface()
    {
        IOperationProgress progress = new TuiOperationProgress();

        // Should satisfy the interface
        progress.ReportStatus("Test message");
    }

    #endregion

    #region ReportProgress (int, int) Tests

    [Fact]
    public void ReportProgress_IntInt_WithNullControls_DoesNotThrow()
    {
        var progress = new TuiOperationProgress(progressBar: null, statusLabel: null);

        // Should not throw
        progress.ReportProgress(50, 100, "Halfway");
    }

    [Fact]
    public void ReportProgress_IntInt_WithZeroTotal_DoesNotThrow()
    {
        var progress = new TuiOperationProgress(progressBar: null, statusLabel: null);

        // Should not throw when total is 0
        progress.ReportProgress(0, 0, "No items");
    }

    [Fact]
    public void ReportProgress_IntInt_WithNegativeTotal_DoesNotThrow()
    {
        var progress = new TuiOperationProgress(progressBar: null, statusLabel: null);

        // Should not throw when total is negative
        progress.ReportProgress(0, -1, "Invalid");
    }

    [Fact]
    public void ReportProgress_IntInt_ImplementsInterface()
    {
        IOperationProgress progress = new TuiOperationProgress();

        // Should satisfy the interface
        progress.ReportProgress(25, 100, "25% complete");
    }

    #endregion

    #region ReportProgress (double) Tests

    [Fact]
    public void ReportProgress_Double_WithNullControls_DoesNotThrow()
    {
        var progress = new TuiOperationProgress(progressBar: null, statusLabel: null);

        // Should not throw
        progress.ReportProgress(0.5, "Halfway");
    }

    [Fact]
    public void ReportProgress_Double_WithValueGreaterThanOne_DoesNotThrow()
    {
        var progress = new TuiOperationProgress(progressBar: null, statusLabel: null);

        // Should not throw and should clamp to 1.0
        progress.ReportProgress(1.5, "Over 100%");
    }

    [Fact]
    public void ReportProgress_Double_WithNegativeValue_DoesNotThrow()
    {
        var progress = new TuiOperationProgress(progressBar: null, statusLabel: null);

        // Should not throw and should clamp to 0.0
        progress.ReportProgress(-0.5, "Negative");
    }

    [Fact]
    public void ReportProgress_Double_ImplementsInterface()
    {
        IOperationProgress progress = new TuiOperationProgress();

        // Should satisfy the interface
        progress.ReportProgress(0.75, "75% complete");
    }

    #endregion

    #region ReportComplete Tests

    [Fact]
    public void ReportComplete_WithNullControls_DoesNotThrow()
    {
        var progress = new TuiOperationProgress(progressBar: null, statusLabel: null);

        // Should not throw
        progress.ReportComplete("Done!");
    }

    [Fact]
    public void ReportComplete_ImplementsInterface()
    {
        IOperationProgress progress = new TuiOperationProgress();

        // Should satisfy the interface
        progress.ReportComplete("Operation completed");
    }

    #endregion

    #region ReportError Tests

    [Fact]
    public void ReportError_WithNullControls_DoesNotThrow()
    {
        var progress = new TuiOperationProgress(progressBar: null, statusLabel: null);

        // Should not throw
        progress.ReportError("Something went wrong");
    }

    [Fact]
    public void ReportError_ImplementsInterface()
    {
        IOperationProgress progress = new TuiOperationProgress();

        // Should satisfy the interface
        progress.ReportError("Error occurred");
    }

    #endregion

    #region Integration Tests

    [Fact]
    public void FullWorkflow_WithNullControls_DoesNotThrow()
    {
        var progress = new TuiOperationProgress(progressBar: null, statusLabel: null);

        // Simulate a complete operation workflow
        progress.ReportStatus("Starting...");
        progress.ReportProgress(0, 100, "Initializing");
        progress.ReportProgress(25, 100, "Processing");
        progress.ReportProgress(0.5, "Halfway");
        progress.ReportProgress(75, 100);
        progress.ReportProgress(1.0, "Finishing");
        progress.ReportComplete("All done!");
    }

    [Fact]
    public void FullWorkflowWithError_WithNullControls_DoesNotThrow()
    {
        var progress = new TuiOperationProgress(progressBar: null, statusLabel: null);

        // Simulate an operation that fails
        progress.ReportStatus("Starting...");
        progress.ReportProgress(25, 100, "Processing");
        progress.ReportError("Connection failed");
    }

    #endregion
}
