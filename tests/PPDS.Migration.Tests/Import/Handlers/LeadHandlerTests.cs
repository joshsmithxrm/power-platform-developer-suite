using FluentAssertions;
using Microsoft.Xrm.Sdk;
using PPDS.Migration.Import;
using PPDS.Migration.Import.Handlers;
using PPDS.Migration.Models;
using Xunit;

namespace PPDS.Migration.Tests.Import.Handlers;

[Trait("Category", "Unit")]
public class LeadHandlerTests
{
    private readonly LeadHandler _handler = new();
    private readonly ImportContext _context;

    public LeadHandlerTests()
    {
        _context = new ImportContext(
            new MigrationData(),
            new ExecutionPlan(),
            new ImportOptions(),
            new IdMappingCollection(),
            new FieldMetadataCollection(new Dictionary<string, Dictionary<string, FieldValidity>>()));
    }

    [Fact]
    public void CanHandle_Lead_ReturnsTrue()
    {
        _handler.CanHandle("lead").Should().BeTrue();
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
        var record = new Entity("lead") { Id = Guid.NewGuid() };
        record["subject"] = "Test Lead";

        var result = _handler.Transform(record, _context);

        result.Attributes.Should().ContainKey("subject");
        result.Should().BeSameAs(record);
    }

    [Fact]
    public void ReturnsNullForOpenLead()
    {
        var record = new Entity("lead") { Id = Guid.NewGuid() };
        record["statecode"] = new OptionSetValue(0);
        record["statuscode"] = new OptionSetValue(1);

        var result = _handler.GetTransition(record, _context);

        result.Should().BeNull();
    }

    [Fact]
    public void EmitsQualifyWithSuppressedSideEffects()
    {
        var record = new Entity("lead") { Id = Guid.NewGuid() };
        record["statecode"] = new OptionSetValue(1);
        record["statuscode"] = new OptionSetValue(3);

        var result = _handler.GetTransition(record, _context);

        result.Should().NotBeNull();
        result!.SdkMessage.Should().Be("QualifyLead");
        result.EntityName.Should().Be("lead");
        result.RecordId.Should().Be(record.Id);
        result.StateCode.Should().Be(1);
        result.StatusCode.Should().Be(3);
        result.MessageData.Should().ContainKey("LeadId");
        result.MessageData.Should().ContainKey("CreateAccount");
        result.MessageData.Should().ContainKey("CreateContact");
        result.MessageData.Should().ContainKey("CreateOpportunity");
        result.MessageData.Should().ContainKey("Status");

        var leadRef = (EntityReference)result.MessageData!["LeadId"];
        leadRef.Id.Should().Be(record.Id);
        leadRef.LogicalName.Should().Be("lead");

        result.MessageData["CreateAccount"].Should().Be(false);
        result.MessageData["CreateContact"].Should().Be(false);
        result.MessageData["CreateOpportunity"].Should().Be(false);

        var status = (OptionSetValue)result.MessageData["Status"];
        status.Value.Should().Be(3);
    }

    [Fact]
    public void UsesSetStateForDisqualified()
    {
        var record = new Entity("lead") { Id = Guid.NewGuid() };
        record["statecode"] = new OptionSetValue(2);
        record["statuscode"] = new OptionSetValue(5);

        var result = _handler.GetTransition(record, _context);

        result.Should().NotBeNull();
        result!.SdkMessage.Should().BeNull();
        result.EntityName.Should().Be("lead");
        result.RecordId.Should().Be(record.Id);
        result.StateCode.Should().Be(2);
        result.StatusCode.Should().Be(5);
        result.MessageData.Should().BeNull();
    }
}
