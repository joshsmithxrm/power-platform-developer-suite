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
        // Track mutable children lists separately to avoid casting IReadOnlyList to List
        var rootNodes = new List<TimelineNode>();
        var childrenMap = new Dictionary<TimelineNode, List<TimelineNode>>();
        var parentStack = new Stack<(TimelineNode Node, int Depth)>();

        foreach (var trace in sortedTraces)
        {
            var children = new List<TimelineNode>();
            var node = new TimelineNode
            {
                Trace = trace,
                HierarchyDepth = trace.Depth - 1, // Convert 1-based depth to 0-based
                Children = children
            };
            childrenMap[node] = children;

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
                childrenMap[parent].Add(node);
            }

            // This node becomes a potential parent for subsequent nodes
            parentStack.Push((node, trace.Depth));
        }

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
        // Track mutable children lists separately to avoid casting IReadOnlyList to List
        var rootNodes = new List<TimelineNode>();
        var childrenMap = new Dictionary<TimelineNode, List<TimelineNode>>();
        var parentStack = new Stack<(TimelineNode Node, int Depth)>();

        foreach (var trace in sortedTraces)
        {
            var offsetMs = (trace.CreatedOn - timelineStart).TotalMilliseconds;
            var offsetPercent = (offsetMs / totalDuration) * 100;
            var widthPercent = Math.Max(0.5, ((trace.DurationMs ?? 0) / totalDuration) * 100);

            var children = new List<TimelineNode>();
            var node = new TimelineNode
            {
                Trace = trace,
                HierarchyDepth = trace.Depth - 1,
                Children = children,
                OffsetPercent = offsetPercent,
                WidthPercent = widthPercent
            };
            childrenMap[node] = children;

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
                childrenMap[parent].Add(node);
            }

            parentStack.Push((node, trace.Depth));
        }

        return rootNodes;
    }
}
