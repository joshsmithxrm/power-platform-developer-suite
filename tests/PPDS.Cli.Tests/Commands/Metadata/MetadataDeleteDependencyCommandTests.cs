using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Metadata;
using PPDS.Cli.Commands.Metadata.Attribute;
using PPDS.Cli.Commands.Metadata.Choice;
using PPDS.Cli.Commands.Metadata.Column;
using PPDS.Cli.Commands.Metadata.Key;
using PPDS.Cli.Commands.Metadata.Relationship;
using PPDS.Cli.Commands.Metadata.Table;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Metadata.Authoring;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;
using Fixture = PPDS.Cli.Tests.Services.Metadata.Authoring.MetadataDeleteDependencyTests.Fixture;

namespace PPDS.Cli.Tests.Commands.Metadata;

[Trait("Category", "Unit")]
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class MetadataDeleteDependencyCommandTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("entity", true)]
    [InlineData("table", true)]
    [InlineData("attribute", true)]
    [InlineData("column", true)]
    [InlineData("relationship", true)]
    [InlineData("key", true)]
    [InlineData("optionset", true)]
    [InlineData("choice", true)]
    [InlineData("entity", false)]
    [InlineData("table", false)]
    [InlineData("attribute", false)]
    [InlineData("column", false)]
    [InlineData("relationship", false)]
    [InlineData("key", false)]
    [InlineData("optionset", false)]
    [InlineData("choice", false)]
    [Trait("AC", "AC-120")]
    public async Task AllAliases_BlockedDryRunShowsComponentsAndNonzero(string noun, bool json)
    {
        using var f = new Fixture(Family(noun)) { Blockers = true };
        var result = await Run(f, noun, json, dryRun: true);
        Assert.Equal(ExitCodes.PreconditionFailed, result.Exit);
        var text = json ? result.Out : result.Error;
        Assert.Contains(Fixture.ViewId.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Fixture.UnknownId.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("90000", text);
        if (json)
        {
            using var document = JsonDocument.Parse(result.Out);
            Assert.False(document.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("DEPENDENCY_CONFLICT", document.RootElement.GetProperty("error").GetProperty("code").GetString());
            var preview = document.RootElement.GetProperty("data").GetProperty("preview");
            Assert.True(preview.GetProperty("dependenciesChecked").GetBoolean());
            Assert.False(preview.GetProperty("deletionFeasibilityChecked").GetBoolean());
        }
        else Assert.Empty(result.Out);
        f.AssertReadsAndReturned();
    }

    [Theory]
    [InlineData("entity")]
    [InlineData("table")]
    [InlineData("attribute")]
    [InlineData("column")]
    [InlineData("relationship")]
    [InlineData("key")]
    [InlineData("optionset")]
    [InlineData("choice")]
    [Trait("AC", "AC-115")]
    [Trait("AC", "AC-120")]
    public async Task AllAliases_EmptyCompleteObservationDoesNotClaimFeasibility(string noun)
    {
        using var f = new Fixture(Family(noun));
        var result = await Run(f, noun, true, dryRun: true);
        Assert.Equal(0, result.Exit);
        using var document = JsonDocument.Parse(result.Out);
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        var preview = document.RootElement.GetProperty("data").GetProperty("preview");
        Assert.True(preview.GetProperty("targetExistenceChecked").GetBoolean());
        Assert.True(preview.GetProperty("dependenciesChecked").GetBoolean());
        Assert.False(preview.GetProperty("deletionFeasibilityChecked").GetBoolean());
        Assert.Equal(2, f.Requests.Count);
        f.AssertReadsAndReturned();
    }

    [Theory]
    [InlineData("entity")]
    [InlineData("table")]
    [InlineData("attribute")]
    [InlineData("column")]
    [InlineData("relationship")]
    [InlineData("key")]
    [InlineData("optionset")]
    [InlineData("choice")]
    [Trait("AC", "AC-117")]
    [Trait("AC", "AC-120")]
    public async Task AllAliases_IncompleteDependencyResultIsNeverSuccess(string noun)
    {
        using var f = new Fixture(Family(noun)) { DependencyShape = "more-records" };
        var result = await Run(f, noun, true, dryRun: true);
        Assert.NotEqual(0, result.Exit);
        using var document = JsonDocument.Parse(result.Out);
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Single(f.Requests.OfType<Microsoft.Crm.Sdk.Messages.RetrieveDependenciesForDeleteRequest>());
        f.AssertReadsAndReturned();
    }

    [Theory]
    [InlineData("entity")]
    [InlineData("table")]
    [InlineData("attribute")]
    [InlineData("column")]
    [InlineData("relationship")]
    [InlineData("key")]
    [InlineData("optionset")]
    [InlineData("choice")]
    [Trait("AC", "AC-119")]
    public async Task Force_DoesNotBypassDependencyRefusal(string noun)
    {
        using var f = new Fixture(Family(noun)) { Blockers = true };
        var result = await Run(f, noun, true, dryRun: false);
        Assert.Equal(ExitCodes.PreconditionFailed, result.Exit);
        f.AssertReadsAndReturned();
    }

    private static string Family(string noun) => noun switch
    {
        "entity" or "table" => "table", "attribute" or "column" => "column",
        "optionset" or "choice" => "choice", _ => noun
    };

    private async Task<(int Exit, string Out, string Error)> Run(Fixture fixture, string noun, bool json, bool dryRun)
    {
        var providerCalls = 0;
        Task<ServiceProvider> Provider(CancellationToken token)
        {
            providerCalls++;
            // System.CommandLine supplies its actual linked invocation token.
            // Bind this exact token before any real service/mocked SDK call.
            fixture.BindOperationToken(token);
            return Task.FromResult(new ServiceCollection().AddSingleton<IMetadataAuthoringService>(fixture.Service)
                .AddSingleton(new ResolvedConnectionInfo { Profile = new AuthProfile { Name = "synthetic-only" }, EnvironmentUrl = "https://fixture.example.test" })
                .BuildServiceProvider());
        }
        var command = noun switch
        {
            "entity" => EntityCommand.CreateDeleteCommand(Provider), "table" => TableCommandGroup.CreateDeleteCommand(Provider),
            "attribute" => AttributeCommandGroup.CreateDeleteCommand(Provider), "column" => ColumnCommandGroup.CreateDeleteCommand(Provider),
            "relationship" => RelationshipCommandGroup.CreateDeleteCommand(Provider), "key" => KeyCommandGroup.CreateDeleteCommand(Provider),
            "optionset" => OptionSetCommand.CreateDeleteCommand(Provider), "choice" => ChoiceCommandGroup.CreateDeleteCommand(Provider),
            _ => throw new ArgumentOutOfRangeException()
        };
        var args = new List<string> { "--solution", "SyntheticSolution" };
        if (noun is "entity" or "table" or "attribute" or "column" or "key") args.AddRange(["--entity", "new_table"]);
        if (noun is "attribute" or "column") args.AddRange(["--column", "new_column"]);
        if (noun is "relationship" or "key" or "optionset" or "choice") args.AddRange(["--name", "new_name"]);
        args.Add(dryRun ? "--dry-run" : "--force");
        if (json) args.AddRange(["--output-format", "json"]);
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var oldOut = Console.Out; var oldError = Console.Error;
        int? exitCode = null; var parseErrors = -1; var preconditionsPassed = false;
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            var parsed = command.Parse(args.ToArray()); parseErrors = parsed.Errors.Count; Assert.Empty(parsed.Errors);
            var exit = await parsed.InvokeAsync(cancellationToken: fixture.Token); exitCode = exit;
            Assert.Equal(1, providerCalls); Assert.True(fixture.ServiceEntries > 0);
            Assert.True(fixture.OperationTokenWasBound); Assert.Equal(0, fixture.TokenMismatches);
            preconditionsPassed = true;
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(oldOut); Console.SetError(oldError);
            output.WriteLine("PPDS_MD3_CASE_PROOF=" + JsonSerializer.Serialize(new
            {
                noun, json, dryRun, exitCode, parseErrors, providerCalls,
                serviceEntries = fixture.ServiceEntries, operationTokenBound = fixture.OperationTokenWasBound,
                tokenMismatches = fixture.TokenMismatches, preconditionsPassed,
                requests = fixture.Requests.Select(r => r.RequestName).ToArray(),
                stdout = stdout.ToString(), stderr = stderr.ToString()
            }));
        }
    }
}
