using System.CommandLine;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Cli.Commands.Forms;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Services.Forms;
using PPDS.Cli.Tests.Services.Forms;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Forms;

[Collection(nameof(ConsoleCaptureCollection))]
[Trait("Category", "Unit")]
public sealed class FormPlacementNoticeCommandTests
{
    private static void CaseVariantName(XDocument doc) =>
        doc.Descendants("control").First(c => (string?)c.Attribute("datafieldname") == "name")
            .SetAttributeValue("datafieldname", "Name");

    [Fact]
    [Trait("AC", "AC-70")]
    public async Task AddField_Json_HasOtherPlacementsAndEmptyNonCanonicalBindings()
    {
        var (exit, stdout, stderr) = await Run(AddFieldCommand.Create(Provider(null)),
            ["--section", "General", "--field", "telephone1"], json: true);
        Assert.True(exit == 0, stderr);
        using var doc = JsonDocument.Parse(stdout);
        var data = doc.RootElement.GetProperty("data");
        var placement = Assert.Single(data.GetProperty("otherPlacements").EnumerateArray());
        Assert.Equal("telephone1", placement.GetProperty("field").GetString());
        Assert.Equal(new[] { "My Tab / Other" }, placement.GetProperty("locations").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal(0, data.GetProperty("nonCanonicalBindings").GetArrayLength());
        Assert.Equal(1, data.GetProperty("addedCount").GetInt32());
    }

    [Fact]
    [Trait("AC", "AC-72")]
    public async Task AddField_Json_ReportsAssignedControlIdsAdditively()
    {
        var (exit, stdout, stderr) = await Run(AddFieldCommand.Create(Provider(null)),
            ["--section", "General", "--field", "telephone1"], json: true);
        Assert.True(exit == 0, stderr);
        using var doc = JsonDocument.Parse(stdout);
        var data = doc.RootElement.GetProperty("data");
        // Additive: every pre-existing key is still present, in its original order, and the new key comes last.
        Assert.Equal(new[] { "section", "fields", "message", "outcomeKnown", "addedCount", "alreadyPresentCount", "duplicateRequestCount",
            "addedFields", "alreadyPresentFields", "duplicateRequests", "formXmlChanged", "solutionAssociationRequested",
            "publicationRequested", "otherPlacements", "nonCanonicalBindings", "addedControls" }, data.EnumerateObject().Select(p => p.Name));
        var added = Assert.Single(data.GetProperty("addedControls").EnumerateArray());
        Assert.Equal(new[] { "field", "controlId" }, added.EnumerateObject().Select(p => p.Name));
        Assert.Equal("telephone1", added.GetProperty("field").GetString());
        Assert.Equal("telephone11", added.GetProperty("controlId").GetString());

        // Nothing added (same-section re-add): the key is present and empty, not omitted.
        var (noopExit, noopStdout, noopStderr) = await Run(AddFieldCommand.Create(Provider(null)),
            ["--section", "General", "--field", "name"], json: true);
        Assert.True(noopExit == 0, noopStderr);
        using var noop = JsonDocument.Parse(noopStdout);
        Assert.Equal(JsonValueKind.Array, noop.RootElement.GetProperty("data").GetProperty("addedControls").ValueKind);
        Assert.Equal(0, noop.RootElement.GetProperty("data").GetProperty("addedControls").GetArrayLength());
    }

    [Fact]
    [Trait("AC", "AC-71")]
    public async Task AddField_Json_HasNonCanonicalBindingShape()
    {
        var (exit, stdout, stderr) = await Run(AddFieldCommand.Create(Provider(CaseVariantName)),
            ["--section", "General", "--field", "name"], json: true);
        Assert.True(exit == 0, stderr);
        using var doc = JsonDocument.Parse(stdout);
        var data = doc.RootElement.GetProperty("data");
        var binding = Assert.Single(data.GetProperty("nonCanonicalBindings").EnumerateArray());
        Assert.Equal("Name", binding.GetProperty("storedName").GetString());
        Assert.Equal("name", binding.GetProperty("canonicalName").GetString());
        Assert.Equal(0, data.GetProperty("otherPlacements").GetArrayLength());
    }

    [Fact]
    [Trait("AC", "AC-70")]
    [Trait("AC", "AC-71")]
    public async Task AddField_Text_NoticesGoToStderrOnly()
    {
        var (exit, stdout, stderr) = await Run(AddFieldCommand.Create(Provider(CaseVariantName)),
            ["--section", "General", "--field", "name", "--field", "telephone1"], json: false);
        Assert.True(exit == 0, stderr);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("Notice: 'telephone1' is also placed elsewhere on this form: My Tab / Other", stderr);
        Assert.Contains("bound to 'Name'", stderr);
        Assert.Contains("logical name 'name'", stderr);
        Assert.Contains("#1527", stderr);
    }

    [Fact]
    [Trait("AC", "AC-71")]
    public async Task Reorder_Json_GainsNonCanonicalBindingsOnlyWhenNonEmpty()
    {
        var (exit, stdout, stderr) = await Run(ReorderFieldsCommand.Create(Provider(CaseVariantName)),
            ["--section", "General", "--fields", "name"], json: true);
        Assert.True(exit == 0, stderr);
        using var variant = JsonDocument.Parse(stdout);
        var data = variant.RootElement.GetProperty("data");
        Assert.Equal(new[] { "section", "fields", "message", "nonCanonicalBindings" }, data.EnumerateObject().Select(p => p.Name));
        var binding = Assert.Single(data.GetProperty("nonCanonicalBindings").EnumerateArray());
        Assert.Equal("Name", binding.GetProperty("storedName").GetString());
        Assert.Equal("name", binding.GetProperty("canonicalName").GetString());

        (exit, stdout, stderr) = await Run(ReorderFieldsCommand.Create(Provider(null)),
            ["--section", "General", "--fields", "name"], json: true);
        Assert.True(exit == 0, stderr);
        using var canonical = JsonDocument.Parse(stdout);
        Assert.Equal(new[] { "section", "fields", "message" }, canonical.RootElement.GetProperty("data").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    [Trait("AC", "AC-71")]
    public async Task Reorder_Text_WarningGoesToStderrOnly()
    {
        var (exit, stdout, stderr) = await Run(ReorderFieldsCommand.Create(Provider(CaseVariantName)),
            ["--section", "General", "--fields", "name"], json: false);
        Assert.True(exit == 0, stderr);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("bound to 'Name'", stderr);
        Assert.Contains("#1527", stderr);
    }

    [Fact]
    [Trait("AC", "AC-71")]
    public async Task Reorder_LegacyImplementationWithOnlyDefaultMethod_AddsNoKeys()
    {
        var legacy = new Mock<IFormService> { CallBase = true };
        legacy.Setup(s => s.ReorderFieldsAsync(It.IsAny<ReorderFieldsRequest>(), It.IsAny<IProgressReporter?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var command = ReorderFieldsCommand.Create((_, _, _, ct) =>
            Task.FromResult(new ContributorFormHarness(ct).CreateTextProvider(legacy.Object)));
        var (exit, stdout, stderr) = await Run(command, ["--section", "General", "--fields", "name"], json: true);
        Assert.True(exit == 0, stderr);
        using var doc = JsonDocument.Parse(stdout);
        Assert.Equal(new[] { "section", "fields", "message" }, doc.RootElement.GetProperty("data").EnumerateObject().Select(p => p.Name));
        legacy.Verify(s => s.ReorderFieldsAsync(It.IsAny<ReorderFieldsRequest>(), It.IsAny<IProgressReporter?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Func<string?, string?, GlobalOptionValues, CancellationToken, Task<ServiceProvider>> Provider(Action<XDocument>? transform) =>
        (_, _, _, ct) => Task.FromResult(new ContributorFormHarness(ct, transform: transform).CreateTextProvider());

    private static async Task<(int Exit, string Out, string Error)> Run(Command command, string[] args, bool json)
    {
        var previousOut = Console.Out; var previousError = Console.Error;
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            var all = new List<string> { "--entity", "account", "--form", "Fixture" };
            all.AddRange(args);
            all.AddRange(["--confirm-env", "https://forms.example.test"]);
            if (json) all.AddRange(["--output-format", "Json"]);
            var parsed = command.Parse(all.ToArray());
            Assert.Empty(parsed.Errors);
            var exit = await parsed.InvokeAsync();
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally { Console.SetOut(previousOut); Console.SetError(previousError); }
    }
}
