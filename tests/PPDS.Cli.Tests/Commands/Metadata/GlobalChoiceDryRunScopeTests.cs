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
/// #1535 follow-up: input-only dry-runs that take a global choice name do not look the name up, so a mis-cased or
/// unknown name passes the dry-run and is resolved or rejected only on the real run. The preview says so.
/// </summary>
[Trait("Category", "Unit")]
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class GlobalChoiceDryRunScopeTests
{
    private const string Typed = "mayo_AIRequestType";

    public static TheoryData<string> GlobalChoiceDryRuns => new()
    {
        $"optionset update --solution Sol --name {Typed} --display-name Renamed --dry-run",
        $"optionset add-option --solution Sol --name {Typed} --label New --dry-run",
        $"optionset add-option --solution Sol --name {Typed} --label New --value 5 --dry-run",
        $"attribute create --solution Sol --entity new_table --name new_choice --display-name Choice --type Choice --choice {Typed} --dry-run",
        $"column create --solution Sol --entity new_table --name new_choice --display-name Choice --type Choices --option-set-name {Typed} --dry-run"
    };

    [Theory]
    [MemberData(nameof(GlobalChoiceDryRuns))]
    [Trait("AC", "AC-143")]
    public async Task Json_PreviewSaysTheGlobalChoiceNameWasNotLookedUp(string command)
    {
        var (exit, stdout, _) = await Invoke(command + " --output-format json");
        Assert.Equal(0, exit);
        var preview = Preview(stdout);
        Assert.False(preview.GetProperty("globalChoiceNameChecked").GetBoolean());
        Assert.False(preview.GetProperty("targetExistenceChecked").GetBoolean());
    }

    [Theory]
    [MemberData(nameof(GlobalChoiceDryRuns))]
    [Trait("AC", "AC-143")]
    public async Task Text_DiagnosticSaysTheGlobalChoiceNameWasNotLookedUp(string command)
    {
        var (exit, _, stderr) = await Invoke(command);
        Assert.Equal(0, exit);
        Assert.Contains($"Global choice name '{Typed}' was not looked up", stderr);
    }

    [Theory]
    [InlineData("attribute create --solution Sol --entity new_table --name new_choice --display-name Choice --type Choice --options A,B --dry-run")]
    [InlineData("attribute create --solution Sol --entity new_table --name new_text --display-name Text --type String --dry-run")]
    [Trait("AC", "AC-143")]
    public async Task OtherDryRuns_DoNotReportAGlobalChoiceName(string command)
    {
        var (exit, stdout, _) = await Invoke(command + " --output-format json");
        Assert.Equal(0, exit);
        Assert.False(Preview(stdout).TryGetProperty("globalChoiceNameChecked", out _));
        var (_, _, stderr) = await Invoke(command);
        Assert.DoesNotContain("Global choice name", stderr);
    }

    private static JsonElement Preview(string stdout)
    {
        var root = JsonDocument.Parse(stdout).RootElement;
        return root.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("preview", out var p)
            ? p.Clone()
            : root.GetProperty("data").GetProperty("preview").Clone();
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> Invoke(string command)
    {
        var service = new Mock<IMetadataAuthoringService>(MockBehavior.Strict);
        service.Setup(s => s.UpdateGlobalChoiceAsync(It.IsAny<UpdateGlobalChoiceRequest>(), It.IsAny<IMetadataAuthoringProgressReporter?>(), It.IsAny<CancellationToken>()))
            .Callback<UpdateGlobalChoiceRequest, IMetadataAuthoringProgressReporter?, CancellationToken>((r, _, _) => Assert.True(r.DryRun))
            .Returns(Task.CompletedTask);
        service.Setup(s => s.AddOptionValueAsync(It.IsAny<AddOptionValueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AddOptionValueRequest r, CancellationToken _) => { Assert.True(r.DryRun); return r.Value ?? 0; });
        service.Setup(s => s.CreateColumnAsync(It.IsAny<CreateColumnRequest>(), It.IsAny<IMetadataAuthoringProgressReporter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateColumnRequest r, IMetadataAuthoringProgressReporter? _, CancellationToken _) =>
            {
                Assert.True(r.DryRun);
                return new CreateColumnResult { LogicalName = r.SchemaName.ToLowerInvariant(), WasDryRun = true, ValidationMessages = [] };
            });

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
