using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Metadata;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.TestHelpers;
using PPDS.Dataverse.Metadata.Authoring;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Metadata;

/// <summary>
/// A label written for English only while the caller's own-language label is not English is reported: a stderr
/// warning in text mode and an additive <c>validationMessages</c> array in JSON. The exit code stays 0.
/// </summary>
[Trait("Category", "Unit")]
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class LabelNoticeCommandTests
{
    private static readonly MetadataLabelWriteResult Notice = new()
    {
        ValidationMessages =
        [
            new ValidationMessage
            {
                Field = "NewLabel", Rule = MetadataLabelWriteResult.EnglishLabelOnlyRule,
                Message = "Only the English (LCID 1033) label is written. The label in your language (LCID 1036) is not changed and still reads 'Ouvert'."
            }
        ]
    };

    public static TheoryData<string> Commands => new()
    {
        "optionset update-option --solution Sol --name new_choice --value 1 --new-label Ferme",
        "optionset update-option --solution Sol --name new_choice --value 1 --new-label Ferme --dry-run",
        "attribute update-option --solution Sol --entity new_table --column new_pick --value 1 --new-label Ferme",
        "entity update-statusreason --solution Sol --entity new_table --value 1 --new-label Ferme",
        "optionset update --solution Sol --name new_choice --display-name Renamed",
        // AC-147: table and column label updates report the same notice.
        "entity update --solution Sol --entity new_table --display-name Renamed",
        "table update --solution Sol --entity new_table --display-name Renamed --publish",
        "attribute update --solution Sol --entity new_table --column new_text --display-name Renamed",
        "column update --solution Sol --entity new_table --column new_text --description Renamed"
    };

    [Theory]
    [MemberData(nameof(Commands))]
    [Trait("AC", "AC-145")]
    public async Task Text_WarnsThatOnlyTheEnglishLabelIsWritten(string command)
    {
        var (exit, stdout, stderr) = await Invoke(command, Notice);
        Assert.Equal(0, exit);
        Assert.Empty(stdout);
        Assert.Contains("Warning: Only the English (LCID 1033) label is written.", stderr);
    }

    [Theory]
    [MemberData(nameof(Commands))]
    [Trait("AC", "AC-145")]
    public async Task Json_ReportsTheNoticeInValidationMessages(string command)
    {
        var (exit, stdout, _) = await Invoke(command + " --output-format json", Notice);
        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(stdout);
        var message = Assert.Single(doc.RootElement.GetProperty("data").GetProperty("validationMessages").EnumerateArray());
        Assert.Equal(MetadataLabelWriteResult.EnglishLabelOnlyRule, message.GetProperty("rule").GetString());
        Assert.Equal("NewLabel", message.GetProperty("field").GetString());
    }

    [Theory]
    [MemberData(nameof(Commands))]
    [Trait("AC", "AC-145")]
    public async Task NoNotice_AddsNothing(string command)
    {
        var (_, stdout, _) = await Invoke(command + " --output-format json", MetadataLabelWriteResult.None);
        using var doc = JsonDocument.Parse(stdout);
        Assert.False(doc.RootElement.GetProperty("data").TryGetProperty("validationMessages", out _));
        var (_, _, stderr) = await Invoke(command, MetadataLabelWriteResult.None);
        Assert.DoesNotContain("Warning:", stderr);
    }

    [Theory]
    [InlineData("optionset", "update-option", "--new-label")]
    [InlineData("attribute", "update-option", "--new-label")]
    [InlineData("entity", "update-statusreason", "--new-label")]
    [InlineData("optionset", "update", "--display-name")]
    [InlineData("optionset", "update", "--description")]
    [InlineData("choice", "update", "--display-name")]
    [InlineData("choice", "update", "--description")]
    [InlineData("choice", "update-option", "--label")]
    [InlineData("entity", "update", "--display-name")]
    [InlineData("entity", "update", "--plural-name")]
    [InlineData("entity", "update", "--description")]
    [InlineData("table", "update", "--display-name")]
    [InlineData("table", "update", "--plural-name")]
    [InlineData("table", "update", "--description")]
    [InlineData("attribute", "update", "--display-name")]
    [InlineData("attribute", "update", "--description")]
    [InlineData("column", "update", "--display-name")]
    [InlineData("column", "update", "--description")]
    [Trait("AC", "AC-145")]
    public void Help_SaysTheLabelIsEnglish(string group, string verb, string option)
    {
        var command = MetadataCommandGroup.Create(null).Subcommands.Single(c => c.Name == group).Subcommands.Single(c => c.Name == verb);
        Assert.EndsWith("(English, LCID 1033)", command.Options.Single(o => o.Name == option).Description);
    }

    [Fact]
    [Trait("AC", "AC-145")]
    public void Tui_ChoiceUpdateStatus_ShowsTheNotice()
    {
        Assert.Equal("Choice updated: new_choice", PPDS.Cli.Tui.Screens.MetadataExplorerScreen.ChoiceUpdatedStatus("new_choice", MetadataLabelWriteResult.None));
        Assert.Equal("Choice updated: new_choice", PPDS.Cli.Tui.Screens.MetadataExplorerScreen.ChoiceUpdatedStatus("new_choice", null));
        var status = PPDS.Cli.Tui.Screens.MetadataExplorerScreen.ChoiceUpdatedStatus("new_choice", Notice);
        Assert.StartsWith("Choice updated: new_choice Warning: Only the English (LCID 1033) label is written.", status);
    }

    [Theory]
    [InlineData("optionset update-option --solution Sol --name new_choice --value 1")]
    [InlineData("attribute update-option --solution Sol --entity new_table --column new_pick --value 1")]
    [InlineData("entity update-statusreason --solution Sol --entity new_table --value 1")]
    [Trait("AC", "AC-146")]
    public async Task UpdateOption_WithNothingToChange_IsAValidationErrorBeforeConnecting(string command)
    {
        var (exit, stdout, stderr) = await Invoke(command + " --output-format json", MetadataLabelWriteResult.None);
        Assert.Equal(ExitCodes.ValidationError, exit);
        Assert.Contains("Nothing to update: provide --new-label or --color.", stdout + stderr);

        // The check runs before any connection: a provider factory that must not be called.
        var connected = 0;
        Task<ServiceProvider> NeverConnect(CancellationToken _) { connected++; throw new InvalidOperationException("connected"); }
        using var quiet = new StringWriter();
        var oldOut = Console.Out; var oldError = Console.Error;
        try
        {
            Console.SetOut(quiet); Console.SetError(quiet);
            var parsed = MetadataCommandGroup.Create(NeverConnect).Parse(command.Split(' '));
            Assert.Equal(ExitCodes.ValidationError, await parsed.InvokeAsync());
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
        Assert.Equal(0, connected);
    }

    [Fact]
    [Trait("AC", "AC-147")]
    public void Tui_UpdateStatus_KeepsAReloadError()
    {
        Assert.Equal("Column updated: new_text", PPDS.Cli.Tui.Screens.MetadataExplorerScreen.WithReloadOutcome("Column updated: new_text", "new_table", reloaded: true));
        Assert.Equal("Column updated: new_text Error loading new_table",
            PPDS.Cli.Tui.Screens.MetadataExplorerScreen.WithReloadOutcome("Column updated: new_text", "new_table", reloaded: false));
    }

    [Fact]
    [Trait("AC", "AC-147")]
    public void Tui_ColumnUpdateStatus_ShowsTheNotice()
    {
        Assert.Equal("Column updated: new_text", PPDS.Cli.Tui.Screens.MetadataExplorerScreen.UpdatedStatus("Column", "new_text", MetadataLabelWriteResult.None));
        Assert.StartsWith("Column updated: new_text Warning: Only the English (LCID 1033) label is written.",
            PPDS.Cli.Tui.Screens.MetadataExplorerScreen.UpdatedStatus("Column", "new_text", Notice));
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> Invoke(string command, MetadataLabelWriteResult result)
    {
        var service = new Mock<IMetadataAuthoringService>(MockBehavior.Strict);
        service.Setup(s => s.UpdateOptionValueAsync(It.IsAny<UpdateOptionValueRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        service.Setup(s => s.UpdateColumnOptionAsync(It.IsAny<UpdateColumnOptionRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        service.Setup(s => s.UpdateStatusReasonAsync(It.IsAny<UpdateStatusReasonRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        service.Setup(s => s.UpdateGlobalChoiceAsync(It.IsAny<UpdateGlobalChoiceRequest>(), It.IsAny<IMetadataAuthoringProgressReporter?>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        service.Setup(s => s.UpdateTableAsync(It.IsAny<UpdateTableRequest>(), It.IsAny<IMetadataAuthoringProgressReporter?>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        service.Setup(s => s.UpdateColumnAsync(It.IsAny<UpdateColumnRequest>(), It.IsAny<IMetadataAuthoringProgressReporter?>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);

        Task<ServiceProvider> Provider(CancellationToken _) => Task.FromResult(new ServiceCollection()
            .AddSingleton(service.Object)
            .AddSingleton(new ResolvedConnectionInfo { Profile = new AuthProfile { Name = "synthetic-only" }, EnvironmentUrl = "https://fixture.example.test" })
            .BuildServiceProvider());

        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var oldOut = Console.Out; var oldError = Console.Error;
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            var parsed = MetadataCommandGroup.Create(Provider).Parse(command.Split(' '));
            Assert.Empty(parsed.Errors);
            var exit = await parsed.InvokeAsync();
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }
}
