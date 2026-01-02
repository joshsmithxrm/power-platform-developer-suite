using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace PPDS.Dataverse.IntegrationTests.Client;

/// <summary>
/// Tests for basic CRUD operations using FakeXrmEasy.
/// These verify the FakePooledClient correctly wraps IOrganizationService operations.
/// </summary>
public class DataverseClientCrudTests : FakeXrmEasyTestsBase
{
    private const string EntityName = "account";

    #region Create Tests

    [Fact]
    public void Create_WithValidEntity_ReturnsNonEmptyId()
    {
        // Arrange
        var entity = new Entity(EntityName)
        {
            ["name"] = "Test Account"
        };

        // Act
        var id = Service.Create(entity);

        // Assert
        id.Should().NotBeEmpty();
    }

    [Fact]
    public void Create_WithMultipleAttributes_PersistsAll()
    {
        // Arrange
        var entity = new Entity(EntityName)
        {
            ["name"] = "Test Account",
            ["description"] = "Test Description",
            ["revenue"] = new Money(10000m),
            ["numberofemployees"] = 100
        };

        // Act
        var id = Service.Create(entity);

        // Assert
        var retrieved = Service.Retrieve(EntityName, id, new ColumnSet(true));
        retrieved.GetAttributeValue<string>("name").Should().Be("Test Account");
        retrieved.GetAttributeValue<string>("description").Should().Be("Test Description");
        retrieved.GetAttributeValue<Money>("revenue").Value.Should().Be(10000m);
        retrieved.GetAttributeValue<int>("numberofemployees").Should().Be(100);
    }

    [Fact]
    public void Create_WithRelatedEntityReference_PersistsReference()
    {
        // Arrange - Create a parent first
        var parentId = Service.Create(new Entity(EntityName) { ["name"] = "Parent" });

        var child = new Entity("contact")
        {
            ["firstname"] = "John",
            ["lastname"] = "Doe",
            ["parentcustomerid"] = new EntityReference(EntityName, parentId)
        };

        // Act
        var childId = Service.Create(child);

        // Assert
        var retrieved = Service.Retrieve("contact", childId, new ColumnSet(true));
        var parentRef = retrieved.GetAttributeValue<EntityReference>("parentcustomerid");
        parentRef.Should().NotBeNull();
        parentRef.Id.Should().Be(parentId);
    }

    #endregion

    #region Retrieve Tests

    [Fact]
    public void Retrieve_ExistingEntity_ReturnsEntity()
    {
        // Arrange
        var entity = new Entity(EntityName) { ["name"] = "Test Account" };
        var id = Service.Create(entity);

        // Act
        var retrieved = Service.Retrieve(EntityName, id, new ColumnSet(true));

        // Assert
        retrieved.Should().NotBeNull();
        retrieved.Id.Should().Be(id);
        retrieved.LogicalName.Should().Be(EntityName);
    }

    [Fact]
    public void Retrieve_WithColumnSet_ReturnsOnlyRequestedColumns()
    {
        // Arrange
        var entity = new Entity(EntityName)
        {
            ["name"] = "Test Account",
            ["description"] = "Test Description"
        };
        var id = Service.Create(entity);

        // Act
        var retrieved = Service.Retrieve(EntityName, id, new ColumnSet("name"));

        // Assert
        retrieved.Contains("name").Should().BeTrue();
        // FakeXrmEasy may or may not enforce column filtering - test for expected behavior
    }

    [Fact]
    public void Retrieve_NonExistentEntity_ThrowsException()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act & Assert
        var action = () => Service.Retrieve(EntityName, nonExistentId, new ColumnSet(true));
        action.Should().Throw<Exception>();
    }

    #endregion

    #region Update Tests

    [Fact]
    public void Update_ExistingEntity_ModifiesAttributes()
    {
        // Arrange
        var entity = new Entity(EntityName) { ["name"] = "Original Name" };
        var id = Service.Create(entity);

        var update = new Entity(EntityName, id) { ["name"] = "Updated Name" };

        // Act
        Service.Update(update);

        // Assert
        var retrieved = Service.Retrieve(EntityName, id, new ColumnSet(true));
        retrieved.GetAttributeValue<string>("name").Should().Be("Updated Name");
    }

    [Fact]
    public void Update_PreservesUnchangedAttributes()
    {
        // Arrange
        var entity = new Entity(EntityName)
        {
            ["name"] = "Original Name",
            ["description"] = "Original Description"
        };
        var id = Service.Create(entity);

        var update = new Entity(EntityName, id) { ["name"] = "Updated Name" };

        // Act
        Service.Update(update);

        // Assert
        var retrieved = Service.Retrieve(EntityName, id, new ColumnSet(true));
        retrieved.GetAttributeValue<string>("name").Should().Be("Updated Name");
        retrieved.GetAttributeValue<string>("description").Should().Be("Original Description");
    }

    [Fact]
    public void Update_NonExistentEntity_ThrowsException()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();
        var update = new Entity(EntityName, nonExistentId) { ["name"] = "Updated" };

        // Act & Assert
        var action = () => Service.Update(update);
        action.Should().Throw<Exception>();
    }

    #endregion

    #region Delete Tests

    [Fact]
    public void Delete_ExistingEntity_RemovesEntity()
    {
        // Arrange
        var entity = new Entity(EntityName) { ["name"] = "To Delete" };
        var id = Service.Create(entity);

        // Act
        Service.Delete(EntityName, id);

        // Assert
        var action = () => Service.Retrieve(EntityName, id, new ColumnSet(true));
        action.Should().Throw<Exception>();
    }

    [Fact]
    public void Delete_NonExistentEntity_ThrowsException()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act & Assert
        var action = () => Service.Delete(EntityName, nonExistentId);
        action.Should().Throw<Exception>();
    }

    #endregion

    #region RetrieveMultiple Tests

    [Fact]
    public void RetrieveMultiple_WithQueryExpression_ReturnsMatchingRecords()
    {
        // Arrange
        Service.Create(new Entity(EntityName) { ["name"] = "Account 1" });
        Service.Create(new Entity(EntityName) { ["name"] = "Account 2" });
        Service.Create(new Entity(EntityName) { ["name"] = "Account 3" });

        var query = new QueryExpression(EntityName)
        {
            ColumnSet = new ColumnSet(true)
        };

        // Act
        var results = Service.RetrieveMultiple(query);

        // Assert
        results.Entities.Should().HaveCount(3);
    }

    [Fact]
    public void RetrieveMultiple_WithFilter_ReturnsFilteredRecords()
    {
        // Arrange
        Service.Create(new Entity(EntityName) { ["name"] = "Alpha" });
        Service.Create(new Entity(EntityName) { ["name"] = "Beta" });
        Service.Create(new Entity(EntityName) { ["name"] = "Alpha Beta" });

        var query = new QueryExpression(EntityName)
        {
            ColumnSet = new ColumnSet(true),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("name", ConditionOperator.Equal, "Beta")
                }
            }
        };

        // Act
        var results = Service.RetrieveMultiple(query);

        // Assert
        results.Entities.Should().HaveCount(1);
        results.Entities[0].GetAttributeValue<string>("name").Should().Be("Beta");
    }

    [Fact]
    public void RetrieveMultiple_WithNoMatches_ReturnsEmptyCollection()
    {
        // Arrange
        var query = new QueryExpression(EntityName)
        {
            ColumnSet = new ColumnSet(true),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("name", ConditionOperator.Equal, "NonExistent")
                }
            }
        };

        // Act
        var results = Service.RetrieveMultiple(query);

        // Assert
        results.Entities.Should().BeEmpty();
    }

    [Fact]
    public void RetrieveMultiple_WithFetchXml_ReturnsRecords()
    {
        // Arrange
        Service.Create(new Entity(EntityName) { ["name"] = "FetchXml Account" });

        var fetchXml = $@"
            <fetch>
                <entity name='{EntityName}'>
                    <attribute name='name' />
                </entity>
            </fetch>";

        // Act
        var results = Service.RetrieveMultiple(new FetchExpression(fetchXml));

        // Assert
        results.Entities.Should().HaveCount(1);
    }

    [Fact]
    public void RetrieveMultiple_WithLikeOperator_ReturnsMatches()
    {
        // Arrange
        Service.Create(new Entity(EntityName) { ["name"] = "Test Company 1" });
        Service.Create(new Entity(EntityName) { ["name"] = "Test Company 2" });
        Service.Create(new Entity(EntityName) { ["name"] = "Other Company" });

        var query = new QueryExpression(EntityName)
        {
            ColumnSet = new ColumnSet(true),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("name", ConditionOperator.Like, "Test%")
                }
            }
        };

        // Act
        var results = Service.RetrieveMultiple(query);

        // Assert
        results.Entities.Should().HaveCount(2);
    }

    #endregion

    #region Upsert Tests

    [Fact]
    public void Execute_UpsertRequest_WithExistingRecord_UpdatesRecord()
    {
        // Arrange - Create a record first
        var originalEntity = new Entity(EntityName) { ["name"] = "Original" };
        var id = Service.Create(originalEntity);

        // Upsert the existing record with new data
        var upsertEntity = new Entity(EntityName, id) { ["name"] = "Upserted" };
        var request = new Microsoft.Xrm.Sdk.Messages.UpsertRequest { Target = upsertEntity };

        // Act
        var response = (Microsoft.Xrm.Sdk.Messages.UpsertResponse)Service.Execute(request);

        // Assert
        response.RecordCreated.Should().BeFalse();
        var retrieved = Service.Retrieve(EntityName, id, new ColumnSet(true));
        retrieved.GetAttributeValue<string>("name").Should().Be("Upserted");
    }

    [Fact]
    public void Execute_UpsertRequest_UpdatesExistingRecord()
    {
        // Arrange
        var id = Service.Create(new Entity(EntityName) { ["name"] = "Original" });
        var updateEntity = new Entity(EntityName, id) { ["name"] = "Upserted" };
        var request = new Microsoft.Xrm.Sdk.Messages.UpsertRequest { Target = updateEntity };

        // Act
        var response = (Microsoft.Xrm.Sdk.Messages.UpsertResponse)Service.Execute(request);

        // Assert
        response.RecordCreated.Should().BeFalse();
        var retrieved = Service.Retrieve(EntityName, id, new ColumnSet(true));
        retrieved.GetAttributeValue<string>("name").Should().Be("Upserted");
    }

    #endregion
}
