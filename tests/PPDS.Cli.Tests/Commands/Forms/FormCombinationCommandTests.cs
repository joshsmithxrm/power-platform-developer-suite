using System.CommandLine;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Cli.Commands.Forms;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Forms;
using PPDS.Cli.Tests.Services.Forms;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Forms;

[Collection(nameof(ConsoleCaptureCollection))]
[Trait("Category", "Unit")]
public sealed class FormCombinationCommandTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("AC", "AC-56")]
    [Trait("AC", "AC-57")]
    public async Task CanonicalMutation_ThenRawGetSet_PreservesPayload(bool reorder, bool unpublished)
    {
        var binding = await MutateAsync(reorder);
        var document = binding.WrittenDocument();
        var original = XDocument.Parse(binding.Original);
        var added = document.Descendants("control").Single(c =>
            (string?)c.Attribute("datafieldname") == FormFieldBindingHarness.Canonical);
        Assert.Equal(FormFieldBindingHarness.Canonical, (string?)added.Attribute("id"));
        foreach (var cell in original.Descendants("cell"))
            Assert.True(XNode.DeepEquals(cell, document.Descendants("cell").Single(c =>
                (string?)c.Attribute("id") == (string?)cell.Attribute("id"))));
        original.Descendants("rows").Single().Remove();
        var surrounding = new XDocument(document);
        surrounding.Descendants("rows").Single().Remove();
        Assert.True(XNode.DeepEquals(original, surrounding));

        var xml = binding.Writes.Single().GetAttributeValue<string>("formxml");
        var roundTrip = new ControlTypeHarness(xml);
        var providers = 0;
        Task<ServiceProvider> Provider(string? profile, string? environment, GlobalOptionValues options, CancellationToken ct)
        {
            Assert.Equal("offline-fixture", profile);
            Assert.Equal("https://forms.example.test", environment);
            providers++;
            return Task.FromResult(roundTrip.Provider());
        }
        string[] target = ["--entity", "account", "--form", ControlTypeHarness.FormId.ToString(),
            "--profile", "offline-fixture", "--environment", "https://forms.example.test"];
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var raw = new StringWriter();
        using var diagnostic = new StringWriter();
        var path = Path.Combine(Path.GetTempPath(), "forms-combined-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            Console.SetOut(raw);
            Console.SetError(diagnostic);
            var get = GetCommand.Create(Provider).Parse(target.Concat(unpublished ? ["--raw", "--unpublished"] : new[] { "--raw" }).ToArray());
            Assert.Empty(get.Errors);
            Assert.Equal(0, await get.InvokeAsync());
            Assert.Equal(xml, raw.ToString());
            await File.WriteAllTextAsync(path, raw.ToString());
            using var output = new StringWriter();
            Console.SetOut(output);
            var set = SetXmlCommand.Create(Provider).Parse(target.Concat(new[] { "--xml", path,
                "--confirm-env", "https://forms.example.test", "--output-format", "Json" }).ToArray());
            Assert.Empty(set.Errors);
            Assert.Equal(0, await set.InvokeAsync());
            using var result = JsonDocument.Parse(output.ToString());
            Assert.True(result.RootElement.GetProperty("success").GetBoolean());
            var write = Assert.Single(roundTrip.Writes);
            Assert.Equal(raw.ToString(), write.GetAttributeValue<string>("formxml"));
            Assert.Equal(ControlTypeHarness.FormId, write.Id);
            Assert.Equal(2, providers);
            roundTrip.Confirmation.Verify(c => c.EnsureConfirmedAsync("modify form", "https://forms.example.test", It.IsAny<CancellationToken>()), Times.Once);
            roundTrip.Confirmation.Verify(c => c.EnsureConfirmedAsync("forms mutation", null, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData("duplicate-cell")]
    [InlineData("type-guid")]
    [InlineData("timeline-bool")]
    [InlineData("timeline-json-structure")]
    [Trait("AC", "AC-58")]
    public async Task MutatedForm_InvalidPayload_DoesNotWrite(string variant)
    {
        var document = (await MutateAsync(false)).WrittenDocument();
        switch (variant)
        {
            case "duplicate-cell": document.Descendants("cell").Last().SetAttributeValue("id", (string?)document.Descendants("cell").First().Attribute("id")); break;
            case "type-guid": document.Descendants("customControl").First().SetAttributeValue("id", "invalid"); break;
            case "timeline-bool": document.Descendants("UClientShowFilterPane").Single().Value = "invalid"; break;
            case "timeline-json-structure": document.Descendants("UClientActivitiesConfigurationJSON").Single().Add(new XElement("nested")); break;
        }
        var h = new ControlTypeHarness(document.ToString());
        var error = await Assert.ThrowsAsync<PpdsValidationException>(() => h.Service.SetFormXmlAsync(
            new("account", "Fixture", h.Xml, "fixture_solution", true)));
        Assert.Equal(variant == "duplicate-cell" ? FormErrorCodes.DuplicateGuid : FormErrorCodes.InvalidFormXml, error.ErrorCode);
        h.AssertNoWrites();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("AC", "AC-58")]
    public async Task CanonicalMutation_DeniedConfirmation_DoesNotWrite(bool reorder)
    {
        var h = new FormFieldBindingHarness(originalXml: ControlTypeFixture.Xml());
        h.Confirmation.Setup(c => c.EnsureConfirmedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PpdsException("Environment.ReadOnly", "Offline fixture denies writes."));
        var error = await Assert.ThrowsAsync<PpdsException>(() => MutateAsync(h, reorder));
        Assert.Equal("Environment.ReadOnly", error.ErrorCode);
        h.AssertNoWrites();
    }

    private static async Task<FormFieldBindingHarness> MutateAsync(bool reorder)
    {
        var h = new FormFieldBindingHarness(originalXml: ControlTypeFixture.Xml());
        await MutateAsync(h, reorder);
        return h;
    }

    private static Task MutateAsync(FormFieldBindingHarness h, bool reorder) => reorder
        ? h.Service.ReorderFieldsAsync(new("account", "Fixture", "General", ["NEW_CANONICALFIELD", "NAME", "TELEPHONE1"]))
        : h.Service.AddFieldAsync(new("account", "Fixture", "General", ["NEW_CANONICALFIELD"]));
}
