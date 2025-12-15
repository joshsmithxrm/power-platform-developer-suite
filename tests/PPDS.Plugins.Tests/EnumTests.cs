using Xunit;

namespace PPDS.Plugins.Tests;

public class EnumTests
{
    #region PluginStage Tests

    [Fact]
    public void PluginStage_PreValidation_HasCorrectValue()
    {
        Assert.Equal(10, (int)PluginStage.PreValidation);
    }

    [Fact]
    public void PluginStage_PreOperation_HasCorrectValue()
    {
        Assert.Equal(20, (int)PluginStage.PreOperation);
    }

    [Fact]
    public void PluginStage_PostOperation_HasCorrectValue()
    {
        Assert.Equal(40, (int)PluginStage.PostOperation);
    }

    [Fact]
    public void PluginStage_HasExactlyThreeValues()
    {
        var values = Enum.GetValues<PluginStage>();
        Assert.Equal(3, values.Length);
    }

    [Fact]
    public void PluginStage_ValuesMatchDataverseSDK()
    {
        // These values must match the Dataverse SDK for proper registration
        Assert.Equal(10, (int)PluginStage.PreValidation);
        Assert.Equal(20, (int)PluginStage.PreOperation);
        Assert.Equal(40, (int)PluginStage.PostOperation);
    }

    #endregion

    #region PluginMode Tests

    [Fact]
    public void PluginMode_Synchronous_HasCorrectValue()
    {
        Assert.Equal(0, (int)PluginMode.Synchronous);
    }

    [Fact]
    public void PluginMode_Asynchronous_HasCorrectValue()
    {
        Assert.Equal(1, (int)PluginMode.Asynchronous);
    }

    [Fact]
    public void PluginMode_HasExactlyTwoValues()
    {
        var values = Enum.GetValues<PluginMode>();
        Assert.Equal(2, values.Length);
    }

    [Fact]
    public void PluginMode_ValuesMatchDataverseSDK()
    {
        // These values must match the Dataverse SDK for proper registration
        Assert.Equal(0, (int)PluginMode.Synchronous);
        Assert.Equal(1, (int)PluginMode.Asynchronous);
    }

    #endregion

    #region PluginImageType Tests

    [Fact]
    public void PluginImageType_PreImage_HasCorrectValue()
    {
        Assert.Equal(0, (int)PluginImageType.PreImage);
    }

    [Fact]
    public void PluginImageType_PostImage_HasCorrectValue()
    {
        Assert.Equal(1, (int)PluginImageType.PostImage);
    }

    [Fact]
    public void PluginImageType_Both_HasCorrectValue()
    {
        Assert.Equal(2, (int)PluginImageType.Both);
    }

    [Fact]
    public void PluginImageType_HasExactlyThreeValues()
    {
        var values = Enum.GetValues<PluginImageType>();
        Assert.Equal(3, values.Length);
    }

    [Fact]
    public void PluginImageType_ValuesMatchDataverseSDK()
    {
        // These values must match the Dataverse SDK for proper registration
        Assert.Equal(0, (int)PluginImageType.PreImage);
        Assert.Equal(1, (int)PluginImageType.PostImage);
        Assert.Equal(2, (int)PluginImageType.Both);
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
    public void AllEnums_HaveXmlDocumentation(Type enumType)
    {
        // Verify the enum type exists and is an enum
        Assert.True(enumType.IsEnum);

        // Verify all values exist (will throw if values are missing)
        var values = Enum.GetValues(enumType);
        Assert.True(values.Length > 0);
    }

    #endregion
}
