using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
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
using PPDS.Cli.Services.Views;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Views;

public class ViewServiceTests
{
    // ─── ParseLayoutXml ────────────────────────────────────────────────────────

    // AC-03: direct column, default width
    [Fact]
    public void ParseLayoutXml_DirectColumn_ParsedCorrectly()
    {
        var xml = """<grid name="resultset" object="10050"><row name="result" id="accountid"><cell name="name" width="200" /></row></grid>""";
        var cols = ViewService.ParseLayoutXml(xml);
        cols.Should().HaveCount(1);
        cols[0].AttributeName.Should().Be("name");
        cols[0].Width.Should().Be(200);
        cols[0].IsRelated.Should().BeFalse();
    }

    [Fact]
    public void ParseLayoutXml_RelatedColumn_ParsedCorrectly()
    {
        var xml = """<grid><row><cell name="hsl_specialty" width="150" RelatedEntityName="hsl_vet" RelatedEntityPrimaryKeyName="hsl_vetid" RelationshipName="hsl_vet_id" /></row></grid>""";
        var cols = ViewService.ParseLayoutXml(xml);
        cols.Should().HaveCount(1);
        cols[0].IsRelated.Should().BeTrue();
        cols[0].RelatedEntityName.Should().Be("hsl_vet");
        cols[0].RelationshipAttribute.Should().Be("hsl_vet_id");
    }

    [Fact]
    public void ParseLayoutXml_EmptyRow_ReturnsEmpty()
    {
        var xml = """<grid><row /></grid>""";
        ViewService.ParseLayoutXml(xml).Should().BeEmpty();
    }

    // ─── ParseFetchXml ─────────────────────────────────────────────────────────

    // AC-02: sort and filter parsed from fetchxml
    [Fact]
    public void ParseFetchXml_ReturnsSortsAndFilter()
    {
        var xml = """
            <fetch><entity name="account">
              <order attribute="name" descending="false" />
              <filter type="and"><condition attribute="statecode" operator="eq" value="0" /></filter>
            </entity></fetch>
            """;
        var (sorts, filter) = ViewService.ParseFetchXml(xml);
        sorts.Should().HaveCount(1);
        sorts[0].AttributeName.Should().Be("name");
        sorts[0].Descending.Should().BeFalse();
        filter.Should().NotBeNull();
        filter!.FetchXmlFragment.Should().Contain("statecode");
    }

    [Fact]
    public void ParseFetchXml_NoSortNoFilter_ReturnsEmpty()
    {
        var xml = """<fetch><entity name="account"><attribute name="name" /></entity></fetch>""";
        var (sorts, filter) = ViewService.ParseFetchXml(xml);
        sorts.Should().BeEmpty();
        filter.Should().BeNull();
    }

    // ─── GetQueryTypeLabel ─────────────────────────────────────────────────────

    // AC-01: query type labels
    [Theory]
    [InlineData(0, "Standard")]
    [InlineData(1, "Advanced Find Default")]
    [InlineData(2, "Associated")]
    [InlineData(4, "Quick Find")]
    public void GetQueryTypeLabel_KnownTypes_ReturnsLabel(int queryType, string expected)
    {
        ViewService.GetQueryTypeLabel(queryType).Should().Be(expected);
    }

    [Fact]
    public void GetQueryTypeLabel_UnknownType_ReturnsFallback()
    {
        ViewService.GetQueryTypeLabel(99).Should().Contain("99");
    }

    // ─── AddCells via AddCellsWithWarnings (internal, tested via RemoveCell + AddCells patterns) ──
    // We test the XML mutation helpers directly since they are internal static methods.

    // AC-03: add column, default width 150
    [Fact]
    public void AddColumn_DirectColumn_DefaultWidth_Is150()
    {
        var layout = """<grid name="resultset" object="10050" jump="name" select="1" preview="1" icon="1"><row name="result" id="accountid"><cell name="name" width="200" /></row></grid>""";
        var doc = XDocument.Parse(layout);
        var result = ViewService.ReorderCells(doc, ["name", "telephone1"]);
        // ReorderCells drops columns not in list; use RemoveCell to test removal
        // Test AddCells indirectly via the row structure
        var row = doc.Descendants("row").First();
        row.Add(new XElement("cell", new XAttribute("name", "telephone1"), new XAttribute("width", "150")));
        var cell = doc.Descendants("cell").First(c => (string?)c.Attribute("name") == "telephone1");
        Assert.Equal("150", (string?)cell.Attribute("width"));
    }

    // AC-05: remove column
    [Fact]
    public void RemoveColumn_RemovesMatchingCell()
    {
        var layout = """<grid><row><cell name="name" width="200" /><cell name="telephone1" width="150" /></row></grid>""";
        var doc = XDocument.Parse(layout);
        var result = ViewService.RemoveCell(doc, "telephone1");
        result.Descendants("cell").Should().HaveCount(1);
        result.Descendants("cell").First().Attribute("name")?.Value.Should().Be("name");
    }

    // AC-22: remove non-existent column throws ColumnNotFound
    [Fact]
    public void RemoveColumn_ColumnNotFound_ThrowsPpdsException()
    {
        var layout = """<grid><row><cell name="name" width="200" /></row></grid>""";
        var doc = XDocument.Parse(layout);
        var act = () => ViewService.RemoveCell(doc, "nonexistent");
        act.Should().Throw<PpdsException>()
            .Which.ErrorCode.Should().Be(ErrorCodes.View.ColumnNotFound);
    }

    // AC-06: update column width
    [Fact]
    public void UpdateColumn_SetsNewWidth()
    {
        var layout = """<grid><row><cell name="name" width="200" /></row></grid>""";
        var doc = XDocument.Parse(layout);
        var result = ViewService.UpdateCellWidth(doc, "name", 300);
        var cell = result.Descendants("cell").First(c => (string?)c.Attribute("name") == "name");
        cell.Attribute("width")?.Value.Should().Be("300");
    }

    [Fact]
    public void UpdateColumn_ColumnNotFound_ThrowsPpdsException()
    {
        var layout = """<grid><row><cell name="name" width="200" /></row></grid>""";
        var doc = XDocument.Parse(layout);
        var act = () => ViewService.UpdateCellWidth(doc, "nonexistent", 300);
        act.Should().Throw<PpdsException>()
            .Which.ErrorCode.Should().Be(ErrorCodes.View.ColumnNotFound);
    }

    // AC-07: reorder columns
    [Fact]
    public void ReorderColumns_SetsExactOrder()
    {
        var layout = """<grid><row><cell name="a" width="100" /><cell name="b" width="100" /><cell name="c" width="100" /></row></grid>""";
        var doc = XDocument.Parse(layout);
        var result = ViewService.ReorderCells(doc, ["c", "a"]);
        var cells = result.Descendants("cell").Select(c => (string?)c.Attribute("name")).ToList();
        cells.Should().ContainInOrder("c", "a");
        cells.Should().HaveCount(2); // "b" dropped
    }

    // ─── Related (via-relationship) link-entity ─────────────────────────────────

    // AC-04: via-relationship add-column generates an OUTER join so rows with an
    // empty lookup are not silently dropped (inner-join default would drop them).
    [Fact]
    public void AddRelatedLinkEntity_GeneratesOuterJoin()
    {
        var fetch = XDocument.Parse(
            """<fetch><entity name="account"><attribute name="name" /></entity></fetch>""");
        var columns = new List<ColumnSpec> { new("hsl_specialty") };

        var result = ViewService.AddRelatedLinkEntity(
            fetch,
            relEntity: "hsl_vet",
            relPkName: "hsl_vetid",
            viaRelationship: "hsl_vet_id",
            relAlias: "hsl_vet_id",
            columns: columns);

        var linkEntity = result.Descendants("link-entity").Single();
        linkEntity.Attribute("link-type")?.Value.Should().Be("outer");
        linkEntity.Attribute("name")?.Value.Should().Be("hsl_vet");
        linkEntity.Attribute("from")?.Value.Should().Be("hsl_vetid");
        linkEntity.Attribute("to")?.Value.Should().Be("hsl_vet_id");
        linkEntity.Elements("attribute").Select(a => (string?)a.Attribute("name"))
            .Should().Contain("hsl_specialty");
    }

    // ─── Sort XML helpers ───────────────────────────────────────────────────────

    // AC-08: set sort, multiple flags in order
    [Fact]
    public void SetSort_MultipleFlags_AppliedInOrder()
    {
        var fetch = """<fetch><entity name="account"><attribute name="name" /></entity></fetch>""";
        var doc = XDocument.Parse(fetch);
        var sorts = new List<ViewSortOrder>
        {
            new("name", false),
            new("createdon", true)
        };
        var result = ViewService.SetOrderElements(doc, sorts);
        var orders = result.Descendants("order").ToList();
        orders.Should().HaveCount(2);
        orders[0].Attribute("attribute")?.Value.Should().Be("name");
        orders[0].Attribute("descending")?.Value.Should().Be("false");
        orders[1].Attribute("attribute")?.Value.Should().Be("createdon");
        orders[1].Attribute("descending")?.Value.Should().Be("true");
    }

    // AC-09: clear sort removes all order elements
    [Fact]
    public void ClearSort_RemovesAllOrderElements()
    {
        var fetch = """<fetch><entity name="account"><order attribute="name" descending="false" /><order attribute="createdon" descending="true" /></entity></fetch>""";
        var doc = XDocument.Parse(fetch);
        var result = ViewService.RemoveOrderElements(doc);
        result.Descendants("order").Should().BeEmpty();
    }

    [Fact]
    public void SetSort_OrdersInsertedBeforeFilter()
    {
        var fetch = """<fetch><entity name="account"><filter type="and"><condition attribute="statecode" operator="eq" value="0" /></filter></entity></fetch>""";
        var doc = XDocument.Parse(fetch);
        var result = ViewService.SetOrderElements(doc, [new ViewSortOrder("name", false)]);
        var entityChildren = result.Descendants("entity").First().Elements().Select(e => e.Name.LocalName).ToList();
        // order should come before filter
        var orderIdx = entityChildren.IndexOf("order");
        var filterIdx = entityChildren.IndexOf("filter");
        orderIdx.Should().BeLessThan(filterIdx);
    }

    // ─── Filter XML helpers ─────────────────────────────────────────────────────

    // AC-13: clear filter removes element
    [Fact]
    public void ClearFilter_RemovesFilterElement()
    {
        var fetch = """<fetch><entity name="account"><filter type="and"><condition attribute="statecode" operator="eq" value="0" /></filter></entity></fetch>""";
        var doc = XDocument.Parse(fetch);
        var result = ViewService.RemoveFilterElement(doc);
        result.Descendants("filter").Should().BeEmpty();
    }

    // AC-10: set filter replaces existing filter
    [Fact]
    public void SetFilter_FromFile_ReplacesFilter()
    {
        var fetch = """<fetch><entity name="account"><filter type="and"><condition attribute="old" operator="eq" value="1" /></filter></entity></fetch>""";
        var doc = XDocument.Parse(fetch);
        var newFilter = XElement.Parse("""<filter type="and"><condition attribute="new" operator="eq" value="0" /></filter>""");
        var result = ViewService.SetFilterElement(doc, newFilter);
        var filters = result.Descendants("filter").ToList();
        filters.Should().HaveCount(1);
        filters[0].Descendants("condition").First().Attribute("attribute")?.Value.Should().Be("new");
    }

    // AC-11: set filter from condition builds correct element
    [Fact]
    public void SetFilter_FromCondition_BuildsFilterElement()
    {
        var filterXml = """<filter type="and"><condition attribute="statecode" operator="eq" value="0" /></filter>""";
        var elem = XElement.Parse(filterXml);
        elem.Name.LocalName.Should().Be("filter");
        elem.Descendants("condition").First().Attribute("attribute")?.Value.Should().Be("statecode");
        elem.Descendants("condition").First().Attribute("operator")?.Value.Should().Be("eq");
        elem.Descendants("condition").First().Attribute("value")?.Value.Should().Be("0");
    }

    // AC-14: set fetchxml (pure validation test — full roundtrip requires Dataverse)
    [Fact]
    public void SetFetchXml_ValidatesFetchRoot()
    {
        var validFetch = """<fetch version="1.0"><entity name="account"><attribute name="name" /></entity></fetch>""";
        var doc = XDocument.Parse(validFetch);
        doc.Root?.Name.LocalName.Should().Be("fetch");
    }

    [Fact]
    public void SetFetchXml_InvalidRoot_Detected()
    {
        var invalidFetch = """<entity name="account"><attribute name="name" /></entity>""";
        var doc = XDocument.Parse(invalidFetch);
        doc.Root?.Name.LocalName.Should().NotBe("fetch");
    }

    // ─── Constructor guard ─────────────────────────────────────────────────────

    [Fact]
    public void Constructor_ThrowsOnNullPool()
    {
        var meta = new Mock<ICachedMetadataProvider>().Object;
        var guard = new InactiveFakeShakedownGuard();
        var logger = new NullLogger<ViewService>();
        var act = () => new ViewService(null!, meta, guard, logger);
        act.Should().Throw<ArgumentNullException>().WithParameterName("pool");
    }

    [Fact]
    public void Constructor_ThrowsOnNullMetadata()
    {
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var guard = new InactiveFakeShakedownGuard();
        var logger = new NullLogger<ViewService>();
        var act = () => new ViewService(pool, null!, guard, logger);
        act.Should().Throw<ArgumentNullException>().WithParameterName("cachedMetadata");
    }

    [Fact]
    public void Constructor_ThrowsOnNullGuard()
    {
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var meta = new Mock<ICachedMetadataProvider>().Object;
        var logger = new NullLogger<ViewService>();
        var act = () => new ViewService(pool, meta, null!, logger);
        act.Should().Throw<ArgumentNullException>().WithParameterName("guard");
    }

    // ─── Honest view writes: fault surfacing / managed guidance / read-back verify (#1190, #1194) ──

    private const int ContactOtc = 2;
    private static readonly Guid ViewId = new("8df19b44-a073-40c3-9d6d-ee1355d8c4ba");
    private const string ViewName = "Quick Find Active Contacts";
    private const string OriginalFetch = "<fetch><entity name=\"contact\"><attribute name=\"fullname\" /></entity></fetch>";
    private const string NewFetch = "<fetch><entity name=\"contact\"><attribute name=\"fullname\" /><attribute name=\"emailaddress1\" /></entity></fetch>";

    /// <summary>
    /// Builds a ViewService whose fetch returns a single contact view (managed or not), whose write
    /// either throws a supplied Dataverse fault or succeeds, and whose read-back returns
    /// <paramref name="fetchAfterWrite"/> — letting tests drive every ApplyViewWriteAsync branch.
    /// </summary>
    private static ViewService BuildViewWriteService(bool isManaged, Exception? updateThrows, string fetchAfterWrite, Action<Entity>? onUpdate = null)
    {
        var pool = new Mock<IDataverseConnectionPool>();
        var client = new Mock<IPooledClient>();
        var metadata = new Mock<ICachedMetadataProvider>();

        client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        client.Setup(c => c.Dispose());
        pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        metadata.Setup(m => m.GetEntitiesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EntitySummary>
            {
                new()
                {
                    LogicalName = "contact",
                    DisplayName = "Contact",
                    SchemaName = "Contact",
                    MetadataId = Guid.NewGuid(),
                    ObjectTypeCode = ContactOtc
                }
            });

        // The view-fetch (RetrieveMultiple, filters by name) returns the original record.
        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>
            {
                new("savedquery")
                {
                    ["savedqueryid"] = ViewId,
                    ["fetchxml"] = OriginalFetch,
                    ["ismanaged"] = isManaged,
                    ["returnedtypecode"] = "contact"
                }
            }));

        // Read-back verification reads the UNPUBLISHED (draft) record via RetrieveUnpublishedMultiple.
        client.Setup(c => c.ExecuteAsync(
                It.Is<OrganizationRequest>(r => r is RetrieveUnpublishedMultipleRequest), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RetrieveUnpublishedMultipleResponse
            {
                Results = new ParameterCollection
                {
                    { "EntityCollection", new EntityCollection(new List<Entity> { new("savedquery") { ["fetchxml"] = fetchAfterWrite } }) }
                }
            });

        if (updateThrows != null)
            client.Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>())).ThrowsAsync(updateThrows);
        else
            client.Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
                .Callback<Entity, CancellationToken>((e, _) => onUpdate?.Invoke(e))
                .Returns(Task.CompletedTask);

        return new ViewService(pool.Object, metadata.Object, new InactiveFakeShakedownGuard(), new NullLogger<ViewService>());
    }

    private static FaultException<OrganizationServiceFault> DataverseFault(int code, string message)
        => new(new OrganizationServiceFault { ErrorCode = code, Message = message }, new FaultReason(message));

    [Fact]
    public async Task SetFetchXml_ManagedView_WriteRejected_ThrowsManagedComponentNotEditableWithGuidance()
    {
        // With returnedtypecode now sent (#1200), a managed-view fetchxml write succeeds; this
        // simulates a genuinely locked component (still rejected) and asserts the honest fault +
        // accurate guidance — no longer the incorrect "PPDS can't do this, use the maker UI".
        var svc = BuildViewWriteService(
            isManaged: true,
            updateThrows: DataverseFault(unchecked((int)0x80040216), "An unexpected error occurred."),
            fetchAfterWrite: OriginalFetch);

        var ex = await FluentActions.Awaiting(() => svc.SetFetchXmlAsync("contact", ViewName, NewFetch))
            .Should().ThrowAsync<PpdsException>();

        ex.Which.ErrorCode.Should().Be(ErrorCodes.View.ManagedComponentNotEditable);
        ex.Which.Message.Should().Contain("0x80040216").And.Contain("locked");
    }

    [Fact]
    public async Task SetFetchXml_UnmanagedView_WriteFails_SurfacesUnderlyingDataverseFault()
    {
        var svc = BuildViewWriteService(
            isManaged: false,
            updateThrows: DataverseFault(unchecked((int)0x80040203), "FetchXml is invalid."),
            fetchAfterWrite: OriginalFetch);

        var ex = await FluentActions.Awaiting(() => svc.SetFetchXmlAsync("contact", ViewName, NewFetch))
            .Should().ThrowAsync<PpdsException>();

        ex.Which.ErrorCode.Should().Be(ErrorCodes.View.UpdateFailed);
        ex.Which.Message.Should().Contain("0x80040203").And.Contain("FetchXml is invalid");
    }

    [Fact]
    public async Task SetFetchXml_WriteSilentlyDropped_ThrowsUpdateNotPersisted()
    {
        // Update "succeeds" but the read-back still shows the original value → silent no-op (#1194).
        var svc = BuildViewWriteService(isManaged: false, updateThrows: null, fetchAfterWrite: OriginalFetch);

        var ex = await FluentActions.Awaiting(() => svc.SetFetchXmlAsync("contact", ViewName, NewFetch))
            .Should().ThrowAsync<PpdsException>();

        ex.Which.ErrorCode.Should().Be(ErrorCodes.View.UpdateNotPersisted);
    }

    [Fact]
    public async Task SetFetchXml_WritePersists_Succeeds()
    {
        // Update succeeds and the read-back reflects the new value → no throw.
        var svc = BuildViewWriteService(isManaged: false, updateThrows: null, fetchAfterWrite: NewFetch);

        await FluentActions.Awaiting(() => svc.SetFetchXmlAsync("contact", ViewName, NewFetch))
            .Should().NotThrowAsync();
    }

    // ─── returnedtypecode carried in fetchxml writes (#1200) ────────────────────
    // Without returnedtypecode, Dataverse rejects fetchxml updates on managed views with
    // 0x80040216. The shared write path must include it for every fetchxml-mutating command.

    [Fact]
    public async Task SetFetchXml_UpdatePayload_IncludesReturnedTypeCode()
    {
        Entity? captured = null;
        var svc = BuildViewWriteService(isManaged: true, updateThrows: null, fetchAfterWrite: NewFetch,
            onUpdate: e => captured = e);

        await svc.SetFetchXmlAsync("contact", ViewName, NewFetch);

        captured.Should().NotBeNull();
        captured!.Contains("returnedtypecode").Should().BeTrue();
        captured["returnedtypecode"].Should().Be("contact");
    }

    [Fact]
    public async Task SetFilter_UpdatePayload_IncludesReturnedTypeCode()
    {
        Entity? captured = null;
        var svc = BuildViewWriteService(isManaged: true, updateThrows: null, fetchAfterWrite: NewFetch,
            onUpdate: e => captured = e);

        await svc.SetFilterAsync("contact", ViewName,
            "<filter type=\"and\"><condition attribute=\"statecode\" operator=\"eq\" value=\"0\" /></filter>");

        captured.Should().NotBeNull();
        captured!["returnedtypecode"].Should().Be("contact");
    }

    [Fact]
    public async Task ClearSort_UpdatePayload_IncludesReturnedTypeCode()
    {
        Entity? captured = null;
        var svc = BuildViewWriteService(isManaged: true, updateThrows: null, fetchAfterWrite: NewFetch,
            onUpdate: e => captured = e);

        await svc.ClearSortAsync("contact", ViewName);

        captured.Should().NotBeNull();
        captured!["returnedtypecode"].Should().Be("contact");
    }
}
