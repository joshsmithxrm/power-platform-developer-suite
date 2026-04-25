using PPDS.Cli.Tui.Screens;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Screens;

[Trait("Category", "TuiUnit")]
public sealed class PluginTreeNodeTests
{
    [Theory]
    [InlineData("package", "[PKG]")]
    [InlineData("assembly", "[ASM]")]
    [InlineData("type", "[TYPE]")]
    [InlineData("step", "[STEP]")]
    [InlineData("image", "[IMG]")]
    [InlineData("webhook", "[WH]")]
    [InlineData("serviceEndpoint", "[SVC]")]
    [InlineData("customApi", "[API]")]
    [InlineData("dataSource", "[DS]")]
    [InlineData("dataProvider", "[DP]")]
    public void Text_IncludesCorrectIcon(string nodeType, string expectedIcon)
    {
        var node = new PluginTreeNode
        {
            NodeType = nodeType,
            DisplayName = "TestNode"
        };

        Assert.StartsWith(expectedIcon, node.Text);
        Assert.Contains("TestNode", node.Text);
    }

    [Fact]
    public void Text_UnknownType_ShowsQuestionMark()
    {
        var node = new PluginTreeNode
        {
            NodeType = "unknown",
            DisplayName = "Mystery"
        };

        Assert.StartsWith("[?]", node.Text);
    }

    [Fact]
    public void Text_LoadingNode_ReturnsLoadingText()
    {
        var node = new PluginTreeNode
        {
            NodeType = "loading",
            DisplayName = "Loading..."
        };

        Assert.Equal("  Loading...", node.Text);
    }

    [Fact]
    public void Text_DisabledStep_IncludesDisabledSuffix()
    {
        var node = new PluginTreeNode
        {
            NodeType = "step",
            DisplayName = "PreCreate",
            IsEnabled = false
        };

        Assert.Contains("[disabled]", node.Text);
        Assert.Contains("PreCreate", node.Text);
    }

    [Fact]
    public void Text_EnabledStep_NoDisabledSuffix()
    {
        var node = new PluginTreeNode
        {
            NodeType = "step",
            DisplayName = "PreCreate",
            IsEnabled = true
        };

        Assert.DoesNotContain("[disabled]", node.Text);
    }

    [Fact]
    public void Text_ManagedNode_IncludesManagedSuffix()
    {
        var node = new PluginTreeNode
        {
            NodeType = "assembly",
            DisplayName = "MyPlugin",
            IsManaged = true
        };

        Assert.Contains("(managed)", node.Text);
    }

    [Fact]
    public void Text_UnmanagedNode_NoManagedSuffix()
    {
        var node = new PluginTreeNode
        {
            NodeType = "assembly",
            DisplayName = "MyPlugin",
            IsManaged = false
        };

        Assert.DoesNotContain("(managed)", node.Text);
    }

    [Fact]
    public void Text_DisabledAndManaged_IncludesBothSuffixes()
    {
        var node = new PluginTreeNode
        {
            NodeType = "step",
            DisplayName = "PostUpdate",
            IsEnabled = false,
            IsManaged = true
        };

        Assert.Contains("[disabled]", node.Text);
        Assert.Contains("(managed)", node.Text);
    }

    [Fact]
    public void Children_DefaultEmpty()
    {
        var node = new PluginTreeNode
        {
            NodeType = "package",
            DisplayName = "TestPkg"
        };

        Assert.Empty(node.Children);
    }

    [Fact]
    public void Children_CanAddChildNodes()
    {
        var parent = new PluginTreeNode
        {
            NodeType = "package",
            DisplayName = "TestPkg"
        };
        var child = new PluginTreeNode
        {
            NodeType = "assembly",
            DisplayName = "ChildAsm"
        };

        parent.Children.Add(child);

        Assert.Single(parent.Children);
        Assert.Equal("ChildAsm", ((PluginTreeNode)parent.Children[0]).DisplayName);
    }

    [Fact]
    public void IsLoaded_DefaultFalse()
    {
        var node = new PluginTreeNode { NodeType = "package", DisplayName = "Pkg" };

        Assert.False(node.IsLoaded);
    }

    [Fact]
    public void Id_StoresEntityId()
    {
        var id = Guid.NewGuid();
        var node = new PluginTreeNode
        {
            NodeType = "assembly",
            Id = id,
            DisplayName = "Asm"
        };

        Assert.Equal(id, node.Id);
    }
}
