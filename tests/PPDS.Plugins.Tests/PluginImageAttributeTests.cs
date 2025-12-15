using System.Reflection;
using Xunit;

namespace PPDS.Plugins.Tests;

public class PluginImageAttributeTests
{
    #region Constructor Tests

    [Fact]
    public void DefaultConstructor_SetsDefaultValues()
    {
        var attribute = new PluginImageAttribute();

        Assert.Equal(string.Empty, attribute.Name);
        Assert.Equal(default(PluginImageType), attribute.ImageType);
        Assert.Null(attribute.Attributes);
        Assert.Null(attribute.EntityAlias);
        Assert.Null(attribute.StepId);
    }

    [Fact]
    public void TwoParameterConstructor_SetsImageTypeAndName()
    {
        var attribute = new PluginImageAttribute(PluginImageType.PreImage, "PreImage");

        Assert.Equal(PluginImageType.PreImage, attribute.ImageType);
        Assert.Equal("PreImage", attribute.Name);
    }

    [Fact]
    public void ThreeParameterConstructor_SetsImageTypeNameAndAttributes()
    {
        var attribute = new PluginImageAttribute(PluginImageType.PostImage, "PostImage", "name,revenue");

        Assert.Equal(PluginImageType.PostImage, attribute.ImageType);
        Assert.Equal("PostImage", attribute.Name);
        Assert.Equal("name,revenue", attribute.Attributes);
    }

    #endregion

    #region Property Tests

    [Theory]
    [InlineData(PluginImageType.PreImage)]
    [InlineData(PluginImageType.PostImage)]
    [InlineData(PluginImageType.Both)]
    public void ImageType_AcceptsAllValidTypes(PluginImageType imageType)
    {
        var attribute = new PluginImageAttribute { ImageType = imageType };
        Assert.Equal(imageType, attribute.ImageType);
    }

    [Theory]
    [InlineData("PreImage")]
    [InlineData("PostImage")]
    [InlineData("Target")]
    [InlineData("CustomImageName")]
    public void Name_AcceptsValidNames(string name)
    {
        var attribute = new PluginImageAttribute { Name = name };
        Assert.Equal(name, attribute.Name);
    }

    [Fact]
    public void Attributes_AcceptsCommaSeparatedList()
    {
        var attribute = new PluginImageAttribute
        {
            Attributes = "name,telephone1,revenue,modifiedon"
        };

        Assert.Equal("name,telephone1,revenue,modifiedon", attribute.Attributes);
    }

    [Fact]
    public void Attributes_CanBeNull()
    {
        var attribute = new PluginImageAttribute
        {
            ImageType = PluginImageType.PreImage,
            Name = "PreImage",
            Attributes = null
        };

        Assert.Null(attribute.Attributes);
    }

    [Fact]
    public void EntityAlias_CanBeSet()
    {
        var attribute = new PluginImageAttribute
        {
            EntityAlias = "pre"
        };

        Assert.Equal("pre", attribute.EntityAlias);
    }

    [Fact]
    public void StepId_CanBeSetForMultiStepPlugins()
    {
        var attribute = new PluginImageAttribute { StepId = "step1" };
        Assert.Equal("step1", attribute.StepId);
    }

    #endregion

    #region Attribute Usage Tests

    [Fact]
    public void AttributeUsage_AllowsMultipleOnClass()
    {
        var attributeUsage = typeof(PluginImageAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(attributeUsage);
        Assert.True(attributeUsage.AllowMultiple);
    }

    [Fact]
    public void AttributeUsage_TargetsClassOnly()
    {
        var attributeUsage = typeof(PluginImageAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(attributeUsage);
        Assert.Equal(AttributeTargets.Class, attributeUsage.ValidOn);
    }

    [Fact]
    public void AttributeUsage_DoesNotInherit()
    {
        var attributeUsage = typeof(PluginImageAttribute)
            .GetCustomAttribute<AttributeUsageAttribute>();

        Assert.NotNull(attributeUsage);
        Assert.False(attributeUsage.Inherited);
    }

    [Fact]
    public void Attribute_IsSealed()
    {
        Assert.True(typeof(PluginImageAttribute).IsSealed);
    }

    #endregion

    #region Real-World Usage Scenarios

    [Fact]
    public void TypicalPreImageConfiguration()
    {
        var attribute = new PluginImageAttribute(PluginImageType.PreImage, "PreImage", "name,telephone1,revenue");

        Assert.Equal(PluginImageType.PreImage, attribute.ImageType);
        Assert.Equal("PreImage", attribute.Name);
        Assert.Equal("name,telephone1,revenue", attribute.Attributes);
    }

    [Fact]
    public void TypicalPostImageConfiguration()
    {
        var attribute = new PluginImageAttribute(PluginImageType.PostImage, "PostImage")
        {
            Attributes = "statecode,statuscode"
        };

        Assert.Equal(PluginImageType.PostImage, attribute.ImageType);
        Assert.Equal("PostImage", attribute.Name);
        Assert.Equal("statecode,statuscode", attribute.Attributes);
    }

    [Fact]
    public void BothImagesConfiguration()
    {
        var attribute = new PluginImageAttribute
        {
            ImageType = PluginImageType.Both,
            Name = "Image",
            Attributes = "name,revenue"
        };

        Assert.Equal(PluginImageType.Both, attribute.ImageType);
    }

    [Fact]
    public void ImageWithStepIdForMultiStepPlugin()
    {
        var attribute = new PluginImageAttribute(PluginImageType.PreImage, "PreImage")
        {
            Attributes = "name",
            StepId = "updateStep"
        };

        Assert.Equal("updateStep", attribute.StepId);
    }

    [Fact]
    public void ImageWithEntityAlias()
    {
        var attribute = new PluginImageAttribute(PluginImageType.PreImage, "PreImage")
        {
            EntityAlias = "pre",
            Attributes = "name,revenue"
        };

        Assert.Equal("pre", attribute.EntityAlias);
    }

    #endregion
}
