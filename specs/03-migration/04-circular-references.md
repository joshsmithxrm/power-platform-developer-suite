# PPDS.Migration: Circular References

## Overview

The Circular References subsystem detects and resolves circular dependencies between entities during data migration. It uses Tarjan's Strongly Connected Components (SCC) algorithm to identify cycles, builds optimized execution tiers, and applies a deferred field strategy to break dependency cycles while ensuring data integrity through a three-phase import process.

## Public API

### Interfaces

| Interface | Purpose |
|-----------|---------|
| `IDependencyGraphBuilder` | Builds dependency graph and detects circular references |
| `IExecutionPlanBuilder` | Builds tiered execution plan with deferred field identification |

### Classes

| Class | Purpose |
|-------|---------|
| `DependencyGraphBuilder` | Implements Tarjan SCC detection and tier construction |
| `ExecutionPlanBuilder` | Determines deferred fields based on circular reference analysis |
| `DeferredFieldProcessor` | Phase 2 processor for updating deferred lookup fields |

### DTOs/Models

| Type | Purpose |
|------|---------|
| `DependencyGraph` | Graph structure with nodes, edges, and circular references |
| `EntityNode` | Entity in the graph with tier assignment |
| `DependencyEdge` | Lookup relationship between entities |
| `CircularReference` | Group of entities forming a cycle |
| `ExecutionPlan` | Tiered execution order with deferred fields |
| `ImportTier` | Group of entities importable in parallel |

## Behaviors

### Normal Operation

1. **Graph Building**: Analyze schema lookups to build dependency graph
2. **Circular Detection**: Identify self-references and multi-entity cycles via Tarjan SCC
3. **Tier Construction**: Condense SCCs and topologically sort for import order
4. **Deferred Field Identification**: Mark fields that must be null during Phase 1
5. **Three-Phase Import**: Import records, update deferred fields, create M2M associations

### Circular Reference Detection

#### Self-Referential Lookups
- Detected separately before Tarjan's algorithm runs
- Entity references itself (e.g., `account.parentaccountid → account`)
- Each self-reference wrapped in its own `CircularReference` object
- **Always deferred** regardless of other cycle analysis

#### Multi-Entity Cycles (Tarjan's SCC)
- Tarjan's algorithm finds strongly connected components with size > 1
- Example: Account → Contact → Account
  - `Account.primarycontactid → Contact`
  - `Contact.parentcustomerid → Account`
- Only cycles involving 2+ entities included in results

### Tarjan's Algorithm

**Time Complexity:** O(V + E) where V = entities, E = lookup edges

**Algorithm State:**
```
indices[v]   - Discovery time when v is first visited
lowLinks[v]  - Lowest discovery time reachable from v's subtree
stack        - Tracks nodes in current SCC being explored
onStack      - Tracks which nodes are currently in stack
```

**Process:**
1. Build adjacency list from all lookup edges
2. For each unvisited node, call StrongConnect
3. StrongConnect recursively visits successors
4. When `lowLinks[v] == indices[v]`, found SCC root
5. Pop all nodes until popping v to form SCC
6. Only add SCC if size > 1

### Tier Building (Kahn's Algorithm)

**Process:**
1. **SCC Condensation**: Treat each SCC as single virtual node
2. **Dependency Counting**: Count incoming edges for each node/SCC
3. **Tier Assignment**:
   - Tier 0: Entities with no dependencies
   - Each subsequent tier: Entities whose dependencies are all in previous tiers
4. **Parallel Processing**: Entities in same tier can import concurrently

**Result Ordering:**
- Tiers ordered so dependencies come BEFORE dependents
- All entities in a tier can be imported in parallel
- Tiers must be processed sequentially

### Deferred Field Strategy

**Purpose:** Break circular dependencies by nulling lookup fields during initial import

**Process:**
1. For each circular reference group:
   - Sort entities by lookup count (ascending) for minimal deferrals
   - For each entity's lookup fields pointing within the group:
     - If target entity processed AFTER current: defer field
     - If self-referential: always defer

**Example - Account ↔ Contact Cycle:**
```
Processing order: [Contact, Account] (Contact has 0 intra-group lookups)

Contact: imports normally (Account doesn't exist yet)
Account: imports with primarycontactid = NULL (deferred, Contact processed first)
Account: defers lookup because Contact was already processed

Phase 2: Updates Account.primarycontactid → Contact (now exists)
```

### Three-Phase Import

| Phase | Purpose | Deferred Fields |
|-------|---------|-----------------|
| 1 | Import records tier-by-tier | Set to NULL |
| 2 | Update deferred lookup fields | Set to proper values |
| 3 | Create M2M associations | N/A |

### Lifecycle

- **Graph Build**: Invoked during import preparation via `IDependencyGraphBuilder.Build()`
- **Plan Build**: Creates `ExecutionPlan` via `IExecutionPlanBuilder.Build()`
- **Phase 1**: `TieredImporter` nulls deferred fields via `PrepareRecordForImport()`
- **Phase 2**: `DeferredFieldProcessor` updates deferred fields with mapped references

## Edge Cases

| Scenario | Behavior | Notes |
|----------|----------|-------|
| Self-referential lookup | Always deferred | Parent and child may be in same batch |
| External lookup (entity not in schema) | Silently ignored | No edge created in graph |
| Polymorphic lookup (`account|contact`) | Split into separate edges | Each target creates own dependency |
| Empty schema | Empty graph, 0 tiers | No circular references detected |
| Large circular group (>2 entities) | Full SCC detection | Heuristic minimizes deferred fields |
| Multiple independent cycles | Each detected separately | Processed in optimal tier order |
| Cycle within same tier | Deferred fields break cycle | Phase 2 resolves after all records exist |

## Error Handling

| Exception | Condition | Recovery |
|-----------|-----------|----------|
| `InvalidOperationException` | Cycle detection fails | Should not occur with valid schema |
| `ArgumentNullException` | Null schema provided | Validate input before calling |

**Note:** Circular reference detection is deterministic and should not fail with valid input. The system gracefully handles external lookups and missing entities by ignoring them.

## Dependencies

- **Internal**:
  - `PPDS.Migration.Models` - Graph and plan data structures
  - `PPDS.Migration.Import` - `DeferredFieldProcessor` for Phase 2
- **External**:
  - None (pure algorithmic implementation)

## Configuration

The circular reference subsystem has no direct configuration. Behavior is controlled via:

| Related Setting | Location | Effect |
|-----------------|----------|--------|
| `DeferredFields` | `ExecutionPlan` | Output of analysis, consumed by import |
| `HasCircularReferences` | `ImportTier` | Marks tiers containing cycles |

## Thread Safety

- **`DependencyGraphBuilder`**: Stateless, thread-safe for concurrent calls
- **`ExecutionPlanBuilder`**: Stateless, thread-safe for concurrent calls
- **`DependencyGraph`**: Immutable after construction
- **`ExecutionPlan`**: Immutable after construction
- **`CircularReference`**: Immutable after construction

All graph building operations are single-threaded during the analysis phase. Thread safety matters during import where `DeferredFieldProcessor` uses concurrent operations.

## Performance Characteristics

| Operation | Complexity | Notes |
|-----------|------------|-------|
| Graph building | O(V + E) | V = entities, E = lookups |
| Tarjan SCC | O(V + E) | Single DFS traversal |
| Tier assignment | O(V + E) | Kahn's topological sort |
| Deferred field ID | O(F) | F = fields in circular groups |

**Memory:** O(V + E) for graph storage

**Typical Performance:**
- 100 entities, 500 lookups: < 10ms
- Graph analysis is negligible compared to Dataverse operations

## Algorithm Details

### Tarjan's SCC Implementation

```
StrongConnect(v):
  indices[v] = lowLinks[v] = index++
  stack.push(v)
  onStack[v] = true

  for each edge v → w:
    if w not visited:
      StrongConnect(w)
      lowLinks[v] = min(lowLinks[v], lowLinks[w])
    else if onStack[w]:
      lowLinks[v] = min(lowLinks[v], indices[w])

  if lowLinks[v] == indices[v]:  // v is root of SCC
    scc = []
    repeat:
      w = stack.pop()
      onStack[w] = false
      scc.add(w)
    until w == v
    if scc.size > 1:
      sccs.add(scc)
```

### Deferred Field Heuristic

**Goal:** Minimize number of deferred fields while maintaining correctness

**Strategy:**
1. Sort entities in circular group by count of intra-group lookups (ascending)
2. Entities with fewer lookups processed first
3. Fields pointing to already-processed entities are NOT deferred
4. Fields pointing to later-processed entities ARE deferred
5. Self-references always deferred (regardless of order)

**Example:**
```
Circular group: [A, B, C]
Lookup counts: A=3, B=1, C=2

Processing order: [B, C, A]

B: 0 deferred (no lookups to later entities)
C: 1 deferred (lookup to A)
A: 2 deferred (lookups to B, C - but B was first, so only to C and self)
```

## Related

- [Spec: Dependency Analysis](../specs/03-migration/01-dependency-analysis.md)
- [Spec: Import Pipeline](../specs/03-migration/03-import-pipeline.md)
- [Wikipedia: Tarjan's SCC Algorithm](https://en.wikipedia.org/wiki/Tarjan%27s_strongly_connected_components_algorithm)
- [Wikipedia: Topological Sorting](https://en.wikipedia.org/wiki/Topological_sorting)

## Source Files

| File | Purpose |
|------|---------|
| `src/PPDS.Migration/Analysis/IDependencyGraphBuilder.cs` | Graph builder interface |
| `src/PPDS.Migration/Analysis/DependencyGraphBuilder.cs` | Tarjan SCC and tier construction |
| `src/PPDS.Migration/Analysis/IExecutionPlanBuilder.cs` | Plan builder interface |
| `src/PPDS.Migration/Analysis/ExecutionPlanBuilder.cs` | Deferred field identification |
| `src/PPDS.Migration/Models/DependencyGraph.cs` | Graph, node, edge, circular reference models |
| `src/PPDS.Migration/Models/ExecutionPlan.cs` | Execution plan and tier models |
| `src/PPDS.Migration/Import/DeferredFieldProcessor.cs` | Phase 2 deferred field updates |
| `src/PPDS.Migration/Import/TieredImporter.cs` | Three-phase import orchestration |
| `tests/PPDS.Migration.Tests/Analysis/DependencyGraphBuilderTests.cs` | Graph builder unit tests |
| `tests/PPDS.Migration.Tests/Analysis/ExecutionPlanBuilderTests.cs` | Plan builder unit tests |
