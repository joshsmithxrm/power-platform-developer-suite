using System.Reflection;
using Xunit;

namespace PPDS.Plugins.Tests;

public class CustomApiParameterAttributeTests
{
    #region Constructor Tests

    [Fact]
    public void DefaultConstructor_SetsDefaultValues()
    {
        var attribute = new CustomApiParameterAttribute();

        Assert.Equal(string.Empty, attribute.Name);
        Assert.Null(attribute.UniqueName);
        Assert.Null(attribute.DisplayName);
        Assert.Null(attribute.Description);
        Assert.Equal(default(ApiParameterType), attribute.Type);
        Assert.Null(attribute.LogicalEntityName);
        Assert.False(attribute.IsOptional);
        Assert.Equal(ParameterDirection.Input, attribute.Direction);
    }

    #endregion

    #region Property Tests

    [Fact]
    public void Name_CanBeSet()
    {
        var attribute = new CustomApiParameterAttribute { Name = "OrderId" };
        Assert.Equal("OrderId", attribute.Name);
    }

    [Fact]
    public void UniqueName_DefaultsToNull()
    {
        var attribute = new CustomApiParameterAttribute();
        Assert.Null(attribute.UniqueName);
    }

    [Fact]
    public void UniqueName_CanBeSet()
    {
        var attribute = new CustomApiParameterAttribute { UniqueName = "ppds_OrderId" };
        Assert.Equal("ppds_OrderId", attribute.UniqueName);
    }

    [Fact]
    public void DisplayName_DefaultsToNull()
    {
        var attribute = new CustomApiParameterAttribute();
        Assert.Null(attribute.DisplayName);
    }

    [Fact]
    public void DisplayName_CanBeSet()
    {
        var attribute = new CustomApiParameterAttribute { DisplayName = "Order ID" };
        Assert.Equal("Order ID", attribute.DisplayName);
    }

    [Fact]
    public void Description_DefaultsToNull()
    {
        var attribute = new CustomApiParameterAttribute();
        Assert.Null(attribute.Description);
    }

    [Fact]
    public void Description_CanBeSet()
    {
        var attribute = new CustomApiParameterAttribute { Description = "The unique identifier of the order" };
        Assert.Equal("The unique identifier of the order", attribute.Description);
    }

    [Theory]
    [InlineData(ApiParameterType.Boolean)]
    [InlineData(ApiParameterType.DateTime)]
    [InlineData(ApiParameterType.Decimal)]
    [InlineData(ApiParameterType.Entity)]
    [InlineData(ApiParameterType.EntityCollection)]
    [InlineData(ApiParameterType.EntityReference)]
    [InlineData(ApiParameterType.Float)]
    [InlineData(ApiParameterType.Integer)]
    [InlineData(ApiParameterType.Money)]
    [InlineData(ApiParameterType.Picklist)]
    [InlineData(ApiParameterType.String)]
    [InlineData(ApiParameterType.StringArray)]
    [InlineData(ApiParameterType.Guid)]
    public void Type_AcceptsAllValidValues(ApiParameterType paramType)
    {
        var attribute = new CustomApiParameterAttribute { Type = paramType };
        Assert.Equal(paramType, attribute.Type);
    }

    [Fact]
    public void LogicalEntityName_DefaultsToNull()
    {
        var attribute = new CustomApiParameterAttribute();
        Assert.Null(attribute.LogicalEntityName);
    }

    [Fact]
    public void LogicalEntityName_CanBeSet()
    {
        var attribute = new CustomApiParameterAttribute
        {
            Type = ApiParameterType.EntityReference,
            LogicalEntityName = "account"
        };
        Assert.Equal("account", attribute.LogicalEntityName);
    }

    [Fact]
    public void IsOptional_DefaultsToFalse()
    {
        var attribute = new CustomApiParameterAttribute();
        Assert.False(attribute.IsOptional);
    }

    [Fact]
    public void IsOptional_CanBeSetToTrue()
    {
        var attribute = new CustomApiParameterAttribute { IsOptional = true };
        Assert.True(attribute.IsOptional);
    }

    [Theory]
    [InlineData(ParameterDirection.Input)]
    [InlineData(ParameterDirection.Output)]
    public void Direction_AcceptsAllValidValues(ParameterDirection direction)
    {
        var attribute = new CustomApiParameterAttribute { Direction = direction };
        Assert.Equal(direction, attribute.Direction);
    }

    [Fact]
    public void Direction_DefaultsToInput()
    {
        var attribute = new CustomApiParameterAttribute();
        Assert.Equal(ParameterDirection.Input, attribute.Direction);
    }

    #endregion

    #region Attribute Usage Tests

    [Fact]
    public void AttributeUsage_AllowsMultiple()
    {
        var attributeUsage = typeof(CustomApiParameterAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(attributeUsage);
        Assert.True(attributeUsage.AllowMultiple);
    }

    [Fact]
    public void AttributeUsage_TargetsClassOnly()
    {
        var attributeUsage = typeof(CustomApiParameterAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(attributeUsage);
        Assert.Equal(AttributeTargets.Class, attributeUsage.ValidOn);
    }

    [Fact]
    public void AttributeUsage_DoesNotInherit()
    {
        var attributeUsage = typeof(CustomApiParameterAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(attributeUsage);
        Assert.False(attributeUsage.Inherited);
    }

    [Fact]
    public void Attribute_IsSealed()
    {
        Assert.True(typeof(CustomApiParameterAttribute).IsSealed);
    }

    #endregion

    #region Real-World Usage Scenarios

    [Fact]
    public void TypicalStringInputParameter()
    {
        var attribute = new CustomApiParameterAttribute
        {
            Name = "OrderNumber",
            DisplayName = "Order Number",
            Type = ApiParameterType.String,
            Direction = ParameterDirection.Input
        };

        Assert.Equal("OrderNumber", attribute.Name);
        Assert.Equal(ApiParameterType.String, attribute.Type);
        Assert.Equal(ParameterDirection.Input, attribute.Direction);
        Assert.False(attribute.IsOptional);
    }

    [Fact]
    public void TypicalEntityReferenceInputParameter()
    {
        var attribute = new CustomApiParameterAttribute
        {
            Name = "Target",
            DisplayName = "Account",
            Type = ApiParameterType.EntityReference,
            LogicalEntityName = "account",
            Direction = ParameterDirection.Input
        };

        Assert.Equal(ApiParameterType.EntityReference, attribute.Type);
        Assert.Equal("account", attribute.LogicalEntityName);
    }

    [Fact]
    public void TypicalOutputParameter()
    {
        var attribute = new CustomApiParameterAttribute
        {
            Name = "ConfirmationNumber",
            DisplayName = "Confirmation Number",
            Type = ApiParameterType.String,
            Direction = ParameterDirection.Output
        };

        Assert.Equal(ParameterDirection.Output, attribute.Direction);
        Assert.Equal(ApiParameterType.String, attribute.Type);
    }

    [Fact]
    public void OptionalInputParameter()
    {
        var attribute = new CustomApiParameterAttribute
        {
            Name = "Notes",
            Type = ApiParameterType.String,
            Direction = ParameterDirection.Input,
            IsOptional = true
        };

        Assert.True(attribute.IsOptional);
    }

    #endregion
}
