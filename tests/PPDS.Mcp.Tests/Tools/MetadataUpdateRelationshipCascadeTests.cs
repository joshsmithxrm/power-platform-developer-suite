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
/// #1919 through the MCP tool and the shared authoring service: a cascade change on a parental or a mixed relationship
/// changes that setting only, and the result lists every setting before and after; text that names no valid value,
/// and a request with nothing given, are refused before anything is read.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Issue", "1919")]
public sealed class MetadataUpdateRelationshipCascadeTests
{
    /// <summary>The fixtures: a parental relationship (all Cascade) and a mixed one, as the eight values.</summary>
    private static readonly Dictionary<string, CascadeType[]> Kinds = new()
    {
        // Assign, Delete, Merge, Reparent, Share, Unshare, Archive, RollupView
        ["parental"] = [CascadeType.Cascade, CascadeType.Cascade, CascadeType.Cascade, CascadeType.Cascade, CascadeType.Cascade, CascadeType.Cascade, CascadeType.Cascade, CascadeType.Cascade],
        ["mixed"] = [CascadeType.UserOwned, CascadeType.Restrict, CascadeType.NoCascade, CascadeType.Active, CascadeType.Cascade, CascadeType.NoCascade, CascadeType.RemoveLink, CascadeType.Active],
    };

    private static readonly string[] Eight = ["Assign", "Delete", "Merge", "Reparent", "Share", "Unshare", "Archive", "RollupView"];

    [Theory]
    [InlineData("parental", "Restrict", null, false)]
    [InlineData("parental", "Restrict", null, true)]
    [InlineData("parental", null, "UserOwned", false)]
    [InlineData("parental", null, "Active", true)]
    [InlineData("parental", "RemoveLink", "NoCascade", false)]
    [InlineData("parental", "restrict", " useROWNED ", false)] // names in any case, spaces trimmed
    [InlineData("mixed", "Cascade", null, false)]
    [InlineData("mixed", "RemoveLink", null, true)]
    [InlineData("mixed", null, "Cascade", false)]
    [InlineData("mixed", null, "NoCascade", false)]
    [InlineData("mixed", "Cascade", "Active", false)]
    [InlineData("mixed", "Cascade", "UserOwned", false)] // assign already UserOwned: only delete changes
    [InlineData("mixed", "Restrict", "UserOwned", false)] // both already current: nothing to change, nothing sent
    [InlineData("mixed", "restrict", null, true)]
    public async Task GivenSettings_ChangeOnly_TheOthersAreSentAsRead(string kind, string? cascadeDelete, string? cascadeAssign, bool dryRun)
    {
        using var fx = await Fixture.CreateAsync(Kinds[kind]);

        var result = await new MetadataUpdateRelationshipTool(fx.Context).ExecuteAsync("Sol", "new_parent_child",
            cascadeDelete: cascadeDelete, cascadeAssign: cascadeAssign, dryRun: dryRun);

        var expected = Kinds[kind].ToArray();
        if (cascadeDelete != null) expected[1] = Enum.Parse<CascadeType>(cascadeDelete.Trim(), ignoreCase: true);
        if (cascadeAssign != null) expected[0] = Enum.Parse<CascadeType>(cascadeAssign.Trim(), ignoreCase: true);
        var changes = !expected.SequenceEqual(Kinds[kind]);

        Assert.True(result.Success);
        Assert.Equal(dryRun, result.WasDryRun);
        Assert.Equal(!dryRun && changes, result.Updated);
        Assert.Equal(!changes, result.NothingToChange);
        Assert.Equal(["Assign", "Delete", "Merge", "Reparent", "Share", "Unshare"], result.CascadeSettings.Select(s => s.Setting));
        foreach (var s in result.CascadeSettings)
        {
            var i = Array.IndexOf(Eight, s.Setting);
            Assert.Equal(Kinds[kind][i].ToString(), s.Before);
            Assert.Equal(expected[i].ToString(), s.After);
            Assert.Equal((s.Setting == "Delete" && cascadeDelete != null) || (s.Setting == "Assign" && cascadeAssign != null), s.Requested);
            Assert.Equal(Kinds[kind][i] != expected[i], s.Changed);
        }

        Assert.Single(fx.Reads);
        if (dryRun || !changes)
        {
            Assert.Empty(fx.Sent);
            return;
        }
        var sent = Assert.Single(fx.Sent);
        Assert.Same(fx.Stored, sent.Relationship);
        Assert.Equal("Sol", sent.SolutionUniqueName);
        Assert.True(sent.MergeLabels);
        var c = ((OneToManyRelationshipMetadata)sent.Relationship).CascadeConfiguration;
        Assert.Equal(expected, new[] { c.Assign, c.Delete, c.Merge, c.Reparent, c.Share, c.Unshare, c.Archive, c.RollupView }.Select(v => v!.Value));
    }

    [Theory]
    [InlineData("NoCascade", null, "Cascade, RemoveLink or Restrict")]
    [InlineData("5", null, "Cascade, RemoveLink or Restrict")]
    [InlineData("0", null, "Cascade, RemoveLink or Restrict")]
    [InlineData("+5", null, "Cascade, RemoveLink or Restrict")]
    [InlineData("05", null, "Cascade, RemoveLink or Restrict")]
    [InlineData("Active,RemoveLink", null, "Cascade, RemoveLink or Restrict")]
    [InlineData("", null, "Cascade, RemoveLink or Restrict")]
    [InlineData(null, "Active,NoCascade", "Active, Cascade, NoCascade or UserOwned")]
    [InlineData(null, "3", "Active, Cascade, NoCascade or UserOwned")]
    [InlineData(null, " ", "Active, Cascade, NoCascade or UserOwned")]
    [InlineData(null, "Restrict", "Active, Cascade, NoCascade or UserOwned")]
    [InlineData(null, "RemoveLink", "Active, Cascade, NoCascade or UserOwned")]
    public async Task ValueNotValidForTheAction_IsRefused_ListingOnlyThatActionsValues(string? cascadeDelete, string? cascadeAssign, string listed)
    {
        using var fx = await Fixture.CreateAsync(Kinds["mixed"]);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => new MetadataUpdateRelationshipTool(fx.Context)
            .ExecuteAsync("Sol", "new_parent_child", cascadeDelete: cascadeDelete, cascadeAssign: cascadeAssign));

        Assert.Contains("Valid values: " + listed + ".", ex.Message);
        Assert.Empty(fx.Reads);
        Assert.Empty(fx.Sent);
    }

    [Fact]
    public async Task NothingGiven_NamesOnlyTheParametersTheToolHas()
    {
        using var fx = await Fixture.CreateAsync(Kinds["mixed"]);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => new MetadataUpdateRelationshipTool(fx.Context).ExecuteAsync("Sol", "new_parent_child"));

        Assert.Contains("cascadeDelete", ex.Message);
        Assert.Contains("cascadeAssign", ex.Message);
        foreach (var other in new[] { "Merge", "Reparent", "Share", "Unshare", "hierarchical" })
            Assert.DoesNotContain(other, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fx.Reads);
    }

    [Fact]
    public void Result_HasNoInheritedAccessFields_TheToolCannotChangeReparentOrShare()
    {
        var names = typeof(MetadataUpdateRelationshipResult).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain(names, n => n.Contains("InheritedAccess", StringComparison.Ordinal));
        var parameters = typeof(MetadataUpdateRelationshipTool).GetMethod(nameof(MetadataUpdateRelationshipTool.ExecuteAsync))!.GetParameters().Select(p => p.Name);
        Assert.DoesNotContain(parameters, n => n!.Contains("Reparent", StringComparison.OrdinalIgnoreCase) || n.Contains("Share", StringComparison.OrdinalIgnoreCase));
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

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "ppds-cascade-1919-" + Guid.NewGuid().ToString("N"));
        private ProfileStore? _store;
        internal McpToolContext Context { get; private set; } = null!;
        internal List<RetrieveRelationshipRequest> Reads { get; } = [];
        internal List<SdkUpdateRelationshipRequest> Sent { get; } = [];
        internal OneToManyRelationshipMetadata Stored { get; private init; } = null!;

        internal static async Task<Fixture> CreateAsync(CascadeType[] v)
        {
            var fx = new Fixture
            {
                Stored = new OneToManyRelationshipMetadata
                {
                    SchemaName = "new_parent_child", MetadataId = new Guid("6f1d7c3e-2a4b-4c5d-8e9f-0a1b2c3d4e5f"),
                    CascadeConfiguration = new CascadeConfiguration
                    {
                        Assign = v[0], Delete = v[1], Merge = v[2], Reparent = v[3], Share = v[4], Unshare = v[5], Archive = v[6], RollupView = v[7]
                    }
                }
            };
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
