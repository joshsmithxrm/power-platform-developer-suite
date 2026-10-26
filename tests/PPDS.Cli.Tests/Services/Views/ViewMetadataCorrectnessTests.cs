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
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.Views;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Views;

[Trait("Category", "Unit")]
public class ViewMetadataCorrectnessTests
{
    private const string Fetch = "<fetch><entity name='account'><attribute name='accountid'/><attribute name='name'/><order attribute='name'/><filter><condition attribute='statecode' operator='eq' value='0'/></filter><link-entity name='contact' from='contactid' to='primarycontactid' alias='contact'><attribute name='fullname' alias='contact_name'/></link-entity></entity></fetch>";
    private const string Layout = "<grid><row id='accountid'><cell name='name' width='200'/></row></grid>";

    private sealed class Harness
    {
        public Entity State { get; } = new("savedquery", Guid.NewGuid())
        {
            ["layoutxml"] = Layout,
            ["fetchxml"] = Fetch,
            ["returnedtypecode"] = "account",
            ["ismanaged"] = false
        };
        public Mock<IPooledClient> Client { get; } = new();
        public Action<Entity>? AfterWrite { get; set; }
        public Func<EntityCollection>? ReadBack { get; set; }
        public int ReadBackCount { get; private set; }
        public bool Written { get; private set; }

        public ViewService Build()
        {
            State["savedqueryid"] = State.Id;
            Client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
            Client.Setup(c => c.ExecuteAsync(It.IsAny<RetrieveUnpublishedMultipleRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    if (Written) ReadBackCount++;
                    return new RetrieveUnpublishedMultipleResponse
                    {
                        Results = new ParameterCollection
                        {
                            ["EntityCollection"] = Written && ReadBack != null ? ReadBack() : new EntityCollection([State])
                        }
                    };
                });
            Client.Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
                .Callback<Entity, CancellationToken>((update, _) =>
                {
                    foreach (var attribute in update.Attributes) State[attribute.Key] = attribute.Value;
                    Written = true;
                    AfterWrite?.Invoke(State);
                }).Returns(Task.CompletedTask);
            var pool = new Mock<IDataverseConnectionPool>();
            pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(Client.Object);
            var metadata = new Mock<ICachedMetadataProvider>();
            metadata.Setup(m => m.GetEntitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([
                new EntitySummary { LogicalName = "account", SchemaName = "Account", DisplayName = "Account", ObjectTypeCode = 1 }
            ]);
            metadata.Setup(m => m.GetRelationshipsAsync("account", It.IsAny<CancellationToken>())).ReturnsAsync(new EntityRelationshipsDto
            {
                EntityLogicalName = "account",
                ManyToOne = new[] { "parentaccountid", "otherparentid" }.Select(lookup => new RelationshipMetadataDto
                {
                    ReferencingAttribute = lookup,
                    ReferencedEntity = "account",
                    ReferencedAttribute = "accountid",
                    ReferencingEntity = "account",
                    SchemaName = "account_" + lookup,
                    RelationshipType = "ManyToOne"
                }).ToList()
            });
            return new ViewService(pool.Object, metadata.Object, Mock.Of<IShakedownGuard>(), NullLogger<ViewService>.Instance);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("AC", "AC-25")]
    public async Task AddDirectColumn_RepairsProjection(bool inLayout, bool allAttributes)
    {
        var h = new Harness();
        if (inLayout) h.State["layoutxml"] = Layout.Replace("</row>", "<cell name='telephone1' width='150'/></row>");
        if (allAttributes) h.State["fetchxml"] = Fetch.Replace("<attribute name='name'/>", "<all-attributes/>");
        var before = XDocument.Parse(h.State.GetAttributeValue<string>("fetchxml"));
        var service = h.Build();
        await service.AddColumnAsync("account", "Test", [new ColumnSpec("telephone1")]);
        await service.AddColumnAsync("account", "Test", [new ColumnSpec("telephone1")]);
        var after = XDocument.Parse(h.State.GetAttributeValue<string>("fetchxml"));
        var added = after.Root!.Element("entity")!.Elements("attribute").Where(a => (string?)a.Attribute("name") == "telephone1").ToList();
        added.Should().HaveCount(allAttributes ? 0 : 1);
        added.Remove();
        XNode.DeepEquals(before, after).Should().BeTrue("other query structure must survive");
        ViewService.ParseLayoutXml(h.State.GetAttributeValue<string>("layoutxml")).Count(c => c.AttributeName == "telephone1").Should().Be(1);
    }

    [Fact]
    [Trait("AC", "AC-26")]
    public async Task RelatedColumns_RoundTripDistinctIdentities()
    {
        var h = new Harness();
        var service = h.Build();
        foreach (var lookup in new[] { "parentaccountid", "otherparentid" })
            await service.AddColumnAsync("account", "Test", [new ColumnSpec("name"), new ColumnSpec("telephone1")], lookup);
        await service.AddColumnAsync("account", "Test", [new ColumnSpec("name")], "parentaccountid");
        var detail = ViewService.ParseLayoutXml(h.State.GetAttributeValue<string>("layoutxml"), h.State.GetAttributeValue<string>("fetchxml"));
        detail.Select(c => c.AttributeName).Should().Equal("name", "parentaccountid.name", "parentaccountid.telephone1", "otherparentid.name", "otherparentid.telephone1");
        detail[1].IsRelated.Should().BeTrue();
        detail[1].RelationshipAttribute.Should().Be("parentaccountid");
        detail[1].RelatedEntityName.Should().Be("account");
        detail[1].RelatedEntityPrimaryKeyName.Should().Be("accountid");
        XDocument.Parse(h.State.GetAttributeValue<string>("layoutxml")).Descendants("cell")
            .SelectMany(c => c.Attributes()).Select(a => a.Name.LocalName).Distinct().Should().BeEquivalentTo("name", "width");
        await service.UpdateColumnAsync("account", "Test", "PARENTACCOUNTID.NAME", 321);
        await service.ReorderColumnsAsync("account", "Test", ["otherparentid.name", "parentaccountid.name", "name"]);
        await service.RemoveColumnAsync("account", "Test", "otherparentid.name");
        var remaining = ViewService.ParseLayoutXml(h.State.GetAttributeValue<string>("layoutxml"));
        remaining.Select(c => c.AttributeName).Should().Equal("parentaccountid.name", "name");
        remaining[0].Width.Should().Be(321);
    }

    [Fact]
    public async Task RelatedColumn_ConflictingAlias_DoesNotWrite()
    {
        var h = new Harness();
        h.State["fetchxml"] = Fetch.Replace("alias='contact'", "alias='parentaccountid'");
        await FluentActions.Awaiting(() => h.Build().AddColumnAsync("account", "Test", [new ColumnSpec("name")], "parentaccountid"))
            .Should().ThrowAsync<PpdsException>();
        h.Written.Should().BeFalse();
    }

    [Theory]
    [InlineData("unrelated")]
    [InlineData("missing")]
    [InlineData("partial")]
    [InlineData("noop-missing")]
    [Trait("AC", "AC-27")]
    public async Task WriteVerification_RejectsIncorrectState(string mode)
    {
        var h = new Harness();
        h.AfterWrite = state =>
        {
            if (mode == "unrelated") state["fetchxml"] = Fetch.Replace("name='name'", "name='createdon'");
            if (mode == "partial") state["fetchxml"] = Fetch;
        };
        if (mode is "missing" or "noop-missing") h.ReadBack = () => new EntityCollection();
        var service = h.Build();
        Func<Task> action = mode == "noop-missing"
            ? () => service.SetFetchXmlAsync("account", "Test", Fetch)
            : () => service.AddColumnAsync("account", "Test", [new ColumnSpec("telephone1")]);
        (await action.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(ErrorCodes.View.UpdateNotPersisted);
        h.ReadBackCount.Should().Be(3);
    }

    [Fact]
    [Trait("AC", "AC-28")]
    public async Task WriteVerification_NormalizesPlatformAnnotation()
    {
        var h = new Harness();
        h.AfterWrite = state => state["fetchxml"] = Fetch.Replace("<fetch>", $"<fetch savedqueryid='{state.Id}'>");
        await h.Build().SetFetchXmlAsync("account", "Test", Fetch);
        h.ReadBackCount.Should().Be(1);
    }

    [Fact]
    public async Task WriteVerification_RetriesLagAndNormalizesAttributeOrder()
    {
        var h = new Harness();
        h.ReadBack = () => new EntityCollection([new Entity("savedquery")
        {
            ["fetchxml"] = h.ReadBackCount == 1 ? Fetch : "<fetch><entity name='account'><attribute name='telephone1' alias='phone'/></entity></fetch>"
        }]);
        await h.Build().SetFetchXmlAsync("account", "Test", "<fetch> <entity name='account'><attribute alias='phone' name='telephone1'/></entity> </fetch>");
        h.ReadBackCount.Should().Be(2);
    }

    [Fact]
    [Trait("AC", "AC-28")]
    public async Task WriteVerification_UnavailableReadIsExplicit()
    {
        var h = new Harness { ReadBack = () => throw new InvalidOperationException("Read denied") };
        (await FluentActions.Awaiting(() => h.Build().SetFetchXmlAsync("account", "Test", Fetch))
            .Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(ErrorCodes.View.VerificationFailed);
        h.ReadBackCount.Should().Be(3);
    }

    [Fact]
    public async Task WriteVerification_CancellationPropagates()
    {
        var h = new Harness { ReadBack = () => throw new OperationCanceledException() };
        await FluentActions.Awaiting(() => h.Build().SetFetchXmlAsync("account", "Test", Fetch))
            .Should().ThrowAsync<OperationCanceledException>();
        h.ReadBackCount.Should().Be(1);
    }
}
