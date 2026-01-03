using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace PPDS.Dataverse.IntegrationTests.Query;

/// <summary>
/// Tests for query execution functionality using FakeXrmEasy.
/// Covers paging, aggregates, and complex query scenarios.
/// </summary>
public class QueryExecutionTests : FakeXrmEasyTestsBase
{
    private const string EntityName = "account";

    #region Paging Tests

    [Fact]
    public void RetrieveMultiple_WithPaging_ReturnsPagedResults()
    {
        // Arrange - Create more records than the page size
        for (int i = 1; i <= 15; i++)
        {
            Service.Create(new Entity(EntityName) { ["name"] = $"Account {i:D2}" });
        }

        var query = new QueryExpression(EntityName)
        {
            ColumnSet = new ColumnSet("name"),
            PageInfo = new PagingInfo
            {
                Count = 5,
                PageNumber = 1
            }
        };

        // Act
        var page1 = Service.RetrieveMultiple(query);

        // Assert
        page1.Entities.Should().HaveCount(5, "First page should contain 5 records");
        page1.MoreRecords.Should().BeTrue("There should be more records available");
    }

    [Fact]
    public void RetrieveMultiple_WithPaging_CanRetrieveSecondPage()
    {
        // Arrange - Create records
        for (int i = 1; i <= 15; i++)
        {
            Service.Create(new Entity(EntityName) { ["name"] = $"Account {i:D2}" });
        }

        var query = new QueryExpression(EntityName)
        {
            ColumnSet = new ColumnSet("name"),
            PageInfo = new PagingInfo
            {
                Count = 5,
                PageNumber = 1
            }
        };

        // Get first page
        var page1 = Service.RetrieveMultiple(query);

        // Set up for second page
        query.PageInfo.PageNumber = 2;
        query.PageInfo.PagingCookie = page1.PagingCookie;

        // Act
        var page2 = Service.RetrieveMultiple(query);

        // Assert
        page2.Entities.Should().HaveCount(5, "Second page should contain 5 records");
        page2.MoreRecords.Should().BeTrue("There should still be more records");
    }

    [Fact]
    public void RetrieveMultiple_WithPaging_LastPageIndicatesNoMore()
    {
        // Arrange - Create exactly 10 records
        for (int i = 1; i <= 10; i++)
        {
            Service.Create(new Entity(EntityName) { ["name"] = $"Account {i:D2}" });
        }

        var query = new QueryExpression(EntityName)
        {
            ColumnSet = new ColumnSet("name"),
            PageInfo = new PagingInfo
            {
                Count = 5,
                PageNumber = 2
            }
        };

        // Act - Get second (last) page
        var page2 = Service.RetrieveMultiple(query);

        // Assert
        page2.Entities.Should().HaveCount(5);
        page2.MoreRecords.Should().BeFalse("Last page should indicate no more records");
    }

    [Fact]
    public void RetrieveMultiple_WithTopCount_LimitsResults()
    {
        // Arrange
        for (int i = 1; i <= 10; i++)
        {
            Service.Create(new Entity(EntityName) { ["name"] = $"Account {i}" });
        }

        var query = new QueryExpression(EntityName)
        {
            ColumnSet = new ColumnSet("name"),
            TopCount = 3
        };

        // Act
        var results = Service.RetrieveMultiple(query);

        // Assert
        results.Entities.Should().HaveCount(3, "TopCount should limit results to 3");
    }

    #endregion

    #region Ordering Tests

    [Fact]
    public void RetrieveMultiple_WithOrderBy_SortsAscending()
    {
        // Arrange
        Service.Create(new Entity(EntityName) { ["name"] = "Charlie" });
        Service.Create(new Entity(EntityName) { ["name"] = "Alpha" });
        Service.Create(new Entity(EntityName) { ["name"] = "Bravo" });

        var query = new QueryExpression(EntityName)
        {
            ColumnSet = new ColumnSet("name"),
            Orders = { new OrderExpression("name", OrderType.Ascending) }
        };

        // Act
        var results = Service.RetrieveMultiple(query);

        // Assert
        results.Entities.Should().HaveCount(3);
        results.Entities[0].GetAttributeValue<string>("name").Should().Be("Alpha");
        results.Entities[1].GetAttributeValue<string>("name").Should().Be("Bravo");
        results.Entities[2].GetAttributeValue<string>("name").Should().Be("Charlie");
    }

    [Fact]
    public void RetrieveMultiple_WithOrderBy_SortsDescending()
    {
        // Arrange
        Service.Create(new Entity(EntityName) { ["name"] = "Alpha" });
        Service.Create(new Entity(EntityName) { ["name"] = "Charlie" });
        Service.Create(new Entity(EntityName) { ["name"] = "Bravo" });

        var query = new QueryExpression(EntityName)
        {
            ColumnSet = new ColumnSet("name"),
            Orders = { new OrderExpression("name", OrderType.Descending) }
        };

        // Act
        var results = Service.RetrieveMultiple(query);

        // Assert
        results.Entities.Should().HaveCount(3);
        results.Entities[0].GetAttributeValue<string>("name").Should().Be("Charlie");
        results.Entities[1].GetAttributeValue<string>("name").Should().Be("Bravo");
        results.Entities[2].GetAttributeValue<string>("name").Should().Be("Alpha");
    }

    #endregion

    #region Complex Filter Tests

    [Fact]
    public void RetrieveMultiple_WithAndFilter_ReturnsBothConditions()
    {
        // Arrange - Use simple integer fields that FakeXrmEasy handles well
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Big Old Corp",
            ["numberofemployees"] = 1000,
            ["revenue"] = new Money(500000m)
        });
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Big New Corp",
            ["numberofemployees"] = 1000,
            ["revenue"] = new Money(100000m)
        });
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Small Old Corp",
            ["numberofemployees"] = 10,
            ["revenue"] = new Money(500000m)
        });

        var query = new QueryExpression(EntityName)
        {
            ColumnSet = new ColumnSet(true),
            Criteria = new FilterExpression(LogicalOperator.And)
            {
                Conditions =
                {
                    new ConditionExpression("numberofemployees", ConditionOperator.GreaterEqual, 500),
                    new ConditionExpression("name", ConditionOperator.Like, "%Old%")
                }
            }
        };

        // Act
        var results = Service.RetrieveMultiple(query);

        // Assert - Only "Big Old Corp" matches both conditions
        results.Entities.Should().HaveCount(1);
        results.Entities[0].GetAttributeValue<string>("name").Should().Be("Big Old Corp");
    }

    [Fact]
    public void RetrieveMultiple_WithOrFilter_ReturnsEitherCondition()
    {
        // Arrange
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Alpha Corp",
            ["numberofemployees"] = 100
        });
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Beta Corp",
            ["numberofemployees"] = 200
        });
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Gamma Corp",
            ["numberofemployees"] = 300
        });

        var query = new QueryExpression(EntityName)
        {
            ColumnSet = new ColumnSet(true),
            Criteria = new FilterExpression(LogicalOperator.Or)
            {
                Conditions =
                {
                    new ConditionExpression("name", ConditionOperator.Equal, "Alpha Corp"),
                    new ConditionExpression("numberofemployees", ConditionOperator.GreaterEqual, 300)
                }
            }
        };

        // Act
        var results = Service.RetrieveMultiple(query);

        // Assert - Alpha Corp and Gamma Corp match
        results.Entities.Should().HaveCount(2);
    }

    [Fact]
    public void RetrieveMultiple_WithInOperator_ReturnsMatchingValues()
    {
        // Arrange
        Service.Create(new Entity(EntityName) { ["name"] = "Alpha" });
        Service.Create(new Entity(EntityName) { ["name"] = "Beta" });
        Service.Create(new Entity(EntityName) { ["name"] = "Gamma" });
        Service.Create(new Entity(EntityName) { ["name"] = "Delta" });

        var query = new QueryExpression(EntityName)
        {
            ColumnSet = new ColumnSet("name"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("name", ConditionOperator.In, "Alpha", "Gamma")
                }
            }
        };

        // Act
        var results = Service.RetrieveMultiple(query);

        // Assert
        results.Entities.Should().HaveCount(2);
        results.Entities.Select(e => e.GetAttributeValue<string>("name"))
            .Should().BeEquivalentTo(new[] { "Alpha", "Gamma" });
    }

    [Fact]
    public void RetrieveMultiple_WithBetweenOperator_ReturnsValuesInRange()
    {
        // Arrange
        Service.Create(new Entity(EntityName) { ["name"] = "Small", ["numberofemployees"] = 10 });
        Service.Create(new Entity(EntityName) { ["name"] = "Medium", ["numberofemployees"] = 50 });
        Service.Create(new Entity(EntityName) { ["name"] = "Large", ["numberofemployees"] = 100 });
        Service.Create(new Entity(EntityName) { ["name"] = "Huge", ["numberofemployees"] = 500 });

        var query = new QueryExpression(EntityName)
        {
            ColumnSet = new ColumnSet("name", "numberofemployees"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("numberofemployees", ConditionOperator.Between, 40, 150)
                }
            }
        };

        // Act
        var results = Service.RetrieveMultiple(query);

        // Assert
        results.Entities.Should().HaveCount(2);
        results.Entities.Select(e => e.GetAttributeValue<string>("name"))
            .Should().BeEquivalentTo(new[] { "Medium", "Large" });
    }

    #endregion

    #region FetchXML Advanced Tests

    [Fact]
    public void RetrieveMultiple_FetchXml_WithPaging_ReturnsPagedResults()
    {
        // Arrange
        for (int i = 1; i <= 10; i++)
        {
            Service.Create(new Entity(EntityName) { ["name"] = $"Account {i:D2}" });
        }

        var fetchXml = $@"
            <fetch count='5' page='1'>
                <entity name='{EntityName}'>
                    <attribute name='name' />
                </entity>
            </fetch>";

        // Act
        var results = Service.RetrieveMultiple(new FetchExpression(fetchXml));

        // Assert
        results.Entities.Should().HaveCount(5);
    }

    [Fact]
    public void RetrieveMultiple_FetchXml_WithFilter_ReturnsFilteredResults()
    {
        // Arrange
        Service.Create(new Entity(EntityName) { ["name"] = "Test Alpha" });
        Service.Create(new Entity(EntityName) { ["name"] = "Test Beta" });
        Service.Create(new Entity(EntityName) { ["name"] = "Other Corp" });

        var fetchXml = $@"
            <fetch>
                <entity name='{EntityName}'>
                    <attribute name='name' />
                    <filter>
                        <condition attribute='name' operator='like' value='Test%' />
                    </filter>
                </entity>
            </fetch>";

        // Act
        var results = Service.RetrieveMultiple(new FetchExpression(fetchXml));

        // Assert
        results.Entities.Should().HaveCount(2);
    }

    [Fact]
    public void RetrieveMultiple_FetchXml_WithOrderBy_SortsResults()
    {
        // Arrange
        Service.Create(new Entity(EntityName) { ["name"] = "Charlie" });
        Service.Create(new Entity(EntityName) { ["name"] = "Alpha" });
        Service.Create(new Entity(EntityName) { ["name"] = "Bravo" });

        var fetchXml = $@"
            <fetch>
                <entity name='{EntityName}'>
                    <attribute name='name' />
                    <order attribute='name' />
                </entity>
            </fetch>";

        // Act
        var results = Service.RetrieveMultiple(new FetchExpression(fetchXml));

        // Assert
        results.Entities.Should().HaveCount(3);
        results.Entities[0].GetAttributeValue<string>("name").Should().Be("Alpha");
    }

    #endregion
}
