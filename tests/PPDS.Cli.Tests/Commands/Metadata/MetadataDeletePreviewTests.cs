using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
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
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Cli.Tests.TestHelpers;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Metadata;

[Trait("Category", "Unit")]
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class MetadataDeletePreviewTests
{
    public static IEnumerable<object[]> SchemaCases => new[]
    {
        "entity", "attribute", "relationship", "optionset", "key", "table", "column", "choice"
    }.SelectMany(noun => new[] { new object[] { noun, false }, new object[] { noun, true } });

    [Theory]
    [MemberData(nameof(SchemaCases))]
    public async Task SchemaDelete_ReportsInputOnlyScopeWithoutSdkCalls(string noun, bool json)
    {
        using var fixture = new Fixture();
        var result = await fixture.RunAsync(noun, true, json);
        Assert.Equal(ExitCodes.Success, result.Exit);
        fixture.Pool.VerifyNoOtherCalls(); // Even invented/nonexistent names cannot be checked locally.
        fixture.Client.Verify(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        if (json)
        {
            using var document = JsonDocument.Parse(result.Out);
            Assert.True(document.RootElement.GetProperty("success").GetBoolean());
            var data = document.RootElement.GetProperty("data");
            Assert.True(data.GetProperty("dryRun").GetBoolean());
            AssertScope(data.GetProperty("preview"), "input-only", false);
            Assert.DoesNotContain("[Dry-Run]", result.Error);
            if (noun is "table" or "column" or "choice")
                Assert.Contains("deprecated", result.Error);
            else
                Assert.Empty(result.Error);
        }
        else
        {
            Assert.Empty(result.Out);
            Assert.Contains("Input validation only", result.Error);
            Assert.Contains("target existence", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("dependencies", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("deletion feasibility", result.Error);
            Assert.Contains("not checked", result.Error);
        }
    }

    [Theory]
    [InlineData("global-option", false)]
    [InlineData("global-option", true)]
    [InlineData("local-option", false)]
    [InlineData("local-option", true)]
    public async Task OptionRemoval_ReportsExistenceOnlyAndNeverWrites(string noun, bool json)
    {
        using var fixture = new Fixture();
        var result = await fixture.RunAsync(noun, true, json);
        Assert.Equal(ExitCodes.Success, result.Exit);
        Assert.Single(fixture.Requests);
        Assert.IsAssignableFrom<OrganizationRequest>(fixture.Requests[0]);
        Assert.True(fixture.Requests[0] is RetrieveOptionSetRequest or RetrieveAttributeRequest);
        Assert.DoesNotContain("can be removed", result.Error);
        if (json)
        {
            using var document = JsonDocument.Parse(result.Out);
            var data = document.RootElement.GetProperty("data");
            Assert.False(data.GetProperty("removed").GetBoolean());
            Assert.True(data.GetProperty("dryRun").GetBoolean());
            AssertScope(data.GetProperty("preview"), "option-existence", true);
            Assert.Empty(result.Error);
        }
        else
        {
            Assert.Empty(result.Out);
            Assert.Contains("Option exists", result.Error);
            Assert.Contains("dependencies", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not checked", result.Error);
        }
    }

    [Theory]
    [InlineData("entity")]
    [InlineData("attribute")]
    [InlineData("relationship")]
    [InlineData("optionset")]
    [InlineData("key")]
    [InlineData("table")]
    [InlineData("column")]
    [InlineData("choice")]
    [InlineData("global-option")]
    [InlineData("local-option")]
    public void PreviewHelp_StatesLimitations(string noun)
    {
        using var fixture = new Fixture();
        var help = fixture.Command(noun).Options.Single(o => o.Name == "--dry-run").Description!;
        Assert.Contains("dependencies", help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not checked", help);
        Assert.DoesNotContain("Show dependencies", help);
    }

    [Theory]
    [InlineData("global-option")]
    [InlineData("local-option")]
    public async Task MissingOption_RemainsValidationFailure(string noun)
    {
        using var fixture = new Fixture { IncludeOption = false };
        var result = await fixture.RunAsync(noun, true, true);
        Assert.Equal(ExitCodes.ValidationError, result.Exit);
        using var document = JsonDocument.Parse(result.Out);
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("OPTION_NOT_FOUND", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("[Dry-Run]", result.Error);
        Assert.Single(fixture.Requests);
        Assert.True(fixture.Requests[0] is RetrieveOptionSetRequest or RetrieveAttributeRequest);
    }

    [Fact]
    public async Task SchemaDelete_MissingInput_RemainsValidationFailure()
    {
        using var fixture = new Fixture();
        var result = await fixture.RunAsync("entity", true, true, blankSolution: true);
        Assert.Equal(ExitCodes.ValidationError, result.Exit);
        using var document = JsonDocument.Parse(result.Out);
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("MISSING_REQUIRED_FIELD", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        fixture.Pool.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("entity", "DeleteEntity")]
    [InlineData("table", "DeleteEntity")]
    [InlineData("attribute", "DeleteAttribute")]
    [InlineData("column", "DeleteAttribute")]
    [InlineData("relationship", "DeleteRelationship")]
    [InlineData("optionset", "DeleteOptionSet")]
    [InlineData("choice", "DeleteOptionSet")]
    [InlineData("key", "DeleteEntityKey")]
    public async Task SchemaDelete_RealFlowRetainsDeleteResult(string noun, string requestName)
    {
        using var fixture = new Fixture();
        var result = await fixture.RunAsync(noun, false, true);
        Assert.Equal(ExitCodes.Success, result.Exit);
        Assert.Equal(requestName, Assert.Single(fixture.Requests).RequestName);
        using var document = JsonDocument.Parse(result.Out);
        var data = document.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("deleted").GetBoolean());
        Assert.False(data.TryGetProperty("preview", out _));
    }

    [Fact]
    public async Task SchemaDelete_WithoutForceStillRequiresConfirmation()
    {
        using var fixture = new Fixture();
        // The test runner has redirected stdin; if a runner supplies a terminal, reject the prompt.
        var result = await fixture.RunAsync("entity", false, true, force: false);
        Assert.Equal(Console.IsInputRedirected ? ExitCodes.ConfirmationRequired : ExitCodes.Success, result.Exit);
        fixture.Pool.VerifyNoOtherCalls();
        if (Console.IsInputRedirected)
        {
            using var document = JsonDocument.Parse(result.Out);
            Assert.Equal("CONFIRMATION_REQUIRED", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    private static void AssertScope(JsonElement scope, string expected, bool existence)
    {
        Assert.Equal(expected, scope.GetProperty("validationScope").GetString());
        Assert.Equal(existence, scope.GetProperty("targetExistenceChecked").GetBoolean());
        Assert.False(scope.GetProperty("dependenciesChecked").GetBoolean());
        Assert.False(scope.GetProperty("deletionFeasibilityChecked").GetBoolean());
    }

    private sealed class Fixture : IDisposable
    {
        public Mock<IDataverseConnectionPool> Pool { get; } = new(MockBehavior.Strict);
        public Mock<IPooledClient> Client { get; } = new(MockBehavior.Loose);
        public List<OrganizationRequest> Requests { get; } = [];
        public bool IncludeOption { get; init; } = true;

        private Task<ServiceProvider> Provider(CancellationToken ct)
        {
            Pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(Client.Object);
            Client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                .Returns<OrganizationRequest, CancellationToken>((request, token) =>
                {
                    Requests.Add(request);
                    var options = new OptionSetMetadata();
                    if (IncludeOption) options.Options.Add(new OptionMetadata(new Label("Existing", 1033), 42));
                    OrganizationResponse response = request switch
                    {
                        RetrieveOptionSetRequest => new RetrieveOptionSetResponse { Results = new ParameterCollection { ["OptionSetMetadata"] = options } },
                        RetrieveAttributeRequest => new RetrieveAttributeResponse { Results = new ParameterCollection { ["AttributeMetadata"] = new PicklistAttributeMetadata { OptionSet = options } } },
                        _ => new OrganizationResponse()
                    };
                    return Task.FromResult(response);
                });
            var services = new ServiceCollection();
            services.AddSingleton<IMetadataAuthoringService>(new DataverseMetadataAuthoringService(Pool.Object, new SchemaValidator(), new InactiveFakeShakedownGuard()));
            services.AddSingleton(new ResolvedConnectionInfo { Profile = new AuthProfile { Name = "offline-test" }, EnvironmentUrl = "https://example.invalid" });
            return Task.FromResult(services.BuildServiceProvider());
        }

        public Command Command(string noun) => noun switch
        {
            "entity" => EntityCommand.CreateDeleteCommand(Provider),
            "table" => TableCommandGroup.CreateDeleteCommand(Provider),
            "attribute" => AttributeCommandGroup.CreateDeleteCommand(Provider),
            "column" => ColumnCommandGroup.CreateDeleteCommand(Provider),
            "relationship" => RelationshipCommandGroup.CreateDeleteCommand(Provider),
            "optionset" => OptionSetCommand.CreateDeleteCommand(Provider),
            "choice" => ChoiceCommandGroup.CreateDeleteCommand(Provider),
            "key" => KeyCommandGroup.CreateDeleteCommand(Provider),
            "global-option" => OptionSetCommand.CreateRemoveOptionCommand(Provider),
            "local-option" => AttributeCommandGroup.CreateRemoveOptionCommand(Provider),
            _ => throw new ArgumentOutOfRangeException(nameof(noun))
        };

        public async Task<(int Exit, string Out, string Error)> RunAsync(string noun, bool dryRun, bool json, bool blankSolution = false, bool force = true)
        {
            var args = new List<string> { "--solution", blankSolution ? " " : "UnverifiedSolution" };
            if (noun is "entity" or "table" or "attribute" or "column" or "key" or "local-option") args.AddRange(["--entity", "unverified_entity"]);
            if (noun is "attribute" or "column" or "local-option") args.AddRange(["--column", "unverified_column"]);
            if (noun is "relationship" or "optionset" or "choice" or "key" or "global-option") args.AddRange(["--name", "unverified_name"]);
            if (noun is "global-option" or "local-option") args.AddRange(["--value", "42"]);
            if (dryRun) args.Add("--dry-run");
            else if (force) args.Add("--force");
            if (json) args.AddRange(["--output-format", "json"]);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            using var stdin = new StringReader("no\n");
            var oldOut = Console.Out; var oldError = Console.Error; var oldIn = Console.In;
            try
            {
                Console.SetOut(stdout); Console.SetError(stderr); Console.SetIn(stdin);
                var parsed = Command(noun).Parse(args.ToArray());
                Assert.Empty(parsed.Errors);
                var exit = await parsed.InvokeAsync();
                return (exit, stdout.ToString(), stderr.ToString());
            }
            finally { Console.SetOut(oldOut); Console.SetError(oldError); Console.SetIn(oldIn); }
        }

        public void Dispose() { }
    }
}
