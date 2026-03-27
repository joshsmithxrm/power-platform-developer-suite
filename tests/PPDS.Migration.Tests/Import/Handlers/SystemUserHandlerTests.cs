using System;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using PPDS.Migration.Import;
using PPDS.Migration.Import.Handlers;
using PPDS.Migration.Models;
using Xunit;

namespace PPDS.Migration.Tests.Import.Handlers;

[Trait("Category", "Unit")]
public class SystemUserHandlerTests
{
    private readonly SystemUserHandler _handler = new();

    private static ImportContext CreateContext() =>
        new(
            new MigrationData(),
            new ExecutionPlan(),
            new ImportOptions(),
            new IdMappingCollection(),
            new FieldMetadataCollection(new Dictionary<string, Dictionary<string, FieldValidity>>()));

    [Fact]
    public void CanHandle_ReturnsTrueForSystemUser()
    {
        _handler.CanHandle("systemuser").Should().BeTrue();
    }

    [Fact]
    public void CanHandle_IsCaseInsensitive()
    {
        _handler.CanHandle("SystemUser").Should().BeTrue();
    }

    [Fact]
    public void CanHandle_ReturnsFalseForOtherEntity()
    {
        _handler.CanHandle("account").Should().BeFalse();
    }

    [Fact]
    public void SkipsSystemUser()
    {
        // AC-17: Skip SYSTEM user by fullname
        var record = new Entity("systemuser") { Id = Guid.NewGuid() };
        record["fullname"] = "SYSTEM";
        var context = CreateContext();

        _handler.ShouldSkip(record, context).Should().BeTrue();
    }

    [Fact]
    public void SkipsIntegrationUser()
    {
        // AC-17: Skip INTEGRATION user by fullname
        var record = new Entity("systemuser") { Id = Guid.NewGuid() };
        record["fullname"] = "INTEGRATION";
        var context = CreateContext();

        _handler.ShouldSkip(record, context).Should().BeTrue();
    }

    [Fact]
    public void SkipsSupportAndIntegrationUsers()
    {
        // AC-17: Skip support users (accessmode=3) and integration users (accessmode=5)
        var context = CreateContext();

        var supportUser = new Entity("systemuser") { Id = Guid.NewGuid() };
        supportUser["fullname"] = "Some Support User";
        supportUser["accessmode"] = new OptionSetValue(3);
        _handler.ShouldSkip(supportUser, context).Should().BeTrue();

        var integrationAccessUser = new Entity("systemuser") { Id = Guid.NewGuid() };
        integrationAccessUser["fullname"] = "Some Integration User";
        integrationAccessUser["accessmode"] = new OptionSetValue(5);
        _handler.ShouldSkip(integrationAccessUser, context).Should().BeTrue();
    }

    [Fact]
    public void DoesNotSkipRegularUsers()
    {
        var record = new Entity("systemuser") { Id = Guid.NewGuid() };
        record["fullname"] = "John Doe";
        record["accessmode"] = new OptionSetValue(0);
        var context = CreateContext();

        _handler.ShouldSkip(record, context).Should().BeFalse();
    }
}
