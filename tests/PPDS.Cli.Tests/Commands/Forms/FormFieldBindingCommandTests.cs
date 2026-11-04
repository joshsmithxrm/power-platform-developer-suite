using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Cli.Commands.Forms;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Tests.Services.Forms;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Forms;

[Collection(nameof(ConsoleCaptureCollection))]
public sealed class FormFieldBindingCommandTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Command_UsesRealService(bool reorder, bool invalid)
    {
        var h = new FormFieldBindingHarness();
        var providerCalls = 0;
        Task<ServiceProvider> Provider(string? profile, string? environment, GlobalOptionValues options, CancellationToken ct)
        {
            Assert.Equal("offline-fixture", profile);
            Assert.Equal("https://forms.example.test", environment);
            Assert.True(options.IsJsonMode);
            providerCalls++;
            return Task.FromResult(h.CreateProvider());
        }
        var command = reorder ? ReorderFieldsCommand.Create(Provider) : AddFieldCommand.Create(Provider);
        var requested = invalid ? "MISSING" : "NEW_CANONICALFIELD";
        var args = new[] { "--entity", "account", "--form", "Fixture", "--section", "General",
            reorder ? "--fields" : "--field", requested,
            "--profile", "offline-fixture", "--environment", "https://forms.example.test",
            "--confirm-env", "https://forms.example.test", "--output-format", "Json" };
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var parsed = command.Parse(args);
            Assert.Empty(parsed.Errors);
            var exit = await parsed.InvokeAsync();
            using var json = JsonDocument.Parse(stdout.ToString());
            Assert.Equal(!invalid, json.RootElement.GetProperty("success").GetBoolean());
            if (invalid)
            {
                Assert.NotEqual(0, exit);
                h.AssertNoWrites();
            }
            else
            {
                Assert.Equal(0, exit);
                var control = h.WrittenDocument().Descendants("control").Single(c =>
                    (string?)c.Attribute("datafieldname") == FormFieldBindingHarness.Canonical);
                Assert.Equal(FormFieldBindingHarness.Canonical, (string?)control.Attribute("id"));
            }
            Assert.Equal(1, providerCalls);
            h.Confirmation.Verify(c => c.EnsureConfirmedAsync("modify form", "https://forms.example.test", It.IsAny<CancellationToken>()), Times.Once);
            h.Confirmation.Verify(c => c.EnsureConfirmedAsync("forms mutation", null, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }
}
