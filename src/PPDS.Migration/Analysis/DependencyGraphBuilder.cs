using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using PPDS.Migration.Models;

namespace PPDS.Migration.Analysis
{
    /// <summary>
    /// Builds entity dependency graphs using Tarjan's algorithm for cycle detection.
    /// </summary>
    public class DependencyGraphBuilder : IDependencyGraphBuilder
    {
        private readonly ILogger<DependencyGraphBuilder>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="DependencyGraphBuilder"/> class.
        /// </summary>
        public DependencyGraphBuilder()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DependencyGraphBuilder"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        public DependencyGraphBuilder(ILogger<DependencyGraphBuilder> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public DependencyGraph Build(MigrationSchema schema)
        {
            if (schema == null)
            {
                throw new ArgumentNullException(nameof(schema));
            }

            _logger?.LogInformation("Building dependency graph for {Count} entities", schema.Entities.Count);

            // Build entity nodes
            var entityNodes = schema.Entities
                .Select(e => new EntityNode
                {
                    LogicalName = e.LogicalName,
                    DisplayName = e.DisplayName
                })
                .ToList();

            // Build entity name set for validation
            var entitySet = new HashSet<string>(
                schema.Entities.Select(e => e.LogicalName),
                StringComparer.OrdinalIgnoreCase);

            // Build dependency edges
            var edges = new List<DependencyEdge>();

            foreach (var entity in schema.Entities)
            {
                foreach (var field in entity.Fields)
                {
                    if (!field.IsLookup || string.IsNullOrEmpty(field.LookupEntity))
                    {
                        continue;
                    }

                    // Handle polymorphic lookups (e.g., "account|contact")
                    var targetEntities = field.LookupEntity.Split('|')
                        .Select(t => t.Trim())
                        .Where(trimmedTarget =>
                        {
                            if (!entitySet.Contains(trimmedTarget))
                            {
                                _logger?.LogDebug("Ignoring lookup {Entity}.{Field} -> {Target} (not in schema)",
                                    entity.LogicalName, field.LogicalName, trimmedTarget);
                                return false;
                            }
                            return true;
                        })
                        .Select(trimmedTarget => new DependencyEdge
                        {
                            FromEntity = entity.LogicalName,
                            ToEntity = trimmedTarget,
                            FieldName = field.LogicalName,
                            Type = field.Type.ToLowerInvariant() switch
                            {
                                "owner" => DependencyType.Owner,
                                "customer" => DependencyType.Customer,
                                _ => DependencyType.Lookup
                            }
                        });

                    edges.AddRange(targetEntities);
                }
            }

            _logger?.LogInformation("Found {Count} dependencies", edges.Count);

            // Find circular references using Tarjan's algorithm
            var circularReferences = FindCircularReferences(entitySet, edges);

            if (circularReferences.Count > 0)
            {
                _logger?.LogInformation("Detected {Count} circular reference groups", circularReferences.Count);
            }

            // Build tiers using topological sort
            var tiers = BuildTiers(entitySet, edges, circularReferences);

            // Assign tier numbers to nodes
            for (var tierIndex = 0; tierIndex < tiers.Count; tierIndex++)
            {
                foreach (var entityName in tiers[tierIndex])
                {
                    var node = entityNodes.FirstOrDefault(n =>
                        n.LogicalName.Equals(entityName, StringComparison.OrdinalIgnoreCase));
                    if (node != null)
                    {
                        node.TierNumber = tierIndex;
                    }
                }
            }

            return new DependencyGraph
            {
                Entities = entityNodes,
                Dependencies = edges,
                CircularReferences = circularReferences,
                Tiers = tiers
            };
        }

        private List<CircularReference> FindCircularReferences(
            HashSet<string> entities,
            List<DependencyEdge> edges)
        {
            // Build adjacency list
            var adjacency = new Dictionary<string, List<DependencyEdge>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entity in entities)
            {
                adjacency[entity] = new List<DependencyEdge>();
            }
            foreach (var edge in edges)
            {
                if (adjacency.TryGetValue(edge.FromEntity, out var edgeList))
                {
                    edgeList.Add(edge);
                }
            }

            // Tarjan's algorithm for strongly connected components
            var index = 0;
            var stack = new Stack<string>();
            var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var indices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var lowLinks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var sccs = new List<List<string>>();

            void StrongConnect(string v)
            {
                indices[v] = index;
                lowLinks[v] = index;
                index++;
                stack.Push(v);
                onStack.Add(v);

                foreach (var edge in adjacency[v])
                {
                    var w = edge.ToEntity;
                    if (!indices.TryGetValue(w, out var wIndex))
                    {
                        StrongConnect(w);
                        lowLinks[v] = Math.Min(lowLinks[v], lowLinks[w]);
                    }
                    else if (onStack.Contains(w))
                    {
                        lowLinks[v] = Math.Min(lowLinks[v], wIndex);
                    }
                }

                if (lowLinks[v] == indices[v])
                {
                    var scc = new List<string>();
                    string w;
                    do
                    {
                        w = stack.Pop();
                        onStack.Remove(w);
                        scc.Add(w);
                    } while (!w.Equals(v, StringComparison.OrdinalIgnoreCase));

                    if (scc.Count > 1)
                    {
                        sccs.Add(scc);
                    }
                }
            }

            foreach (var entity in entities)
            {
                if (!indices.ContainsKey(entity))
                {
                    StrongConnect(entity);
                }
            }

            // Convert SCCs to CircularReference objects
            return sccs.Select(scc =>
            {
                var sccSet = new HashSet<string>(scc, StringComparer.OrdinalIgnoreCase);
                var sccEdges = edges
                    .Where(e => sccSet.Contains(e.FromEntity) && sccSet.Contains(e.ToEntity))
                    .ToList();

                return new CircularReference
                {
                    Entities = scc,
                    Edges = sccEdges
                };
            }).ToList();
        }

        private List<IReadOnlyList<string>> BuildTiers(
            HashSet<string> entities,
            List<DependencyEdge> edges,
            List<CircularReference> circularReferences)
        {
            // Create SCC to entity mapping
            var entityToScc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < circularReferences.Count; i++)
            {
                foreach (var entity in circularReferences[i].Entities)
                {
                    entityToScc[entity] = i;
                }
            }

            // Condense graph: treat each SCC as a single node
            var condensedNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entity in entities)
            {
                if (entityToScc.TryGetValue(entity, out var sccId))
                {
                    condensedNodes.Add($"__SCC_{sccId}__");
                }
                else
                {
                    condensedNodes.Add(entity);
                }
            }

            // Build condensed edges
            var condensedEdges = new HashSet<(string, string)>();
            foreach (var edge in edges)
            {
                var from = entityToScc.TryGetValue(edge.FromEntity, out var fromScc)
                    ? $"__SCC_{fromScc}__"
                    : edge.FromEntity;
                var to = entityToScc.TryGetValue(edge.ToEntity, out var toScc)
                    ? $"__SCC_{toScc}__"
                    : edge.ToEntity;

                if (!from.Equals(to, StringComparison.OrdinalIgnoreCase))
                {
                    condensedEdges.Add((from, to));
                }
            }

            // Calculate dependency counts for condensed graph
            // Edges are (from=dependent, to=dependency), meaning "from depends on to"
            // We count how many dependencies each node has (edges FROM it)
            // Nodes with zero dependencies (no edges FROM them) are processed first
            var dependencyCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in condensedNodes)
            {
                dependencyCount[node] = 0;
            }
            foreach (var (from, _) in condensedEdges)
            {
                dependencyCount[from]++;
            }

            // Kahn's algorithm for topological sort (processing dependencies before dependents)
            var tiers = new List<IReadOnlyList<string>>();
            var remaining = new HashSet<string>(condensedNodes, StringComparer.OrdinalIgnoreCase);

            while (remaining.Count > 0)
            {
                // Find all nodes with zero dependencies (no unprocessed prerequisites)
                var tier = remaining.Where(n => dependencyCount[n] == 0).ToList();

                if (tier.Count == 0)
                {
                    // Should not happen after SCC processing, but handle gracefully
                    _logger?.LogWarning("Unexpected cycle detected in condensed graph");
                    tier = remaining.ToList();
                }

                // Expand SCCs back to entities
                var expandedTier = new List<string>();
                foreach (var node in tier)
                {
                    if (node.StartsWith("__SCC_", StringComparison.Ordinal))
                    {
                        var sccId = int.Parse(node.Substring(6, node.Length - 8));
                        expandedTier.AddRange(circularReferences[sccId].Entities);
                    }
                    else
                    {
                        expandedTier.Add(node);
                    }
                }

                tiers.Add(expandedTier);

                // Update dependency counts: when we process a dependency, its dependents have one less unmet dependency
                foreach (var node in tier)
                {
                    remaining.Remove(node);
                    foreach (var (from, to) in condensedEdges)
                    {
                        // When we process 'to' (the dependency), decrement count for 'from' (the dependent)
                        if (to.Equals(node, StringComparison.OrdinalIgnoreCase))
                        {
                            dependencyCount[from]--;
                        }
                    }
                }
            }

            _logger?.LogInformation("Built {Count} tiers", tiers.Count);

            return tiers;
        }
    }
}
