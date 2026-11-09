using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Commands.Forms;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Tests.Services.Forms;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Forms;

[Collection(nameof(ConsoleCaptureCollection))]
[Trait("Category", "Unit")]
[Trait("AC", "AC-65")]
public sealed class FormExplicitLayoutCommandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewCommand_ParsesAndExecutesRealServiceThroughPrivateMocks(bool move)
    {
        ContributorFormHarness? h = null;
        CancellationToken? invocationToken = null;
        Task<ServiceProvider> Provider(string? profile, string? environment, GlobalOptionValues options, CancellationToken ct)
        {
            Assert.True(options.IsJsonMode); Assert.Null(h); invocationToken = ct;
            h = new ContributorFormHarness(ct); h.AssertPreconditions("base");
            return Task.FromResult(h.CreateProvider());
        }
        var command = move ? MoveFieldCommand.Create(Provider) : ReorderFieldsStrictCommand.Create(Provider);
        string[] specific = move ? ["--field", "telephone1", "--from-section", "Other", "--to-section", "General", "--before-field", "name"]
            : ["--section", "General", "--fields", "NAME"];
        var (exit, stdout, stderr) = await Invoke(command, specific);
        Assert.True(exit == 0, $"Expected command exit 0; actual {exit}. stdout: {stdout}; stderr: {stderr}");
        var actual = Assert.IsType<ContributorFormHarness>(h);
        Assert.True(invocationToken.HasValue); Assert.NotEmpty(actual.ObservedTokens);
        Assert.All(actual.ObservedTokens, token => Assert.Equal(invocationToken.GetValueOrDefault(), token));
        using var json = JsonDocument.Parse(stdout);
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(move ? 1 : 0, actual.Writes.Count); Assert.DoesNotContain("deprecated", stderr);
        actual.AssertIsolation();
    }

    [Fact]
    public async Task LegacyReorder_DeprecatesOnlyOnStderr_AndPreservesJsonDataShape()
    {
        ContributorFormHarness? h = null;
        CancellationToken? invocationToken = null;
        var command = ReorderFieldsCommand.Create((_, _, _, ct) =>
        {
            Assert.Null(h); invocationToken = ct; h = new ContributorFormHarness(ct);
            h.AssertPreconditions("base"); return Task.FromResult(h.CreateProvider());
        });
        var (exit, stdout, stderr) = await Invoke(command, ["--section", "General", "--fields", "NAME"]);
        Assert.True(exit == 0, $"Expected command exit 0; actual {exit}. stdout: {stdout}; stderr: {stderr}");
        var actual = Assert.IsType<ContributorFormHarness>(h);
        Assert.True(invocationToken.HasValue); Assert.NotEmpty(actual.ObservedTokens);
        Assert.All(actual.ObservedTokens, token => Assert.Equal(invocationToken.GetValueOrDefault(), token));
        using var json = JsonDocument.Parse(stdout);
        Assert.Equal(new[] { "section", "fields", "message" }, json.RootElement.GetProperty("data").EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("deprecated", stdout); Assert.Contains("deprecated", stderr);
        Assert.Contains("CLI 2.0", stderr); Assert.Contains("90 days", stderr); Assert.Contains("stable release", stderr);
        Assert.Single(actual.Writes); actual.AssertIsolation();
    }

    [Fact]
    public async Task StrictCommand_EmptySegmentRejectsBeforeProviderCreation()
    {
        var providerCalled = false;
        var command = ReorderFieldsStrictCommand.Create((_, _, _, _) =>
        {
            providerCalled = true;
            throw new Xunit.Sdk.XunitException("Provider must not be constructed.");
        });
        var (exit, _, _) = await Invoke(command, ["--section", "General", "--fields", "name,,telephone1"]);
        Assert.NotEqual(0, exit);
        Assert.False(providerCalled);
    }

    private static async Task<(int Exit, string Out, string Error)> Invoke(Command command, string[] args)
    {
        var previousOut = Console.Out; var previousError = Console.Error;
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            var parsed = command.Parse(["--entity", "account", "--form", "Fixture", ..args,
                "--confirm-env", "https://forms.example.test", "--output-format", "Json"]);
            Assert.Empty(parsed.Errors); var exit = await parsed.InvokeAsync(); return (exit, stdout.ToString(), stderr.ToString());
        }
        finally { Console.SetOut(previousOut); Console.SetError(previousError); }
    }
}
