# PPDS.Migration: Dependency Analysis (Tarjan)

## Overview

The Dependency Analysis subsystem uses Tarjan's algorithm to detect circular references in entity lookup relationships, then builds a topologically sorted dependency graph with tiered execution plans. This enables correct import ordering where dependencies are created before dependents, with special handling for circular references via deferred field updates.

## Public API

### Interfaces

| Interface | Purpose |
|-----------|---------|
| `IDependencyGraphBuilder` | Builds dependency graph from schema |
| `IExecutionPlanBuilder` | Creates execution plan from graph |

### Classes

| Class | Purpose |
|-------|---------|
| `DependencyGraphBuilder` | Tarjan's algorithm implementation |
| `ExecutionPlanBuilder` | Deferred field and tier planning |

### DTOs/Models

| Type | Purpose |
|------|---------|
| `DependencyGraph` | Complete graph with tiers and cycles |
| `EntityNode` | Entity vertex in graph |
| `DependencyEdge` | Lookup relationship edge |
| `DependencyType` | Edge type (Lookup, Owner, Customer, ParentChild) |
| `CircularReference` | Strongly connected component |
| `ExecutionPlan` | Import plan with deferred fields |
| `ImportTier` | Set of entities importable in parallel |
| `DeferredField` | Field requiring second-pass update |

## Algorithm Details

### Tarjan's Algorithm

Detects strongly connected components (SCCs) in the dependency graph:

1. **DFS Traversal**: Visit each unvisited node
2. **Index Assignment**: Assign monotonic index on first visit
3. **Low-link Tracking**: Track smallest reachable index
4. **Stack Management**: Push nodes on visit, pop on SCC completion
5. **SCC Detection**: Node is SCC root when `lowlink[v] == index[v]`

**Output**: List of SCCs where each SCC with size > 1 is a circular reference.

### Self-Reference Detection

Self-referential lookups (e.g., parent account) are not detected by Tarjan's algorithm since SCCs of size 1 are filtered. These are detected separately:

```
edges.Where(e => e.FromEntity == e.ToEntity)
```

### Kahn's Algorithm

After SCC detection, builds topologically sorted tiers using Kahn's algorithm:

1. **Graph Condensation**: Treat each SCC as single node
2. **Dependency Count**: Count edges TO each node (dependencies)
3. **Zero-dependency tier**: Process nodes with no dependencies first
4. **Decrement and repeat**: When processing a dependency, decrement dependents' counts
5. **Expand SCCs**: Replace SCC placeholders with member entities

## Behaviors

### Edge Direction

Edges represent "A depends on B":
- `FromEntity`: The entity with the lookup field (dependent)
- `ToEntity`: The entity being referenced (dependency)

Dependencies must be imported before dependents.

### Polymorphic Lookups

Customer and Owner lookups reference multiple targets (e.g., `account|contact`):

```csharp
field.LookupEntity.Split('|')
    .Where(target => entitySet.Contains(target))
```

Each target creates a separate edge.

### Deferred Field Selection

For circular references, fields are deferred when:
1. Target entity is in same circular reference
2. Target is processed after source (based on ordering heuristic)
3. Self-references are always deferred

**Ordering heuristic**: Process entities with fewer intra-cycle lookups first.

### Lifecycle

1. **Build graph**: Analyze schema, create nodes and edges
2. **Detect cycles**: Run Tarjan's algorithm
3. **Sort tiers**: Run Kahn's algorithm on condensed graph
4. **Plan execution**: Identify deferred fields for each tier

## Edge Cases

| Scenario | Behavior | Notes |
|----------|----------|-------|
| Self-reference | Detected separately | Not an SCC of size 1 |
| Polymorphic lookup | Creates multiple edges | One per valid target |
| Unknown target | Edge filtered out | Target not in schema |
| Acyclic graph | Single SCC per entity | Standard topological sort |
| Fully connected cycle | All entities in one tier | All lookups deferred |
| Empty schema | Empty graph | No entities to process |

## Error Handling

| Condition | Behavior |
|-----------|----------|
| Null schema | Throws `ArgumentNullException` |
| Missing entity in cycle | Logged, continues gracefully |
| Unexpected cycle in condensed graph | Warns and includes remaining nodes |

## DependencyGraph Properties

| Property | Type | Description |
|----------|------|-------------|
| `Entities` | List&lt;EntityNode&gt; | All entity vertices |
| `Dependencies` | List&lt;DependencyEdge&gt; | All lookup edges |
| `CircularReferences` | List&lt;CircularReference&gt; | Detected SCCs (cycles) |
| `Tiers` | List&lt;List&lt;string&gt;&gt; | Topologically sorted tiers |
| `TierCount` | int | Number of tiers |
| `HasCircularReferences` | bool | Any cycles detected |

## ExecutionPlan Properties

| Property | Type | Description |
|----------|------|-------------|
| `Tiers` | List&lt;ImportTier&gt; | Ordered import tiers |
| `DeferredFields` | Dict&lt;string, List&lt;string&gt;&gt; | Fields to defer per entity |
| `ManyToManyRelationships` | List&lt;RelationshipSchema&gt; | N:N relationships to process after |
| `TierCount` | int | Number of tiers |
| `DeferredFieldCount` | int | Total deferred fields |

## ImportTier Properties

| Property | Type | Description |
|----------|------|-------------|
| `TierNumber` | int | 0-based tier index |
| `Entities` | List&lt;string&gt; | Entities in this tier |
| `HasCircularReferences` | bool | Tier contains cycle members |
| `RequiresWait` | bool | Wait for completion before next tier |

## Thread Safety

- **DependencyGraphBuilder**: Thread-safe (stateless per call)
- **ExecutionPlanBuilder**: Thread-safe (stateless per call)
- **DTOs**: Mutable; not thread-safe after construction

## Complexity Analysis

| Operation | Time Complexity | Space Complexity |
|-----------|-----------------|------------------|
| Edge extraction | O(E) | O(E) |
| Tarjan's algorithm | O(V + E) | O(V) |
| Graph condensation | O(V + E) | O(V) |
| Kahn's algorithm | O(V + E) | O(V) |
| **Total** | O(V + E) | O(V + E) |

Where V = entities, E = lookup relationships.

## Dependencies

- **Internal**:
  - `PPDS.Migration.Models` - Schema and result types
- **External**:
  - `Microsoft.Extensions.Logging.Abstractions`

## Related

- [Import Pipeline spec](./03-import-pipeline.md) - Uses execution plans
- [Circular References spec](./04-circular-references.md) - Handles deferred fields
- [Export Pipeline spec](./02-export-pipeline.md) - Generates schema for analysis

## Source Files

| File | Purpose |
|------|---------|
| `src/PPDS.Migration/Analysis/IDependencyGraphBuilder.cs` | Graph builder interface |
| `src/PPDS.Migration/Analysis/DependencyGraphBuilder.cs` | Tarjan's algorithm implementation |
| `src/PPDS.Migration/Analysis/IExecutionPlanBuilder.cs` | Plan builder interface |
| `src/PPDS.Migration/Analysis/ExecutionPlanBuilder.cs` | Deferred field identification |
| `src/PPDS.Migration/Models/DependencyGraph.cs` | Graph DTOs |
| `src/PPDS.Migration/Models/ExecutionPlan.cs` | Plan DTOs |
