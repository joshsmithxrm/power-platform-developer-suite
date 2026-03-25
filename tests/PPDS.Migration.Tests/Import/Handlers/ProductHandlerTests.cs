using FluentAssertions;
using Microsoft.Xrm.Sdk;
using PPDS.Migration.Import;
using PPDS.Migration.Import.Handlers;
using PPDS.Migration.Models;
using Xunit;

namespace PPDS.Migration.Tests.Import.Handlers;

[Trait("Category", "Unit")]
public class ProductHandlerTests
{
    private readonly ProductHandler _handler = new();
    private readonly ImportContext _context;

    public ProductHandlerTests()
    {
        _context = new ImportContext(
            new MigrationData(),
            new ExecutionPlan(),
            new ImportOptions(),
            new IdMappingCollection(),
            new FieldMetadataCollection(new Dictionary<string, Dictionary<string, FieldValidity>>()));
    }

    [Fact]
    public void CanHandle_Product_ReturnsTrue()
    {
        _handler.CanHandle("product").Should().BeTrue();
    }

    [Fact]
    public void CanHandle_OtherEntity_ReturnsFalse()
    {
        _handler.CanHandle("account").Should().BeFalse();
    }

    [Fact]
    public void CascadesSkipToChildren()
    {
        // Track parent product as failed
        var parentId = Guid.NewGuid();
        _handler.TrackFailure(parentId);

        // Create child product referencing failed parent
        var childRecord = new Entity("product") { Id = Guid.NewGuid() };
        childRecord["parentproductid"] = new EntityReference("product", parentId);

        var result = _handler.ShouldSkip(childRecord, _context);

        result.Should().BeTrue();
    }

    [Fact]
    public void ReportsRootCauseInSkipReason()
    {
        // Track parent product as failed
        var parentId = Guid.NewGuid();
        _handler.TrackFailure(parentId);

        // Create child product referencing failed parent
        var childId = Guid.NewGuid();
        var childRecord = new Entity("product") { Id = childId };
        childRecord["parentproductid"] = new EntityReference("product", parentId);

        // Skip the child — this should also track the child as failed
        _handler.ShouldSkip(childRecord, _context);

        // Create grandchild product referencing the (now-failed) child
        var grandchildRecord = new Entity("product") { Id = Guid.NewGuid() };
        grandchildRecord["parentproductid"] = new EntityReference("product", childId);

        // Grandchild should also be skipped (cascading failure)
        var grandchildResult = _handler.ShouldSkip(grandchildRecord, _context);

        grandchildResult.Should().BeTrue();
    }

    [Fact]
    public void DoesNotSkipProductWithHealthyParent()
    {
        // Parent is NOT tracked as failed
        var healthyParentId = Guid.NewGuid();

        var childRecord = new Entity("product") { Id = Guid.NewGuid() };
        childRecord["parentproductid"] = new EntityReference("product", healthyParentId);

        var result = _handler.ShouldSkip(childRecord, _context);

        result.Should().BeFalse();
    }

    [Fact]
    public void DoesNotSkipProductWithNoParent()
    {
        var record = new Entity("product") { Id = Guid.NewGuid() };

        var result = _handler.ShouldSkip(record, _context);

        result.Should().BeFalse();
    }

    [Fact]
    public void ReturnsNullForDraftProduct()
    {
        var record = new Entity("product") { Id = Guid.NewGuid() };
        record["statecode"] = new OptionSetValue(0);
        record["statuscode"] = new OptionSetValue(0);

        var result = _handler.GetTransition(record, _context);

        result.Should().BeNull();
    }

    [Fact]
    public void ReturnsSetStateForNonDraftProduct()
    {
        var record = new Entity("product") { Id = Guid.NewGuid() };
        record["statecode"] = new OptionSetValue(1);
        record["statuscode"] = new OptionSetValue(1);

        var result = _handler.GetTransition(record, _context);

        result.Should().NotBeNull();
        result!.SdkMessage.Should().BeNull(); // null means SetStateRequest
        result.EntityName.Should().Be("product");
        result.RecordId.Should().Be(record.Id);
        result.StateCode.Should().Be(1);
        result.StatusCode.Should().Be(1);
    }
}
