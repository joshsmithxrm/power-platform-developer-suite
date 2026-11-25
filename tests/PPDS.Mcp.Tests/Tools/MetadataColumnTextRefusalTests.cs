using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using ModelContextProtocol;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// #1919, review round 2, through the MCP column tools: required-level and format text that names nothing is refused
/// before anything is read, never sent as a default.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Issue", "1919")]
public sealed class MetadataColumnTextRefusalTests
{
    [Theory]
    [InlineData("Requird")]
    [InlineData("Mandatory")]
    [InlineData("")]
    [InlineData("2")]
    public async Task UpdateColumn_RequiredLevelTypo_IsRefused_AndNothingIsRead(string text)
    {
        using var fx = await Fixture.CreateAsync();

        var ex = await Assert.ThrowsAsync<McpException>(() => new MetadataUpdateColumnTool(fx.Context)
            .ExecuteAsync("Sol", "new_table", "new_thing", requiredLevel: text));

        Assert.Contains(MetadataErrorCodes.InvalidConstraint, ex.Message);
        Assert.Contains("None, Recommended, Required, ApplicationRequired or SystemRequired", ex.Message);
        Assert.Empty(fx.Requests);
    }

    [Theory]
    [InlineData("String", "Emial")]
    [InlineData("Integer", "Durration")]
    [InlineData("DateTime", "DateOnyl")]
    [InlineData("Integer", "Email")]
    public async Task AddColumn_FormatTypo_IsRefused_AndNothingIsRead(string type, string format)
    {
        using var fx = await Fixture.CreateAsync();

        var ex = await Assert.ThrowsAsync<McpException>(() => new MetadataAddColumnTool(fx.Context)
            .ExecuteAsync("Sol", "new_table", "new_Thing", "Thing", type, format: format));

        Assert.Contains(MetadataErrorCodes.InvalidConstraint, ex.Message);
        Assert.Contains($"a {type} column takes", ex.Message);
        Assert.Empty(fx.Requests);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "ppds-cascade-1919-" + Guid.NewGuid().ToString("N"));
        private ProfileStore? _store;
        internal McpToolContext Context { get; private set; } = null!;
        internal List<OrganizationRequest> Requests { get; } = [];

        internal static async Task<Fixture> CreateAsync()
        {
            var fx = new Fixture();
            try
            {
                await fx.InitializeAsync();
                return fx;
            }
            catch
            {
                fx.Dispose();
                throw;
            }
        }

        private async Task InitializeAsync()
        {
            var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
            pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(() =>
            {
                var client = new Mock<IPooledClient>(MockBehavior.Loose);
                client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                    .Returns<OrganizationRequest, CancellationToken>((request, _) =>
                    {
                        Requests.Add(request);
                        return Task.FromResult(new OrganizationResponse());
                    });
                return client.Object;
            });
            var service = new DataverseMetadataAuthoringService(pool.Object, new SchemaValidator(),
                new Mock<PPDS.Cli.Infrastructure.Safety.IShakedownGuard>().Object, baseLanguage: new FixedOrganizationBaseLanguage(1033));

            Directory.CreateDirectory(_directory);
            _store = new ProfileStore(Path.Combine(_directory, "profiles.json"));
            await _store.SaveAsync(new ProfileCollection
            {
                ActiveProfileIndex = 1,
                Profiles = [new AuthProfile { Index = 1, Name = "fixture", Environment = new() { Url = "https://fixture.example.test" } }]
            });
            Context = new McpToolContext(new Mock<IMcpConnectionPoolManager>(MockBehavior.Strict).Object, _store,
                new Mock<ISecureCredentialStore>(MockBehavior.Strict).Object, new McpSessionOptions())
            {
                ServiceProviderFactory = (_, _, _, _) => new ServiceCollection().AddSingleton<IMetadataAuthoringService>(service).BuildServiceProvider()
            };
            await Context.InitializeAsync();
        }

        public void Dispose()
        {
            Context?.Dispose();
            _store?.Dispose();
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
    }
}
