using FluentAssertions;
using Microsoft.Xrm.Sdk;
using PPDS.Migration.Import;
using PPDS.Migration.Import.Handlers;
using PPDS.Migration.Models;
using Xunit;

namespace PPDS.Migration.Tests.Import.Handlers;

[Trait("Category", "Unit")]
public class IncidentHandlerTests
{
    private readonly IncidentHandler _handler = new();

    private static ImportContext CreateContext() =>
        new(
            new MigrationData(),
            new ExecutionPlan(),
            new ImportOptions(),
            new IdMappingCollection(),
            new FieldMetadataCollection(new Dictionary<string, Dictionary<string, FieldValidity>>()));

    [Fact]
    public void CanHandle_Incident_ReturnsTrue()
    {
        _handler.CanHandle("incident").Should().BeTrue();
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
        var record = new Entity("incident") { Id = Guid.NewGuid() };
        record["title"] = "Test Case";

        var result = _handler.Transform(record, CreateContext());

        result.Attributes.Should().ContainKey("title");
        result.Should().BeSameAs(record);
    }

    [Fact]
    public void ReturnsNullForActiveIncident()
    {
        var record = new Entity("incident") { Id = Guid.NewGuid() };
        record["statecode"] = new OptionSetValue(0);
        record["statuscode"] = new OptionSetValue(1);

        var result = _handler.GetTransition(record, CreateContext());

        result.Should().BeNull();
    }

    [Fact]
    public void EmitsCloseIncidentForResolvedState()
    {
        var record = new Entity("incident") { Id = Guid.NewGuid() };
        record["statecode"] = new OptionSetValue(1);
        record["statuscode"] = new OptionSetValue(5);

        var result = _handler.GetTransition(record, CreateContext());

        result.Should().NotBeNull();
        result!.SdkMessage.Should().Be("CloseIncident");
        result.EntityName.Should().Be("incident");
        result.RecordId.Should().Be(record.Id);
        result.StateCode.Should().Be(1);
        result.StatusCode.Should().Be(5);
        result.MessageData.Should().ContainKey("IncidentResolution");
        result.MessageData.Should().ContainKey("Status");

        var resolution = (Entity)result.MessageData!["IncidentResolution"];
        resolution.LogicalName.Should().Be("incidentresolution");
        resolution.GetAttributeValue<EntityReference>("incidentid").Id.Should().Be(record.Id);
        resolution.GetAttributeValue<string>("subject").Should().Be("Resolved (migrated)");

        var status = (OptionSetValue)result.MessageData["Status"];
        status.Value.Should().Be(5);
    }

    [Fact]
    public void EmitsCloseIncidentForCanceledState()
    {
        var record = new Entity("incident") { Id = Guid.NewGuid() };
        record["statecode"] = new OptionSetValue(2);
        record["statuscode"] = new OptionSetValue(6);

        var result = _handler.GetTransition(record, CreateContext());

        result.Should().NotBeNull();
        result!.SdkMessage.Should().Be("CloseIncident");
        result.StateCode.Should().Be(2);
        result.StatusCode.Should().Be(6);

        var resolution = (Entity)result.MessageData!["IncidentResolution"];
        resolution.GetAttributeValue<string>("subject").Should().Be("Canceled (migrated)");
    }
}
