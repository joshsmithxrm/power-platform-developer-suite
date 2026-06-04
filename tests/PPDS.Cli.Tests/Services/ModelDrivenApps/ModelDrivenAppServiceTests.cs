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
using PPDS.Cli.Infrastructure.Errors;
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

    private sealed class Harness
    {
        public Mock<IDataverseConnectionPool> Pool { get; } = new();
        public Mock<IPooledClient> Client { get; } = new();
        public Mock<ICachedMetadataProvider> Metadata { get; } = new();

        /// <summary>The XML written by the last UpdateAsync on the sitemap record, if any.</summary>
        public string? WrittenSitemapXml { get; private set; }

        /// <summary>All organization requests passed to ExecuteAsync (for RemoveAppComponents assertions).</summary>
        public List<OrganizationRequest> ExecutedRequests { get; } = new();

        public ModelDrivenAppService Build()
        {
            return new ModelDrivenAppService(
                Pool.Object,
                Metadata.Object,
                new SitemapXmlValidator(new SitemapSchemaResources()),
                new NullLogger<ModelDrivenAppService>());
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

    [Fact]
    [Trait("Category", "Unit")]
    public void Constructor_ThrowsOnNullPool()
    {
        var act = () => new ModelDrivenAppService(
            null!,
            new Mock<ICachedMetadataProvider>().Object,
            new SitemapXmlValidator(new SitemapSchemaResources()),
            new NullLogger<ModelDrivenAppService>());

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
            new NullLogger<ModelDrivenAppService>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("metadata");
    }
}
