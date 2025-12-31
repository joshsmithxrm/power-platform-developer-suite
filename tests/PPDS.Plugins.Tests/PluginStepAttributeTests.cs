using System.Reflection;
using Xunit;

namespace PPDS.Plugins.Tests;

public class PluginStepAttributeTests
{
    #region Constructor Tests

    [Fact]
    public void DefaultConstructor_SetsDefaultValues()
    {
        var attribute = new PluginStepAttribute();

        Assert.Equal(string.Empty, attribute.Message);
        Assert.Equal(string.Empty, attribute.EntityLogicalName);
        Assert.Equal(PluginMode.Synchronous, attribute.Mode);
        Assert.Equal(1, attribute.ExecutionOrder);
        Assert.Null(attribute.FilteringAttributes);
        Assert.Null(attribute.Name);
        Assert.Null(attribute.UnsecureConfiguration);
        Assert.Null(attribute.Description);
        Assert.False(attribute.AsyncAutoDelete);
        Assert.Null(attribute.StepId);
        Assert.Null(attribute.SecondaryEntityLogicalName);
    }

    [Fact]
    public void ParameterizedConstructor_SetsRequiredProperties()
    {
        var attribute = new PluginStepAttribute("Create", "account", PluginStage.PostOperation);

        Assert.Equal("Create", attribute.Message);
        Assert.Equal("account", attribute.EntityLogicalName);
        Assert.Equal(PluginStage.PostOperation, attribute.Stage);
    }

    [Fact]
    public void ParameterizedConstructor_KeepsDefaultsForOptionalProperties()
    {
        var attribute = new PluginStepAttribute("Update", "contact", PluginStage.PreOperation);

        Assert.Equal(PluginMode.Synchronous, attribute.Mode);
        Assert.Equal(1, attribute.ExecutionOrder);
        Assert.Null(attribute.FilteringAttributes);
    }

    #endregion

    #region Property Tests

    [Theory]
    [InlineData("Create")]
    [InlineData("Update")]
    [InlineData("Delete")]
    [InlineData("Retrieve")]
    [InlineData("RetrieveMultiple")]
    [InlineData("Associate")]
    [InlineData("Disassociate")]
    public void Message_AcceptsValidMessages(string message)
    {
        var attribute = new PluginStepAttribute { Message = message };
        Assert.Equal(message, attribute.Message);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("contact")]
    [InlineData("custom_entity")]
    [InlineData("none")]
    public void EntityLogicalName_AcceptsValidEntityNames(string entityName)
    {
        var attribute = new PluginStepAttribute { EntityLogicalName = entityName };
        Assert.Equal(entityName, attribute.EntityLogicalName);
    }

    [Fact]
    public void SecondaryEntityLogicalName_CanBeSet()
    {
        var attribute = new PluginStepAttribute
        {
            Message = "Associate",
            EntityLogicalName = "account",
            SecondaryEntityLogicalName = "contact"
        };

        Assert.Equal("contact", attribute.SecondaryEntityLogicalName);
    }

    [Theory]
    [InlineData(PluginStage.PreValidation)]
    [InlineData(PluginStage.PreOperation)]
    [InlineData(PluginStage.PostOperation)]
    public void Stage_AcceptsAllValidStages(PluginStage stage)
    {
        var attribute = new PluginStepAttribute { Stage = stage };
        Assert.Equal(stage, attribute.Stage);
    }

    [Theory]
    [InlineData(PluginMode.Synchronous)]
    [InlineData(PluginMode.Asynchronous)]
    public void Mode_AcceptsAllValidModes(PluginMode mode)
    {
        var attribute = new PluginStepAttribute { Mode = mode };
        Assert.Equal(mode, attribute.Mode);
    }

    [Fact]
    public void FilteringAttributes_AcceptsCommaSeparatedList()
    {
        var attribute = new PluginStepAttribute
        {
            FilteringAttributes = "name,telephone1,emailaddress1"
        };

        Assert.Equal("name,telephone1,emailaddress1", attribute.FilteringAttributes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(int.MaxValue)]
    public void ExecutionOrder_AcceptsPositiveValues(int order)
    {
        var attribute = new PluginStepAttribute { ExecutionOrder = order };
        Assert.Equal(order, attribute.ExecutionOrder);
    }

    [Fact]
    public void Name_CanBeSetToCustomValue()
    {
        const string customName = "MyPlugin: Create of account";
        var attribute = new PluginStepAttribute { Name = customName };
        Assert.Equal(customName, attribute.Name);
    }

    [Fact]
    public void UnsecureConfiguration_CanBeSet()
    {
        const string config = "{\"setting\": \"value\"}";
        var attribute = new PluginStepAttribute { UnsecureConfiguration = config };
        Assert.Equal(config, attribute.UnsecureConfiguration);
    }

    [Fact]
    public void Description_CanBeSet()
    {
        const string description = "Logs account changes to audit table";
        var attribute = new PluginStepAttribute { Description = description };
        Assert.Equal(description, attribute.Description);
    }

    [Fact]
    public void AsyncAutoDelete_CanBeSet()
    {
        var attribute = new PluginStepAttribute { AsyncAutoDelete = true };
        Assert.True(attribute.AsyncAutoDelete);
    }

    [Fact]
    public void AsyncAutoDelete_DefaultsToFalse()
    {
        var attribute = new PluginStepAttribute();
        Assert.False(attribute.AsyncAutoDelete);
    }

    [Fact]
    public void StepId_CanBeSetForMultiStepPlugins()
    {
        var attribute = new PluginStepAttribute { StepId = "step1" };
        Assert.Equal("step1", attribute.StepId);
    }

    #endregion

    #region Attribute Usage Tests

    [Fact]
    public void AttributeUsage_AllowsMultipleOnClass()
    {
        var attributeUsage = typeof(PluginStepAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(attributeUsage);
        Assert.True(attributeUsage.AllowMultiple);
    }

    [Fact]
    public void AttributeUsage_TargetsClassOnly()
    {
        var attributeUsage = typeof(PluginStepAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(attributeUsage);
        Assert.Equal(AttributeTargets.Class, attributeUsage.ValidOn);
    }

    [Fact]
    public void AttributeUsage_DoesNotInherit()
    {
        var attributeUsage = typeof(PluginStepAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(attributeUsage);
        Assert.False(attributeUsage.Inherited);
    }

    [Fact]
    public void Attribute_IsSealed()
    {
        Assert.True(typeof(PluginStepAttribute).IsSealed);
    }

    #endregion

    #region Real-World Usage Scenarios

    [Fact]
    public void TypicalCreatePluginConfiguration()
    {
        var attribute = new PluginStepAttribute("Create", "account", PluginStage.PostOperation)
        {
            Mode = PluginMode.Asynchronous,
            Name = "AccountCreatePlugin: Create of account"
        };

        Assert.Equal("Create", attribute.Message);
        Assert.Equal("account", attribute.EntityLogicalName);
        Assert.Equal(PluginStage.PostOperation, attribute.Stage);
        Assert.Equal(PluginMode.Asynchronous, attribute.Mode);
    }

    [Fact]
    public void TypicalUpdatePluginWithFilteringAttributes()
    {
        var attribute = new PluginStepAttribute("Update", "contact", PluginStage.PreOperation)
        {
            FilteringAttributes = "firstname,lastname,emailaddress1",
            ExecutionOrder = 10
        };

        Assert.Equal("Update", attribute.Message);
        Assert.Equal("firstname,lastname,emailaddress1", attribute.FilteringAttributes);
        Assert.Equal(10, attribute.ExecutionOrder);
    }

    [Fact]
    public void PluginWithConfigurationAndDescription()
    {
        var attribute = new PluginStepAttribute("Create", "email", PluginStage.PostOperation)
        {
            UnsecureConfiguration = "{\"retryCount\": 3}",
            Description = "Sends email notifications on record creation"
        };

        Assert.NotNull(attribute.UnsecureConfiguration);
        Assert.NotNull(attribute.Description);
    }

    [Fact]
    public void AsyncPluginWithAutoDelete()
    {
        var attribute = new PluginStepAttribute("Update", "account", PluginStage.PostOperation)
        {
            Mode = PluginMode.Asynchronous,
            AsyncAutoDelete = true,
            Description = "Async audit logging with auto-cleanup"
        };

        Assert.Equal(PluginMode.Asynchronous, attribute.Mode);
        Assert.True(attribute.AsyncAutoDelete);
        Assert.NotNull(attribute.Description);
    }

    #endregion
}
