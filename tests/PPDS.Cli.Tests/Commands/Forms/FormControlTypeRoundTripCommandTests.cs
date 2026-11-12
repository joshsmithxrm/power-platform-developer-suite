using System.CommandLine;
using System.Text.Json;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Cli.Commands.Forms;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Tests.Services.Forms;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Forms;

[Collection(nameof(ConsoleCaptureCollection))]
public sealed class FormControlTypeRoundTripCommandTests
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "cells")]
    [InlineData(true, "type-format")]
    public async Task RawGetThenSet_PreservesExactPayload(bool unpublished, string? invalid)
    {
        var xml = invalid == null ? ControlTypeFixture.Xml() : ControlTypeFixture.InvalidXml(invalid);
        // Both payloads are synthetic. Distinct comments make the selected read source
        // observable without changing the repeated control-type IDs under test.
        var publishedXml = xml + "<!-- published fixture source -->\r\n";
        var unpublishedXml = xml + "<!-- unpublished fixture source -->\r\n";
        Assert.NotEqual(publishedXml, unpublishedXml);
        var expectedXml = unpublished ? unpublishedXml : publishedXml;
        var h = new ControlTypeHarness(publishedXml, unpublishedXml);
        var providerCalls = 0;
        Task<ServiceProvider> Provider(string? profile, string? environment, GlobalOptionValues options, CancellationToken ct)
        {
            Assert.Equal("offline-fixture", profile);
            Assert.Equal("https://forms.example.test", environment);
            providerCalls++;
            return Task.FromResult(h.Provider());
        }
        var target = new[] { "--entity", "account", "--form", ControlTypeHarness.FormId.ToString(),
            "--profile", "offline-fixture", "--environment", "https://forms.example.test" };
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var rawOut = new StringWriter();
        using var diagnostics = new StringWriter();
        var file = Path.Combine(Path.GetTempPath(), "ppds-control-type-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            Console.SetOut(rawOut);
            Console.SetError(diagnostics);
            var get = GetCommand.Create(Provider).Parse(target.Concat(unpublished ? ["--raw", "--unpublished"] : new[] { "--raw" }).ToArray());
            Assert.Empty(get.Errors);
            Assert.Equal(0, await get.InvokeAsync());
            var captured = rawOut.ToString();
            Assert.Equal(expectedXml, captured); // The actual raw stdout, not a second copy of the fixture.
            // Check before set-xml performs its own draft lookup.
            Assert.Single(h.Queries);
            Assert.Equal(unpublished ? 1 : 0, h.Requests.OfType<RetrieveUnpublishedMultipleRequest>().Count());
            await File.WriteAllTextAsync(file, captured);

            using var setOut = new StringWriter();
            Console.SetOut(setOut);
            var setArgs = target.Concat(new[] { "--xml", file, "--confirm-env", "https://forms.example.test", "--output-format", "Json" });
            if (invalid != null) setArgs = setArgs.Concat(["--publish", "--solution", "fixture_solution"]);
            var set = SetXmlCommand.Create(Provider).Parse(setArgs.ToArray());
            Assert.Empty(set.Errors);
            var exit = await set.InvokeAsync();
            using var result = JsonDocument.Parse(setOut.ToString());
            Assert.Equal(invalid == null, result.RootElement.GetProperty("success").GetBoolean());
            if (invalid == null)
            {
                Assert.Equal(0, exit);
                var update = Assert.Single(h.Writes);
                Assert.Equal(captured, update.GetAttributeValue<string>("formxml"));
                Assert.Equal(ControlTypeHarness.FormId, update.Id);
            }
            else
            {
                Assert.NotEqual(0, exit);
                h.AssertNoWrites();
            }
            Assert.Equal(2, providerCalls);
            Assert.All(h.Queries, query =>
            {
                Assert.Contains(query.Criteria.Conditions, c => c.AttributeName == "objecttypecode" && (string)c.Values[0] == "account");
                Assert.Contains(query.Criteria.Conditions, c => c.AttributeName == "formid" && (Guid)c.Values[0] == ControlTypeHarness.FormId);
            });
            h.Confirmation.Verify(c => c.EnsureConfirmedAsync("modify form", "https://forms.example.test", It.IsAny<CancellationToken>()), Times.Once);
            h.Confirmation.Verify(c => c.EnsureConfirmedAsync("forms mutation", null, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void Help_ExplainsInstanceAndTypeIds()
    {
        var command = SetXmlCommand.Create();
        foreach (var description in new[] { command.Description!, command.Options.Single(o => o.Name == "--xml").Description! })
        {
            Assert.Contains("schema", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("instance", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("type", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("unique", description, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("must use brace-format", description);
        }
    }
}
