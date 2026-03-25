using FluentAssertions;
using Microsoft.Xrm.Sdk;
using PPDS.Migration.Import;
using PPDS.Migration.Import.Handlers;
using PPDS.Migration.Models;
using Xunit;

namespace PPDS.Migration.Tests.Import.Handlers;

[Trait("Category", "Unit")]
public class OpportunityHandlerTests
{
    private readonly OpportunityHandler _handler = new();

    private static ImportContext CreateContext() =>
        new(
            new MigrationData(),
            new ExecutionPlan(),
            new ImportOptions(),
            new IdMappingCollection(),
            new FieldMetadataCollection(new Dictionary<string, Dictionary<string, FieldValidity>>()));

    [Fact]
    public void CanHandle_Opportunity_ReturnsTrue()
    {
        _handler.CanHandle("opportunity").Should().BeTrue();
    }

    [Fact]
    public void CanHandle_OtherEntity_ReturnsFalse()
    {
        _handler.CanHandle("account").Should().BeFalse();
    }

    [Fact]
    public void StripsStateStatusFromRecord()
    {
        var record = new Entity("opportunity") { Id = Guid.NewGuid() };
        record["statecode"] = new OptionSetValue(1);
        record["statuscode"] = new OptionSetValue(3);
        record["name"] = "Test Opp";

        var result = _handler.Transform(record, CreateContext());

        result.Attributes.Should().NotContainKey("statecode");
        result.Attributes.Should().NotContainKey("statuscode");
        result.Attributes.Should().ContainKey("name");
    }

    [Fact]
    public void ReturnsNullForActiveOpportunity()
    {
        var record = new Entity("opportunity") { Id = Guid.NewGuid() };
        record["statecode"] = new OptionSetValue(0);
        record["statuscode"] = new OptionSetValue(1);

        var result = _handler.GetTransition(record, CreateContext());

        result.Should().BeNull();
    }

    [Fact]
    public void EmitsWinOpportunityForStateCodeOne()
    {
        var record = new Entity("opportunity") { Id = Guid.NewGuid() };
        record["statecode"] = new OptionSetValue(1);
        record["statuscode"] = new OptionSetValue(3);

        var result = _handler.GetTransition(record, CreateContext());

        result.Should().NotBeNull();
        result!.SdkMessage.Should().Be("WinOpportunity");
        result.EntityName.Should().Be("opportunity");
        result.RecordId.Should().Be(record.Id);
        result.StateCode.Should().Be(1);
        result.StatusCode.Should().Be(3);
        result.MessageData.Should().ContainKey("OpportunityClose");
        result.MessageData.Should().ContainKey("Status");

        var closeEntity = result.MessageData!["OpportunityClose"] as Entity;
        closeEntity.Should().NotBeNull();
        closeEntity!.LogicalName.Should().Be("opportunityclose");
        closeEntity.GetAttributeValue<EntityReference>("opportunityid").Id.Should().Be(record.Id);
        closeEntity.GetAttributeValue<string>("subject").Should().Be("Won (migrated)");

        var status = result.MessageData["Status"] as OptionSetValue;
        status.Should().NotBeNull();
        status!.Value.Should().Be(3);
    }

    [Fact]
    public void EmitsLoseOpportunityForStateCodeTwo()
    {
        var record = new Entity("opportunity") { Id = Guid.NewGuid() };
        record["statecode"] = new OptionSetValue(2);
        record["statuscode"] = new OptionSetValue(4);

        var result = _handler.GetTransition(record, CreateContext());

        result.Should().NotBeNull();
        result!.SdkMessage.Should().Be("LoseOpportunity");
        result.EntityName.Should().Be("opportunity");
        result.RecordId.Should().Be(record.Id);
        result.StateCode.Should().Be(2);
        result.StatusCode.Should().Be(4);
        result.MessageData.Should().ContainKey("OpportunityClose");
        result.MessageData.Should().ContainKey("Status");

        var closeEntity = result.MessageData!["OpportunityClose"] as Entity;
        closeEntity.Should().NotBeNull();
        closeEntity!.LogicalName.Should().Be("opportunityclose");
        closeEntity.GetAttributeValue<EntityReference>("opportunityid").Id.Should().Be(record.Id);
        closeEntity.GetAttributeValue<string>("subject").Should().Be("Lost (migrated)");

        var status = result.MessageData["Status"] as OptionSetValue;
        status.Should().NotBeNull();
        status!.Value.Should().Be(4);
    }
}
