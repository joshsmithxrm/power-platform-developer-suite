using System.Xml.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Forms;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Forms;

public sealed class FormFieldBindingTests
{
    [Theory]
    [InlineData("new_canonicalfield")]
    [InlineData("NEW_CANONICALFIELD")]
    [InlineData("new_CanonicalField")]
    public async Task Add_UsesMetadataLogicalName(string requested)
    {
        var h = new FormFieldBindingHarness();
        await h.Service.AddFieldAsync(new("account", "Fixture", "General", [requested]));
        var doc = h.WrittenDocument();
        var control = doc.Descendants("control").Last();
        Assert.Equal(FormFieldBindingHarness.Canonical, (string?)control.Attribute("datafieldname"));
        Assert.Equal(FormFieldBindingHarness.Canonical, (string?)control.Attribute("id"));
        Assert.Equal("Canonical display label", (string?)control.Parent!.Element("labels")!.Element("label")!.Attribute("description"));
        control.Parent!.Parent!.Remove();
        Assert.True(XNode.DeepEquals(XDocument.Parse(h.Original), doc));
    }

    [Fact]
    public async Task Add_ExistingCaseVariantsAreUnchanged()
    {
        var h = new FormFieldBindingHarness();
        await h.Service.AddFieldAsync(new("account", "Fixture", "General", ["NAME", "name"]));
        h.AssertNoWrites();
    }

    [Theory]
    [InlineData("new_canonicalfield")]
    [InlineData("NEW_CANONICALFIELD")]
    [InlineData("new_CanonicalField")]
    public async Task Reorder_CanonicalizesOnlyNewCells(string requested)
    {
        var h = new FormFieldBindingHarness();
        await h.Service.ReorderFieldsAsync(new("account", "Fixture", "General", [requested, "NAME"]));
        var original = XDocument.Parse(h.Original);
        var doc = h.WrittenDocument();
        var controls = doc.Descendants("control").ToList();
        Assert.Equal(new[] { FormFieldBindingHarness.Canonical, "name", null }, controls.Select(c => (string?)c.Attribute("datafieldname")));
        Assert.Equal(FormFieldBindingHarness.Canonical, (string?)controls[0].Attribute("id"));
        Assert.Equal("Canonical display label", (string?)controls[0].Parent!.Element("labels")!.Element("label")!.Attribute("description"));
        Assert.True(XNode.DeepEquals(original.Descendants("cell").First(), controls[1].Parent));
        Assert.True(XNode.DeepEquals(original.Descendants("control").Single(c => (string?)c.Attribute("id") == "notescontrol").Parent, controls[2].Parent));
        Assert.DoesNotContain(doc.Descendants("control"), c => (string?)c.Attribute("datafieldname") == "telephone1");
        // Removing only the rows from both documents leaves identical surrounding XML/references.
        original.Descendants("rows").Single().Remove();
        doc.Descendants("rows").Single().Remove();
        Assert.True(XNode.DeepEquals(original, doc));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reorder_ExistingFieldNeedsNoSupportedMetadata(bool unsupported)
    {
        var h = new FormFieldBindingHarness();
        h.Attributes.Clear();
        if (unsupported) h.Attributes.Add(new AttributeMetadataDto
            { LogicalName = "name", SchemaName = "Name", DisplayName = "Name", AttributeType = "BigInt" });
        await h.Service.ReorderFieldsAsync(new("account", "Fixture", "General", ["NAME"]));
        Assert.True(XNode.DeepEquals(XDocument.Parse(h.Original).Descendants("cell").First(), h.WrittenDocument().Descendants("cell").First()));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unsupported")]
    public async Task Reorder_RowlessInvalidField_DoesNotWrite(string field)
    {
        var h = new FormFieldBindingHarness(rowless: true);
        var error = await Record.ExceptionAsync(() => h.Service.ReorderFieldsAsync(
            new("account", "Fixture", "General", [field], "solution", true)));
        h.AssertNoWrites();
        Assert.IsAssignableFrom<PpdsException>(error);
    }

    [Fact]
    public async Task Reorder_RowlessValidField_PreservesNoNewCellBehavior()
    {
        var h = new FormFieldBindingHarness(rowless: true);
        await h.Service.ReorderFieldsAsync(new("account", "Fixture", "General", ["NEW_CANONICALFIELD"]));
        Assert.True(XNode.DeepEquals(XDocument.Parse(h.Original), h.WrittenDocument()));
    }

    [Theory]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "unsupported")]
    [InlineData(true, "unsupported")]
    [InlineData(false, "")]
    [InlineData(true, "")]
    public async Task InvalidFields_DoNotWrite(bool reorder, string field)
    {
        var h = new FormFieldBindingHarness();
        string[] fields = field.Length == 0 ? [] : ["name", field];
        var error = await Record.ExceptionAsync(() => reorder
            ? h.Service.ReorderFieldsAsync(new("account", "Fixture", "General", fields, "solution", true))
            : h.Service.AddFieldAsync(new("account", "Fixture", "General", fields, "solution", true)));
        Assert.IsAssignableFrom<PpdsException>(error);
        h.AssertNoWrites();
    }
}

internal sealed class FormFieldBindingHarness
{
    internal const string Canonical = "new_CanonicalField";
    internal string Original { get; }
    internal List<Entity> Writes { get; } = [];
    internal List<OrganizationRequest> Requests { get; } = [];
    internal List<AttributeMetadataDto> Attributes { get; } = [];
    internal FormService Service { get; }
    internal Mock<IEnvironmentConfirmationService> Confirmation { get; } = new();

    internal FormFieldBindingHarness(bool rowless = false, string? originalXml = null)
    {
        var doc = XDocument.Parse(originalXml ?? TimelineFormFixture.Xml);
        if (originalXml is null)
        {
            var existing = doc.Descendants("control").First();
            existing.SetAttributeValue("id", "existing_custom_control_id");
            existing.SetAttributeValue("disabled", "true");
            doc.Root!.Add(new XElement("controlDescriptions",
                new XElement("controlDescription", new XAttribute("forControl", "existing_custom_control_id"))));
            FormXmlEditor.AddField(doc, "General", "telephone1", ClassIdResolver.ResolveForField("String"), "Existing phone label");
        }
        if (rowless) doc.Descendants("rows").Single().Remove();
        Original = doc.ToString(SaveOptions.DisableFormatting);
        var formId = Guid.NewGuid();
        var state = new Entity("systemform", formId)
        {
            ["formid"] = formId, ["name"] = "Fixture", ["type"] = new OptionSetValue(2),
            ["ismanaged"] = false, ["iscustomizable"] = new BooleanManagedProperty(true), ["formxml"] = Original
        };
        var client = new Mock<IPooledClient>(MockBehavior.Strict);
        client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<OrganizationRequest, CancellationToken>((r, _) => Requests.Add(r))
            .ReturnsAsync((OrganizationRequest r, CancellationToken _) => r is RetrieveUnpublishedMultipleRequest
                ? new RetrieveUnpublishedMultipleResponse { Results = new ParameterCollection { ["EntityCollection"] = new EntityCollection([state]) } }
                : new OrganizationResponse());
        client.Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((entity, _) => Writes.Add(entity)).Returns(Task.CompletedTask);
        var pool = new Mock<IDataverseConnectionPool>();
        pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(client.Object);
        foreach (var name in new[] { "name", "telephone1", Canonical, "unsupported" })
            Attributes.Add(new AttributeMetadataDto { LogicalName = name, SchemaName = "Schema_" + name,
                DisplayName = "Canonical display label", AttributeType = name == "unsupported" ? "BigInt" : "String" });
        var metadata = new Mock<IMetadataQueryService>();
        metadata.Setup(m => m.GetAttributesAsync("account", null, It.IsAny<CancellationToken>())).ReturnsAsync(() => Attributes);
        Confirmation.Setup(c => c.EnsureConfirmedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Service = new FormService(pool.Object, metadata.Object, NullLogger<FormService>.Instance, Confirmation.Object);
    }

    internal XDocument WrittenDocument()
    {
        var write = Assert.Single(Writes);
        Assert.Equal(new[] { "formxml" }, write.Attributes.Keys);
        return XDocument.Parse(write.GetAttributeValue<string>("formxml"));
    }

    internal ServiceProvider CreateProvider() => new ServiceCollection()
        .AddSingleton<IFormService>(Service).AddSingleton(Confirmation.Object).BuildServiceProvider();

    internal void AssertNoWrites()
    {
        Assert.Empty(Writes);
        Assert.DoesNotContain(Requests, r => r is PublishXmlRequest or AddSolutionComponentRequest);
    }
}
