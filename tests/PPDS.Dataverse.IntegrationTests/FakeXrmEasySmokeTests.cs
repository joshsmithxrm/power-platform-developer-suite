using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace PPDS.Dataverse.IntegrationTests;

/// <summary>
/// Smoke tests to verify FakeXrmEasy infrastructure is working correctly.
/// </summary>
public class FakeXrmEasySmokeTests : FakeXrmEasyTestsBase
{
    [Fact]
    public void Context_IsInitialized()
    {
        Context.Should().NotBeNull();
    }

    [Fact]
    public void Service_IsInitialized()
    {
        Service.Should().NotBeNull();
    }

    [Fact]
    public void Create_WithValidEntity_ReturnsId()
    {
        // Arrange
        var account = new Entity("account")
        {
            ["name"] = "Test Account"
        };

        // Act
        var id = Service.Create(account);

        // Assert
        id.Should().NotBeEmpty();
    }

    [Fact]
    public void Retrieve_AfterCreate_ReturnsEntity()
    {
        // Arrange
        var account = new Entity("account")
        {
            ["name"] = "Test Account"
        };
        var id = Service.Create(account);

        // Act
        var retrieved = Service.Retrieve("account", id, new Microsoft.Xrm.Sdk.Query.ColumnSet(true));

        // Assert
        retrieved.Should().NotBeNull();
        retrieved.GetAttributeValue<string>("name").Should().Be("Test Account");
    }

    [Fact]
    public void InitializeWith_SeedsContext()
    {
        // Arrange
        var accountId = Guid.NewGuid();
        var account = new Entity("account", accountId)
        {
            ["name"] = "Seeded Account"
        };

        // Act
        InitializeWith(account);
        var retrieved = Service.Retrieve("account", accountId, new Microsoft.Xrm.Sdk.Query.ColumnSet(true));

        // Assert
        retrieved.Should().NotBeNull();
        retrieved.GetAttributeValue<string>("name").Should().Be("Seeded Account");
    }
}
