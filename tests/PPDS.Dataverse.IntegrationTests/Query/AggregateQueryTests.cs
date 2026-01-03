using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace PPDS.Dataverse.IntegrationTests.Query;

/// <summary>
/// Tests for aggregate query functionality using FakeXrmEasy.
/// Note: FakeXrmEasy has limited support for aggregates. These tests document what works.
/// </summary>
public class AggregateQueryTests : FakeXrmEasyTestsBase
{
    private const string EntityName = "account";

    #region Count Tests

    [Fact]
    public void FetchXml_Count_ReturnsRecordCount()
    {
        // Arrange
        Service.Create(new Entity(EntityName) { ["name"] = "Account 1" });
        Service.Create(new Entity(EntityName) { ["name"] = "Account 2" });
        Service.Create(new Entity(EntityName) { ["name"] = "Account 3" });

        var fetchXml = $@"
            <fetch aggregate='true'>
                <entity name='{EntityName}'>
                    <attribute name='accountid' alias='count' aggregate='count' />
                </entity>
            </fetch>";

        // Act
        var results = Service.RetrieveMultiple(new FetchExpression(fetchXml));

        // Assert
        results.Entities.Should().HaveCount(1);
        var countValue = results.Entities[0].GetAttributeValue<AliasedValue>("count");
        countValue.Should().NotBeNull();
        ((int)countValue.Value).Should().Be(3);
    }

    [Fact]
    public void FetchXml_CountWithFilter_ReturnsFilteredCount()
    {
        // Arrange - Use a simple string filter that FakeXrmEasy can handle
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Big Account 1"
        });
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Big Account 2"
        });
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Small Account"
        });

        var fetchXml = $@"
            <fetch aggregate='true'>
                <entity name='{EntityName}'>
                    <attribute name='accountid' alias='count' aggregate='count' />
                    <filter>
                        <condition attribute='name' operator='like' value='Big%' />
                    </filter>
                </entity>
            </fetch>";

        // Act
        var results = Service.RetrieveMultiple(new FetchExpression(fetchXml));

        // Assert
        results.Entities.Should().HaveCount(1);
        var countValue = results.Entities[0].GetAttributeValue<AliasedValue>("count");
        countValue.Should().NotBeNull();
        ((int)countValue.Value).Should().Be(2);
    }

    #endregion

    #region Sum Tests

    [Fact]
    public void FetchXml_Sum_ReturnsSumOfValues()
    {
        // Arrange
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Small Corp",
            ["numberofemployees"] = 10
        });
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Medium Corp",
            ["numberofemployees"] = 50
        });
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Large Corp",
            ["numberofemployees"] = 100
        });

        var fetchXml = $@"
            <fetch aggregate='true'>
                <entity name='{EntityName}'>
                    <attribute name='numberofemployees' alias='total' aggregate='sum' />
                </entity>
            </fetch>";

        // Act
        var results = Service.RetrieveMultiple(new FetchExpression(fetchXml));

        // Assert
        results.Entities.Should().HaveCount(1);
        var sumValue = results.Entities[0].GetAttributeValue<AliasedValue>("total");
        sumValue.Should().NotBeNull();
        ((int)sumValue.Value).Should().Be(160);
    }

    #endregion

    #region Average Tests

    [Fact]
    public void FetchXml_Avg_ReturnsAverageOfValues()
    {
        // Arrange
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Corp 1",
            ["numberofemployees"] = 10
        });
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Corp 2",
            ["numberofemployees"] = 20
        });
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Corp 3",
            ["numberofemployees"] = 30
        });

        var fetchXml = $@"
            <fetch aggregate='true'>
                <entity name='{EntityName}'>
                    <attribute name='numberofemployees' alias='average' aggregate='avg' />
                </entity>
            </fetch>";

        // Act
        var results = Service.RetrieveMultiple(new FetchExpression(fetchXml));

        // Assert
        results.Entities.Should().HaveCount(1);
        var avgValue = results.Entities[0].GetAttributeValue<AliasedValue>("average");
        avgValue.Should().NotBeNull();
        // Average of 10, 20, 30 = 20
        Convert.ToDecimal(avgValue.Value).Should().Be(20m);
    }

    #endregion

    #region Min/Max Tests

    [Fact]
    public void FetchXml_Min_ReturnsMinimumValue()
    {
        // Arrange
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Corp 1",
            ["numberofemployees"] = 50
        });
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Corp 2",
            ["numberofemployees"] = 10
        });
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Corp 3",
            ["numberofemployees"] = 100
        });

        var fetchXml = $@"
            <fetch aggregate='true'>
                <entity name='{EntityName}'>
                    <attribute name='numberofemployees' alias='minimum' aggregate='min' />
                </entity>
            </fetch>";

        // Act
        var results = Service.RetrieveMultiple(new FetchExpression(fetchXml));

        // Assert
        results.Entities.Should().HaveCount(1);
        var minValue = results.Entities[0].GetAttributeValue<AliasedValue>("minimum");
        minValue.Should().NotBeNull();
        ((int)minValue.Value).Should().Be(10);
    }

    [Fact]
    public void FetchXml_Max_ReturnsMaximumValue()
    {
        // Arrange
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Corp 1",
            ["numberofemployees"] = 50
        });
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Corp 2",
            ["numberofemployees"] = 10
        });
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Corp 3",
            ["numberofemployees"] = 100
        });

        var fetchXml = $@"
            <fetch aggregate='true'>
                <entity name='{EntityName}'>
                    <attribute name='numberofemployees' alias='maximum' aggregate='max' />
                </entity>
            </fetch>";

        // Act
        var results = Service.RetrieveMultiple(new FetchExpression(fetchXml));

        // Assert
        results.Entities.Should().HaveCount(1);
        var maxValue = results.Entities[0].GetAttributeValue<AliasedValue>("maximum");
        maxValue.Should().NotBeNull();
        ((int)maxValue.Value).Should().Be(100);
    }

    #endregion

    #region Group By Tests

    [Fact]
    public void FetchXml_GroupBy_ReturnsGroupedCounts()
    {
        // Arrange - Use a simple integer field for grouping
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Small 1",
            ["numberofemployees"] = 10
        });
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Small 2",
            ["numberofemployees"] = 10
        });
        Service.Create(new Entity(EntityName)
        {
            ["name"] = "Large 1",
            ["numberofemployees"] = 100
        });

        var fetchXml = $@"
            <fetch aggregate='true'>
                <entity name='{EntityName}'>
                    <attribute name='numberofemployees' groupby='true' alias='size' />
                    <attribute name='accountid' alias='count' aggregate='count' />
                </entity>
            </fetch>";

        // Act
        var results = Service.RetrieveMultiple(new FetchExpression(fetchXml));

        // Assert
        results.Entities.Should().HaveCount(2, "Should have two groups: 10 and 100 employees");

        // Find the small group (numberofemployees = 10)
        var smallGroup = results.Entities.FirstOrDefault(e =>
        {
            var sizeValue = e.GetAttributeValue<AliasedValue>("size");
            return sizeValue?.Value is int size && size == 10;
        });

        smallGroup.Should().NotBeNull();
        var smallCount = smallGroup!.GetAttributeValue<AliasedValue>("count");
        ((int)smallCount.Value).Should().Be(2);
    }

    #endregion
}
