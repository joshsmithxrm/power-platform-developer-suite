using FluentAssertions;
using Microsoft.Xrm.Sdk;
using PPDS.Migration.Import;
using PPDS.Migration.Import.Handlers;
using PPDS.Migration.Models;
using Xunit;

namespace PPDS.Migration.Tests.Import.Handlers;

[Trait("Category", "Unit")]
public class QuoteHandlerTests
{
    private readonly QuoteHandler _handler = new();

    private static ImportContext CreateContext() =>
        new(
            new MigrationData(),
            new ExecutionPlan(),
            new ImportOptions(),
            new IdMappingCollection(),
            new FieldMetadataCollection(new Dictionary<string, Dictionary<string, FieldValidity>>()));

    [Fact]
    public void CanHandle_Quote_ReturnsTrue()
    {
        _handler.CanHandle("quote").Should().BeTrue();
    }

    [Fact]
    public void CanHandle_OtherEntity_ReturnsFalse()
    {
        _handler.CanHandle("account").Should().BeFalse();
    }

    [Fact]
    public void TransformIsPassThrough()
    {
        // statecode/statuscode stripping is handled by TieredImporter before Transform is called
        var record = new Entity("quote") { Id = Guid.NewGuid() };
        record["name"] = "Test Quote";

        var result = _handler.Transform(record, CreateContext());

        result.Attributes.Should().ContainKey("name");
        result.Should().BeSameAs(record);
    }

    [Fact]
    public void ReturnsNullForDraftQuote()
    {
        var record = new Entity("quote") { Id = Guid.NewGuid() };
        record["statecode"] = new OptionSetValue(0);
        record["statuscode"] = new OptionSetValue(1);

        var result = _handler.GetTransition(record, CreateContext());

        result.Should().BeNull();
    }

    [Fact]
    public void ReturnsNullForActiveQuote()
    {
        var record = new Entity("quote") { Id = Guid.NewGuid() };
        record["statecode"] = new OptionSetValue(1);
        record["statuscode"] = new OptionSetValue(2);

        var result = _handler.GetTransition(record, CreateContext());

        result.Should().BeNull();
    }

    [Fact]
    public void EmitsWinQuoteForStateCodeTwo()
    {
        var record = new Entity("quote") { Id = Guid.NewGuid() };
        record["statecode"] = new OptionSetValue(2);
        record["statuscode"] = new OptionSetValue(4);

        var result = _handler.GetTransition(record, CreateContext());

        result.Should().NotBeNull();
        result!.SdkMessage.Should().Be("WinQuote");
        result.EntityName.Should().Be("quote");
        result.RecordId.Should().Be(record.Id);
        result.StateCode.Should().Be(2);
        result.StatusCode.Should().Be(4);
        result.MessageData.Should().ContainKey("QuoteClose");
        result.MessageData.Should().ContainKey("Status");

        var closeEntity = result.MessageData!["QuoteClose"] as Entity;
        closeEntity.Should().NotBeNull();
        closeEntity!.LogicalName.Should().Be("quoteclose");
        closeEntity.GetAttributeValue<EntityReference>("quoteid").Id.Should().Be(record.Id);
        closeEntity.GetAttributeValue<string>("subject").Should().Be("Won (migrated)");

        var status = result.MessageData["Status"] as OptionSetValue;
        status.Should().NotBeNull();
        status!.Value.Should().Be(4);
    }

    [Fact]
    public void EmitsCloseQuoteForStateCodeThree()
    {
        var record = new Entity("quote") { Id = Guid.NewGuid() };
        record["statecode"] = new OptionSetValue(3);
        record["statuscode"] = new OptionSetValue(7);

        var result = _handler.GetTransition(record, CreateContext());

        result.Should().NotBeNull();
        result!.SdkMessage.Should().Be("CloseQuote");
        result.EntityName.Should().Be("quote");
        result.RecordId.Should().Be(record.Id);
        result.StateCode.Should().Be(3);
        result.StatusCode.Should().Be(7);
        result.MessageData.Should().ContainKey("QuoteClose");
        result.MessageData.Should().ContainKey("Status");

        var closeEntity = result.MessageData!["QuoteClose"] as Entity;
        closeEntity.Should().NotBeNull();
        closeEntity!.LogicalName.Should().Be("quoteclose");
        closeEntity.GetAttributeValue<EntityReference>("quoteid").Id.Should().Be(record.Id);
        closeEntity.GetAttributeValue<string>("subject").Should().Be("Closed (migrated)");

        var status = result.MessageData["Status"] as OptionSetValue;
        status.Should().NotBeNull();
        status!.Value.Should().Be(7);
    }
}
