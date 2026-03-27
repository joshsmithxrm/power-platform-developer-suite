using System;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using PPDS.Migration.Import;
using PPDS.Migration.Import.Handlers;
using PPDS.Migration.Models;
using Xunit;

namespace PPDS.Migration.Tests.Import.Handlers;

[Trait("Category", "Unit")]
public class BusinessUnitHandlerTests
{
    private readonly BusinessUnitHandler _handler = new();

    private static ImportContext CreateContext(Guid? targetRootBuId = null)
    {
        var context = new ImportContext(
            new MigrationData(),
            new ExecutionPlan(),
            new ImportOptions(),
            new IdMappingCollection(),
            new FieldMetadataCollection(new Dictionary<string, Dictionary<string, FieldValidity>>()));
        context.TargetRootBusinessUnitId = targetRootBuId;
        return context;
    }

    [Fact]
    public void CanHandle_ReturnsTrueForBusinessUnit()
    {
        _handler.CanHandle("businessunit").Should().BeTrue();
    }

    [Fact]
    public void CanHandle_ReturnsFalseForOtherEntity()
    {
        _handler.CanHandle("account").Should().BeFalse();
    }

    [Fact]
    public void MapsRootBusinessUnit()
    {
        // AC-18: Root BU (no parentbusinessunitid) gets ID remapped to target root BU
        var sourceRootId = Guid.NewGuid();
        var targetRootId = Guid.NewGuid();
        var record = new Entity("businessunit") { Id = sourceRootId };
        record["businessunitid"] = sourceRootId;
        record["name"] = "Root BU";
        var context = CreateContext(targetRootId);

        var result = _handler.Transform(record, context);

        result.Id.Should().Be(targetRootId);
        result.GetAttributeValue<Guid>("businessunitid").Should().Be(targetRootId);
    }

    [Fact]
    public void DoesNotMapChildBusinessUnit()
    {
        // Child BU (has parentbusinessunitid) is not remapped
        var childId = Guid.NewGuid();
        var targetRootId = Guid.NewGuid();
        var record = new Entity("businessunit") { Id = childId };
        record["businessunitid"] = childId;
        record["name"] = "Child BU";
        record["parentbusinessunitid"] = new EntityReference("businessunit", Guid.NewGuid());
        var context = CreateContext(targetRootId);

        var result = _handler.Transform(record, context);

        result.Id.Should().Be(childId);
        result.GetAttributeValue<Guid>("businessunitid").Should().Be(childId);
    }
}
