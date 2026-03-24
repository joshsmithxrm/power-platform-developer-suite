using System.Reflection;
using Xunit;

namespace PPDS.Plugins.Tests;

public class CustomApiAttributeTests
{
    #region Constructor Tests

    [Fact]
    public void DefaultConstructor_SetsDefaultValues()
    {
        var attribute = new CustomApiAttribute();

        Assert.Equal(string.Empty, attribute.UniqueName);
        Assert.Equal(string.Empty, attribute.DisplayName);
        Assert.Null(attribute.Name);
        Assert.Null(attribute.Description);
        Assert.Equal(ApiBindingType.Global, attribute.BindingType);
        Assert.Null(attribute.BoundEntity);
        Assert.False(attribute.IsFunction);
        Assert.False(attribute.IsPrivate);
        Assert.Null(attribute.ExecutePrivilegeName);
        Assert.Equal(ApiProcessingStepType.None, attribute.AllowedProcessingStepType);
    }

    #endregion

    #region Property Tests

    [Fact]
    public void UniqueName_CanBeSet()
    {
        var attribute = new CustomApiAttribute { UniqueName = "ppds_MyCustomApi" };
        Assert.Equal("ppds_MyCustomApi", attribute.UniqueName);
    }

    [Fact]
    public void DisplayName_CanBeSet()
    {
        var attribute = new CustomApiAttribute { DisplayName = "My Custom API" };
        Assert.Equal("My Custom API", attribute.DisplayName);
    }

    [Fact]
    public void Name_CanBeSet()
    {
        var attribute = new CustomApiAttribute { Name = "ppds_MyCustomApi" };
        Assert.Equal("ppds_MyCustomApi", attribute.Name);
    }

    [Fact]
    public void Description_CanBeSet()
    {
        const string description = "Performs a custom business operation";
        var attribute = new CustomApiAttribute { Description = description };
        Assert.Equal(description, attribute.Description);
    }

    [Theory]
    [InlineData(ApiBindingType.Global)]
    [InlineData(ApiBindingType.Entity)]
    [InlineData(ApiBindingType.EntityCollection)]
    public void BindingType_AcceptsAllValidValues(ApiBindingType bindingType)
    {
        var attribute = new CustomApiAttribute { BindingType = bindingType };
        Assert.Equal(bindingType, attribute.BindingType);
    }

    [Fact]
    public void BindingType_DefaultsToGlobal()
    {
        var attribute = new CustomApiAttribute();
        Assert.Equal(ApiBindingType.Global, attribute.BindingType);
    }

    [Fact]
    public void BoundEntity_CanBeSet()
    {
        var attribute = new CustomApiAttribute
        {
            BindingType = ApiBindingType.Entity,
            BoundEntity = "account"
        };
        Assert.Equal("account", attribute.BoundEntity);
    }

    [Fact]
    public void BoundEntity_DefaultsToNull()
    {
        var attribute = new CustomApiAttribute();
        Assert.Null(attribute.BoundEntity);
    }

    [Fact]
    public void IsFunction_DefaultsToFalse()
    {
        var attribute = new CustomApiAttribute();
        Assert.False(attribute.IsFunction);
    }

    [Fact]
    public void IsFunction_CanBeSetToTrue()
    {
        var attribute = new CustomApiAttribute { IsFunction = true };
        Assert.True(attribute.IsFunction);
    }

    [Fact]
    public void IsPrivate_DefaultsToFalse()
    {
        var attribute = new CustomApiAttribute();
        Assert.False(attribute.IsPrivate);
    }

    [Fact]
    public void IsPrivate_CanBeSetToTrue()
    {
        var attribute = new CustomApiAttribute { IsPrivate = true };
        Assert.True(attribute.IsPrivate);
    }

    [Fact]
    public void ExecutePrivilegeName_DefaultsToNull()
    {
        var attribute = new CustomApiAttribute();
        Assert.Null(attribute.ExecutePrivilegeName);
    }

    [Fact]
    public void ExecutePrivilegeName_CanBeSet()
    {
        var attribute = new CustomApiAttribute { ExecutePrivilegeName = "ppds_ExecuteMyApi" };
        Assert.Equal("ppds_ExecuteMyApi", attribute.ExecutePrivilegeName);
    }

    [Theory]
    [InlineData(ApiProcessingStepType.None)]
    [InlineData(ApiProcessingStepType.AsyncOnly)]
    [InlineData(ApiProcessingStepType.SyncAndAsync)]
    public void AllowedProcessingStepType_AcceptsAllValidValues(ApiProcessingStepType stepType)
    {
        var attribute = new CustomApiAttribute { AllowedProcessingStepType = stepType };
        Assert.Equal(stepType, attribute.AllowedProcessingStepType);
    }

    [Fact]
    public void AllowedProcessingStepType_DefaultsToNone()
    {
        var attribute = new CustomApiAttribute();
        Assert.Equal(ApiProcessingStepType.None, attribute.AllowedProcessingStepType);
    }

    #endregion

    #region Attribute Usage Tests

    [Fact]
    public void AttributeUsage_DoesNotAllowMultiple()
    {
        var attributeUsage = typeof(CustomApiAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(attributeUsage);
        Assert.False(attributeUsage.AllowMultiple);
    }

    [Fact]
    public void AttributeUsage_TargetsClassOnly()
    {
        var attributeUsage = typeof(CustomApiAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(attributeUsage);
        Assert.Equal(AttributeTargets.Class, attributeUsage.ValidOn);
    }

    [Fact]
    public void AttributeUsage_DoesNotInherit()
    {
        var attributeUsage = typeof(CustomApiAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(attributeUsage);
        Assert.False(attributeUsage.Inherited);
    }

    [Fact]
    public void Attribute_IsSealed()
    {
        Assert.True(typeof(CustomApiAttribute).IsSealed);
    }

    #endregion

    #region Real-World Usage Scenarios

    [Fact]
    public void TypicalGlobalActionConfiguration()
    {
        var attribute = new CustomApiAttribute
        {
            UniqueName = "ppds_ProcessOrder",
            DisplayName = "Process Order",
            Description = "Processes an order and returns a confirmation number"
        };

        Assert.Equal("ppds_ProcessOrder", attribute.UniqueName);
        Assert.Equal("Process Order", attribute.DisplayName);
        Assert.Equal(ApiBindingType.Global, attribute.BindingType);
        Assert.False(attribute.IsFunction);
    }

    [Fact]
    public void TypicalEntityBoundFunctionConfiguration()
    {
        var attribute = new CustomApiAttribute
        {
            UniqueName = "ppds_CalculateDiscount",
            DisplayName = "Calculate Discount",
            BindingType = ApiBindingType.Entity,
            BoundEntity = "opportunity",
            IsFunction = true
        };

        Assert.Equal(ApiBindingType.Entity, attribute.BindingType);
        Assert.Equal("opportunity", attribute.BoundEntity);
        Assert.True(attribute.IsFunction);
    }

    [Fact]
    public void PrivateApiWithPrivilege()
    {
        var attribute = new CustomApiAttribute
        {
            UniqueName = "ppds_InternalOperation",
            DisplayName = "Internal Operation",
            IsPrivate = true,
            ExecutePrivilegeName = "ppds_ExecuteInternalOperation"
        };

        Assert.True(attribute.IsPrivate);
        Assert.Equal("ppds_ExecuteInternalOperation", attribute.ExecutePrivilegeName);
    }

    #endregion
}
