# Metadata Browser Panel Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the Metadata Browser panel across all 4 surfaces (Daemon RPC, VS Code extension, TUI, MCP), providing entity/attribute/relationship/key/privilege/choices browsing with a split-pane layout.

**Architecture:** The domain service (`IMetadataService`) already exists with full entity metadata retrieval. We replace the stub `schema/*` RPC endpoints with richer `metadata/*` endpoints, add a VS Code webview panel with a split-pane layout (entity list + 5-tab detail), a TUI screen with SplitterView, and one new MCP tool. All surfaces call the same service methods (Constitution A1, A2).

**Tech Stack:** C# (.NET 8), TypeScript (VS Code extension + webview), Terminal.Gui (TUI), StreamJsonRpc (RPC), ModelContextProtocol (MCP)

**Spec:** `specs/panel-parity.md` — Panel 5 (Metadata Browser), Acceptance Criteria AC-MB-01 through AC-MB-10

**Issues:** #338, #347, #354, #590

---

## Design Decisions

### Replace `schema/*` with `metadata/*` RPC endpoints
The existing `schema/entities` and `schema/attributes` RPC methods are unused stubs — the IntelliSense pipeline uses `query/complete` which calls `ICachedMetadataProvider` server-side, not through RPC. The new `metadata/entities` and `metadata/entity` endpoints return richer payloads and serve the panel. The old `schema/*` methods, their DTOs (`SchemaEntitiesResponse`, `EntitySummaryDto`, `SchemaAttributesResponse`, `AttributeSummaryDto`), the TypeScript client wrappers (`schemaEntities()`, `schemaAttributes()`), and their TypeScript interfaces are removed.

### Single `GetEntityAsync()` call for entity detail
`metadata/entity` uses a single `IMetadataService.GetEntityAsync()` call with all EntityFilters (Entity | Attributes | Relationships | Privileges). One round-trip, atomic response, already implemented and tested. Keys are included in `EntityFilters.Entity` automatically. No parallel decomposition needed — the service layer has individual methods (`GetAttributesAsync`, etc.) for IntelliSense, but the panel doesn't use them.

### Global option sets via `includeGlobalOptionSets` flag
`metadata/entity` accepts an optional `includeGlobalOptionSets` parameter (default `false`). When `true`, the server inspects the entity's attributes for `isGlobalOptionSet: true`, extracts the distinct option set names, calls `GetOptionSetAsync()` for each in parallel, and returns them in a `globalOptionSets` array alongside the entity detail. This avoids a client-side bounce (the client would need the attribute list first to know which option sets to request).

### Choices tab shows both entity-scoped and global option sets
The Choices tab has two sections: (1) entity-scoped choice attributes with their inline option values (filtered from the attributes array where `options?.length > 0`), and (2) global option sets used by this entity's attributes (from the `globalOptionSets` response array). The old metadata browser showed both.

### Flat DOM list for entity list (no virtual scrolling)
500+ entity `<div>` rows is trivially small for modern browsers. Client-side search filters with `display: none`. Virtual scrolling would be over-engineering for a simple text list.

### Reuse DataTable component for detail tabs
Attributes tab (400+ rows) benefits from existing sort and status bar. DataTable is proven and avoids building a second table component. Nested scrolling managed with proper CSS containment.

---

## File Structure

### Files to modify
| File | Change |
|------|--------|
| `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs` | Remove `schema/*` methods + DTOs; add `metadata/entities` and `metadata/entity` RPC methods + DTOs |
| `src/PPDS.Dataverse/Metadata/IMetadataService.cs` | Add `GetEntityWithGlobalOptionSetsAsync()` method |
| `src/PPDS.Dataverse/Metadata/DataverseMetadataService.cs` | Implement `GetEntityWithGlobalOptionSetsAsync()` |
| `src/PPDS.Extension/src/types.ts` | Remove `Schema*` interfaces; add `Metadata*` interfaces |
| `src/PPDS.Extension/src/daemonClient.ts` | Remove `schemaEntities()`/`schemaAttributes()`; add `metadataEntities()`/`metadataEntity()` |
| `src/PPDS.Extension/src/panels/webview/shared/message-types.ts` | Add Metadata Browser panel message types |
| `src/PPDS.Extension/esbuild.js` | Add metadata-browser-panel JS + CSS entries |
| `src/PPDS.Extension/src/extension.ts` | Register `ppds.openMetadataBrowser` command |
| `src/PPDS.Extension/src/views/toolsTreeView.ts` | Add Metadata Browser to the Tools tree |
| `src/PPDS.Extension/package.json` | Add command contributions + menu items |
| `src/PPDS.Cli/Tui/TuiShell.cs` | Add menu item + NavigateToMetadataBrowser() method |

### Files to create
| File | Purpose |
|------|---------|
| `src/PPDS.Extension/src/panels/MetadataBrowserPanel.ts` | Host-side panel (extends WebviewPanelBase) |
| `src/PPDS.Extension/src/panels/webview/metadata-browser-panel.ts` | Browser-side webview script |
| `src/PPDS.Extension/src/panels/styles/metadata-browser-panel.css` | Panel-specific CSS |
| `src/PPDS.Cli/Tui/Screens/MetadataExplorerScreen.cs` | TUI screen (extends TuiScreenBase) |
| `src/PPDS.Mcp/Tools/MetadataEntitiesListTool.cs` | MCP tool: `ppds_metadata_entities` |
| `src/PPDS.Extension/src/__tests__/panels/metadataBrowserPanel.test.ts` | Unit tests for panel message handling |

### Files to delete
| File | Reason |
|------|--------|
| (none — removals are inline edits to existing files) | |

---

## Chunk 1: Service Layer — Global Option Set Aggregation

### Task 1: Add `GetEntityWithGlobalOptionSetsAsync` to IMetadataService

**Files:**
- Modify: `src/PPDS.Dataverse/Metadata/IMetadataService.cs`

- [ ] **Step 1: Add the new method to the interface**

Add after `GetEntityAsync` (line 41):

```csharp
/// <summary>
/// Gets full metadata for a specific entity, optionally including
/// global option set values for picklist attributes.
/// </summary>
/// <param name="logicalName">The entity logical name.</param>
/// <param name="includeGlobalOptionSets">If true, fetches values for any global option sets used by picklist attributes.</param>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>Full entity metadata with optional global option sets.</returns>
Task<(EntityMetadataDto Entity, IReadOnlyList<OptionSetMetadataDto> GlobalOptionSets)> GetEntityWithGlobalOptionSetsAsync(
    string logicalName,
    bool includeGlobalOptionSets = false,
    CancellationToken cancellationToken = default);
```

### Task 2: Implement `GetEntityWithGlobalOptionSetsAsync` in DataverseMetadataService

**Files:**
- Modify: `src/PPDS.Dataverse/Metadata/DataverseMetadataService.cs`

- [ ] **Step 1: Implement the method**

Add after `GetEntityAsync` (around line 152):

```csharp
/// <inheritdoc />
public async Task<(EntityMetadataDto Entity, IReadOnlyList<OptionSetMetadataDto> GlobalOptionSets)> GetEntityWithGlobalOptionSetsAsync(
    string logicalName,
    bool includeGlobalOptionSets = false,
    CancellationToken cancellationToken = default)
{
    var entity = await GetEntityAsync(
        logicalName,
        includeAttributes: true,
        includeRelationships: true,
        includeKeys: true,
        includePrivileges: true,
        cancellationToken: cancellationToken);

    if (!includeGlobalOptionSets)
    {
        return (entity, Array.Empty<OptionSetMetadataDto>());
    }

    // Extract distinct global option set names from picklist attributes
    var globalOptionSetNames = entity.Attributes
        .Where(a => a.IsGlobalOptionSet && !string.IsNullOrEmpty(a.OptionSetName))
        .Select(a => a.OptionSetName!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (globalOptionSetNames.Count == 0)
    {
        return (entity, Array.Empty<OptionSetMetadataDto>());
    }

    // Fetch all global option sets in parallel
    var optionSetTasks = globalOptionSetNames.Select(name =>
        GetOptionSetAsync(name, cancellationToken));
    var optionSets = await Task.WhenAll(optionSetTasks).ConfigureAwait(false);

    return (entity, optionSets);
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/PPDS.Dataverse/PPDS.Dataverse.csproj -v q`
Expected: Build succeeded.

---

## Chunk 2: RPC Layer — Replace `schema/*` with `metadata/*`

### Task 3: Remove `schema/*` RPC methods and DTOs

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

- [ ] **Step 1: Remove the `#region Schema Methods` block**

Remove lines 779-841 (the `SchemaEntitiesAsync` and `SchemaAttributesAsync` methods and their `#region`).

- [ ] **Step 2: Remove Schema DTO classes**

Remove from the DTO section at end of file (lines 2848-2884):
- `SchemaEntitiesResponse`
- `EntitySummaryDto`
- `SchemaAttributesResponse`
- `AttributeSummaryDto`

### Task 4: Add `metadata/entities` RPC method

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

- [ ] **Step 1: Add `metadata/entities` method**

Add in a new `#region Metadata Methods` section (where Schema Methods was):

```csharp
#region Metadata Methods

/// <summary>
/// Lists all entities in the environment with summary metadata.
/// Replaces schema/entities with richer payload for Metadata Browser panel.
/// </summary>
[JsonRpcMethod("metadata/entities")]
public async Task<MetadataEntitiesResponse> MetadataEntitiesAsync(
    string? environmentUrl = null,
    CancellationToken cancellationToken = default)
{
    return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
    {
        var metadataService = sp.GetRequiredService<IMetadataService>();
        var entities = await metadataService.GetEntitiesAsync(cancellationToken: ct);

        return new MetadataEntitiesResponse
        {
            Entities = entities.Select(MapEntitySummaryToDto).ToList()
        };
    }, cancellationToken);
}

private static MetadataEntitySummaryDto MapEntitySummaryToDto(EntitySummary e) => new()
{
    LogicalName = e.LogicalName,
    SchemaName = e.SchemaName,
    DisplayName = e.DisplayName,
    IsCustomEntity = e.IsCustomEntity,
    IsManaged = e.IsManaged,
    OwnershipType = e.OwnershipType,
    ObjectTypeCode = e.ObjectTypeCode,
    Description = e.Description
};
```

### Task 5: Add `metadata/entity` RPC method

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

- [ ] **Step 1: Add `metadata/entity` method**

Add after `metadata/entities` in the same region:

```csharp
/// <summary>
/// Gets full metadata for a single entity including attributes, relationships,
/// keys, privileges, and optionally global option set values.
/// </summary>
[JsonRpcMethod("metadata/entity")]
public async Task<MetadataEntityResponse> MetadataEntityAsync(
    string logicalName,
    bool includeGlobalOptionSets = false,
    string? environmentUrl = null,
    CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(logicalName))
    {
        throw new RpcException(
            ErrorCodes.Validation.RequiredField,
            "The 'logicalName' parameter is required");
    }

    return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
    {
        var metadataService = sp.GetRequiredService<IMetadataService>();
        var (entity, globalOptionSets) = await metadataService
            .GetEntityWithGlobalOptionSetsAsync(logicalName, includeGlobalOptionSets, ct);

        return new MetadataEntityResponse
        {
            Entity = MapEntityMetadataToDto(entity, globalOptionSets)
        };
    }, cancellationToken);
}

#endregion
```

- [ ] **Step 2: Add entity detail mapping helpers**

Add private mapping methods for entity detail DTO construction. These map from the existing `EntityMetadataDto`, `AttributeMetadataDto`, `RelationshipMetadataDto`, `ManyToManyRelationshipDto`, `EntityKeyDto`, `PrivilegeDto`, and `OptionSetMetadataDto` domain models to the wire DTOs.

The mapper should be straightforward field-by-field copying — the domain models already have the right shape. Key fields to include:

**Attributes:** logicalName, displayName, schemaName, attributeType, isPrimaryId, isPrimaryName, isCustomAttribute, requiredLevel, maxLength, minValue, maxValue, precision, targets (for lookups), optionSetName, isGlobalOptionSet, options (inline OptionValueDto list), format, dateTimeBehavior, sourceType, isSecured, isValidForGrid, isValidForForm, description, autoNumberFormat

**Relationships (1:N/N:1):** schemaName, relationshipType, referencedEntity, referencedAttribute, referencingEntity, referencingAttribute, cascadeAssign, cascadeDelete, cascadeMerge, cascadeReparent, cascadeShare, cascadeUnshare, isHierarchical

**ManyToMany:** schemaName, entity1LogicalName, entity1IntersectAttribute, entity2LogicalName, entity2IntersectAttribute, intersectEntityName

**Keys:** schemaName, logicalName, displayName, keyAttributes, entityKeyIndexStatus, isManaged

**Privileges:** privilegeId, name, privilegeType, canBeLocal, canBeDeep, canBeGlobal, canBeBasic

**Global option sets:** name, displayName, optionSetType, isGlobal, options (value, label, color, description)

### Task 6: Add Metadata RPC DTOs

**Files:**
- Modify: `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs`

- [ ] **Step 1: Add DTO classes at end of file**

Add response and detail DTO classes following the existing pattern (`[JsonPropertyName]` attributes, `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` for nullable fields):

- `MetadataEntitiesResponse` — `{ entities: MetadataEntitySummaryDto[] }`
- `MetadataEntitySummaryDto` — logicalName, schemaName, displayName, isCustomEntity, isManaged, ownershipType, objectTypeCode, description
- `MetadataEntityResponse` — `{ entity: MetadataEntityDetailDto }`
- `MetadataEntityDetailDto` — all summary fields + attributes[], oneToManyRelationships[], manyToOneRelationships[], manyToManyRelationships[], keys[], privileges[], globalOptionSets[]
- `MetadataAttributeDto` — attribute wire format
- `MetadataRelationshipDto` — 1:N/N:1 wire format
- `MetadataManyToManyDto` — N:N wire format
- `MetadataKeyDto` — alternate key wire format
- `MetadataPrivilegeDto` — privilege wire format
- `MetadataOptionSetDto` — global option set wire format
- `MetadataOptionValueDto` — option value wire format (value, label, color, description)

- [ ] **Step 2: Build full solution**

Run: `dotnet build PPDS.sln -v q`
Expected: Build succeeded. No downstream breaks — the `query/complete` endpoint uses `ICachedMetadataProvider` directly, not through the removed `schema/*` RPC methods.

---

## Chunk 3: TypeScript Client — Wire Types + Daemon Client

### Task 7: Update TypeScript types

**Files:**
- Modify: `src/PPDS.Extension/src/types.ts`

- [ ] **Step 1: Replace Schema interfaces with Metadata interfaces**

Remove the `// ── Schema` section (lines 271-293: `SchemaEntitiesResponse`, `EntitySummaryDto`, `SchemaAttributesResponse`, `AttributeSummaryDto`).

Add a `// ── Metadata` section with TypeScript interfaces mirroring the C# DTOs:

```typescript
// ── Metadata ─────────────────────────────────────────────────────────────────

export interface MetadataEntitiesResponse {
    entities: MetadataEntitySummaryDto[];
}

export interface MetadataEntitySummaryDto {
    logicalName: string;
    schemaName: string;
    displayName: string;
    isCustomEntity: boolean;
    isManaged: boolean;
    ownershipType: string | null;
    objectTypeCode: number;
    description: string | null;
}

export interface MetadataEntityResponse {
    entity: MetadataEntityDetailDto;
}

export interface MetadataEntityDetailDto extends MetadataEntitySummaryDto {
    primaryIdAttribute: string | null;
    primaryNameAttribute: string | null;
    entitySetName: string | null;
    isActivity: boolean;
    attributes: MetadataAttributeDto[];
    oneToManyRelationships: MetadataRelationshipDto[];
    manyToOneRelationships: MetadataRelationshipDto[];
    manyToManyRelationships: MetadataManyToManyDto[];
    keys: MetadataKeyDto[];
    privileges: MetadataPrivilegeDto[];
    globalOptionSets: MetadataOptionSetDto[];
}

export interface MetadataAttributeDto {
    logicalName: string;
    displayName: string | null;
    schemaName: string | null;
    attributeType: string;
    isPrimaryId: boolean;
    isPrimaryName: boolean;
    isCustomAttribute: boolean;
    requiredLevel: string | null;
    maxLength: number | null;
    minValue: number | null;
    maxValue: number | null;
    precision: number | null;
    targets: string[] | null;
    optionSetName: string | null;
    isGlobalOptionSet: boolean;
    options: MetadataOptionValueDto[] | null;
    format: string | null;
    dateTimeBehavior: string | null;
    sourceType: number | null;
    isSecured: boolean;
    description: string | null;
}

export interface MetadataRelationshipDto {
    schemaName: string;
    relationshipType: string;
    referencedEntity: string | null;
    referencedAttribute: string | null;
    referencingEntity: string | null;
    referencingAttribute: string | null;
    cascadeAssign: string | null;
    cascadeDelete: string | null;
    cascadeMerge: string | null;
    cascadeReparent: string | null;
    cascadeShare: string | null;
    cascadeUnshare: string | null;
    isHierarchical: boolean;
}

export interface MetadataManyToManyDto {
    schemaName: string;
    entity1LogicalName: string | null;
    entity1IntersectAttribute: string | null;
    entity2LogicalName: string | null;
    entity2IntersectAttribute: string | null;
    intersectEntityName: string | null;
}

export interface MetadataKeyDto {
    schemaName: string;
    logicalName: string;
    displayName: string | null;
    keyAttributes: string[];
    entityKeyIndexStatus: string | null;
    isManaged: boolean;
}

export interface MetadataPrivilegeDto {
    privilegeId: string;
    name: string;
    privilegeType: string;
    canBeLocal: boolean;
    canBeDeep: boolean;
    canBeGlobal: boolean;
    canBeBasic: boolean;
}

export interface MetadataOptionSetDto {
    name: string;
    displayName: string | null;
    optionSetType: string;
    isGlobal: boolean;
    options: MetadataOptionValueDto[];
}

export interface MetadataOptionValueDto {
    value: number;
    label: string;
    color: string | null;
    description: string | null;
}
```

### Task 8: Update daemon client

**Files:**
- Modify: `src/PPDS.Extension/src/daemonClient.ts`

- [ ] **Step 1: Replace schema methods with metadata methods**

Remove `schemaEntities()` and `schemaAttributes()` (lines 737-768).

Add in their place:

```typescript
// ── Metadata ────────────────────────────────────────────────────────────

async metadataEntities(environmentUrl?: string): Promise<MetadataEntitiesResponse> {
    await this.ensureConnected();

    const params: Record<string, unknown> = {};
    if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;

    this.log.debug('Calling metadata/entities...');
    const result = await this.connection!.sendRequest<MetadataEntitiesResponse>(
        'metadata/entities',
        params
    );
    this.log.debug(`Got ${result.entities.length} entities`);

    return result;
}

async metadataEntity(
    logicalName: string,
    includeGlobalOptionSets = false,
    environmentUrl?: string,
): Promise<MetadataEntityResponse> {
    await this.ensureConnected();

    const params: Record<string, unknown> = { logicalName, includeGlobalOptionSets };
    if (environmentUrl !== undefined) params.environmentUrl = environmentUrl;

    this.log.debug(`Calling metadata/entity for "${logicalName}"...`);
    const result = await this.connection!.sendRequest<MetadataEntityResponse>(
        'metadata/entity',
        params
    );
    this.log.debug(`Got entity "${logicalName}" with ${result.entity.attributes.length} attributes`);

    return result;
}
```

- [ ] **Step 2: Update imports in daemonClient.ts**

Add `MetadataEntitiesResponse`, `MetadataEntityResponse` to the import from `./types.js`.

- [ ] **Step 3: Build extension to verify**

Run: `cd src/PPDS.Extension && npm run build`
Expected: Build succeeded. If any TS code referenced `schemaEntities`/`schemaAttributes`, compiler will flag it — fix those callers.

---

## Chunk 4: VS Code Extension — Metadata Browser Panel

### Task 9: Add message types

**Files:**
- Modify: `src/PPDS.Extension/src/panels/webview/shared/message-types.ts`

- [ ] **Step 1: Add Metadata Browser message types**

Add at end of file:

```typescript
// ── Metadata Browser Panel ─────────────────────────────────────────────

/** Entity summary as sent to the webview for the entity list. */
export interface MetadataEntityViewDto {
    logicalName: string;
    schemaName: string;
    displayName: string;
    isCustomEntity: boolean;
    isManaged: boolean;
    ownershipType: string | null;
    objectTypeCode: number;
    description: string | null;
}

/** Messages the Metadata Browser Panel webview sends to the extension host. */
export type MetadataBrowserPanelWebviewToHost =
    | { command: 'ready' }
    | { command: 'refresh' }
    | { command: 'selectEntity'; logicalName: string }
    | { command: 'requestEnvironmentList' }
    | { command: 'openInMaker'; entityLogicalName?: string }
    | { command: 'copyToClipboard'; text: string }
    | { command: 'webviewError'; error: string; stack?: string };

/** Messages the extension host sends to the Metadata Browser Panel webview. */
export type MetadataBrowserPanelHostToWebview =
    | { command: 'updateEnvironment'; name: string; envType: string | null; envColor: string | null }
    | { command: 'entitiesLoaded'; entities: MetadataEntityViewDto[] }
    | { command: 'entityDetailLoaded'; entity: import('../../../types.js').MetadataEntityDetailDto }
    | { command: 'entityDetailLoading'; logicalName: string }
    | { command: 'loading' }
    | { command: 'error'; message: string }
    | { command: 'daemonReconnected' };
```

### Task 10: Create MetadataBrowserPanel host class

**Files:**
- Create: `src/PPDS.Extension/src/panels/MetadataBrowserPanel.ts`

- [ ] **Step 1: Create the panel class**

Follow the ImportJobsPanel pattern:
- Extend `WebviewPanelBase<MetadataBrowserPanelWebviewToHost, MetadataBrowserPanelHostToWebview>`
- Static `show()` factory with MAX_PANELS = 5
- `handleMessage()` switch on commands:
  - `ready` → load entity list via `daemon.metadataEntities(envUrl)`
  - `refresh` → invalidate and reload entity list
  - `selectEntity` → call `daemon.metadataEntity(logicalName, true, envUrl)`, send `entityDetailLoaded`
  - `requestEnvironmentList` → show environment picker
  - `openInMaker` → open entity in Power Apps Maker (`/entities/{objectTypeCode}/details`)
  - `copyToClipboard` → write to clipboard
  - `webviewError` → log error
- `onDaemonReconnected()` → reload entity list
- `getHtmlContent()` → CSP-safe HTML with split-pane layout structure

Key differences from ImportJobsPanel:
- The panel caches the entity list client-side (in a class member) to avoid re-fetching on every entity selection
- Entity detail is fetched on demand when an entity is selected
- `includeGlobalOptionSets: true` passed to get choices data

### Task 11: Create metadata-browser-panel webview script

**Files:**
- Create: `src/PPDS.Extension/src/panels/webview/metadata-browser-panel.ts`

- [ ] **Step 1: Implement the webview script**

Structure:
1. **Left pane — Entity list:**
   - Render all entities as `<div class="entity-row">` inside a scrollable container
   - Each row shows: icon (custom entity = `◆`, system = `○`), display name, logical name
   - FilterBar component for client-side search (searches displayName + logicalName + schemaName)
   - Click handler sends `selectEntity` message with logicalName
   - Selected entity highlighted with `.selected` CSS class

2. **Right pane — Tabbed detail:**
   - 5 tab buttons: Attributes, Relationships, Keys, Privileges, Choices
   - Each tab renders a DataTable when active
   - Tab content stored in memory, rendered on tab switch (avoid re-creating DataTable on each click)

3. **Tab: Attributes**
   - DataTable columns: Name (logicalName), Display Name, Type, Required, Custom, Max Length, Description
   - Sort by logicalName ascending default
   - Status bar: "X attributes"

4. **Tab: Relationships**
   - DataTable columns: Schema Name, Type (1:N/N:1/N:N), Related Entity, Lookup Field, Cascade Delete
   - Combine oneToManyRelationships + manyToOneRelationships + manyToManyRelationships into one table
   - For N:N: Related Entity shows the other entity, Lookup Field shows intersect entity
   - Sort by schemaName ascending default

5. **Tab: Keys**
   - DataTable columns: Display Name, Schema Name, Key Attributes (comma-separated), Index Status
   - Sort by schemaName ascending default

6. **Tab: Privileges**
   - DataTable columns: Name, Type (Create/Read/Write/Delete/Assign/Share/Append/AppendTo), Local, Deep, Global, Basic
   - Scope columns render checkmarks (✓/—)
   - Sort by name ascending default

7. **Tab: Choices**
   - Two sections with headers:
     - "Entity Choices" — filter attributes where `options?.length > 0 && !isGlobalOptionSet`, render DataTable: Attribute Name, Option Set Name, Values (count)
       - Click row to expand/show values inline or in sub-table
     - "Global Option Sets" — from `entity.globalOptionSets`, render DataTable: Name, Display Name, Type, Values (count)
       - Click row to expand/show values
   - Each expanded section shows value table: Value (int), Label, Color (swatch), Description

### Task 12: Create metadata-browser-panel CSS

**Files:**
- Create: `src/PPDS.Extension/src/panels/styles/metadata-browser-panel.css`

- [ ] **Step 1: Create panel styles**

Import shared.css, then define:
- `.split-pane` — flexbox row, 100% height
- `.entity-list-pane` — fixed width (280px), left side, overflow-y auto, border-right
- `.entity-detail-pane` — flex: 1, right side
- `.entity-row` — padding, cursor pointer, hover/selected states
- `.entity-row.selected` — background highlight using VS Code theme vars
- `.entity-row .entity-icon` — inline icon span
- `.entity-search` — input at top of left pane, full width
- `.tab-bar` — flex row of tab buttons, bottom border
- `.tab-button` — padding, border-bottom transparent, cursor pointer
- `.tab-button.active` — border-bottom with accent color
- `.tab-content` — flex: 1, overflow hidden, contains DataTable
- `.choices-section-header` — section header for Entity Choices / Global Option Sets
- `.color-swatch` — small inline color indicator for option set colors

### Task 13: Register panel in build and extension

**Files:**
- Modify: `src/PPDS.Extension/esbuild.js`
- Modify: `src/PPDS.Extension/src/extension.ts`
- Modify: `src/PPDS.Extension/src/views/toolsTreeView.ts`
- Modify: `src/PPDS.Extension/package.json`

- [ ] **Step 1: Add esbuild entries**

Add after the Import Jobs panel entries in esbuild.js:

```javascript
// Metadata Browser panel webview (browser, IIFE)
{
    entryPoints: ['src/panels/webview/metadata-browser-panel.ts'],
    bundle: true,
    format: 'iife',
    minify: production,
    sourcemap: !production,
    sourcesContent: false,
    platform: 'browser',
    outfile: 'dist/metadata-browser-panel.js',
    logLevel: 'warning',
},
```

```javascript
// Metadata Browser panel CSS
{
    entryPoints: ['src/panels/styles/metadata-browser-panel.css'],
    bundle: true,
    minify: production,
    outfile: 'dist/metadata-browser-panel.css',
    logLevel: 'warning',
},
```

- [ ] **Step 2: Register command in extension.ts**

Add to `registerPanelCommands()`:

```typescript
vscode.commands.registerCommand('ppds.openMetadataBrowser', () => {
    MetadataBrowserPanel.show(context.extensionUri, client);
}),
```

Add import for `MetadataBrowserPanel`.

- [ ] **Step 3: Add to Tools tree view**

In `toolsTreeView.ts`, add to the `tools` array:

```typescript
{ label: 'Metadata Browser', commandId: 'ppds.openMetadataBrowser', icon: 'symbol-class' },
```

- [ ] **Step 4: Add package.json contributions**

Add command and menu contributions following the Import Jobs pattern:

```json
{
    "command": "ppds.openMetadataBrowser",
    "title": "Open Metadata Browser",
    "category": "PPDS",
    "icon": "$(symbol-class)"
}
```

Add environment context menu entry:
```json
{
    "command": "ppds.openMetadataBrowserForEnv",
    "when": "view == ppds.profiles && viewItem == environment",
    "group": "env-tools@4"
}
```

- [ ] **Step 5: Build and verify**

Run: `cd src/PPDS.Extension && npm run build`
Expected: Build succeeded with new panel JS + CSS in dist/.

---

## Chunk 5: TUI Screen — MetadataExplorerScreen

### Task 14: Create MetadataExplorerScreen

**Files:**
- Create: `src/PPDS.Cli/Tui/Screens/MetadataExplorerScreen.cs`

- [ ] **Step 1: Create the screen class**

Extend `TuiScreenBase`. Layout:
- **Left pane:** FrameView titled "Entities" containing:
  - TextField for search (top, full width)
  - ListView of entity names (below search, fills remaining space)
  - Client-side filtering on TextField.TextChanged (debounced 300ms)
- **SplitterView:** Vertical resize between left and right panes
- **Right pane:** FrameView titled "Details" containing:
  - Tab bar (Label buttons: Attributes | Relationships | Keys | Privileges | Choices)
  - TableView below tab bar showing the active tab's data

Key behaviors:
- On entity selection (ListView.SelectedItemChanged): call `IMetadataService.GetEntityWithGlobalOptionSetsAsync()` via the session's service provider, populate right pane
- Tab switching updates the TableView data source
- Loading indicator while fetching entity detail
- Handle empty states ("Select an entity to view details", "No attributes", etc.)

Hotkeys:
- `Ctrl+R`: Refresh (clear all caches, reload entity list)
- `Ctrl+F`: Focus search field
- `Tab`: Cycle detail tabs (Attributes → Relationships → Keys → Privileges → Choices → Attributes)
- `Enter`: Select entity (if entity list focused)
- `Ctrl+O`: Open in Maker (launch browser URL)

### Task 15: Register screen in TuiShell

**Files:**
- Modify: `src/PPDS.Cli/Tui/TuiShell.cs`

- [ ] **Step 1: Add Tools menu entry**

Add after the Import Jobs menu item (around line 283):

```csharp
new("Metadata Browser", "Browse entity metadata", () => NavigateToMetadataBrowser()),
```

- [ ] **Step 2: Add NavigateToMetadataBrowser method**

Add after `NavigateToImportJobs()` (around line 486):

```csharp
private void NavigateToMetadataBrowser()
{
    HideSplash();

    var loadingLabel = new Label("Loading Metadata Browser...")
    {
        X = Pos.Center(),
        Y = Pos.Center()
    };
    _contentArea.Add(loadingLabel);
    _contentArea.Title = "Loading";

    Application.Refresh();

    Application.MainLoop?.AddIdle(() =>
    {
        _contentArea.Remove(loadingLabel);

        var screen = new MetadataExplorerScreen(_session);
        NavigateTo(screen);

        return false;
    });
}
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build PPDS.sln -v q`
Expected: Build succeeded.

---

## Chunk 6: MCP Tool — `ppds_metadata_entities`

### Task 16: Create MetadataEntitiesListTool

**Files:**
- Create: `src/PPDS.Mcp/Tools/MetadataEntitiesListTool.cs`

- [ ] **Step 1: Create the MCP tool**

Follow the ImportJobsListTool pattern:

```csharp
[McpServerToolType]
public sealed class MetadataEntitiesListTool
{
    private readonly McpToolContext _context;

    public MetadataEntitiesListTool(McpToolContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    [McpServerTool(Name = "ppds_metadata_entities")]
    [Description("Lists all entities (tables) in the connected Dataverse environment. Returns entity logical names, display names, schema names, and ownership type. Use this to discover available entities before querying with ppds_metadata_entity for full details.")]
    public async Task<MetadataEntitiesResult> ExecuteAsync(
        [Description("If true, only return custom entities (not system entities).")] bool customOnly = false,
        [Description("Optional filter pattern to match entity logical names. Supports * wildcard (e.g., 'account*', '*custom*').")] string? filter = null,
        CancellationToken cancellationToken = default)
    {
        await using var serviceProvider = await _context
            .CreateServiceProviderAsync(cancellationToken)
            .ConfigureAwait(false);

        var metadataService = serviceProvider
            .GetRequiredService<IMetadataService>();

        var entities = await metadataService
            .GetEntitiesAsync(customOnly, filter, cancellationToken)
            .ConfigureAwait(false);

        return new MetadataEntitiesResult
        {
            Entities = entities.Select(e => new EntityListItem
            {
                LogicalName = e.LogicalName,
                DisplayName = e.DisplayName,
                SchemaName = e.SchemaName,
                IsCustomEntity = e.IsCustomEntity,
                IsManaged = e.IsManaged,
                OwnershipType = e.OwnershipType,
                Description = e.Description
            }).ToList()
        };
    }
}

public sealed class MetadataEntitiesResult
{
    [JsonPropertyName("entityCount")]
    public int EntityCount => Entities.Count;

    [JsonPropertyName("entities")]
    public List<EntityListItem> Entities { get; set; } = [];
}

public sealed class EntityListItem
{
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = "";

    [JsonPropertyName("isCustomEntity")]
    public bool IsCustomEntity { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("ownershipType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnershipType { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}
```

- [ ] **Step 2: Build MCP project**

Run: `dotnet build src/PPDS.Mcp/PPDS.Mcp.csproj -v q`
Expected: Build succeeded. Tool auto-discovered via `[McpServerToolType]` attribute.

---

## Chunk 7: Cleanup + Tests

### Task 17: Remove stale TypeScript test references

**Files:**
- Modify: `src/PPDS.Extension/src/__tests__/daemonClient.test.ts`

- [ ] **Step 1: Remove or update schema-related tests**

Remove tests for `schemaEntities()` and `schemaAttributes()`. Add tests for `metadataEntities()` and `metadataEntity()` following the same mock pattern used by import job tests.

### Task 18: Add panel unit tests

**Files:**
- Create: `src/PPDS.Extension/src/__tests__/panels/metadataBrowserPanel.test.ts`

- [ ] **Step 1: Write unit tests for message handling**

Test cases:
- `ready` command triggers entity list load
- `selectEntity` command triggers entity detail load with `includeGlobalOptionSets: true`
- `refresh` command reloads entity list
- Error handling when daemon call fails
- Multiple `selectEntity` calls — latest wins (stale response protection)

### Task 19: Final build + test

- [ ] **Step 1: Full .NET build**

Run: `dotnet build PPDS.sln -v q`

- [ ] **Step 2: .NET unit tests**

Run: `dotnet test PPDS.sln --filter "Category!=Integration" -v q`

- [ ] **Step 3: Extension build**

Run: `cd src/PPDS.Extension && npm run build`

- [ ] **Step 4: Extension type check**

Run: `cd src/PPDS.Extension && npx tsc --noEmit`

- [ ] **Step 5: Extension lint**

Run: `cd src/PPDS.Extension && npx eslint src --quiet`

- [ ] **Step 6: Extension unit tests**

Run: `cd src/PPDS.Extension && npm run ext:test`

---

## Acceptance Criteria Mapping

| AC | Criterion | Chunk/Task | Verification |
|----|-----------|------------|--------------|
| AC-MB-01 | `metadata/entities` returns all entity definitions with summary fields | Chunk 2, Task 4 | RPC test or manual daemon call |
| AC-MB-02 | `metadata/entity` returns full detail (attributes, relationships, keys, privileges) in one call | Chunk 2, Task 5 | RPC test or manual daemon call |
| AC-MB-03 | VS Code panel displays entity list with search/filter and tabbed detail pane | Chunk 4, Tasks 10-12 | ext-verify screenshot |
| AC-MB-04 | Search/filter box filters entity list as user types (client-side) | Chunk 4, Task 11 | ext-verify interaction |
| AC-MB-05 | Attributes tab shows type-specific metadata | Chunk 4, Task 11 | ext-verify screenshot |
| AC-MB-06 | Relationships tab shows 1:N and N:N with cascade configuration | Chunk 4, Task 11 | ext-verify screenshot |
| AC-MB-07 | Entity list cached with configurable TTL; refresh clears cache | Chunk 4, Task 10 | Verify refresh behavior |
| AC-MB-08 | TUI MetadataExplorerScreen provides equivalent split pane with tab cycling | Chunk 5, Task 14 | tui-verify PTY interaction |
| AC-MB-09 | MCP ppds_metadata_entities returns entity list for AI discovery | Chunk 6, Task 16 | MCP Inspector test |
| AC-MB-10 | Handles large schemas (500+ entities) without UI lag | Chunk 4, Task 11 | ext-verify with real env |
