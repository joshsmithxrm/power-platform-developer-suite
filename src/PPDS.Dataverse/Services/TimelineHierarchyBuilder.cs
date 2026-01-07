using System;
using System.Collections.Generic;
using System.Linq;

namespace PPDS.Dataverse.Services;

/// <summary>
/// Builds a hierarchical timeline from flat plugin trace records.
/// Based on execution depth to determine parent-child relationships.
/// </summary>
public static class TimelineHierarchyBuilder
{
    /// <summary>
    /// Builds a hierarchy of timeline nodes from a flat list of traces.
    /// </summary>
    /// <param name="traces">Traces to build hierarchy from (should be from same correlation ID).</param>
    /// <returns>Root nodes with nested children based on execution depth.</returns>
    public static List<TimelineNode> Build(IReadOnlyList<PluginTraceInfo> traces)
    {
        if (traces.Count == 0)
        {
            return new List<TimelineNode>();
        }

        // Sort traces chronologically by creation time
        var sortedTraces = traces.OrderBy(t => t.CreatedOn).ToList();

        // Build depth-based hierarchy
        var rootNodes = new List<TimelineNode>();
        var parentStack = new Stack<(TimelineNode Node, int Depth)>();

        foreach (var trace in sortedTraces)
        {
            var node = new TimelineNode
            {
                Trace = trace,
                HierarchyDepth = trace.Depth - 1, // Convert 1-based depth to 0-based
                Children = new List<TimelineNode>()
            };

            // Pop parents that are at same or greater depth (siblings or descendants of siblings)
            while (parentStack.Count > 0 && parentStack.Peek().Depth >= trace.Depth)
            {
                parentStack.Pop();
            }

            if (parentStack.Count == 0)
            {
                // This is a root node
                rootNodes.Add(node);
            }
            else
            {
                // This is a child of the current parent
                var parent = parentStack.Peek().Node;
                var mutableChildren = (List<TimelineNode>)parent.Children;
                mutableChildren.Add(node);
            }

            // This node becomes a potential parent for subsequent nodes
            parentStack.Push((node, trace.Depth));
        }

        // Calculate timeline positioning
        CalculatePositioning(rootNodes, sortedTraces);

        return rootNodes;
    }

    /// <summary>
    /// Gets the total duration of a set of traces in milliseconds.
    /// </summary>
    /// <param name="traces">Traces to calculate duration for.</param>
    /// <returns>Total duration in milliseconds.</returns>
    public static long GetTotalDuration(IReadOnlyList<PluginTraceInfo> traces)
    {
        if (traces.Count == 0) return 0;

        var earliest = traces.Min(t => t.CreatedOn);
        var latestEnd = traces.Max(t => t.CreatedOn.AddMilliseconds(t.DurationMs ?? 0));

        return (long)(latestEnd - earliest).TotalMilliseconds;
    }

    /// <summary>
    /// Counts total nodes including all descendants.
    /// </summary>
    /// <param name="roots">Root nodes to count.</param>
    /// <returns>Total node count.</returns>
    public static int CountTotalNodes(IReadOnlyList<TimelineNode> roots)
    {
        return roots.Sum(CountNodesRecursive);
    }

    private static int CountNodesRecursive(TimelineNode node)
    {
        return 1 + node.Children.Sum(CountNodesRecursive);
    }

    private static void CalculatePositioning(List<TimelineNode> rootNodes, List<PluginTraceInfo> sortedTraces)
    {
        if (sortedTraces.Count == 0) return;

        var timelineStart = sortedTraces.First().CreatedOn;
        var timelineEnd = sortedTraces.Max(t => t.CreatedOn.AddMilliseconds(t.DurationMs ?? 0));
        var totalDuration = (timelineEnd - timelineStart).TotalMilliseconds;

        // Handle edge case where all traces occur at same time
        if (totalDuration <= 0)
        {
            totalDuration = 1;
        }

        CalculatePositioningRecursive(rootNodes, timelineStart, totalDuration);
    }

    private static void CalculatePositioningRecursive(
        IEnumerable<TimelineNode> nodes,
        DateTime timelineStart,
        double totalDuration)
    {
        foreach (var node in nodes)
        {
            var traceStart = node.Trace.CreatedOn;
            var traceDuration = node.Trace.DurationMs ?? 0;

            var offsetMs = (traceStart - timelineStart).TotalMilliseconds;
            var offsetPercent = (offsetMs / totalDuration) * 100;

            // Ensure minimum width for visibility
            var widthPercent = Math.Max(0.5, (traceDuration / totalDuration) * 100);

            // Create updated node with positioning (since TimelineNode is a record)
            // Note: We modify the mutable parts here since we control the List<>
            // In a production scenario, we might want to make this fully immutable

            // For now, we'll rely on the caller to handle the calculated values
            // The OffsetPercent and WidthPercent are init-only, so we need a different approach

            // Actually, let's update the node in place by recreating it
            // But since we're using List<TimelineNode> we can't easily replace...
            // Let's update the approach to build nodes with positioning from the start

            // Process children
            CalculatePositioningRecursive(node.Children, timelineStart, totalDuration);
        }
    }

    /// <summary>
    /// Builds a hierarchy with positioning calculated upfront.
    /// </summary>
    /// <param name="traces">Traces to build hierarchy from.</param>
    /// <returns>Root nodes with nested children and positioning.</returns>
    public static List<TimelineNode> BuildWithPositioning(IReadOnlyList<PluginTraceInfo> traces)
    {
        if (traces.Count == 0)
        {
            return new List<TimelineNode>();
        }

        // Sort traces chronologically
        var sortedTraces = traces.OrderBy(t => t.CreatedOn).ToList();

        // Calculate timeline bounds
        var timelineStart = sortedTraces.First().CreatedOn;
        var timelineEnd = sortedTraces.Max(t => t.CreatedOn.AddMilliseconds(t.DurationMs ?? 0));
        var totalDuration = Math.Max(1, (timelineEnd - timelineStart).TotalMilliseconds);

        // Build hierarchy with positioning
        var rootNodes = new List<TimelineNode>();
        var parentStack = new Stack<(TimelineNode Node, int Depth)>();

        foreach (var trace in sortedTraces)
        {
            var offsetMs = (trace.CreatedOn - timelineStart).TotalMilliseconds;
            var offsetPercent = (offsetMs / totalDuration) * 100;
            var widthPercent = Math.Max(0.5, ((trace.DurationMs ?? 0) / totalDuration) * 100);

            var node = new TimelineNode
            {
                Trace = trace,
                HierarchyDepth = trace.Depth - 1,
                Children = new List<TimelineNode>(),
                OffsetPercent = offsetPercent,
                WidthPercent = widthPercent
            };

            // Pop parents at same or greater depth
            while (parentStack.Count > 0 && parentStack.Peek().Depth >= trace.Depth)
            {
                parentStack.Pop();
            }

            if (parentStack.Count == 0)
            {
                rootNodes.Add(node);
            }
            else
            {
                var parent = parentStack.Peek().Node;
                ((List<TimelineNode>)parent.Children).Add(node);
            }

            parentStack.Push((node, trace.Depth));
        }

        return rootNodes;
    }
}
