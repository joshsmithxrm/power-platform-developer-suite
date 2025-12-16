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

    #region Cross-Enum Tests

    [Fact]
    public void AllEnums_AreInCorrectNamespace()
    {
        Assert.Equal("PPDS.Plugins", typeof(PluginStage).Namespace);
        Assert.Equal("PPDS.Plugins", typeof(PluginMode).Namespace);
        Assert.Equal("PPDS.Plugins", typeof(PluginImageType).Namespace);
    }

    [Theory]
    [InlineData(typeof(PluginStage))]
    [InlineData(typeof(PluginMode))]
    [InlineData(typeof(PluginImageType))]
    public void AllEnums_AreValidAndNotEmpty(Type enumType)
    {
        // Verify the enum type exists and is an enum
        Assert.True(enumType.IsEnum);

        // Verify all values exist (will throw if values are missing)
        var values = Enum.GetValues(enumType);
        Assert.True(values.Length > 0);
    }

    #endregion
}
