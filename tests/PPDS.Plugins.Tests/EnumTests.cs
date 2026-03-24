using Xunit;

namespace PPDS.Plugins.Tests;

public class EnumTests
{
    #region PluginStage Tests

    [Theory]
    [InlineData(PluginStage.PreValidation, 10)]
    [InlineData(PluginStage.PreOperation, 20)]
    [InlineData(PluginStage.PostOperation, 40)]
    public void PluginStage_ValuesMatchDataverseSDK(PluginStage stage, int expectedValue)
    {
        // These values must match the Dataverse SDK for proper registration
        Assert.Equal(expectedValue, (int)stage);
    }

    [Fact]
    public void PluginStage_HasExactlyThreeValues()
    {
        var values = Enum.GetValues<PluginStage>();
        Assert.Equal(3, values.Length);
    }

    #endregion

    #region PluginMode Tests

    [Theory]
    [InlineData(PluginMode.Synchronous, 0)]
    [InlineData(PluginMode.Asynchronous, 1)]
    public void PluginMode_ValuesMatchDataverseSDK(PluginMode mode, int expectedValue)
    {
        // These values must match the Dataverse SDK for proper registration
        Assert.Equal(expectedValue, (int)mode);
    }

    [Fact]
    public void PluginMode_HasExactlyTwoValues()
    {
        var values = Enum.GetValues<PluginMode>();
        Assert.Equal(2, values.Length);
    }

    #endregion

    #region PluginImageType Tests

    [Theory]
    [InlineData(PluginImageType.PreImage, 0)]
    [InlineData(PluginImageType.PostImage, 1)]
    [InlineData(PluginImageType.Both, 2)]
    public void PluginImageType_ValuesMatchDataverseSDK(PluginImageType imageType, int expectedValue)
    {
        // These values must match the Dataverse SDK for proper registration
        Assert.Equal(expectedValue, (int)imageType);
    }

    [Fact]
    public void PluginImageType_HasExactlyThreeValues()
    {
        var values = Enum.GetValues<PluginImageType>();
        Assert.Equal(3, values.Length);
    }

    #endregion

    #region PluginDeployment Tests

    [Theory]
    [InlineData(PluginDeployment.ServerOnly, 0)]
    [InlineData(PluginDeployment.Offline, 1)]
    [InlineData(PluginDeployment.Both, 2)]
    public void PluginDeployment_ValuesMatchDataverseSDK(PluginDeployment deployment, int expectedValue)
    {
        // These values must match the Dataverse SDK for proper registration
        Assert.Equal(expectedValue, (int)deployment);
    }

    [Fact]
    public void PluginDeployment_HasExactlyThreeValues()
    {
        var values = Enum.GetValues<PluginDeployment>();
        Assert.Equal(3, values.Length);
    }

    #endregion

    #region PluginInvocationSource Tests

    [Theory]
    [InlineData(PluginInvocationSource.Parent, 0)]
    [InlineData(PluginInvocationSource.Child, 1)]
    public void PluginInvocationSource_ValuesMatchDataverseSDK(PluginInvocationSource invocationSource, int expectedValue)
    {
        // These values must match the Dataverse SDK for proper registration
        Assert.Equal(expectedValue, (int)invocationSource);
    }

    [Fact]
    public void PluginInvocationSource_HasExactlyTwoValues()
    {
        var values = Enum.GetValues<PluginInvocationSource>();
        Assert.Equal(2, values.Length);
    }

    #endregion

    #region Cross-Enum Tests

    [Fact]
    public void AllEnums_AreInCorrectNamespace()
    {
        Assert.Equal("PPDS.Plugins", typeof(PluginStage).Namespace);
        Assert.Equal("PPDS.Plugins", typeof(PluginMode).Namespace);
        Assert.Equal("PPDS.Plugins", typeof(PluginImageType).Namespace);
        Assert.Equal("PPDS.Plugins", typeof(PluginDeployment).Namespace);
        Assert.Equal("PPDS.Plugins", typeof(PluginInvocationSource).Namespace);
        Assert.Equal("PPDS.Plugins", typeof(ApiBindingType).Namespace);
        Assert.Equal("PPDS.Plugins", typeof(ApiParameterType).Namespace);
        Assert.Equal("PPDS.Plugins", typeof(ParameterDirection).Namespace);
        Assert.Equal("PPDS.Plugins", typeof(ApiProcessingStepType).Namespace);
    }

    [Theory]
    [InlineData(typeof(PluginStage))]
    [InlineData(typeof(PluginMode))]
    [InlineData(typeof(PluginImageType))]
    [InlineData(typeof(PluginDeployment))]
    [InlineData(typeof(PluginInvocationSource))]
    [InlineData(typeof(ApiBindingType))]
    [InlineData(typeof(ApiParameterType))]
    [InlineData(typeof(ParameterDirection))]
    [InlineData(typeof(ApiProcessingStepType))]
    public void AllEnums_AreValidAndNotEmpty(Type enumType)
    {
        // Verify the enum type exists and is an enum
        Assert.True(enumType.IsEnum);

        // Verify all values exist (will throw if values are missing)
        var values = Enum.GetValues(enumType);
        Assert.True(values.Length > 0);
    }

    #endregion

    #region ApiBindingType Tests

    [Theory]
    [InlineData(ApiBindingType.Global, 0)]
    [InlineData(ApiBindingType.Entity, 1)]
    [InlineData(ApiBindingType.EntityCollection, 2)]
    public void ApiBindingType_ValuesMatchDataverseSDK(ApiBindingType bindingType, int expectedValue)
    {
        // These values must match the Dataverse SDK for proper registration
        Assert.Equal(expectedValue, (int)bindingType);
    }

    [Fact]
    public void ApiBindingType_HasExactlyThreeValues()
    {
        var values = Enum.GetValues<ApiBindingType>();
        Assert.Equal(3, values.Length);
    }

    #endregion

    #region ApiParameterType Tests

    [Theory]
    [InlineData(ApiParameterType.Boolean, 0)]
    [InlineData(ApiParameterType.DateTime, 1)]
    [InlineData(ApiParameterType.Decimal, 2)]
    [InlineData(ApiParameterType.Entity, 3)]
    [InlineData(ApiParameterType.EntityCollection, 4)]
    [InlineData(ApiParameterType.EntityReference, 5)]
    [InlineData(ApiParameterType.Float, 6)]
    [InlineData(ApiParameterType.Integer, 7)]
    [InlineData(ApiParameterType.Money, 8)]
    [InlineData(ApiParameterType.Picklist, 9)]
    [InlineData(ApiParameterType.String, 10)]
    [InlineData(ApiParameterType.StringArray, 11)]
    [InlineData(ApiParameterType.Guid, 12)]
    public void ApiParameterType_ValuesMatchDataverseSDK(ApiParameterType paramType, int expectedValue)
    {
        // These values must match the Dataverse SDK for proper registration
        Assert.Equal(expectedValue, (int)paramType);
    }

    [Fact]
    public void ApiParameterType_HasExactlyThirteenValues()
    {
        var values = Enum.GetValues<ApiParameterType>();
        Assert.Equal(13, values.Length);
    }

    #endregion

    #region ParameterDirection Tests

    [Theory]
    [InlineData(ParameterDirection.Input, 0)]
    [InlineData(ParameterDirection.Output, 1)]
    public void ParameterDirection_ValuesAreCorrect(ParameterDirection direction, int expectedValue)
    {
        Assert.Equal(expectedValue, (int)direction);
    }

    [Fact]
    public void ParameterDirection_HasExactlyTwoValues()
    {
        var values = Enum.GetValues<ParameterDirection>();
        Assert.Equal(2, values.Length);
    }

    #endregion

    #region ApiProcessingStepType Tests

    [Theory]
    [InlineData(ApiProcessingStepType.None, 0)]
    [InlineData(ApiProcessingStepType.AsyncOnly, 1)]
    [InlineData(ApiProcessingStepType.SyncAndAsync, 2)]
    public void ApiProcessingStepType_ValuesMatchDataverseSDK(ApiProcessingStepType stepType, int expectedValue)
    {
        // These values must match the Dataverse SDK for proper registration
        Assert.Equal(expectedValue, (int)stepType);
    }

    [Fact]
    public void ApiProcessingStepType_HasExactlyThreeValues()
    {
        var values = Enum.GetValues<ApiProcessingStepType>();
        Assert.Equal(3, values.Length);
    }

    #endregion
}
