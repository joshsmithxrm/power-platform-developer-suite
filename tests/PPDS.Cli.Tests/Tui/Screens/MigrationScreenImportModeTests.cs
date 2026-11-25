using PPDS.Cli.Tui.Screens;
using PPDS.Migration.Import;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Screens;

/// <summary>
/// The Migration screen offers only the modes the importer implements for a whole import, and
/// shows what an import created, updated, skipped and failed (#1914).
/// </summary>
[Trait("Category", "TuiUnit")]
public sealed class MigrationScreenImportModeTests
{
    [Fact]
    public void ImportModes_DoNotOfferSkip()
    {
        Assert.Equal(new[] { ImportMode.Create, ImportMode.Update, ImportMode.Upsert }, MigrationScreen.ImportModes);
    }

    [Theory]
    [InlineData(0, ImportMode.Create)]
    [InlineData(1, ImportMode.Update)]
    [InlineData(2, ImportMode.Upsert)]
    public void ImportModeAt_ReturnsTheSelectedMode(int selected, ImportMode expected)
    {
        Assert.Equal(expected, MigrationScreen.ImportModeAt(selected));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public void ImportModeAt_OutOfRange_IsRefused_NotTheDefault(int selected)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MigrationScreen.ImportModeAt(selected));
    }

    [Fact]
    public void ImportResults_ShowCreatedUpdatedSkippedAndFailed()
    {
        var result = new ImportResult
        {
            Success = true,
            SourceRecordCount = 5,
            RecordsImported = 2,
            CreatedCount = 2,
            UpdatedCount = 0,
            SkippedCount = 3,
            EntityResults = new[]
            {
                new EntityImportResult { EntityLogicalName = "account", RecordCount = 3, SkippedCount = 3 },
                new EntityImportResult { EntityLogicalName = "contact", RecordCount = 2, SuccessCount = 2, CreatedCount = 2, UpdatedCount = 0 }
            }
        };

        var text = MigrationScreen.FormatImportResults(result);

        Assert.Contains("Created: 2  |  Updated: 0  |  Skipped: 3  |  Failed: 0", text);
        Assert.Contains("account (tier 0): 0/3, 3 skipped [OK]", text);
    }

    [Fact]
    public void ImportResults_UnknownUpsertCounts_AreShownAsUnknown()
    {
        var text = MigrationScreen.FormatImportResults(new ImportResult { CreatedCount = null, UpdatedCount = null });

        Assert.Contains("Created: unknown  |  Updated: unknown", text);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    public void FinalStatus_NamesThePluginStepsLeftDisabled(string stateName)
    {
        var state = Enum.Parse<MigrationOperationState>(stateName);
        // #1921 review MF3: the final status is the last text shown; it must not drop the steps.
        var id = Guid.NewGuid();

        var text = MigrationScreen.FinalStatusText(MigrationMode.Import, state, "00:01", "Import completed with errors.",
            new[] { new PluginStepInfo(id, "audit step") });

        Assert.Contains($"1 plug-in step(s) could not be re-enabled and remain disabled; re-enable them in the target: 'audit step' ({id})", text);
    }

    [Fact]
    public void FinalStatus_WithoutStepsLeftDisabled_IsUnchanged()
    {
        Assert.Equal("Import cancelled after 00:01.",
            MigrationScreen.FinalStatusText(MigrationMode.Import, MigrationOperationState.Cancelled, "00:01", null, null));
    }

    [Fact]
    public void ImportResults_ListThePluginStepsLeftDisabled()
    {
        var id = Guid.NewGuid();
        var text = MigrationScreen.FormatImportResults(new ImportResult { PluginStepsLeftDisabled = new[] { new PluginStepInfo(id, "audit step") } });

        Assert.Contains("Plug-in steps left disabled (1)", text);
        Assert.Contains($"audit step ({id})", text);
    }

    // #1934 r2 MF6: every way an import ends keeps the steps left disabled for the final status.
    private static readonly PluginStepInfo Step = new(Guid.NewGuid(), "audit step");

    [Fact]
    public void Outcome_SuccessfulResult_IsCompleted()
    {
        var (state, error, steps) = MigrationScreen.ImportOutcomeOf(new ImportResult { Success = true }, null, cancelRequested: false);

        Assert.Equal(MigrationOperationState.Completed, state);
        Assert.Null(error);
        Assert.Empty(steps!);
    }

    [Fact]
    public void Outcome_FailedResult_KeepsTheSteps()
    {
        var (state, error, steps) = MigrationScreen.ImportOutcomeOf(
            new ImportResult { Success = false, PluginStepsLeftDisabled = new[] { Step } }, null, cancelRequested: false);

        Assert.Equal(MigrationOperationState.Failed, state);
        Assert.Equal("Import completed with errors.", error);
        Assert.Equal(new[] { Step }, steps);
    }

    [Fact]
    public void Outcome_ResultAfterACancel_IsCancelled_AndKeepsTheSteps()
    {
        var (state, _, steps) = MigrationScreen.ImportOutcomeOf(
            new ImportResult { Success = false, PluginStepsLeftDisabled = new[] { Step } }, null, cancelRequested: true);

        Assert.Equal(MigrationOperationState.Cancelled, state);
        Assert.Equal(new[] { Step }, steps);
    }

    [Fact]
    public void Outcome_CancelledImport_KeepsTheSteps()
    {
        var (state, _, steps) = MigrationScreen.ImportOutcomeOf(
            null, new ImportCancelledException(new[] { Step }, new OperationCanceledException()), cancelRequested: false);

        Assert.Equal(MigrationOperationState.Cancelled, state);
        Assert.Equal(new[] { Step }, steps);
    }

    [Fact]
    public void Outcome_PlainCancel_IsCancelled()
    {
        var (state, _, steps) = MigrationScreen.ImportOutcomeOf(null, new OperationCanceledException(), cancelRequested: false);

        Assert.Equal(MigrationOperationState.Cancelled, state);
        Assert.Null(steps);
    }

    [Fact]
    public void Outcome_Refusal_IsFailed_WithItsMessage_AndKeepsTheSteps()
    {
        var refused = new ImportRefusedException(ImportRefusalReasons.PluginStepNotDisabled, "Plug-in step 'x' could not be disabled.")
        {
            PluginStepsLeftDisabled = new[] { Step }
        };

        var (state, error, steps) = MigrationScreen.ImportOutcomeOf(null, refused, cancelRequested: false);

        Assert.Equal(MigrationOperationState.Failed, state);
        Assert.Equal(refused.Message, error);
        Assert.Equal(new[] { Step }, steps);
    }

    [Fact]
    public void Outcome_OtherException_IsFailed_WithItsMessage()
    {
        var (state, error, _) = MigrationScreen.ImportOutcomeOf(null, new InvalidOperationException("boom"), cancelRequested: false);

        Assert.Equal(MigrationOperationState.Failed, state);
        Assert.Equal("boom", error);
    }
}
