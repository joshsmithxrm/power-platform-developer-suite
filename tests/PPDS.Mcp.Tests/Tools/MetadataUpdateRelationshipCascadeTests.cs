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
    [InlineData(false)]
    [InlineData(true)]
    public async Task CascadeDeleteRestrict_OnParental_ChangesDeleteOnly(bool dryRun)
    {
        var sent = new List<SdkUpdateRelationshipRequest>();
        var stored = new OneToManyRelationshipMetadata
        {
            SchemaName = "new_parent_child",
            CascadeConfiguration = new CascadeConfiguration
            {
                Assign = CascadeType.Cascade, Delete = CascadeType.Cascade, Merge = CascadeType.Cascade,
                Reparent = CascadeType.Cascade, Share = CascadeType.Cascade, Unshare = CascadeType.Cascade
            }
        };
        var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
        pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            var client = new Mock<IPooledClient>(MockBehavior.Loose);
            client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                .Returns<OrganizationRequest, CancellationToken>((request, _) =>
                {
                    switch (request)
                    {
                        case RetrieveRelationshipRequest:
                            var response = new RetrieveRelationshipResponse();
                            response.Results["RelationshipMetadata"] = stored;
                            return Task.FromResult<OrganizationResponse>(response);
                        case SdkUpdateRelationshipRequest update:
                            sent.Add(update);
                            return Task.FromResult(new OrganizationResponse());
                        default:
                            throw new InvalidOperationException("Unexpected SDK request: " + request.RequestName);
                    }
                });
            return client.Object;
        });
        var service = new DataverseMetadataAuthoringService(pool.Object, new SchemaValidator(),
            new Mock<PPDS.Cli.Infrastructure.Safety.IShakedownGuard>().Object, baseLanguage: new FixedOrganizationBaseLanguage(1033));

        var directory = Path.Combine(Path.GetTempPath(), "ppds-cascade-1919-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new ProfileStore(Path.Combine(directory, "profiles.json"));
            await store.SaveAsync(new ProfileCollection
            {
                ActiveProfileIndex = 1,
                Profiles = [new AuthProfile { Index = 1, Name = "fixture", Environment = new() { Url = "https://fixture.example.test" } }]
            });
            using var context = new McpToolContext(new Mock<IMcpConnectionPoolManager>(MockBehavior.Strict).Object, store,
                new Mock<ISecureCredentialStore>(MockBehavior.Strict).Object, new McpSessionOptions())
            {
                ServiceProviderFactory = (_, _, _, _) => new ServiceCollection().AddSingleton<IMetadataAuthoringService>(service).BuildServiceProvider()
            };
            await context.InitializeAsync();

            var result = await new MetadataUpdateRelationshipTool(context).ExecuteAsync("Sol", "new_parent_child", cascadeDelete: "Restrict", dryRun: dryRun);

            Assert.True(result.Success);
            Assert.Equal(dryRun, result.WasDryRun);
            Assert.Equal(["Assign", "Delete", "Merge", "Reparent", "Share", "Unshare"], result.CascadeSettings.Select(s => s.Setting));
            var changed = Assert.Single(result.CascadeSettings, s => s.Changed);
            Assert.Equal(("Delete", "Cascade", "Restrict", true), (changed.Setting, changed.Before, changed.After, changed.Requested));
            Assert.All(result.CascadeSettings.Where(s => s.Setting != "Delete"), s => Assert.Equal(("Cascade", "Cascade", false), (s.Before, s.After, s.Requested)));

            if (dryRun)
            {
                Assert.Empty(sent);
                return;
            }
            var cascade = Assert.IsType<OneToManyRelationshipMetadata>(Assert.Single(sent).Relationship).CascadeConfiguration;
            Assert.Equal(CascadeType.Restrict, cascade.Delete);
            Assert.All(new[] { cascade.Assign, cascade.Merge, cascade.Reparent, cascade.Share, cascade.Unshare },
                value => Assert.Equal(CascadeType.Cascade, value));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
