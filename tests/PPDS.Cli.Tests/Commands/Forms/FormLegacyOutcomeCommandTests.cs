using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Cli.Commands.Forms;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Forms;
using PPDS.Cli.Tests.Services.Forms;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Forms;

// Fixed-only: the actual legacy implementation dispatches the new default interface method.
[Collection(nameof(ConsoleCaptureCollection))]
[Trait("Category", "Unit")]
public sealed class FormLegacyOutcomeCommandTests
{
    [Fact]
    public async Task LegacyServiceCompletion_DoesNotFabricateZeroOrAddedCounts()
    {
        var legacy = new LegacyOnlyFormService();
        Mock<IEnvironmentConfirmationService>? confirmation = null;
        CancellationToken? invocationToken = null;
        Task<ServiceProvider> Provider(string? profile, string? environment, GlobalOptionValues options, CancellationToken ct)
        {
            Assert.True(options.IsJsonMode); Assert.Null(confirmation); invocationToken = ct;
            confirmation = new Mock<IEnvironmentConfirmationService>(MockBehavior.Strict);
            confirmation.Setup(c => c.EnsureConfirmedAsync("modify form", "https://forms.example.test", ct)).Returns(Task.CompletedTask);
            return Task.FromResult(new ServiceCollection().AddSingleton<IFormService>(legacy)
                .AddSingleton(confirmation.Object).BuildServiceProvider());
        }
        var command = AddFieldCommand.Create(Provider);
        var previousOut = Console.Out; var previousError = Console.Error;
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            var parsed = command.Parse(["--entity", "account", "--form", "Fixture", "--section", "General", "--field", "name",
                "--confirm-env", "https://forms.example.test", "--output-format", "Json"]);
            Assert.Empty(parsed.Errors); var exit = await parsed.InvokeAsync();
            Assert.True(exit == 0, $"Expected command exit 0; actual {exit}. stdout: {stdout}; stderr: {stderr}");
            Assert.True(invocationToken.HasValue); Assert.Equal(invocationToken.GetValueOrDefault(), legacy.Token);
            using var json = JsonDocument.Parse(stdout.ToString()); var data = json.RootElement.GetProperty("data");
            Assert.False(data.GetProperty("outcomeKnown").GetBoolean());
            Assert.True(!data.TryGetProperty("addedCount", out var added) || added.ValueKind == JsonValueKind.Null);
            Assert.Contains("did not report addition counts", data.GetProperty("message").GetString());
            Assert.Equal(1, legacy.Calls);
            Assert.NotNull(confirmation);
            confirmation.Verify(c => c.EnsureConfirmedAsync("modify form", "https://forms.example.test", invocationToken.GetValueOrDefault()), Times.Once);
            confirmation.VerifyNoOtherCalls();
        }
        finally { Console.SetOut(previousOut); Console.SetError(previousError); }
    }
}
