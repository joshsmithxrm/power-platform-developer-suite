using System;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using PPDS.Migration.Import;
using PPDS.Migration.Import.Handlers;
using PPDS.Migration.Models;
using Xunit;

namespace PPDS.Migration.Tests.Import.Handlers;

[Trait("Category", "Unit")]
public class ActivityPointerHandlerTests
{
    private readonly ActivityPointerHandler _handler = new();

    private static ImportContext CreateContext() =>
        new(
            new MigrationData(),
            new ExecutionPlan(),
            new ImportOptions(),
            new IdMappingCollection(),
            new FieldMetadataCollection(new Dictionary<string, Dictionary<string, FieldValidity>>()));

    [Fact]
    public void CanHandleReturnsTrueForActivityPointer()
    {
        _handler.CanHandle("activitypointer").Should().BeTrue();
    }

    [Fact]
    public void CanHandleReturnsFalseForEmail()
    {
        _handler.CanHandle("email").Should().BeFalse();
    }

    [Fact]
    public void SkipsBaseTypeRecords()
    {
        // AC-19: Always skip activitypointer base type
        var record = new Entity("activitypointer") { Id = Guid.NewGuid() };
        var context = CreateContext();

        _handler.ShouldSkip(record, context).Should().BeTrue();
    }
}
