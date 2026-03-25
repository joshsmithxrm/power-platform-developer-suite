using FluentAssertions;
using Microsoft.Xrm.Sdk;
using PPDS.Migration.Import;
using PPDS.Migration.Import.Handlers;
using PPDS.Migration.Models;
using Xunit;

namespace PPDS.Migration.Tests.Import.Handlers;

[Trait("Category", "Unit")]
public class SalesOrderHandlerTests
{
    private readonly SalesOrderHandler _handler = new();
    private readonly ImportContext _context;

    public SalesOrderHandlerTests()
    {
        _context = new ImportContext(
            new MigrationData(),
            new ExecutionPlan(),
            new ImportOptions(),
            new IdMappingCollection(),
            new FieldMetadataCollection(new Dictionary<string, Dictionary<string, FieldValidity>>()));
    }

    [Fact]
    public void CanHandle_SalesOrder_ReturnsTrue()
    {
        _handler.CanHandle("salesorder").Should().BeTrue();
    }

    [Fact]
    public void CanHandle_OtherEntity_ReturnsFalse()
    {
        _handler.CanHandle("account").Should().BeFalse();
    }

    [Fact]
    public void StripsStateStatusFromRecord()
    {
        var record = new Entity("salesorder") { Id = Guid.NewGuid() };
        record["statecode"] = new OptionSetValue(2);
        record["statuscode"] = new OptionSetValue(100001);
        record["name"] = "Test Order";

        var result = _handler.Transform(record, _context);

        result.Attributes.Should().NotContainKey("statecode");
        result.Attributes.Should().NotContainKey("statuscode");
        result.Attributes.Should().ContainKey("name");
    }

    [Fact]
    public void ReturnsNullForActiveOrder()
    {
        var record = new Entity("salesorder") { Id = Guid.NewGuid() };
        record["statecode"] = new OptionSetValue(0);
        record["statuscode"] = new OptionSetValue(1);

        var result = _handler.GetTransition(record, _context);

        result.Should().BeNull();
    }

    [Fact]
    public void EmitsFulfillForStateCodeThree()
    {
        var record = new Entity("salesorder") { Id = Guid.NewGuid() };
        record["statecode"] = new OptionSetValue(3);
        record["statuscode"] = new OptionSetValue(100001);

        var result = _handler.GetTransition(record, _context);

        result.Should().NotBeNull();
        result!.SdkMessage.Should().Be("FulfillSalesOrder");
        result.EntityName.Should().Be("salesorder");
        result.RecordId.Should().Be(record.Id);
        result.StateCode.Should().Be(3);
        result.StatusCode.Should().Be(100001);
        result.MessageData.Should().ContainKey("OrderClose");
        result.MessageData.Should().ContainKey("Status");

        var closeEntity = result.MessageData!["OrderClose"] as Entity;
        closeEntity.Should().NotBeNull();
        closeEntity!.LogicalName.Should().Be("orderclose");
        closeEntity.GetAttributeValue<EntityReference>("salesorderid").Id.Should().Be(record.Id);
        closeEntity.GetAttributeValue<string>("subject").Should().Be("Fulfilled (migrated)");

        var status = result.MessageData["Status"] as OptionSetValue;
        status.Should().NotBeNull();
        status!.Value.Should().Be(100001);
    }

    [Fact]
    public void EmitsCancelForStateCodeTwo()
    {
        var record = new Entity("salesorder") { Id = Guid.NewGuid() };
        record["statecode"] = new OptionSetValue(2);
        record["statuscode"] = new OptionSetValue(100002);

        var result = _handler.GetTransition(record, _context);

        result.Should().NotBeNull();
        result!.SdkMessage.Should().Be("CancelSalesOrder");
        result.EntityName.Should().Be("salesorder");
        result.RecordId.Should().Be(record.Id);
        result.StateCode.Should().Be(2);
        result.StatusCode.Should().Be(100002);
        result.MessageData.Should().ContainKey("OrderClose");
        result.MessageData.Should().ContainKey("Status");

        var closeEntity = result.MessageData!["OrderClose"] as Entity;
        closeEntity.Should().NotBeNull();
        closeEntity!.LogicalName.Should().Be("orderclose");
        closeEntity.GetAttributeValue<EntityReference>("salesorderid").Id.Should().Be(record.Id);
        closeEntity.GetAttributeValue<string>("subject").Should().Be("Canceled (migrated)");

        var status = result.MessageData["Status"] as OptionSetValue;
        status.Should().NotBeNull();
        status!.Value.Should().Be(100002);
    }

    [Fact]
    public void ReturnsNullForSubmittedOrder()
    {
        var record = new Entity("salesorder") { Id = Guid.NewGuid() };
        record["statecode"] = new OptionSetValue(1);
        record["statuscode"] = new OptionSetValue(2);

        var result = _handler.GetTransition(record, _context);

        result.Should().BeNull();
    }
}
