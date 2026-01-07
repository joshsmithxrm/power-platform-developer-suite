using FluentAssertions;
using PPDS.Dataverse.Services;
using Xunit;

namespace PPDS.Dataverse.Tests.Services;

public class TimelineHierarchyBuilderTests
{
    #region Build Tests

    [Fact]
    public void Build_EmptyList_ReturnsEmptyList()
    {
        // Arrange
        var traces = Array.Empty<PluginTraceInfo>();

        // Act
        var result = TimelineHierarchyBuilder.Build(traces);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void Build_SingleTrace_ReturnsSingleRootNode()
    {
        // Arrange
        var trace = CreateTraceInfo(depth: 1);
        var traces = new[] { trace };

        // Act
        var result = TimelineHierarchyBuilder.Build(traces);

        // Assert
        result.Should().HaveCount(1);
        result[0].Trace.Should().Be(trace);
        result[0].Children.Should().BeEmpty();
        result[0].HierarchyDepth.Should().Be(0); // depth 1 -> hierarchy 0
    }

    [Fact]
    public void Build_TwoRootTraces_ReturnsTwoRootNodes()
    {
        // Arrange
        var trace1 = CreateTraceInfo(depth: 1, createdOn: DateTime.UtcNow);
        var trace2 = CreateTraceInfo(depth: 1, createdOn: DateTime.UtcNow.AddSeconds(1));
        var traces = new[] { trace1, trace2 };

        // Act
        var result = TimelineHierarchyBuilder.Build(traces);

        // Assert
        result.Should().HaveCount(2);
        result[0].Children.Should().BeEmpty();
        result[1].Children.Should().BeEmpty();
    }

    [Fact]
    public void Build_ParentWithChild_BuildsHierarchy()
    {
        // Arrange
        var baseTime = DateTime.UtcNow;
        var parent = CreateTraceInfo(depth: 1, createdOn: baseTime);
        var child = CreateTraceInfo(depth: 2, createdOn: baseTime.AddMilliseconds(10));
        var traces = new[] { parent, child };

        // Act
        var result = TimelineHierarchyBuilder.Build(traces);

        // Assert
        result.Should().HaveCount(1);
        result[0].Trace.Should().Be(parent);
        result[0].Children.Should().HaveCount(1);
        result[0].Children[0].Trace.Should().Be(child);
    }

    [Fact]
    public void Build_ThreeLevelHierarchy_BuildsCorrectly()
    {
        // Arrange
        var baseTime = DateTime.UtcNow;
        var root = CreateTraceInfo(depth: 1, createdOn: baseTime);
        var child = CreateTraceInfo(depth: 2, createdOn: baseTime.AddMilliseconds(10));
        var grandchild = CreateTraceInfo(depth: 3, createdOn: baseTime.AddMilliseconds(20));
        var traces = new[] { root, child, grandchild };

        // Act
        var result = TimelineHierarchyBuilder.Build(traces);

        // Assert
        result.Should().HaveCount(1);
        result[0].Trace.Should().Be(root);
        result[0].HierarchyDepth.Should().Be(0);

        result[0].Children.Should().HaveCount(1);
        result[0].Children[0].Trace.Should().Be(child);
        result[0].Children[0].HierarchyDepth.Should().Be(1);

        result[0].Children[0].Children.Should().HaveCount(1);
        result[0].Children[0].Children[0].Trace.Should().Be(grandchild);
        result[0].Children[0].Children[0].HierarchyDepth.Should().Be(2);
    }

    [Fact]
    public void Build_MultipleSiblings_GroupsUnderParent()
    {
        // Arrange
        var baseTime = DateTime.UtcNow;
        var parent = CreateTraceInfo(depth: 1, createdOn: baseTime);
        var child1 = CreateTraceInfo(depth: 2, createdOn: baseTime.AddMilliseconds(10));
        var child2 = CreateTraceInfo(depth: 2, createdOn: baseTime.AddMilliseconds(20));
        var child3 = CreateTraceInfo(depth: 2, createdOn: baseTime.AddMilliseconds(30));
        var traces = new[] { parent, child1, child2, child3 };

        // Act
        var result = TimelineHierarchyBuilder.Build(traces);

        // Assert
        result.Should().HaveCount(1);
        result[0].Trace.Should().Be(parent);
        result[0].Children.Should().HaveCount(3);
    }

    [Fact]
    public void Build_ChildWithGrandchildThenSibling_BuildsCorrectly()
    {
        // Arrange
        // Tree: root -> child1 -> grandchild
        //            -> child2
        var baseTime = DateTime.UtcNow;
        var root = CreateTraceInfo(depth: 1, createdOn: baseTime);
        var child1 = CreateTraceInfo(depth: 2, createdOn: baseTime.AddMilliseconds(10));
        var grandchild = CreateTraceInfo(depth: 3, createdOn: baseTime.AddMilliseconds(20));
        var child2 = CreateTraceInfo(depth: 2, createdOn: baseTime.AddMilliseconds(30));
        var traces = new[] { root, child1, grandchild, child2 };

        // Act
        var result = TimelineHierarchyBuilder.Build(traces);

        // Assert
        result.Should().HaveCount(1);
        result[0].Children.Should().HaveCount(2);
        result[0].Children[0].Children.Should().HaveCount(1); // child1 has grandchild
        result[0].Children[1].Children.Should().BeEmpty(); // child2 has no children
    }

    [Fact]
    public void Build_UnsortedTraces_SortsChronologically()
    {
        // Arrange
        var baseTime = DateTime.UtcNow;
        var trace3 = CreateTraceInfo(depth: 1, createdOn: baseTime.AddSeconds(2));
        var trace1 = CreateTraceInfo(depth: 1, createdOn: baseTime);
        var trace2 = CreateTraceInfo(depth: 1, createdOn: baseTime.AddSeconds(1));
        var traces = new[] { trace3, trace1, trace2 };

        // Act
        var result = TimelineHierarchyBuilder.Build(traces);

        // Assert
        result.Should().HaveCount(3);
        result[0].Trace.Should().Be(trace1);
        result[1].Trace.Should().Be(trace2);
        result[2].Trace.Should().Be(trace3);
    }

    #endregion

    #region BuildWithPositioning Tests

    [Fact]
    public void BuildWithPositioning_SingleTrace_CalculatesPositioning()
    {
        // Arrange
        var trace = CreateTraceInfo(depth: 1, durationMs: 100);
        var traces = new[] { trace };

        // Act
        var result = TimelineHierarchyBuilder.BuildWithPositioning(traces);

        // Assert
        result.Should().HaveCount(1);
        result[0].OffsetPercent.Should().Be(0);
        result[0].WidthPercent.Should().BeGreaterThanOrEqualTo(0.5); // minimum width
    }

    [Fact]
    public void BuildWithPositioning_TwoSequentialTraces_CalculatesOffsets()
    {
        // Arrange
        var baseTime = DateTime.UtcNow;
        var trace1 = CreateTraceInfo(depth: 1, createdOn: baseTime, durationMs: 50);
        var trace2 = CreateTraceInfo(depth: 1, createdOn: baseTime.AddMilliseconds(50), durationMs: 50);
        var traces = new[] { trace1, trace2 };

        // Act
        var result = TimelineHierarchyBuilder.BuildWithPositioning(traces);

        // Assert
        result.Should().HaveCount(2);
        result[0].OffsetPercent.Should().Be(0);
        result[1].OffsetPercent.Should().BeGreaterThan(0);
    }

    [Fact]
    public void BuildWithPositioning_EmptyList_ReturnsEmptyList()
    {
        // Arrange
        var traces = Array.Empty<PluginTraceInfo>();

        // Act
        var result = TimelineHierarchyBuilder.BuildWithPositioning(traces);

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region GetTotalDuration Tests

    [Fact]
    public void GetTotalDuration_EmptyList_ReturnsZero()
    {
        // Arrange
        var traces = Array.Empty<PluginTraceInfo>();

        // Act
        var result = TimelineHierarchyBuilder.GetTotalDuration(traces);

        // Assert
        result.Should().Be(0);
    }

    [Fact]
    public void GetTotalDuration_SingleTrace_ReturnsDuration()
    {
        // Arrange
        var trace = CreateTraceInfo(depth: 1, durationMs: 100);
        var traces = new[] { trace };

        // Act
        var result = TimelineHierarchyBuilder.GetTotalDuration(traces);

        // Assert
        result.Should().Be(100);
    }

    [Fact]
    public void GetTotalDuration_SequentialTraces_ReturnsSpan()
    {
        // Arrange
        var baseTime = DateTime.UtcNow;
        var trace1 = CreateTraceInfo(depth: 1, createdOn: baseTime, durationMs: 50);
        var trace2 = CreateTraceInfo(depth: 1, createdOn: baseTime.AddMilliseconds(50), durationMs: 50);
        var traces = new[] { trace1, trace2 };

        // Act
        var result = TimelineHierarchyBuilder.GetTotalDuration(traces);

        // Assert
        result.Should().Be(100); // 50ms offset + 50ms duration
    }

    [Fact]
    public void GetTotalDuration_OverlappingTraces_ReturnsMaxSpan()
    {
        // Arrange
        var baseTime = DateTime.UtcNow;
        var trace1 = CreateTraceInfo(depth: 1, createdOn: baseTime, durationMs: 100);
        var trace2 = CreateTraceInfo(depth: 2, createdOn: baseTime.AddMilliseconds(10), durationMs: 20);
        var traces = new[] { trace1, trace2 };

        // Act
        var result = TimelineHierarchyBuilder.GetTotalDuration(traces);

        // Assert
        result.Should().Be(100); // trace1 spans the full duration
    }

    [Fact]
    public void GetTotalDuration_TraceWithNullDuration_TreatsAsZero()
    {
        // Arrange
        var trace = CreateTraceInfo(depth: 1, durationMs: null);
        var traces = new[] { trace };

        // Act
        var result = TimelineHierarchyBuilder.GetTotalDuration(traces);

        // Assert
        result.Should().Be(0);
    }

    #endregion

    #region CountTotalNodes Tests

    [Fact]
    public void CountTotalNodes_EmptyList_ReturnsZero()
    {
        // Arrange
        var roots = new List<TimelineNode>();

        // Act
        var result = TimelineHierarchyBuilder.CountTotalNodes(roots);

        // Assert
        result.Should().Be(0);
    }

    [Fact]
    public void CountTotalNodes_SingleRoot_ReturnsOne()
    {
        // Arrange
        var node = CreateNode(CreateTraceInfo(depth: 1));
        var roots = new List<TimelineNode> { node };

        // Act
        var result = TimelineHierarchyBuilder.CountTotalNodes(roots);

        // Assert
        result.Should().Be(1);
    }

    [Fact]
    public void CountTotalNodes_RootWithChildren_CountsAll()
    {
        // Arrange
        var child1 = CreateNode(CreateTraceInfo(depth: 2));
        var child2 = CreateNode(CreateTraceInfo(depth: 2));
        var root = CreateNode(CreateTraceInfo(depth: 1), new[] { child1, child2 });
        var roots = new List<TimelineNode> { root };

        // Act
        var result = TimelineHierarchyBuilder.CountTotalNodes(roots);

        // Assert
        result.Should().Be(3); // 1 root + 2 children
    }

    [Fact]
    public void CountTotalNodes_DeepNesting_CountsAll()
    {
        // Arrange
        var grandchild = CreateNode(CreateTraceInfo(depth: 3));
        var child = CreateNode(CreateTraceInfo(depth: 2), new[] { grandchild });
        var root = CreateNode(CreateTraceInfo(depth: 1), new[] { child });
        var roots = new List<TimelineNode> { root };

        // Act
        var result = TimelineHierarchyBuilder.CountTotalNodes(roots);

        // Assert
        result.Should().Be(3); // 1 root + 1 child + 1 grandchild
    }

    [Fact]
    public void CountTotalNodes_MultipleRoots_CountsAll()
    {
        // Arrange
        var root1 = CreateNode(CreateTraceInfo(depth: 1));
        var root2 = CreateNode(CreateTraceInfo(depth: 1));
        var root3 = CreateNode(CreateTraceInfo(depth: 1));
        var roots = new List<TimelineNode> { root1, root2, root3 };

        // Act
        var result = TimelineHierarchyBuilder.CountTotalNodes(roots);

        // Assert
        result.Should().Be(3);
    }

    #endregion

    #region Helper Methods

    private static PluginTraceInfo CreateTraceInfo(
        int depth = 1,
        DateTime? createdOn = null,
        int? durationMs = 10)
    {
        return new PluginTraceInfo
        {
            Id = Guid.NewGuid(),
            TypeName = "TestPlugin",
            Depth = depth,
            CreatedOn = createdOn ?? DateTime.UtcNow,
            DurationMs = durationMs,
            Mode = PluginTraceMode.Synchronous,
            OperationType = PluginTraceOperationType.Plugin
        };
    }

    private static TimelineNode CreateNode(PluginTraceInfo trace, IReadOnlyList<TimelineNode>? children = null)
    {
        return new TimelineNode
        {
            Trace = trace,
            Children = children ?? Array.Empty<TimelineNode>(),
            HierarchyDepth = trace.Depth - 1
        };
    }

    #endregion
}
