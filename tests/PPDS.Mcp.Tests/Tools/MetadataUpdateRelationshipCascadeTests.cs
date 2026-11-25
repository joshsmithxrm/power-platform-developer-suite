using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;
using SdkUpdateRelationshipRequest = Microsoft.Xrm.Sdk.Messages.UpdateRelationshipRequest;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// #1919 through the MCP tool and the shared authoring service: a cascade-delete change on a parental relationship
/// changes that setting only, and the result lists every setting before and after.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Issue", "1919")]
public sealed class MetadataUpdateRelationshipCascadeTests
{
    [Theory]
    [InlineData("Restrict", null, false)]
    [InlineData("Restrict", null, true)]
    [InlineData(null, "UserOwned", false)]
    [InlineData(null, "Active", true)]
    [InlineData("RemoveLink", "NoCascade", false)]
    [InlineData("restrict", "useROWNED", false)] // names in any case
    public async Task GivenSettings_ChangeOnly_TheOthersAreSentAsRead(string? cascadeDelete, string? cascadeAssign, bool dryRun)
    {
        using var fx = await Fixture.CreateAsync();

        var result = await new MetadataUpdateRelationshipTool(fx.Context).ExecuteAsync("Sol", "new_parent_child",
            cascadeDelete: cascadeDelete, cascadeAssign: cascadeAssign, dryRun: dryRun);

        var given = new Dictionary<string, string>(StringComparer.Ordinal);
        if (cascadeDelete != null) given["Delete"] = Canonical(cascadeDelete);
        if (cascadeAssign != null) given["Assign"] = Canonical(cascadeAssign);

        Assert.True(result.Success);
        Assert.Equal(dryRun, result.WasDryRun);
        Assert.Equal(["Assign", "Delete", "Merge", "Reparent", "Share", "Unshare"], result.CascadeSettings.Select(s => s.Setting));
        foreach (var s in result.CascadeSettings)
        {
            Assert.Equal("Cascade", s.Before);
            Assert.Equal(given.TryGetValue(s.Setting, out var v) ? v : "Cascade", s.After);
            Assert.Equal(given.ContainsKey(s.Setting), s.Requested);
        }
        Assert.False(result.RemovesInheritedAccess);
        Assert.Null(result.InheritedAccessNotice);

        if (dryRun)
        {
            Assert.Empty(fx.Sent);
            return;
        }
        var sent = Assert.Single(fx.Sent);
        Assert.Same(fx.Stored, sent.Relationship);
        var c = ((OneToManyRelationshipMetadata)sent.Relationship).CascadeConfiguration;
        var expected = new Dictionary<string, CascadeType?>
        {
            ["Assign"] = given.TryGetValue("Assign", out var a) ? Enum.Parse<CascadeType>(a) : CascadeType.Cascade,
            ["Delete"] = given.TryGetValue("Delete", out var d) ? Enum.Parse<CascadeType>(d) : CascadeType.Cascade,
            ["Merge"] = CascadeType.Cascade, ["Reparent"] = CascadeType.Cascade, ["Share"] = CascadeType.Cascade,
            ["Unshare"] = CascadeType.Cascade, ["Archive"] = CascadeType.Cascade, ["RollupView"] = CascadeType.Cascade,
        };
        Assert.Equal(expected, new Dictionary<string, CascadeType?>
        {
            ["Assign"] = c.Assign, ["Delete"] = c.Delete, ["Merge"] = c.Merge, ["Reparent"] = c.Reparent, ["Share"] = c.Share,
            ["Unshare"] = c.Unshare, ["Archive"] = c.Archive, ["RollupView"] = c.RollupView,
        });
    }

    [Theory]
    [InlineData("NoCascade", null, "Cascade, RemoveLink or Restrict")]
    [InlineData("5", null, "Cascade, RemoveLink or Restrict")]
    [InlineData(null, "Restrict", "Active, Cascade, NoCascade or UserOwned")]
    [InlineData(null, "RemoveLink", "Active, Cascade, NoCascade or UserOwned")]
    public async Task ValueNotValidForTheAction_IsRefused_ListingOnlyThatActionsValues(string? cascadeDelete, string? cascadeAssign, string listed)
    {
        using var fx = await Fixture.CreateAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => new MetadataUpdateRelationshipTool(fx.Context)
            .ExecuteAsync("Sol", "new_parent_child", cascadeDelete: cascadeDelete, cascadeAssign: cascadeAssign));

        Assert.Contains("Valid values: " + listed + ".", ex.Message);
        Assert.Empty(fx.Reads);
        Assert.Empty(fx.Sent);
    }

    [Fact]
    public void ParameterDescriptions_NameOnlyTheValuesValidForEachAction()
    {
        var method = typeof(MetadataUpdateRelationshipTool).GetMethod(nameof(MetadataUpdateRelationshipTool.ExecuteAsync))!;
        foreach (var (parameter, action) in new[] { ("cascadeDelete", "Delete"), ("cascadeAssign", "Assign") })
        {
            var description = method.GetParameters().Single(p => p.Name == parameter)
                .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                .Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;
            foreach (var value in Enum.GetValues<CascadeBehavior>())
            {
                if (CascadeActionRules.IsValid(action, value)) Assert.Contains($"'{value}'", description);
                else Assert.DoesNotContain($"'{value}'", description);
            }
        }
    }

    private static string Canonical(string value) => Enum.GetNames<CascadeBehavior>().Single(n => n.Equals(value, StringComparison.OrdinalIgnoreCase));

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "ppds-cascade-1919-" + Guid.NewGuid().ToString("N"));
        private ProfileStore? _store;
        internal McpToolContext Context { get; private set; } = null!;
        internal List<RetrieveRelationshipRequest> Reads { get; } = [];
        internal List<SdkUpdateRelationshipRequest> Sent { get; } = [];
        internal OneToManyRelationshipMetadata Stored { get; } = new()
        {
            SchemaName = "new_parent_child",
            CascadeConfiguration = new CascadeConfiguration
            {
                Assign = CascadeType.Cascade, Delete = CascadeType.Cascade, Merge = CascadeType.Cascade,
                Reparent = CascadeType.Cascade, Share = CascadeType.Cascade, Unshare = CascadeType.Cascade,
                Archive = CascadeType.Cascade, RollupView = CascadeType.Cascade
            }
        };

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
                        switch (request)
                        {
                            case RetrieveRelationshipRequest read:
                                Reads.Add(read);
                                var response = new RetrieveRelationshipResponse();
                                response.Results["RelationshipMetadata"] = Stored;
                                return Task.FromResult<OrganizationResponse>(response);
                            case SdkUpdateRelationshipRequest update:
                                Sent.Add(update);
                                return Task.FromResult(new OrganizationResponse());
                            default:
                                throw new InvalidOperationException("Unexpected SDK request: " + request.RequestName);
                        }
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
