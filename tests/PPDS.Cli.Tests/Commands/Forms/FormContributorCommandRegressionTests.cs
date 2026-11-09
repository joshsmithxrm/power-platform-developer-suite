using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Commands.Forms;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Tests.Services.Forms;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.Cli.Tests.Commands.Forms;

// Same exact source runs against old B3 and the fixed command/real service.
[Collection(nameof(ConsoleCaptureCollection))]
[Trait("Category", "Unit")]
[Trait("AC", "AC-59")]
public sealed class FormContributorCommandRegressionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddCommand_ReportsActualCountsAndAlreadyPresentNotice(bool mixed)
    {
        ContributorFormHarness? h = null;
        CancellationToken? invocationToken = null;
        Task<ServiceProvider> Provider(string? profile, string? environment, GlobalOptionValues options, CancellationToken ct)
        {
            Assert.Equal("offline-fixture", profile); Assert.Equal("https://forms.example.test", environment);
            Assert.True(options.IsJsonMode); Assert.Null(h);
            invocationToken = ct;
            h = new ContributorFormHarness(ct);
            h.AssertPreconditions("base");
            return Task.FromResult(h.CreateProvider());
        }
        string[] fields = mixed ? ["NAME", ContributorFormFixture.Priority, "MAYO_PRIORITYSCORE"] : ["NAME", "name"];
        var args = new List<string> { "--entity", "account", "--form", "Fixture", "--section", "General" };
        foreach (var field in fields) args.AddRange(["--field", field]);
        args.AddRange(["--profile", "offline-fixture", "--environment", "https://forms.example.test",
            "--confirm-env", "https://forms.example.test", "--output-format", "Json"]);
        var command = AddFieldCommand.Create(Provider);
        var previousOut = Console.Out; var previousError = Console.Error;
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            var parsed = command.Parse(args.ToArray()); Assert.Empty(parsed.Errors);
            var exit = await parsed.InvokeAsync();
            output.WriteLine("PPDS_FORMS_COMMAND_EXIT=" + exit);
            output.WriteLine("PPDS_FORMS_COMMAND_STDOUT=" + stdout);
            output.WriteLine("PPDS_FORMS_COMMAND_STDERR=" + stderr);
            Assert.True(exit == 0, $"Expected command exit 0; actual {exit}. stdout: {stdout}; stderr: {stderr}");
            var actual = Assert.IsType<ContributorFormHarness>(h);
            Assert.True(invocationToken.HasValue);
            Assert.NotEmpty(actual.ObservedTokens);
            Assert.All(actual.ObservedTokens, token => Assert.Equal(invocationToken.GetValueOrDefault(), token));
            actual.AssertIsolation();
            output.WriteLine("PPDS_FORMS_COMMAND_SETUP_VALID=true");
            using var json = JsonDocument.Parse(stdout.ToString());
            Assert.True(json.RootElement.GetProperty("success").GetBoolean());
            var data = json.RootElement.GetProperty("data");
            var hasOutcome = data.TryGetProperty("outcomeKnown", out var known);
            output.WriteLine("PPDS_FORMS_ADD_OUTCOME_KNOWN_PRESENT=" + hasOutcome);
            Assert.True(hasOutcome); Assert.True(known.GetBoolean());
            Assert.Equal(mixed ? 1 : 0, data.GetProperty("addedCount").GetInt32());
            Assert.Equal(1, data.GetProperty("alreadyPresentCount").GetInt32());
            Assert.Equal(1, data.GetProperty("duplicateRequestCount").GetInt32());
            Assert.Equal(new[] { "name" }, data.GetProperty("alreadyPresentFields").EnumerateArray().Select(field => field.GetString()));
            Assert.Contains("Already present", data.GetProperty("message").GetString());
            Assert.Equal(mixed ? 1 : 0, actual.Writes.Count);
        }
        finally { Console.SetOut(previousOut); Console.SetError(previousError); }
    }
}
