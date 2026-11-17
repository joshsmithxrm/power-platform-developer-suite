using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Cli.Commands.Metadata;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.TestHelpers;
using PPDS.Dataverse.Metadata.Authoring;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Metadata;

/// <summary>
/// #1530 through the real System.CommandLine parser: the legacy --options CSV never silently drops entries,
/// conflicting local-option inputs are rejected, and an omitted add-option value previews as unallocated.
/// </summary>
[Trait("Category", "Unit")]
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class MetadataOptionInputGrammarCommandTests
{
    private const string CreateArgs = "--solution SyntheticSolution --entity new_table --name new_choice --display-name Choice --output-format json";

    [Theory]
    [InlineData("attribute")]
    [InlineData("column")]
    [Trait("AC", "AC-137")]
    public async Task LegacyOptions_BareLabelsBecomeLabelOnlyOptions(string group)
    {
        var run = await RunCreate(group, ["--options", "Draft,Final"]);
        Assert.Equal(0, run.Exit);
        var options = Assert.IsType<OptionDefinition[]>(run.Request!.Options);
        Assert.Equal(new[] { "Draft", "Final" }, options.Select(o => o.Label));
        Assert.All(options, o => Assert.False(o.HasExplicitValue)); // allocated like --option "Draft"
    }

    [Theory]
    [InlineData("attribute")]
    [InlineData("column")]
    [Trait("AC", "AC-137")]
    public async Task LegacyOptions_MixedEntriesKeepExplicitZeroAndOrder(string group)
    {
        var run = await RunCreate(group, ["--options", "Zero=0,Auto,Fixed=718980005"]);
        Assert.Equal(0, run.Exit);
        var options = run.Request!.Options!;
        Assert.Equal(new[] { "Zero", "Auto", "Fixed" }, options.Select(o => o.Label));
        Assert.True(options[0].HasExplicitValue); Assert.Equal(0, options[0].Value);
        Assert.False(options[1].HasExplicitValue);
        Assert.Equal(718980005, options[2].Value);
    }

    [Theory]
    [InlineData("attribute", "Good=1,Bad=abc")]
    [InlineData("column", "Good=1,Bad=abc")]
    [InlineData("attribute", "=5")]
    [InlineData("column", "Broken=")]
    [Trait("AC", "AC-137")]
    public async Task LegacyOptions_MalformedEntryIsRejectedNotDropped(string group, string raw)
    {
        var run = await RunCreate(group, ["--options", raw]);
        Assert.Equal(PPDS.Cli.Infrastructure.Errors.ExitCodes.ValidationError, run.Exit);
        Assert.Null(run.Request); // the service is never called
        Assert.Equal("INVALID_CONSTRAINT", run.ErrorCode);
    }

    [Theory]
    [InlineData("--option", "A", "--options", "B")]
    [InlineData("--option", "A", "--options-file", "FILE")]
    [InlineData("--options", "B", "--options-file", "FILE")]
    [Trait("AC", "AC-137")]
    public async Task ConflictingLocalOptionInputs_AreRejected(string first, string firstValue, string second, string secondValue)
    {
        var file = Path.Combine(Path.GetTempPath(), "ppds-options-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, "[{\"label\":\"FromFile\",\"value\":1}]");
        try
        {
            string Resolve(string v) => v == "FILE" ? file : v;
            var run = await RunCreate("attribute", [first, Resolve(firstValue), second, Resolve(secondValue)]);
            Assert.Equal(PPDS.Cli.Infrastructure.Errors.ExitCodes.ValidationError, run.Exit);
            Assert.Null(run.Request);
            Assert.Equal("INVALID_CONSTRAINT", run.ErrorCode);
            Assert.Contains(first, run.ErrorMessage);
            Assert.Contains(second, run.ErrorMessage);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(null, "unallocated; Dataverse assigns the value on write")]
    [InlineData("0", "requested-only; SDK assignment occurs on write")]
    [InlineData("718980003", "requested-only; SDK assignment occurs on write")]
    [Trait("AC", "AC-138")]
    public async Task AddOptionDryRun_OmittedValueIsReportedAsUnallocated(string? value, string allocation)
    {
        var service = new Mock<IMetadataAuthoringService>(MockBehavior.Strict);
        AddOptionValueRequest? captured = null;
        service.Setup(s => s.AddOptionValueAsync(It.IsAny<AddOptionValueRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AddOptionValueRequest r, CancellationToken _) => { captured = r; return r.Value ?? 0; });
        var args = new List<string> { "optionset", "add-option", "--solution", "SyntheticSolution", "--name", "new_choice", "--label", "New", "--dry-run", "--output-format", "json" };
        if (value != null) args.AddRange(["--value", value]);
        var (exit, stdout) = await Invoke(service.Object, args.ToArray());
        Assert.Equal(0, exit);
        Assert.Equal(value is null ? null : int.Parse(value), captured!.Value); // explicit values (including 0) unchanged
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var data = root.GetProperty("data");
        var preview = root.TryGetProperty("metadata", out var meta) ? meta.GetProperty("preview") : data.GetProperty("preview");
        Assert.Equal(allocation, preview.GetProperty("optionValueAllocation").GetString());
        if (value is null) Assert.False(data.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number);
        else Assert.Equal(int.Parse(value), data.GetProperty("value").GetInt32());
    }

    [Theory]
    [InlineData("attribute", "--choice")]
    [InlineData("column", "--option-set-name")]
    [Trait("AC", "AC-137")]
    public async Task GlobalChoiceWithLegacyOptions_IsRejected(string group, string flag)
    {
        var run = await RunCreate(group, [flag, "new_global", "--options", "A,B"]);
        Assert.Equal(PPDS.Cli.Infrastructure.Errors.ExitCodes.ValidationError, run.Exit);
        Assert.Null(run.Request);
        Assert.Equal("INVALID_CONSTRAINT", run.ErrorCode);
        Assert.Contains("--options", run.ErrorMessage);
    }

    [Theory]
    [InlineData("attribute")]
    [InlineData("column")]
    [Trait("AC", "AC-137")]
    public async Task StrayLegacyOptions_OnNonChoiceColumn_BehaveAsBefore(string group)
    {
        // Previously a malformed or bare-label --options on a String column was ignored; it still is.
        var run = await RunCreate(group, ["--options", "A,Bad=abc,Kept=1"], type: "String");
        Assert.Equal(0, run.Exit);
        var options = Assert.IsType<OptionDefinition[]>(run.Request!.Options);
        Assert.Equal(new[] { "Kept" }, options.Select(o => o.Label));
    }

    [Theory]
    [InlineData("column", "--option-set-name")]
    [InlineData("attribute", "--choice")]
    [Trait("AC", "AC-137")]
    public async Task GlobalChoiceWithLegacyOptions_OnNonChoiceColumn_IsNotNewlyRejected(string group, string flag)
    {
        // column create: as before (both inputs ignored for a String column). attribute create already rejected
        // --choice with local options for every type before #1530, and still does.
        var run = await RunCreate(group, [flag, "new_global", "--options", "A=1"], type: "String");
        if (group == "column") Assert.Equal(0, run.Exit);
        else Assert.Equal("INVALID_CONSTRAINT", run.ErrorCode);
    }
    [Fact]
    [Trait("AC", "AC-137")]
    public async Task StrayConflictingInputs_OnNonChoiceColumn_BehaveAsBefore()
    {
        var run = await RunCreate("attribute", ["--option", "A", "--options", "B=2"], type: "String");
        Assert.Equal(0, run.Exit);
        Assert.Equal(new[] { "A" }, run.Request!.Options!.Select(o => o.Label));
    }

    private sealed record CreateRun(int Exit, CreateColumnRequest? Request, string? ErrorCode, string ErrorMessage);

    private static async Task<CreateRun> RunCreate(string group, string[] extra, string type = "Choice")
    {
        var service = new Mock<IMetadataAuthoringService>(MockBehavior.Strict);
        CreateColumnRequest? captured = null;
        service.Setup(s => s.CreateColumnAsync(It.IsAny<CreateColumnRequest>(), It.IsAny<IMetadataAuthoringProgressReporter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateColumnRequest r, IMetadataAuthoringProgressReporter? _, CancellationToken _) =>
            {
                captured = r;
                return new CreateColumnResult { LogicalName = "new_choice", WasDryRun = false, ValidationMessages = [] };
            });
        var args = new[] { group, "create", "--type", type }.Concat(CreateArgs.Split(' ')).Concat(extra).ToArray();
        var (exit, stdout) = await Invoke(service.Object, args);
        string? code = null, message = "";
        if (exit != 0)
        {
            using var doc = JsonDocument.Parse(stdout);
            var error = doc.RootElement.GetProperty("error");
            code = error.GetProperty("code").GetString();
            message = error.GetProperty("message").GetString() ?? "";
        }
        return new CreateRun(exit, captured, code, message);
    }

    private static async Task<(int Exit, string Stdout)> Invoke(IMetadataAuthoringService service, string[] args)
    {
        Task<ServiceProvider> Provider(CancellationToken _) =>
            Task.FromResult(new ServiceCollection().AddSingleton(service).BuildServiceProvider());
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var oldOut = Console.Out; var oldError = Console.Error;
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            var parsed = MetadataCommandGroup.Create(Provider).Parse(args);
            Assert.Empty(parsed.Errors);
            var exit = await parsed.InvokeAsync();
            return (exit, stdout.ToString());
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }
}
