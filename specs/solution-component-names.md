# Solution Component Name Resolution

**Status:** Draft
**Version:** 1.0
**Last Updated:** 2026-03-14
**Code:** [src/PPDS.Dataverse/Services/SolutionService.cs](../src/PPDS.Dataverse/Services/SolutionService.cs) | [extension/src/panels/SolutionsPanel.ts](../extension/src/panels/SolutionsPanel.ts)

---

## Overview

Solution panel components currently display as raw GUIDs, and component types in the 10000+ range show as "Unknown (N)". This spec adds component name resolution (mapping objectId GUIDs to human-readable names), fixes the component type label bug, enables daemon logging in serve mode, and adds an inline detail card for component inspection.

### Goals

- **Component names**: Display logical name, schema name, or display name instead of GUIDs in the solution panel component list
- **Component type fix**: Fix the silent fallback that causes 10000+ range types to show as "Unknown"
- **Daemon observability**: Enable logging in daemon serve mode so performance and errors are visible in the PPDS output channel
- **Component detail card**: Click-to-expand inline card showing all component metadata

### Non-Goals

- Metadata browser panel (future spec — this work warms the cache for it)
- Extension-side metadata caching (daemon is the single cache)
- Deep component metadata (attributes, relationships, etc. for individual components)
- Component name editing or interaction beyond inspection
- Eager metadata preload on environment connect

---

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│ VS Code Extension                                            │
│  ┌────────────────────┐                                      │
│  │   SolutionsPanel    │                                     │
│  │  (webview)          │                                     │
│  │  - renders names    │                                     │
│  │  - detail card UI   │                                     │
│  └────────┬───────────┘                                      │
│           │ postMessage                                      │
│  ┌────────┴───────────┐                                      │
│  │  SolutionsPanel.ts  │                                     │
│  │  (extension host)   │◄──── stderr ──── daemon logs        │
│  └────────┬───────────┘                                      │
│           │ JSON-RPC                                         │
└───────────┼──────────────────────────────────────────────────┘
            │
┌───────────┼──────────────────────────────────────────────────┐
│ Daemon    │                                                  │
│  ┌────────┴───────────┐                                      │
│  │ RpcMethodHandler    │                                     │
│  │ solutions/components│                                     │
│  └────────┬───────────┘                                      │
│           │                                                  │
│  ┌────────┴───────────┐     ┌──────────────────────┐         │
│  │  SolutionService    │────▶│ ComponentNameResolver │        │
│  │  GetComponentsAsync │     │ (new)                 │        │
│  └────────────────────┘     └──────────┬───────────┘         │
│                                        │                     │
│                          ┌─────────────┼─────────────┐       │
│                          │             │             │       │
│                    ┌─────┴─────┐ ┌─────┴─────┐ ┌────┴────┐  │
│                    │ Metadata  │ │ Dataverse  │ │ Logger  │  │
│                    │ Provider  │ │ Table      │ │ Stopwatch│  │
│                    │ (cached)  │ │ Queries    │ │ Timing  │  │
│                    └───────────┘ └───────────┘ └─────────┘  │
│                    Entity type 1   All other                 │
│                    (free lookup)   mapped types               │
└──────────────────────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `ComponentNameResolver` | Maps component objectId GUIDs to names via type-specific table queries |
| `SolutionService` | Orchestrates component loading + name resolution |
| `CachedMetadataProvider` | Provides entity names for type 1 components (existing, cached) |
| `SolutionsPanel.ts` (host) | Passes resolved names to webview |
| `SolutionsPanel.ts` (webview) | Renders names and detail cards |
| Daemon logger config | Enables Information-level logging in serve mode |

### Dependencies

- Depends on: [connection-pooling.md](./connection-pooling.md)
- Depends on: [dataverse-services.md](./dataverse-services.md)
- Uses patterns from: [architecture.md](./architecture.md)

---

## Specification

### Core Requirements

1. `SolutionComponentInfo` record gains three nullable fields: `LogicalName`, `SchemaName`, `DisplayName`
2. `ComponentNameResolver` resolves names for all mapped component types via batch queries
3. Entity-type (1) components resolve via `CachedMetadataProvider` — no additional Dataverse call
4. Unmapped component types gracefully degrade to GUID display
5. The `componenttype` option set query (Tier 1) bug is fixed so 10000+ range types resolve
6. Daemon serve mode emits Information-level logs to stderr
7. All name resolution queries are timed with `Stopwatch` and logged
8. Webview displays names with priority: logicalName > schemaName > displayName > objectId
9. Webview renders click-to-expand inline detail cards for components

### Component Type to Table Mapping

| Type Code | Type Name | Table | Name Fields |
|-----------|-----------|-------|-------------|
| 1 | Entity | `CachedMetadataProvider` | logicalName, schemaName, displayName |
| 26 | SavedQuery | `savedquery` | name |
| 29 | Workflow | `workflow` | name, uniquename |
| 60 | SystemForm | `systemform` | name |
| 61 | WebResource | `webresource` | name |
| 66 | CustomControl | `customcontrol` | name |
| 90 | PluginType | `plugintype` | name |
| 91 | PluginAssembly | `pluginassembly` | name |
| 92 | SDKMessageProcessingStep | `sdkmessageprocessingstep` | name |
| 300 | CanvasApp | `canvasapp` | name, displayname |
| 380 | EnvironmentVariableDefinition | `environmentvariabledefinition` | schemaname, displayname |
| 381 | EnvironmentVariableValue | `environmentvariablevalue` | schemaname |
| 371 | Connector | `connector` | name, displayname |
| 372 | Connector | `connector` | name, displayname |

For tables with a single `name` field, that value maps to `LogicalName` on `SolutionComponentInfo`. For tables with distinct schema/display fields, map accordingly.

### Primary Flows

**Component Name Resolution:**

1. **Load components**: `GetComponentsAsync` queries `solutioncomponent` table (existing)
2. **Group by type**: Group returned components by `componentType`
3. **Resolve names**: For each type with a mapping, call `ComponentNameResolver.ResolveAsync(type, objectIds)`
4. **Entity shortcut**: Type 1 uses `CachedMetadataProvider.GetEntitiesAsync()` (cached, no Dataverse call)
5. **Table queries**: Other types batch-query their table: `WHERE <primarykey> IN (id1, id2, ...)`
6. **Merge**: Attach resolved names to `SolutionComponentInfo` records
7. **Log timing**: Log per-type and total resolution time

**Component Type Fix:**

1. **Diagnose**: Determine why `GetComponentTypeNamesAsync` falls back to hardcoded dictionary
2. **Fix cache key**: `client.ConnectedOrgUniqueName` may return null — use environment URL from the RPC request instead
3. **Improve error logging**: Replace bare `catch` with structured logging so failures are visible

**Webview Display:**

1. **List item text**: `logicalName (displayName)` when both available, otherwise first available name, otherwise GUID
2. **Click handler**: Clicking a component item expands an inline detail card below it
3. **Single expand**: Clicking another component collapses the previously expanded one
4. **Detail card fields**: Logical Name, Schema Name, Display Name, Object ID (with copy affordance), Root Behavior, Metadata flag

### Constraints

- All name fields go through `escapeHtml()` before rendering (Constitution S1)
- `CancellationToken` threaded through entire resolution chain (Constitution R2)
- Connection pool clients disposed after each batch query (Constitution D2)
- No new `ServiceClient` instances — use `IDataverseConnectionPool` (Constitution D1)
- Component name resolution uses Application Service pattern (Constitution A1)

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| objectIds batch | Max 100 IDs per IN clause | Split into multiple queries if exceeded |
| Component type | Must be non-negative integer | Log warning, skip resolution |
| Resolved name | Null/empty allowed | Fall through to next priority |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | Entity-type (1) components display logicalName from cached metadata | `ComponentNameResolverTests.ResolveAsync_EntityType_UsesMetadataProvider` | 🔲 |
| AC-02 | WebResource components display name field from webresource table | `ComponentNameResolverTests.ResolveAsync_WebResource_QueriesTable` | 🔲 |
| AC-03 | Unmapped component types display objectId GUID as fallback | `ComponentNameResolverTests.ResolveAsync_UnmappedType_ReturnsNull` | 🔲 |
| AC-04 | Component types in 10000+ range display resolved labels, not "Unknown (N)" | `SolutionServiceTests.GetComponentsAsync_HighRangeTypes_Resolved` | 🔲 |
| AC-05 | Webview displays names with priority: logicalName > schemaName > displayName > objectId | `ext:test SolutionsPanel component name priority` | 🔲 |
| AC-06 | Clicking a component expands an inline detail card; clicking another collapses the first | `ext:test SolutionsPanel detail card toggle` | 🔲 |
| AC-07 | Detail card shows Object ID with copy button | `ext:test SolutionsPanel detail card copy` | 🔲 |
| AC-08 | Daemon serve mode emits Information-level logs to stderr | `ServeCommandTests.ServeMode_EmitsInfoLogs` | 🔲 |
| AC-09 | Component name resolution logs per-type timing: "Resolved {Count} {TypeName} names in {ElapsedMs}ms" | `ComponentNameResolverTests.ResolveAsync_LogsTiming` | 🔲 |
| AC-10 | Batch queries split at 100 IDs to avoid query length limits | `ComponentNameResolverTests.ResolveAsync_LargeBatch_Splits` | 🔲 |
| AC-11 | All resolved name strings are escaped via escapeHtml before innerHTML | `ext:test SolutionsPanel escapes names` | 🔲 |
| AC-12 | Name resolution failure for one type does not block other types | `ComponentNameResolverTests.ResolveAsync_PartialFailure_ContinuesOtherTypes` | 🔲 |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Component type has no mapping | Type 999, objectId = GUID | Display GUID, no error |
| Entity not found in metadata cache | Type 1, objectId not in entity list | Display GUID |
| Batch > 100 components of one type | 150 WebResources | Two queries: 100 + 50 |
| Name resolution query fails | Network error for workflow table | Log warning, display GUIDs for that type, other types unaffected |
| Component name is empty string | webresource.name = "" | Display GUID (treat empty as absent) |
| All name fields null | No logical/schema/display name resolved | Display objectId GUID |
| Metadata service unavailable | GetOptionSetAsync throws | Fall back to hardcoded dictionary, log warning (existing behavior, now visible) |

---

## Core Types

### ComponentNameResolver

Resolves component objectId GUIDs to human-readable names by querying type-specific Dataverse tables.

```csharp
public interface IComponentNameResolver
{
    Task<IReadOnlyDictionary<Guid, ComponentNames>> ResolveAsync(
        int componentType,
        IReadOnlyList<Guid> objectIds,
        CancellationToken cancellationToken = default);
}

public record ComponentNames(
    string? LogicalName,
    string? SchemaName,
    string? DisplayName);
```

### Updated SolutionComponentInfo

```csharp
public record SolutionComponentInfo(
    Guid ComponentId,
    Guid ObjectId,
    int ComponentType,
    string ComponentTypeName,
    int RootComponentBehavior,
    bool IsMetadata,
    string? DisplayName,
    string? LogicalName,
    string? SchemaName);
```

### Usage Pattern

```csharp
// Inside SolutionService.GetComponentsAsync
var resolver = _componentNameResolver;
var grouped = components.GroupBy(c => c.ComponentType);

await Parallel.ForEachAsync(grouped, cancellationToken, async (group, ct) =>
{
    var names = await resolver.ResolveAsync(group.Key, group.Select(c => c.ObjectId).ToList(), ct);
    foreach (var component in group)
    {
        if (names.TryGetValue(component.ObjectId, out var resolved))
        {
            // Attach names to component
        }
    }
});
```

---

## API/Contracts

The existing `solutions/components` RPC method response gains name fields:

```json
{
  "components": [
    {
      "componentId": "...",
      "objectId": "d8f9e4c2-3b1a-4e8c-9f7d-2c1a5e8b9f4d",
      "componentType": 61,
      "componentTypeName": "WebResource",
      "rootComponentBehavior": 0,
      "isMetadata": false,
      "logicalName": "new_scripts/account_form.js",
      "schemaName": null,
      "displayName": null
    },
    {
      "componentId": "...",
      "objectId": "a1b2c3d4-...",
      "componentType": 1,
      "componentTypeName": "Entity",
      "rootComponentBehavior": 0,
      "isMetadata": true,
      "logicalName": "account",
      "schemaName": "Account",
      "displayName": "Account"
    }
  ]
}
```

No new RPC methods. No breaking changes — new fields are nullable and additive.

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| Table query failure | Network error, auth failure, table not found | Log warning with type and error, return empty dict for that type, other types unaffected |
| Metadata provider failure | CachedMetadataProvider throws | Log warning, entity-type components fall back to GUID |
| Option set query failure | GetOptionSetAsync throws for componenttype | Log warning (now visible), fall back to hardcoded dictionary (existing) |
| Batch too large | > 100 objectIds for one type | Split into sub-batches automatically |

### Recovery Strategies

- **Per-type isolation**: Each component type resolves independently. Failure in one type does not affect others.
- **Graceful degradation**: Any resolution failure results in GUID display — the panel never breaks, just shows less information.
- **No retry**: Failed queries are not retried. The next panel load will retry naturally.

---

## Design Decisions

### Why daemon-centric caching (no extension-side cache)?

**Context:** Metadata is needed by the solution panel now and the metadata browser later. Where should the cache live?

**Decision:** All metadata caching stays in the daemon's existing `CachedMetadataProvider`. The extension calls RPC endpoints with no client-side caching.

**Alternatives considered:**
- Extension-host TypeScript cache: Rejected — creates two caches with synchronization/invalidation complexity for marginal benefit. RPC is local IPC (sub-millisecond), not worth optimizing.
- Eager preload on connect: Rejected — slows initial connection, fetches data that may never be needed.

**Consequences:**
- Positive: Single source of truth, no stale-data bugs, existing infrastructure (80% built)
- Negative: Each panel load incurs RPC round-trips, but these hit the daemon cache and are negligible

### Why no additional caching for component name resolution?

**Context:** Should resolved component names be cached?

**Decision:** No separate cache. Queries are simple IN-clause lookups on indexed primary keys. Entity-type names are already cached via `CachedMetadataProvider`.

**Alternatives considered:**
- Per-solution name cache with TTL: Rejected — adds invalidation complexity for component renames, and the queries are fast enough without caching.

**Consequences:**
- Positive: No stale names, no cache invalidation logic, simpler code
- Negative: Re-queries on each panel load (~5-10 small queries). Acceptable for correctness.

### Why click-to-expand instead of hover tooltip or side panel?

**Context:** How should component details be displayed?

**Decision:** Click-to-expand inline detail card, consistent with existing solution card pattern.

**Alternatives considered:**
- Hover tooltip: Rejected — can't copy text (developers need to copy GUIDs), not keyboard accessible, disappears on mouse-out.
- Side panel / master-detail: Rejected — overkill for 4-6 fields of component info. The solution panel already has 3 nesting levels. Save master-detail for the metadata browser where it's needed.

**Consequences:**
- Positive: Consistent UX pattern, copiable text, keyboard accessible, simple implementation
- Negative: Expands the list vertically (mitigated by single-expand behavior)

### Why fix daemon logging as part of this spec?

**Context:** Daemon serve mode uses `NullLoggerFactory` and `LogLevel.Warning` minimum — all Info/Debug logs are silent.

**Decision:** Enable Information-level logging in serve mode. Logs flow to stderr, which the extension captures into the PPDS output channel.

**Alternatives considered:**
- Separate logging spec: Rejected — this work needs observable performance data immediately. Fixing logging is a prerequisite, not a separate initiative.

**Consequences:**
- Positive: Component name resolution timing is visible, metadata query failures are diagnosable, foundation for future observability
- Negative: More log volume in PPDS output channel (acceptable — Information level, not Debug)

---

## Extension Points

### Adding a New Component Type Mapping

1. **Add entry**: Add the component type code, table name, and name field(s) to the mapping dictionary in `ComponentNameResolver`
2. **Test**: Add a test case to `ComponentNameResolverTests` for the new type
3. **No registration needed**: The resolver auto-discovers mappings from its internal dictionary

---

## Related Specs

- [dataverse-services.md](./dataverse-services.md) - IMetadataService used for entity-type resolution
- [connection-pooling.md](./connection-pooling.md) - Connection pool used for batch queries
- [per-panel-environment-scoping.md](./per-panel-environment-scoping.md) - Environment context for daemon RPC calls

---

## Roadmap

- Metadata browser panel — will use the same `CachedMetadataProvider` and `schema/*` RPC endpoints
- Component name resolution for additional types as users report gaps
- Open-in-maker-portal action from component detail card
