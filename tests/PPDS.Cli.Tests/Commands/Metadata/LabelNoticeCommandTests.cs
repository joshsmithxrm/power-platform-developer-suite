using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Metadata;
using PPDS.Cli.Infrastructure;
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
        "optionset update --solution Sol --name new_choice --display-name Renamed"
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
    [Trait("AC", "AC-145")]
    public void Help_SaysTheLabelIsEnglish(string group, string verb, string option)
    {
        var command = MetadataCommandGroup.Create(null).Subcommands.Single(c => c.Name == group).Subcommands.Single(c => c.Name == verb);
        Assert.EndsWith("(English, LCID 1033)", command.Options.Single(o => o.Name == option).Description);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> Invoke(string command, MetadataLabelWriteResult result)
    {
        var service = new Mock<IMetadataAuthoringService>(MockBehavior.Strict);
        service.Setup(s => s.UpdateOptionValueAsync(It.IsAny<UpdateOptionValueRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        service.Setup(s => s.UpdateColumnOptionAsync(It.IsAny<UpdateColumnOptionRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        service.Setup(s => s.UpdateStatusReasonAsync(It.IsAny<UpdateStatusReasonRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        service.Setup(s => s.UpdateGlobalChoiceAsync(It.IsAny<UpdateGlobalChoiceRequest>(), It.IsAny<IMetadataAuthoringProgressReporter?>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);

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
