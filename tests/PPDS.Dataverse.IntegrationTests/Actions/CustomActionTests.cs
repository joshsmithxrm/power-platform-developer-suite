using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Xunit;

namespace PPDS.Dataverse.IntegrationTests.Actions;

/// <summary>
/// Tests for custom action and built-in message execution using FakeXrmEasy.
/// </summary>
/// <remarks>
/// Note: FakeXrmEasy's open-source version (RPL-1.5) has limited message support.
/// The following are NOT supported in the open-source version:
/// - WhoAmIRequest (requires commercial license)
/// - SetStateRequest (requires commercial license)
/// - ExecuteMultipleRequest (requires commercial license)
/// - Associate/Disassociate (requires metadata registration)
///
/// These tests cover what IS supported in the open-source version.
/// For full coverage, live integration tests in PPDS.LiveTests cover the complete API.
/// </remarks>
public class CustomActionTests : FakeXrmEasyTestsBase
{
    #region Request/Response Tests

    [Fact]
    public void Execute_RetrieveRequest_ReturnsEntity()
    {
        // Arrange
        var entity = new Entity("account") { ["name"] = "Test Account" };
        var id = Service.Create(entity);

        var request = new RetrieveRequest
        {
            Target = new EntityReference("account", id),
            ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet(true)
        };

        // Act
        var response = (RetrieveResponse)Service.Execute(request);

        // Assert
        response.Should().NotBeNull();
        response.Entity.Should().NotBeNull();
        response.Entity.Id.Should().Be(id);
        response.Entity.GetAttributeValue<string>("name").Should().Be("Test Account");
    }

    [Fact]
    public void Execute_CreateRequest_ReturnsNewId()
    {
        // Arrange
        var request = new CreateRequest
        {
            Target = new Entity("account") { ["name"] = "New Account" }
        };

        // Act
        var response = (CreateResponse)Service.Execute(request);

        // Assert
        response.Should().NotBeNull();
        response.id.Should().NotBeEmpty();
    }

    [Fact]
    public void Execute_UpdateRequest_ModifiesEntity()
    {
        // Arrange
        var entity = new Entity("account") { ["name"] = "Original" };
        var id = Service.Create(entity);

        var request = new UpdateRequest
        {
            Target = new Entity("account", id) { ["name"] = "Updated" }
        };

        // Act
        Service.Execute(request);

        // Assert
        var retrieved = Service.Retrieve("account", id, new Microsoft.Xrm.Sdk.Query.ColumnSet(true));
        retrieved.GetAttributeValue<string>("name").Should().Be("Updated");
    }

    [Fact]
    public void Execute_DeleteRequest_RemovesEntity()
    {
        // Arrange
        var entity = new Entity("account") { ["name"] = "To Delete" };
        var id = Service.Create(entity);

        var request = new DeleteRequest
        {
            Target = new EntityReference("account", id)
        };

        // Act
        Service.Execute(request);

        // Assert
        var action = () => Service.Retrieve("account", id, new Microsoft.Xrm.Sdk.Query.ColumnSet(true));
        action.Should().Throw<Exception>();
    }

    #endregion

    #region Upsert Tests

    // Note: FakeXrmEasy's UpsertRequest for new records (create path) has issues.
    // The update path works correctly. For full upsert coverage, see live tests.

    [Fact]
    public void Execute_UpsertRequest_UpdatesExistingRecord()
    {
        // Arrange - Create existing record first
        var entity = new Entity("account") { ["name"] = "Original" };
        var id = Service.Create(entity);

        var updateEntity = new Entity("account", id) { ["name"] = "Upserted Update" };
        var request = new UpsertRequest { Target = updateEntity };

        // Act
        var response = (UpsertResponse)Service.Execute(request);

        // Assert
        response.Should().NotBeNull();
        response.RecordCreated.Should().BeFalse("Existing record should be updated");

        var retrieved = Service.Retrieve("account", id, new Microsoft.Xrm.Sdk.Query.ColumnSet(true));
        retrieved.GetAttributeValue<string>("name").Should().Be("Upserted Update");
    }

    #endregion

    #region RetrieveMultiple Request Tests

    [Fact]
    public void Execute_RetrieveMultipleRequest_ReturnsResults()
    {
        // Arrange
        Service.Create(new Entity("account") { ["name"] = "Account 1" });
        Service.Create(new Entity("account") { ["name"] = "Account 2" });

        var request = new RetrieveMultipleRequest
        {
            Query = new Microsoft.Xrm.Sdk.Query.QueryExpression("account")
            {
                ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet(true)
            }
        };

        // Act
        var response = (RetrieveMultipleResponse)Service.Execute(request);

        // Assert
        response.Should().NotBeNull();
        response.EntityCollection.Should().NotBeNull();
        response.EntityCollection.Entities.Should().HaveCount(2);
    }

    [Fact]
    public void Execute_RetrieveMultipleRequest_WithFilter_ReturnsFilteredResults()
    {
        // Arrange
        Service.Create(new Entity("account") { ["name"] = "Alpha Corp" });
        Service.Create(new Entity("account") { ["name"] = "Beta Corp" });
        Service.Create(new Entity("account") { ["name"] = "Gamma Corp" });

        var query = new Microsoft.Xrm.Sdk.Query.QueryExpression("account")
        {
            ColumnSet = new Microsoft.Xrm.Sdk.Query.ColumnSet("name"),
            Criteria = new Microsoft.Xrm.Sdk.Query.FilterExpression
            {
                Conditions =
                {
                    new Microsoft.Xrm.Sdk.Query.ConditionExpression("name", Microsoft.Xrm.Sdk.Query.ConditionOperator.Equal, "Beta Corp")
                }
            }
        };

        var request = new RetrieveMultipleRequest { Query = query };

        // Act
        var response = (RetrieveMultipleResponse)Service.Execute(request);

        // Assert
        response.EntityCollection.Entities.Should().HaveCount(1);
        response.EntityCollection.Entities[0].GetAttributeValue<string>("name").Should().Be("Beta Corp");
    }

    #endregion

    #region Batch Operations via Service Methods

    [Fact]
    public void Service_CreateUpdateDelete_BatchWorkflow()
    {
        // This tests a typical batch workflow using individual operations

        // Create
        var entity = new Entity("account") { ["name"] = "Batch Test" };
        var id = Service.Create(entity);
        id.Should().NotBeEmpty();

        // Update
        var update = new Entity("account", id) { ["name"] = "Batch Updated" };
        Service.Update(update);

        // Verify update
        var retrieved = Service.Retrieve("account", id, new Microsoft.Xrm.Sdk.Query.ColumnSet(true));
        retrieved.GetAttributeValue<string>("name").Should().Be("Batch Updated");

        // Delete
        Service.Delete("account", id);

        // Verify delete
        var action = () => Service.Retrieve("account", id, new Microsoft.Xrm.Sdk.Query.ColumnSet(true));
        action.Should().Throw<Exception>();
    }

    [Fact]
    public void Service_MultipleSequentialCreates_AllSucceed()
    {
        // Arrange
        var entities = Enumerable.Range(1, 5).Select(i => new Entity("account")
        {
            ["name"] = $"Concurrent Account {i}"
        }).ToList();

        // Act
        var ids = entities.Select(e => Service.Create(e)).ToList();

        // Assert
        ids.Should().HaveCount(5);
        ids.All(id => id != Guid.Empty).Should().BeTrue();

        // Verify all records exist
        foreach (var id in ids)
        {
            var retrieved = Service.Retrieve("account", id, new Microsoft.Xrm.Sdk.Query.ColumnSet("name"));
            retrieved.Should().NotBeNull();
        }
    }

    #endregion
}
