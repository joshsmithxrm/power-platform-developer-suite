using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

// Real tool/serializer with synthetic profile metadata and intercepted service factory.
// This is not MCP transport or Dataverse proof. Strict stores/pool manager forbid auth.
[Trait("Category", "Unit")]
[Trait("AC", "AC-113")]
public sealed class MetadataCreateChoiceWarningTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualTool_PreservesServiceWarningsAndOptionPresence(bool dryRun)
    {
        var parent = Path.GetFullPath(Path.GetTempPath());
        var directory = Path.Combine(parent, "ppds-create-choice-warning-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new ProfileStore(Path.Combine(directory, "profiles.json"));
            var pools = new Mock<IMcpConnectionPoolManager>(MockBehavior.Strict);
            var credentials = new Mock<ISecureCredentialStore>(MockBehavior.Strict);
            var service = new Mock<IMetadataAuthoringService>(MockBehavior.Strict);
            using var cts = new CancellationTokenSource();
            CreateGlobalChoiceRequest? captured = null;
            var warning = new ValidationMessage { Field = "Options", Rule = "OptionValueOutsidePublisherRange", Message = "Explicit option value 0 is outside publisher range 718980000–718989999; preserved." };
            service.Setup(s => s.CreateGlobalChoiceAsync(It.IsAny<CreateGlobalChoiceRequest>(), null, It.IsAny<CancellationToken>()))
                .Returns<CreateGlobalChoiceRequest, IMetadataAuthoringProgressReporter?, CancellationToken>((request, _, token) =>
                {
                    Assert.Equal(cts.Token, token); captured = request;
                    return Task.FromResult(new CreateChoiceResult { Name = "new_Choice", WasDryRun = dryRun, ValidationMessages = [warning] });
                });
            var factoryCalls = 0;
            using var context = new McpToolContext(pools.Object, store, credentials.Object, new McpSessionOptions())
            {
                ServiceProviderFactory = (connection, _, _, _) =>
                {
                    Assert.Equal("https://fixture.example.test", connection.EnvironmentUrl); factoryCalls++;
                    return new ServiceCollection().AddSingleton(service.Object).BuildServiceProvider();
                }
            };
            await store.SaveAsync(new ProfileCollection { ActiveProfileIndex = 1, Profiles = [new AuthProfile { Index = 1, Name = "fixture", Environment = new() { Url = "https://fixture.example.test" } }] });
            await context.InitializeAsync();
            var result = await new MetadataCreateChoiceTool(context).ExecuteAsync("SyntheticSolution", "new_Choice", "Choice", "Description",
                "[{\"label\":\"Automatic\"},{\"label\":\"Zero\",\"value\":0}]", dryRun, cts.Token);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            var message = Assert.Single(json.RootElement.GetProperty("validationMessages").EnumerateArray());
            Assert.Equal(warning.Rule, message.GetProperty("rule").GetString());
            Assert.Equal(warning.Message, message.GetProperty("message").GetString());
            Assert.NotNull(captured); Assert.Equal(dryRun, captured.DryRun); Assert.Equal(2, captured.Options!.Length);
            using var omitted = JsonDocument.Parse(JsonSerializer.Serialize(captured.Options[0]));
            using var zero = JsonDocument.Parse(JsonSerializer.Serialize(captured.Options[1]));
            Assert.False(omitted.RootElement.TryGetProperty("Value", out _));
            Assert.Equal(0, zero.RootElement.GetProperty("Value").GetInt32());
            Assert.Equal(1, factoryCalls);
            service.Verify(s => s.CreateGlobalChoiceAsync(It.IsAny<CreateGlobalChoiceRequest>(), null, cts.Token), Times.Once);
            service.VerifyNoOtherCalls(); pools.VerifyNoOtherCalls(); credentials.VerifyNoOtherCalls();
        }
        finally
        {
            Assert.Equal(Path.TrimEndingDirectorySeparator(parent), Path.GetDirectoryName(directory));
            Assert.StartsWith("ppds-create-choice-warning-", Path.GetFileName(directory));
            Directory.Delete(directory, recursive: true); // Only the unique child created by this fixture.
        }
    }
}
