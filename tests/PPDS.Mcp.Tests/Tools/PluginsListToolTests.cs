using FluentAssertions;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="PluginsListTool"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PluginsListToolTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_NullContext_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new PluginsListTool(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("context");
    }

    #endregion

    #region Result Shape Tests (H2 service-layer delegation)

    [Fact]
    public void PluginsListResult_TotalCountDistinctFromCount_WhenTruncated()
    {
        // The result must distinguish between returned (Count) and total matching (TotalCount)
        // so callers can see when maxRows truncated the results.
        var result = new PluginsListResult
        {
            Count = 50,
            TotalCount = 120,
            Assemblies = []
        };

        result.Count.Should().Be(50);
        result.TotalCount.Should().Be(120);
        result.TotalCount.Should().BeGreaterThan(result.Count);
    }

    [Fact]
    public void PluginsListResult_AssemblyResult_HasExpectedFields()
    {
        var assembly = new PluginAssemblyResult
        {
            Id = Guid.NewGuid(),
            Name = "MyPlugin",
            Version = "1.0.0.0",
            IsolationMode = "Sandbox",
            SourceType = "Database",
            Types = []
        };

        assembly.Name.Should().Be("MyPlugin");
        assembly.IsolationMode.Should().Be("Sandbox");
        assembly.SourceType.Should().Be("Database");
    }

    [Fact]
    public void PluginsListResult_StepResult_HasExpectedFields()
    {
        var step = new PluginStepResult
        {
            Name = "MyPlugin.Create",
            Message = "Create",
            Entity = "account",
            Stage = "PostOperation",
            IsEnabled = true
        };

        step.Message.Should().Be("Create");
        step.Entity.Should().Be("account");
        step.Stage.Should().Be("PostOperation");
    }

    #endregion
}
