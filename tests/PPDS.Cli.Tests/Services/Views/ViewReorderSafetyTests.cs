using System.Xml.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Views;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Views;

public sealed class ViewReorderSafetyTests
{
    private const string Layout = """
        <grid name="resultset" object="1" jump="name" select="1"><header marker="keep"/><row id="accountid" name="result"><cell name="name" width="200" disableSorting="1"/><!-- keep position --><cell name="parentaccountid.name" width="321" entityname="account"/><aux keep="yes"/><cell name="otherparentid.name" width="432"/></row><footer marker="keep"/></grid>
        """;
    private const string Fetch = """
        <fetch distinct="true"><entity name="account"><attribute name="accountid"/><attribute name="name"/><order attribute="name" descending="true"/><filter><condition attribute="statecode" operator="eq" value="0"/></filter><link-entity name="account" from="accountid" to="parentaccountid" alias="parentaccountid"><attribute name="name"/></link-entity><link-entity name="account" from="accountid" to="otherparentid" alias="otherparentid"><attribute name="name"/></link-entity></entity></fetch>
        """;

    public static IEnumerable<object[]> InvalidPermutations()
    {
        yield return [Array.Empty<string>()];
        yield return [new[] { "name" }];
        yield return [new[] { "name", "parentaccountid.name", "unknown" }];
        yield return [new[] { "name", "parentaccountid.name", "otherparentid.name", "unknown" }];
        yield return [new[] { "name", "name", "otherparentid.name" }];
        yield return [new[] { "name", "NAME", "otherparentid.name" }];
        yield return [new[] { "name", " name ", "otherparentid.name" }];
        yield return [new[] { "name", "", "otherparentid.name" }];
        yield return [new[] { "name", "  ", "otherparentid.name" }];
        yield return [new[] { "name", null!, "otherparentid.name" }];
    }

    [Theory]
    [MemberData(nameof(InvalidPermutations))]
    public async Task Reorder_InvalidPermutation_DoesNotMutate(string[] order)
    {
        var fixture = new Harness(Layout);
        var error = await Record.ExceptionAsync(() => fixture.Service.ReorderColumnsAsync(
            "account", "Example", order, publish: true, solution: "example_solution"));

        fixture.AssertNoMutation();
        Assert.Equal(ErrorCodes.Validation.InvalidValue, Assert.IsType<PpdsException>(error).ErrorCode);
    }

    [Theory]
    [InlineData("<grid/>")]
    [InlineData("<grid><row/></grid>")]
    [InlineData("<grid><row><cell width='100'/></row></grid>")]
    [InlineData("<grid><row><cell name='' width='100'/></row></grid>")]
    [InlineData("<grid><row><cell name='  ' width='100'/></row></grid>")]
    [InlineData("<grid><row><cell name='name'/><cell name='name'/></row></grid>")]
    [InlineData("<grid><row><cell name='name'/><cell name='NAME'/></row></grid>")]
    [InlineData("<grid><row><cell name='name'/></row><row><cell name='name'/></row></grid>")]
    public async Task Reorder_AmbiguousLayout_DoesNotMutate(string layout)
    {
        var fixture = new Harness(layout);
        var error = await Record.ExceptionAsync(() => fixture.Service.ReorderColumnsAsync(
            "account", "Example", ["name"], publish: true, solution: "example_solution"));

        fixture.AssertNoMutation();
        Assert.Equal(ErrorCodes.Validation.SchemaInvalid, Assert.IsType<PpdsException>(error).ErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reorder_CompletePermutation_PreservesEverythingExceptCellOrder(bool trimInput)
    {
        var fixture = new Harness(Layout);
        var order = new[] { "OTHERPARENTID.NAME", "NAME", "PARENTACCOUNTID.NAME" };
        if (trimInput) order = order.Select(name => $" {name} ").ToArray();

        await fixture.Service.ReorderColumnsAsync("account", "Example", order);

        var update = Assert.Single(fixture.Writes);
        Assert.Equal(new[] { "layoutxml", "returnedtypecode" }, update.Attributes.Keys);
        Assert.Equal("account", update.GetAttributeValue<string>("returnedtypecode"));
        var expected = XDocument.Parse("""
            <grid name="resultset" object="1" jump="name" select="1"><header marker="keep"/><row id="accountid" name="result"><cell name="otherparentid.name" width="432"/><!-- keep position --><cell name="name" width="200" disableSorting="1"/><aux keep="yes"/><cell name="parentaccountid.name" width="321" entityname="account"/></row><footer marker="keep"/></grid>
            """);
        Assert.True(XNode.DeepEquals(expected, XDocument.Parse(update.GetAttributeValue<string>("layoutxml"))));
        Assert.Equal(Fetch, fixture.State.GetAttributeValue<string>("fetchxml"));
        Assert.Equal("retained-description", fixture.State.GetAttributeValue<string>("description"));
        Assert.Equal(0, fixture.State.GetAttributeValue<int>("querytype"));
        Assert.All(fixture.Requests, request => Assert.IsType<RetrieveUnpublishedMultipleRequest>(request));
        Assert.True(fixture.Requests.Count >= 2); // initial unpublished read and persisted-layout verification
    }

    [Fact]
    public async Task RemoveColumn_RemainsExplicit()
    {
        var fixture = new Harness(Layout);

        await fixture.Service.RemoveColumnAsync("account", "Example", "parentaccountid.name");

        var update = Assert.Single(fixture.Writes);
        Assert.Equal(new[] { "layoutxml", "returnedtypecode" }, update.Attributes.Keys);
        Assert.Equal("account", update.GetAttributeValue<string>("returnedtypecode"));
        Assert.Equal(new[] { "name", "otherparentid.name" }, XDocument.Parse(
            update.GetAttributeValue<string>("layoutxml")).Descendants("cell").Select(cell => (string?)cell.Attribute("name")));
        Assert.Equal(Fetch, fixture.State.GetAttributeValue<string>("fetchxml"));
    }

    private sealed class Harness
    {
        private readonly string _originalLayout;
        public Entity State { get; }
        public List<Entity> Writes { get; } = [];
        public List<OrganizationRequest> Requests { get; } = [];
        public ViewService Service { get; }

        public Harness(string layout)
        {
            _originalLayout = layout;
            State = new Entity("savedquery", Guid.NewGuid())
            {
                ["layoutxml"] = layout, ["fetchxml"] = Fetch, ["ismanaged"] = false,
                ["returnedtypecode"] = "account", ["querytype"] = 0, ["description"] = "retained-description"
            };
            State["savedqueryid"] = State.Id;
            var client = new Mock<IPooledClient>(MockBehavior.Strict);
            client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
            client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                .Callback<OrganizationRequest, CancellationToken>((request, _) => Requests.Add(request))
                .ReturnsAsync((OrganizationRequest request, CancellationToken _) => request is RetrieveUnpublishedMultipleRequest
                    ? new RetrieveUnpublishedMultipleResponse
                    {
                        Results = new ParameterCollection { ["EntityCollection"] = new EntityCollection([State]) }
                    }
                    : new OrganizationResponse());
            client.Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
                .Callback<Entity, CancellationToken>((update, _) =>
                {
                    Writes.Add(update);
                    foreach (var field in update.Attributes) State[field.Key] = field.Value;
                }).Returns(Task.CompletedTask);
            var pool = new Mock<IDataverseConnectionPool>();
            pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);
            var metadata = new Mock<ICachedMetadataProvider>();
            metadata.Setup(m => m.GetEntitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
                [new EntitySummary { LogicalName = "account", DisplayName = "Account", SchemaName = "Account", ObjectTypeCode = 1 }]);
            Service = new ViewService(pool.Object, metadata.Object, new InactiveFakeShakedownGuard(), NullLogger<ViewService>.Instance);
        }

        public void AssertNoMutation()
        {
            Assert.Empty(Writes);
            Assert.DoesNotContain(Requests, request => request is PublishXmlRequest or AddSolutionComponentRequest);
            Assert.Equal(_originalLayout, State.GetAttributeValue<string>("layoutxml"));
            Assert.Equal(Fetch, State.GetAttributeValue<string>("fetchxml"));
        }
    }
}
