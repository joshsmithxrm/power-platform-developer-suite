using Moq;
using PPDS.Cli.Commands.Data;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Migration.Import;
using PPDS.Migration.Progress;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Data;

/// <summary>
/// What the import and copy commands say and return for every way an import can end (#1934 r2 MF6).
/// </summary>
[Trait("Category", "Unit")]
public class ImportOutcomeTests
{
    private readonly StringWriter _error = new();
    private readonly Mock<IProgressReporter> _progress = new();
    private readonly List<string?> _progressErrors = new();
    private static readonly PluginStepInfo Step = new(Guid.NewGuid(), "audit step");

    public ImportOutcomeTests()
    {
        _progress.Setup(p => p.Error(It.IsAny<Exception>(), It.IsAny<string?>()))
            .Callback<Exception, string?>((_, context) => _progressErrors.Add(context));
    }

    private string Text => _error.ToString();

    [Fact]
    public void SuccessfulResult_ExitsZero_AndSaysNothing()
    {
        Assert.Equal(ExitCodes.Success, ImportOutcome.Report(new ImportResult { Success = true }, _error));
        Assert.Equal("", Text);
    }

    [Fact]
    public void ResultWithAStepLeftDisabled_ExitsTwo_AndListsTheStepWithItsId()
    {
        var result = new ImportResult { Success = false, PluginStepsLeftDisabled = new[] { Step } };

        Assert.Equal(ExitCodes.Failure, ImportOutcome.Report(result, _error));
        Assert.Contains($"  - audit step ({Step.Id})", Text);
    }

    [Fact]
    public void ResultWarningsAboutDataNotWritten_ArePrinted_OthersAreNot()
    {
        var result = new ImportResult
        {
            Success = false,
            Warnings = new[]
            {
                new ImportWarning { Code = ImportWarningCodes.LaterPhasesSkipped, Message = "later phases skipped for account" },
                new ImportWarning { Code = ImportWarningCodes.ReferencesNotSent, Message = "2 lookups not sent" },
                new ImportWarning { Code = ImportWarningCodes.ColumnSkipped, Message = "column skipped" }
            }
        };

        Assert.Equal(ExitCodes.Failure, ImportOutcome.Report(result, _error));
        Assert.Contains("Warning: later phases skipped for account", Text);
        Assert.Contains("Warning: 2 lookups not sent", Text);
        Assert.DoesNotContain("column skipped", Text);
    }

    [Fact]
    public void Refusal_PrintsTheMessage_TheStepsLeftDisabled_AndTheStepInAnUnknownState()
    {
        var unknown = new PluginStepInfo(Guid.NewGuid(), "no-privilege step");
        var refused = new ImportRefusedException(ImportRefusalReasons.PluginStepNotDisabled, "Plug-in step 'x' could not be disabled.")
        {
            PluginStepsLeftDisabled = new[] { Step },
            PluginStepInUnknownState = unknown
        };

        var code = ImportOutcome.Report(refused, "Import", _progress.Object, _error, debug: false);

        Assert.Equal(ExitCodes.Failure, code);
        Assert.Contains("Error: Plug-in step 'x' could not be disabled.", Text);
        Assert.Contains($"  - audit step ({Step.Id})", Text);
        Assert.Contains("its state is unknown", Text);
        Assert.Contains($"  - no-privilege step ({unknown.Id})", Text);
    }

    [Fact]
    public void RefusedMode_ExitsThree()
    {
        var refused = new ImportRefusedException(ImportRefusalReasons.SkipIsPerTable, "Skip is per table.");

        Assert.Equal(ExitCodes.InvalidArguments, ImportOutcome.Report(refused, "Import", _progress.Object, _error, debug: false));
    }

    [Fact]
    public void SchemaRefusal_AsPpdsException_ExitsThree()
    {
        var refusal = new PpdsException(ErrorCodes.Validation.SchemaInvalid, "Table 'account' carries skipupdate.");

        Assert.Equal(ExitCodes.InvalidArguments, ImportOutcome.Report(refusal, "Import", _progress.Object, _error, debug: false));
        Assert.Contains("Error: Table 'account' carries skipupdate.", Text);
    }

    [Theory]
    [InlineData("Import")]
    [InlineData("Copy")]
    public void CancelWithAStepLeftDisabled_ExitsTwo_ReportsTheCancel_AndListsTheStep(string operation)
    {
        var cancelled = new ImportCancelledException(new[] { Step }, new OperationCanceledException());

        Assert.Equal(ExitCodes.Failure, ImportOutcome.Report(cancelled, operation, _progress.Object, _error, debug: false));
        Assert.Equal(new[] { $"{operation} cancelled by user." }, _progressErrors);
        Assert.Contains($"  - audit step ({Step.Id})", Text);
    }

    [Fact]
    public void PlainCancel_ExitsTwo_WithoutAStepList()
    {
        Assert.Equal(ExitCodes.Failure, ImportOutcome.Report(new OperationCanceledException(), "Import", _progress.Object, _error, debug: false));
        Assert.DoesNotContain("plug-in step", Text);
    }

    [Fact]
    public void SchemaMismatch_PrintsTheDetail_AndExitsTwo()
    {
        var mismatch = new SchemaMismatchException("2 columns missing", new Dictionary<string, List<string>>());

        Assert.Equal(ExitCodes.Failure, ImportOutcome.Report(mismatch, "Import", _progress.Object, _error, debug: false));
        Assert.Contains("2 columns missing", Text);
    }

    [Fact]
    public void OtherException_IsReportedAsAFailure_AndExitsTwo()
    {
        Assert.Equal(ExitCodes.Failure, ImportOutcome.Report(new InvalidOperationException("boom"), "Copy", _progress.Object, _error, debug: false));
        Assert.Equal(new[] { "Copy failed" }, _progressErrors);
    }

    [Fact]
    public async Task RunAsync_ReturnsTheResultsExitCode()
    {
        var code = await ImportOutcome.RunAsync(
            () => Task.FromResult(new ImportResult { Success = false, PluginStepsLeftDisabled = new[] { Step } }),
            "Copy", _progress.Object, _error, debug: false);

        Assert.Equal(ExitCodes.Failure, code);
        Assert.Contains($"  - audit step ({Step.Id})", Text);
    }

    [Fact]
    public async Task RunAsync_ReportsARefusalThrownByTheImport()
    {
        var code = await ImportOutcome.RunAsync(
            () => throw new ImportRefusedException(ImportRefusalReasons.PluginTableNotResolved, "Table 'x' was not found."),
            "Import", _progress.Object, _error, debug: false);

        Assert.Equal(ExitCodes.NotFoundError, code);
        Assert.Contains("Error: Table 'x' was not found.", Text);
    }
}
