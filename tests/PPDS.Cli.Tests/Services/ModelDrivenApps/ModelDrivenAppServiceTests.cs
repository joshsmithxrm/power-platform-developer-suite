using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.ModelDrivenApps;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.ModelDrivenApps;

/// <summary>
/// Behavioral unit tests for <see cref="ModelDrivenAppService"/>.
/// These exercise the real sitemap XDocument manipulation (add-table / remove-table)
/// and the component-option validation logic, with Dataverse I/O mocked via
/// <see cref="IDataverseConnectionPool"/> / <see cref="IPooledClient"/> following the
/// established <c>FormServiceTests</c> / <c>ViewServiceTests</c> pattern.
///
/// Constitution: assertions cover D4 (PpdsException + ErrorCode) and the sitemap XML
/// outputs written through UpdateAsync. No live Dataverse calls — fully deterministic.
/// </summary>
public class ModelDrivenAppServiceTests
{
    private static readonly Guid AppModuleId = new("aaaaaaaa-1111-1111-1111-111111111111");
    private static readonly Guid SitemapId = new("bbbbbbbb-2222-2222-2222-222222222222");
    private static readonly Guid AccountMetadataId = new("cccccccc-3333-3333-3333-333333333333");
    private static readonly Guid ContactMetadataId = new("dddddddd-4444-4444-4444-444444444444");

    private const string AppName = "My App";
    private const string AppUniqueName = "ppds_myapp";

    // A sitemap with one Area / one Group already containing the "account" entity.
    private const string SitemapWithAccount =
        @"<SiteMap><Area Id=""Area1"" Title=""Main""><Group Id=""Group1"" Title=""Core"">"
        + @"<SubArea Id=""sub_account"" Entity=""account"" Title=""Accounts"" /></Group></Area></SiteMap>";

    // An empty sitemap — no areas at all.
    private const string EmptySitemap = "<SiteMap />";

    // ── Mock harness ──────────────────────────────────────────────────────────

    private const string TestEnvUrl = "https://test.crm.dynamics.com/";

    private sealed class Harness
    {
        public Mock<IDataverseConnectionPool> Pool { get; } = new();
        public Mock<IPooledClient> Client { get; } = new();
        public Mock<ICachedMetadataProvider> Metadata { get; } = new();
        public Mock<IEnvironmentConfigService> EnvConfig { get; } = new();

        /// <summary>
        /// The environment type the mocked <see cref="IEnvironmentConfigService"/> reports. Defaults to
        /// Sandbox (Development protection) so write operations are not blocked. Flip to Production to
        /// exercise the #1195 write guard.
        /// </summary>
        public EnvironmentType EnvType { get; set; } = EnvironmentType.Sandbox;

        /// <summary>The XML written by the last UpdateAsync on the sitemap record, if any.</summary>
        public string? WrittenSitemapXml { get; private set; }

        /// <summary>All organization requests passed to ExecuteAsync (for Add/RemoveAppComponents assertions).</summary>
        public List<OrganizationRequest> ExecutedRequests { get; } = new();

        public ModelDrivenAppService Build()
        {
            // Resolve the env type lazily so a test can set EnvType before Build() (or anytime before the call).
            EnvConfig.Setup(e => e.GetConfigAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new EnvironmentConfig { Url = TestEnvUrl, Type = EnvType });

            var connectionInfo = new ResolvedConnectionInfo
            {
                Profile = new AuthProfile(),
                EnvironmentUrl = TestEnvUrl
            };

            return new ModelDrivenAppService(
                Pool.Object,
                Metadata.Object,
                new SitemapXmlValidator(new SitemapSchemaResources()),
                new NullLogger<ModelDrivenAppService>(),
                EnvConfig.Object,
                connectionInfo);
        }

        /// <summary>
        /// Wires the pool/client/metadata mocks for an add-table or remove-table flow
        /// against the supplied starting sitemap XML and entity metadata.
        /// </summary>
        public void Setup(string sitemapXml, IReadOnlyList<EntitySummary> entities)
        {
            Client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
            Client.Setup(c => c.Dispose());

            Pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Client.Object);

            Metadata.Setup(m => m.GetEntitiesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(entities);

            // ResolveAppAsync → appmodule query.
            Client.Setup(c => c.RetrieveMultipleAsync(
                    It.Is<QueryExpression>(qe => qe.EntityName == "appmodule"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EntityCollection(new List<Entity>
                {
                    new("appmodule")
                    {
                        ["appmoduleid"] = AppModuleId,
                        ["uniquename"] = AppUniqueName,
                        ["name"] = AppName
                    }
                }));

            // GetSitemapIdAsync / GetExplicitEntityComponentsAsync → appmodulecomponent query.
            // The sitemap lookup needs objectid == SitemapId; component lookups return empty
            // (no explicit forms/views/charts) so remove-table focuses on the entity component.
            Client.Setup(c => c.RetrieveMultipleAsync(
                    It.Is<QueryExpression>(qe => qe.EntityName == "appmodulecomponent"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((QueryBase q, CancellationToken _) =>
                {
                    var qe = (QueryExpression)q;
                    var isSitemapLookup = qe.Criteria.Conditions.Any(c =>
                        c.AttributeName == "componenttype"
                        && c.Values.Count == 1
                        && Convert.ToInt32(c.Values[0]) == 62);

                    if (isSitemapLookup)
                    {
                        return new EntityCollection(new List<Entity>
                        {
                            new("appmodulecomponent") { ["objectid"] = SitemapId }
                        });
                    }

                    return new EntityCollection(new List<Entity>());
                });

            // FetchSitemapXmlByIdAsync(unpublished: true) → RetrieveUnpublishedMultiple.
            Client.Setup(c => c.ExecuteAsync(
                    It.IsAny<OrganizationRequest>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((OrganizationRequest req, CancellationToken _) =>
                {
                    ExecutedRequests.Add(req);

                    if (req is RetrieveUnpublishedMultipleRequest)
                    {
                        return new RetrieveUnpublishedMultipleResponse
                        {
                            Results =
                            {
                                ["EntityCollection"] = new EntityCollection(new List<Entity>
                                {
                                    new("sitemap")
                                    {
                                        ["sitemapid"] = SitemapId,
                                        ["sitemapxml"] = sitemapXml
                                    }
                                })
                            }
                        };
                    }

                    return new OrganizationResponse();
                });

            // PatchSitemapAsync → capture the written XML.
            Client.Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
                .Callback<Entity, CancellationToken>((e, _) =>
                {
                    if (e.LogicalName == "sitemap" && e.Contains("sitemapxml"))
                    {
                        WrittenSitemapXml = (string)e["sitemapxml"];
                    }
                })
                .Returns(Task.CompletedTask);
        }
    }

    private static EntitySummary Entity(string logical, string display, Guid metadataId) => new()
    {
        LogicalName = logical,
        DisplayName = display,
        SchemaName = logical,
        MetadataId = metadataId
    };

    private static readonly IReadOnlyList<EntitySummary> DefaultEntities = new[]
    {
        Entity("account", "Accounts", AccountMetadataId),
        Entity("contact", "Contacts", ContactMetadataId)
    };

    // ── add-table: append SubArea to an existing group ─────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddTable_ExistingGroup_AddsSubAreaWithDisplayName()
    {
        var h = new Harness();
        h.Setup(SitemapWithAccount, DefaultEntities);
        var service = h.Build();

        await service.AddTableAsync(
            AppName,
            new[] { "contact" },
            new AddTableOptions(Group: null, Area: null, Title: null, Solution: null, Publish: false),
            progress: null,
            CancellationToken.None);

        h.WrittenSitemapXml.Should().NotBeNull();
        var doc = XDocument.Parse(h.WrittenSitemapXml!);

        // Existing account SubArea is preserved; contact is appended to the same group.
        var subAreas = doc.Descendants("SubArea").ToList();
        subAreas.Should().HaveCount(2);

        var contactSub = subAreas.Single(s => (string?)s.Attribute("Entity") == "contact");
        contactSub.Attribute("Title")!.Value.Should().Be("Contacts"); // display name
        // Appended to the pre-existing group, not a new one.
        doc.Descendants("Group").Should().HaveCount(1);
        contactSub.Parent!.Attribute("Id")!.Value.Should().Be("Group1");
    }

    // ── add-table: create area + group when sitemap is empty ───────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddTable_EmptySitemap_CreatesAreaAndGroup()
    {
        var h = new Harness();
        h.Setup(EmptySitemap, DefaultEntities);
        var service = h.Build();

        await service.AddTableAsync(
            AppName,
            new[] { "account" },
            new AddTableOptions(Group: null, Area: null, Title: null, Solution: null, Publish: false),
            progress: null,
            CancellationToken.None);

        var doc = XDocument.Parse(h.WrittenSitemapXml!);
        doc.Descendants("Area").Should().HaveCount(1);
        doc.Descendants("Group").Should().HaveCount(1);
        var sub = doc.Descendants("SubArea").Single();
        sub.Attribute("Entity")!.Value.Should().Be("account");
        sub.Attribute("Title")!.Value.Should().Be("Accounts");
    }

    // ── add-table: named Group/Area is created when absent ─────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddTable_NamedGroupAndArea_CreatesThemWhenAbsent()
    {
        var h = new Harness();
        h.Setup(EmptySitemap, DefaultEntities);
        var service = h.Build();

        await service.AddTableAsync(
            AppName,
            new[] { "account" },
            new AddTableOptions(Group: "Sales", Area: "CRM", Title: null, Solution: null, Publish: false),
            progress: null,
            CancellationToken.None);

        var doc = XDocument.Parse(h.WrittenSitemapXml!);
        var area = doc.Descendants("Area").Single();
        area.Attribute("Title")!.Value.Should().Be("CRM");
        var group = area.Descendants("Group").Single();
        group.Attribute("Title")!.Value.Should().Be("Sales");
        group.Descendants("SubArea").Single().Attribute("Entity")!.Value.Should().Be("account");
    }

    // ── add-table: --title applies to FIRST entity only (fix d) ────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddTable_TitleAppliesToFirstEntityOnly()
    {
        var h = new Harness();
        h.Setup(EmptySitemap, DefaultEntities);
        var service = h.Build();

        await service.AddTableAsync(
            AppName,
            new[] { "account", "contact" },
            new AddTableOptions(Group: null, Area: null, Title: "Custom Title", Solution: null, Publish: false),
            progress: null,
            CancellationToken.None);

        var doc = XDocument.Parse(h.WrittenSitemapXml!);
        var subs = doc.Descendants("SubArea").ToDictionary(
            s => (string)s.Attribute("Entity")!,
            s => (string)s.Attribute("Title")!);

        subs["account"].Should().Be("Custom Title");   // first entity → --title
        subs["contact"].Should().Be("Contacts");        // subsequent → display name
    }

    // ── add-table: XML-injection safety — special chars are escaped (fix a) ────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddTable_TitleWithXmlSpecialChars_IsEscaped()
    {
        var h = new Harness();
        h.Setup(EmptySitemap, DefaultEntities);
        var service = h.Build();

        await service.AddTableAsync(
            AppName,
            new[] { "account" },
            new AddTableOptions(Group: null, Area: null, Title: "A & B <\"injected\"/>", Solution: null, Publish: false),
            progress: null,
            CancellationToken.None);

        // Raw written XML must contain escaped entities, never a raw injected element.
        h.WrittenSitemapXml.Should().NotContain("<\"injected\"");
        // Round-trips back to the exact literal value (proves attribute auto-escaping).
        var doc = XDocument.Parse(h.WrittenSitemapXml!);
        doc.Descendants("SubArea").Single().Attribute("Title")!.Value
            .Should().Be("A & B <\"injected\"/>");
    }

    // ── add-table: duplicate entity throws EntityAlreadyInApp ──────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddTable_EntityAlreadyInApp_ThrowsPpdsException()
    {
        var h = new Harness();
        h.Setup(SitemapWithAccount, DefaultEntities); // account already present
        var service = h.Build();

        var act = async () => await service.AddTableAsync(
            AppName,
            new[] { "account" },
            new AddTableOptions(Group: null, Area: null, Title: null, Solution: null, Publish: false),
            progress: null,
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ModelDrivenAppErrorCodes.EntityAlreadyInApp);
    }

    // ── add-table: unknown entity throws EntityNotFound ────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddTable_UnknownEntity_ThrowsEntityNotFound()
    {
        var h = new Harness();
        h.Setup(EmptySitemap, DefaultEntities);
        var service = h.Build();

        var act = async () => await service.AddTableAsync(
            AppName,
            new[] { "does_not_exist" },
            new AddTableOptions(Group: null, Area: null, Title: null, Solution: null, Publish: false),
            progress: null,
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ModelDrivenAppErrorCodes.EntityNotFound);
    }

    // ── remove-table: SubArea removed from sitemap XML ─────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveTable_RemovesSubAreaFromSitemap()
    {
        var h = new Harness();
        h.Setup(SitemapWithAccount, DefaultEntities);
        var service = h.Build();

        await service.RemoveTableAsync(
            AppName,
            "account",
            new ModifyOptions(Solution: null, Publish: false),
            progress: null,
            CancellationToken.None);

        var doc = XDocument.Parse(h.WrittenSitemapXml!);
        doc.Descendants("SubArea")
            .Any(s => string.Equals((string?)s.Attribute("Entity"), "account", StringComparison.OrdinalIgnoreCase))
            .Should().BeFalse();
    }

    // ── remove-table: entity component (type 1) removed via RemoveAppComponents (fix c) ──

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveTable_RemovesEntityComponentViaRemoveAppComponents()
    {
        var h = new Harness();
        h.Setup(SitemapWithAccount, DefaultEntities);
        var service = h.Build();

        await service.RemoveTableAsync(
            AppName,
            "account",
            new ModifyOptions(Solution: null, Publish: false),
            progress: null,
            CancellationToken.None);

        // A RemoveAppComponents request must target the "entity" component by metadata ID.
        var removeReqs = h.ExecutedRequests
            .Where(r => r.RequestName == "RemoveAppComponents")
            .ToList();
        removeReqs.Should().NotBeEmpty();

        var entityRefRemoved = removeReqs
            .Select(r => r["Components"] as EntityReferenceCollection)
            .Where(c => c != null)
            .SelectMany(c => c!)
            .Any(er => er.LogicalName == "entity" && er.Id == AccountMetadataId);

        entityRefRemoved.Should().BeTrue();
    }

    // ── remove-table: entity not in app throws EntityNotInApp ──────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveTable_EntityNotInApp_ThrowsPpdsException()
    {
        var h = new Harness();
        h.Setup(SitemapWithAccount, DefaultEntities);
        var service = h.Build();

        var act = async () => await service.RemoveTableAsync(
            AppName,
            "contact", // not present in SitemapWithAccount
            new ModifyOptions(Solution: null, Publish: false),
            progress: null,
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ModelDrivenAppErrorCodes.EntityNotInApp);
    }

    // ── set-forms/views/charts: --all XOR named component validation (fix e) ───

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetForms_AllAndNamedComponent_ThrowsInvalidArguments()
    {
        var h = new Harness();
        h.Setup(SitemapWithAccount, DefaultEntities);
        var service = h.Build();

        var options = new ComponentSelectionOptions(
            All: true,
            ComponentNames: new[] { "Main Form" },
            Solution: null,
            Publish: false);

        var act = async () => await service.SetFormsAsync(
            AppName, "account", options, progress: null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ModelDrivenAppErrorCodes.InvalidArguments);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetViews_NeitherAllNorNamed_ThrowsInvalidArguments()
    {
        var h = new Harness();
        h.Setup(SitemapWithAccount, DefaultEntities);
        var service = h.Build();

        var options = new ComponentSelectionOptions(
            All: false,
            ComponentNames: Array.Empty<string>(),
            Solution: null,
            Publish: false);

        var act = async () => await service.SetViewsAsync(
            AppName, "account", options, progress: null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ModelDrivenAppErrorCodes.InvalidArguments);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetCharts_AllAndNamedComponent_ThrowsInvalidArguments()
    {
        var h = new Harness();
        h.Setup(SitemapWithAccount, DefaultEntities);
        var service = h.Build();

        var options = new ComponentSelectionOptions(
            All: true,
            ComponentNames: new[] { "By Owner" },
            Solution: null,
            Publish: false);

        var act = async () => await service.SetChartsAsync(
            AppName, "account", options, progress: null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ModelDrivenAppErrorCodes.InvalidArguments);
    }

    // ── set-views: AddAppComponents passes AppId as Guid, not EntityReference (#1183) ──

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetViews_NamedView_AddAppComponentsPassesAppIdAsGuid()
    {
        var h = new Harness();
        h.Setup(SitemapWithAccount, DefaultEntities);

        var viewId = new Guid("eeeeeeee-5555-5555-5555-555555555555");
        const string viewName = "Active Accounts";

        // ResolveViewIdsAsync → savedquery lookup returns the named view to add.
        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "savedquery"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>
            {
                new("savedquery") { ["savedqueryid"] = viewId, ["name"] = viewName }
            }));

        var service = h.Build();

        // Explicit named view (NOT --all) — this is the only path that hits the add helper.
        var options = new ComponentSelectionOptions(
            All: false,
            ComponentNames: new[] { viewName },
            Solution: null,
            Publish: false);

        await service.SetViewsAsync(
            AppName, "account", options, progress: null, CancellationToken.None);

        // The AddAppComponents message must type AppId as the appmodule Guid. Passing an
        // EntityReference triggers Dataverse's "Input field type 'EntityReference' does not
        // match expected type 'Guid' for field 'AppId'" — the #1183 regression.
        var addReq = h.ExecutedRequests.Single(r => r.RequestName == "AddAppComponents");

        addReq["AppId"].Should().BeOfType<Guid>()
            .Which.Should().Be(AppModuleId);

        // The components themselves are still EntityReferences to the savedquery rows.
        addReq["Components"].Should().BeOfType<EntityReferenceCollection>()
            .Which.Should().ContainSingle(er => er.LogicalName == "savedquery" && er.Id == viewId);
    }

    // ── ResolveAppAsync: app not found throws AppNotFound (D4) ──────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveTable_AppNotFound_ThrowsAppNotFound()
    {
        var h = new Harness();
        h.Setup(SitemapWithAccount, DefaultEntities);
        // Override appmodule lookup to return nothing.
        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "appmodule"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>()));
        var service = h.Build();

        var act = async () => await service.RemoveTableAsync(
            "Ghost App",
            "account",
            new ModifyOptions(Solution: null, Publish: false),
            progress: null,
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ModelDrivenAppErrorCodes.AppNotFound);
    }

    // ── Constructor guards ─────────────────────────────────────────────────────

    private static ResolvedConnectionInfo TestConnectionInfo() => new()
    {
        Profile = new AuthProfile(),
        EnvironmentUrl = TestEnvUrl
    };

    [Fact]
    [Trait("Category", "Unit")]
    public void Constructor_ThrowsOnNullPool()
    {
        var act = () => new ModelDrivenAppService(
            null!,
            new Mock<ICachedMetadataProvider>().Object,
            new SitemapXmlValidator(new SitemapSchemaResources()),
            new NullLogger<ModelDrivenAppService>(),
            new Mock<IEnvironmentConfigService>().Object,
            TestConnectionInfo());

        act.Should().Throw<ArgumentNullException>().WithParameterName("pool");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Constructor_ThrowsOnNullMetadata()
    {
        var act = () => new ModelDrivenAppService(
            new Mock<IDataverseConnectionPool>().Object,
            null!,
            new SitemapXmlValidator(new SitemapSchemaResources()),
            new NullLogger<ModelDrivenAppService>(),
            new Mock<IEnvironmentConfigService>().Object,
            TestConnectionInfo());

        act.Should().Throw<ArgumentNullException>().WithParameterName("metadata");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Constructor_ThrowsOnNullEnvConfig()
    {
        var act = () => new ModelDrivenAppService(
            new Mock<IDataverseConnectionPool>().Object,
            new Mock<ICachedMetadataProvider>().Object,
            new SitemapXmlValidator(new SitemapSchemaResources()),
            new NullLogger<ModelDrivenAppService>(),
            null!,
            TestConnectionInfo());

        act.Should().Throw<ArgumentNullException>().WithParameterName("envConfig");
    }

    // ── Copilot wiring (add/remove/list) ───────────────────────────────────────

    private static readonly Guid CopilotBotId = new("eeeeeeee-6666-6666-6666-666666666666");
    private const string CopilotBotName = "Member Summary Assistant";
    private const string CopilotBotSchema = "cr8a6_test";

    // {prefix}_{appUniqueName}_schemaname_{botSchema} where prefix is the bot schema's leading segment.
    private const string ExpectedCopilotUniqueName = "cr8a6_ppds_myapp_schemaname_cr8a6_test";

    // A sitemap containing BOTH lead and opportunity — exercises the #1192 table-pair eligibility rule.
    private const string SitemapWithLeadAndOpportunity =
        @"<SiteMap><Area Id=""Area1"" Title=""Main""><Group Id=""Group1"" Title=""Sales"">"
        + @"<SubArea Id=""sub_lead"" Entity=""lead"" Title=""Leads"" />"
        + @"<SubArea Id=""sub_opp"" Entity=""opportunity"" Title=""Opportunities"" /></Group></Area></SiteMap>";

    private static void SetupCopilotCommon(Harness h, string sitemapXml = SitemapWithAccount)
    {
        h.Client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        h.Client.Setup(c => c.Dispose());
        h.Pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(h.Client.Object);

        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "appmodule"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>
            {
                new("appmodule")
                {
                    ["appmoduleid"] = AppModuleId,
                    ["uniquename"] = AppUniqueName,
                    ["name"] = AppName
                }
            }));

        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "bot"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>
            {
                new("bot")
                {
                    ["botid"] = CopilotBotId,
                    ["name"] = CopilotBotName,
                    ["schemaname"] = CopilotBotSchema
                }
            }));

        // Eligibility preflight (#1192) reads the app's sitemap: appmodulecomponent (type 62) → sitemap id,
        // then RetrieveUnpublishedMultiple → sitemapxml. The default sitemap has no Lead+Opportunity pair.
        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "appmodulecomponent"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>
            {
                new("appmodulecomponent") { ["objectid"] = SitemapId }
            }));

        h.Client.Setup(c => c.ExecuteAsync(
                It.IsAny<OrganizationRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrganizationRequest req, CancellationToken _) =>
            {
                h.ExecutedRequests.Add(req);
                if (req is RetrieveUnpublishedMultipleRequest)
                {
                    return new RetrieveUnpublishedMultipleResponse
                    {
                        Results =
                        {
                            ["EntityCollection"] = new EntityCollection(new List<Entity>
                            {
                                new("sitemap") { ["sitemapid"] = SitemapId, ["sitemapxml"] = sitemapXml }
                            })
                        }
                    };
                }

                return new OrganizationResponse();
            });
    }

    private static Entity BotBoundAppElement(Guid appElementId)
    {
        var entity = new Entity("appelement")
        {
            ["appelementid"] = appElementId,
            ["uniquename"] = ExpectedCopilotUniqueName,
            ["name"] = CopilotBotSchema,
            ["objectid"] = new EntityReference("bot", CopilotBotId)
        };
        // RetrieveMultiple surfaces the bot display name via FormattedValues, not EntityReference.Name.
        entity.FormattedValues["objectid"] = CopilotBotName;
        return entity;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddCopilot_NewBinding_CreatesAppElementWithBotObjectId()
    {
        var h = new Harness();
        SetupCopilotCommon(h);

        // No existing appelement (unbound row absent) and the bot is not yet wired.
        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "appelement"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>()));

        Entity? created = null;
        var newId = new Guid("ffffffff-7777-7777-7777-777777777777");
        h.Client.Setup(c => c.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => created = e)
            .ReturnsAsync(newId);

        var service = h.Build();
        var result = await service.AddCopilotAsync(
            AppName, CopilotBotName, new CopilotOptions(Publish: false, DryRun: false), progress: null, CancellationToken.None);

        created.Should().NotBeNull();
        created!.LogicalName.Should().Be("appelement");
        created.GetAttributeValue<string>("uniquename").Should().Be(ExpectedCopilotUniqueName);
        created.GetAttributeValue<string>("name").Should().Be(CopilotBotSchema);

        var parent = created.GetAttributeValue<EntityReference>("parentappmoduleid");
        parent.LogicalName.Should().Be("appmodule");
        parent.Id.Should().Be(AppModuleId);

        // The crux: the polymorphic objectid must be an EntityReference whose target type is "bot".
        var objectId = created.GetAttributeValue<EntityReference>("objectid");
        objectId.Should().NotBeNull();
        objectId!.LogicalName.Should().Be("bot");
        objectId.Id.Should().Be(CopilotBotId);

        result.DryRun.Should().BeFalse();
        result.AppElementId.Should().Be(newId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddCopilot_UniqueNameCollision_CreatesWithFallbackName()
    {
        var h = new Harness();
        SetupCopilotCommon(h);

        // Bot not yet wired.
        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "appelement"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>()));

        // First create collides with a stale row on the maker-convention name; the second (suffixed)
        // create succeeds. objectid is never updated/deleted — only created.
        var created = new List<Entity>();
        var calls = 0;
        var newId = new Guid("99999999-8888-8888-8888-999999999999");
        h.Client.Setup(c => c.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Returns<Entity, CancellationToken>((e, _) =>
            {
                created.Add(e);
                calls++;
                if (calls == 1)
                {
                    throw new InvalidOperationException(
                        "Cannot complete the creation of AppElement because it violates a database constraint. " +
                        "The violation happens on the key uniquename: " + ExpectedCopilotUniqueName);
                }

                return Task.FromResult(newId);
            });

        var service = h.Build();
        var result = await service.AddCopilotAsync(
            AppName, CopilotBotName, new CopilotOptions(Publish: false, DryRun: false), progress: null, CancellationToken.None);

        created.Should().HaveCount(2);
        created[0].GetAttributeValue<string>("uniquename").Should().Be(ExpectedCopilotUniqueName);

        // Fallback name keeps the maker-convention base but appends a unique suffix ("_" + 8 hex).
        var fallbackName = created[1].GetAttributeValue<string>("uniquename");
        fallbackName.Should().StartWith(ExpectedCopilotUniqueName + "_");
        fallbackName.Should().NotBe(ExpectedCopilotUniqueName);
        fallbackName.Length.Should().Be(ExpectedCopilotUniqueName.Length + 9);

        var objectId = created[1].GetAttributeValue<EntityReference>("objectid");
        objectId!.LogicalName.Should().Be("bot");
        objectId.Id.Should().Be(CopilotBotId);

        h.Client.Verify(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Client.Verify(c => c.DeleteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        result.AppElementId.Should().Be(newId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddCopilot_LongNames_CapsUniqueNameAt100()
    {
        var h = new Harness();
        SetupCopilotCommon(h);

        // A long app unique name would push the maker-convention name past the 100-char limit.
        var longAppUnique = new string('a', 120);
        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "appmodule"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>
            {
                new("appmodule") { ["appmoduleid"] = AppModuleId, ["uniquename"] = longAppUnique, ["name"] = AppName }
            }));
        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "appelement"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>()));

        Entity? created = null;
        h.Client.Setup(c => c.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => created = e)
            .ReturnsAsync(Guid.NewGuid());

        var service = h.Build();
        await service.AddCopilotAsync(
            AppName, CopilotBotName, new CopilotOptions(Publish: false, DryRun: false), progress: null, CancellationToken.None);

        created.Should().NotBeNull();
        created!.GetAttributeValue<string>("uniquename").Length.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddCopilot_DryRun_DoesNotWrite()
    {
        var h = new Harness();
        SetupCopilotCommon(h);
        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "appelement"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>()));

        var service = h.Build();
        var result = await service.AddCopilotAsync(
            AppName, CopilotBotName, new CopilotOptions(Publish: false, DryRun: true), progress: null, CancellationToken.None);

        result.DryRun.Should().BeTrue();
        result.BotId.Should().Be(CopilotBotId);
        result.UniqueName.Should().Be(ExpectedCopilotUniqueName);
        h.Client.Verify(c => c.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Client.Verify(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddCopilot_AlreadyWired_ThrowsCopilotAlreadyInApp()
    {
        var h = new Harness();
        SetupCopilotCommon(h);

        // The bot is already wired (a bot-bound appelement exists).
        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "appelement"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity> { BotBoundAppElement(Guid.NewGuid()) }));

        var service = h.Build();
        var act = async () => await service.AddCopilotAsync(
            AppName, CopilotBotName, new CopilotOptions(Publish: false, DryRun: false), progress: null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ModelDrivenAppErrorCodes.CopilotAlreadyInApp);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveCopilot_DeletesBoundAppElement()
    {
        var h = new Harness();
        SetupCopilotCommon(h);

        var elementId = new Guid("13131313-aaaa-bbbb-cccc-131313131313");
        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "appelement"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity> { BotBoundAppElement(elementId) }));

        (string Entity, Guid Id)? deleted = null;
        h.Client.Setup(c => c.DeleteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<string, Guid, CancellationToken>((n, id, _) => deleted = (n, id))
            .Returns(Task.CompletedTask);

        var service = h.Build();
        var result = await service.RemoveCopilotAsync(
            AppName, CopilotBotName, new CopilotOptions(Publish: false, DryRun: false), progress: null, CancellationToken.None);

        deleted.Should().NotBeNull();
        deleted!.Value.Entity.Should().Be("appelement");
        deleted.Value.Id.Should().Be(elementId);
        result.AppElementId.Should().Be(elementId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveCopilot_NotWired_ThrowsCopilotNotInApp()
    {
        var h = new Harness();
        SetupCopilotCommon(h);
        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "appelement"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>()));

        var service = h.Build();
        var act = async () => await service.RemoveCopilotAsync(
            AppName, CopilotBotName, new CopilotOptions(Publish: false, DryRun: false), progress: null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ModelDrivenAppErrorCodes.CopilotNotInApp);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RemoveCopilot_DryRun_DoesNotDelete()
    {
        var h = new Harness();
        SetupCopilotCommon(h);
        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "appelement"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity> { BotBoundAppElement(Guid.NewGuid()) }));

        var service = h.Build();
        var result = await service.RemoveCopilotAsync(
            AppName, CopilotBotName, new CopilotOptions(Publish: false, DryRun: true), progress: null, CancellationToken.None);

        result.DryRun.Should().BeTrue();
        h.Client.Verify(c => c.DeleteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListCopilots_ReturnsOnlyBotBindings()
    {
        var h = new Harness();
        SetupCopilotCommon(h);

        // Mixed objectid targets: one bot (kept), one aiskillconfig and one unbound row (filtered out).
        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "appelement"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>
            {
                BotBoundAppElement(new Guid("14141414-aaaa-bbbb-cccc-141414141414")),
                new("appelement")
                {
                    ["appelementid"] = Guid.NewGuid(),
                    ["objectid"] = new EntityReference("aiskillconfig", Guid.NewGuid())
                },
                new("appelement") { ["appelementid"] = Guid.NewGuid() } // objectid null
            }));

        var service = h.Build();
        var copilots = await service.ListCopilotsAsync(AppName, CancellationToken.None);

        copilots.Should().HaveCount(1);
        copilots[0].BotId.Should().Be(CopilotBotId);
        copilots[0].BotName.Should().Be(CopilotBotName);
    }

    // ── #1195: production write-protection gate ────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddCopilot_ProductionWithoutConfirm_ThrowsWriteBlocked()
    {
        var h = new Harness { EnvType = EnvironmentType.Production };
        SetupCopilotCommon(h);
        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "appelement"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>()));

        var service = h.Build();
        var act = async () => await service.AddCopilotAsync(
            AppName, CopilotBotName, new CopilotOptions(Publish: false, DryRun: false), progress: null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ModelDrivenAppErrorCodes.WriteBlockedOnProduction);
        h.Client.Verify(c => c.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddCopilot_ProductionWithConfirm_Proceeds()
    {
        var h = new Harness { EnvType = EnvironmentType.Production };
        SetupCopilotCommon(h);
        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "appelement"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>()));
        var newId = new Guid("ab1d0000-0000-0000-0000-000000000001");
        h.Client.Setup(c => c.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(newId);

        var service = h.Build();
        var result = await service.AddCopilotAsync(
            AppName, CopilotBotName, new CopilotOptions(Publish: false, DryRun: false, Force: false, Confirm: true),
            progress: null, CancellationToken.None);

        result.AppElementId.Should().Be(newId);
        h.Client.Verify(c => c.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddCopilot_ProductionDryRun_NotBlocked()
    {
        // A dry run performs no write, so the production guard must not block it.
        var h = new Harness { EnvType = EnvironmentType.Production };
        SetupCopilotCommon(h);
        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "appelement"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>()));

        var service = h.Build();
        var result = await service.AddCopilotAsync(
            AppName, CopilotBotName, new CopilotOptions(Publish: false, DryRun: true), progress: null, CancellationToken.None);

        result.DryRun.Should().BeTrue();
        h.Client.Verify(c => c.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddTable_ProductionWithoutConfirm_ThrowsWriteBlocked()
    {
        // The gate applies to every write-capable subcommand, not just add-copilot.
        var h = new Harness { EnvType = EnvironmentType.Production };
        h.Setup(SitemapWithAccount, DefaultEntities);
        var service = h.Build();

        var act = async () => await service.AddTableAsync(
            AppName,
            new[] { "contact" },
            new AddTableOptions(Group: null, Area: null, Title: null, Solution: null, Publish: false),
            progress: null,
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ModelDrivenAppErrorCodes.WriteBlockedOnProduction);
        h.WrittenSitemapXml.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddTable_ProductionWithConfirm_Proceeds()
    {
        var h = new Harness { EnvType = EnvironmentType.Production };
        h.Setup(SitemapWithAccount, DefaultEntities);
        var service = h.Build();

        await service.AddTableAsync(
            AppName,
            new[] { "contact" },
            new AddTableOptions(Group: null, Area: null, Title: null, Solution: null, Publish: false, Confirm: true),
            progress: null,
            CancellationToken.None);

        h.WrittenSitemapXml.Should().NotBeNull();
    }

    // ── #1192: app-eligibility preflight ───────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddCopilot_UnsupportedTemplateApp_ThrowsCopilotAppUnsupported()
    {
        var h = new Harness();
        SetupCopilotCommon(h);
        // Override the app to a known-unsupported first-party app (matched by display name).
        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "appmodule"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>
            {
                new("appmodule") { ["appmoduleid"] = AppModuleId, ["uniquename"] = "msdyn_saleshub", ["name"] = "Sales Hub" }
            }));
        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "appelement"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>()));

        var service = h.Build();
        var act = async () => await service.AddCopilotAsync(
            "Sales Hub", CopilotBotName, new CopilotOptions(Publish: false, DryRun: false), progress: null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ModelDrivenAppErrorCodes.CopilotAppUnsupported);
        h.Client.Verify(c => c.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddCopilot_LeadAndOpportunityApp_ThrowsCopilotAppUnsupported()
    {
        var h = new Harness();
        SetupCopilotCommon(h, SitemapWithLeadAndOpportunity);
        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "appelement"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>()));

        var service = h.Build();
        var act = async () => await service.AddCopilotAsync(
            AppName, CopilotBotName, new CopilotOptions(Publish: false, DryRun: false), progress: null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ModelDrivenAppErrorCodes.CopilotAppUnsupported);
        // The table-pair rule is reported distinctly.
        ex.Which.Message.Should().Contain("Lead and Opportunity");
        h.Client.Verify(c => c.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddCopilot_UnsupportedApp_ForceBypass_Creates()
    {
        var h = new Harness();
        SetupCopilotCommon(h, SitemapWithLeadAndOpportunity);
        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "appelement"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>()));
        var newId = new Guid("ab1d0000-0000-0000-0000-000000000002");
        h.Client.Setup(c => c.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(newId);

        var service = h.Build();
        var result = await service.AddCopilotAsync(
            AppName, CopilotBotName, new CopilotOptions(Publish: false, DryRun: false, Force: true, Confirm: false),
            progress: null, CancellationToken.None);

        result.Forced.Should().BeTrue();
        result.EligibilityReason.Should().NotBeNull();
        result.AppElementId.Should().Be(newId);
        h.Client.Verify(c => c.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddCopilot_UnsupportedApp_DryRun_ReportsVerdictWithoutWriting()
    {
        var h = new Harness();
        SetupCopilotCommon(h, SitemapWithLeadAndOpportunity);
        h.Client.Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(qe => qe.EntityName == "appelement"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>()));

        var service = h.Build();
        var result = await service.AddCopilotAsync(
            AppName, CopilotBotName, new CopilotOptions(Publish: false, DryRun: true), progress: null, CancellationToken.None);

        result.DryRun.Should().BeTrue();
        result.EligibilityReason.Should().Contain("Lead and Opportunity");
        result.Forced.Should().BeFalse();
        h.Client.Verify(c => c.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
