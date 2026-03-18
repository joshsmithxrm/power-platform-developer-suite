# Metadata Browser

**Status:** Implemented
**Last Updated:** 2026-03-18
**Code:** [src/PPDS.Dataverse/Services/IMetadataService.cs](../src/PPDS.Dataverse/Services/IMetadataService.cs) | [src/PPDS.Extension/src/panels/MetadataBrowserPanel.ts](../src/PPDS.Extension/src/panels/MetadataBrowserPanel.ts) | [src/PPDS.Cli/Tui/Screens/MetadataExplorerScreen.cs](../src/PPDS.Cli/Tui/Screens/MetadataExplorerScreen.cs)
**Surfaces:** CLI, TUI, Extension, MCP

---

## Overview

Browse entity definitions, attributes, relationships, keys, and privileges. The schema exploration tool for understanding the Dataverse data model without leaving the IDE or TUI.

### Goals

- **Schema discovery:** Browse all entity definitions with attributes, relationships, alternate keys, and security privileges
- **Fast navigation:** Client-side search/filter for instant entity lookup in large schemas (500+ entities)
- **Cached performance:** Entity list cached with configurable TTL to avoid repeated metadata API calls
- **Multi-surface consistency:** Same schema data available via VS Code, TUI, MCP, and CLI (Constitution A1, A2)

### Non-Goals

- Schema modification (entity/attribute creation, relationship management)
- Solution-aware metadata filtering (shows all entities regardless of solution membership)
- Custom metadata views or saved filters

---

## Architecture

```
┌──────────────────────────────────────────────────────┐
│                   UI Surfaces (thin)                  │
│  ┌───────────┐  ┌─────────┐  ┌──────┐  ┌─────────┐ │
│  │  VS Code  │  │   TUI   │  │ MCP  │  │   CLI   │ │
│  │  Webview  │  │ Screen  │  │ Tool │  │ Command │ │
│  └─────┬─────┘  └────┬────┘  └──┬───┘  └────┬────┘ │
│   JSON-RPC        Direct     Direct       Direct     │
│  ┌─────▼──────────────▼──────────▼────────────▼────┐ │
│  │    IMetadataService / ICachedMetadataProvider     │ │
│  │    DataverseMetadataService                      │ │
│  └─────────────────────┬───────────────────────────┘ │
│                        │                              │
│  ┌─────────────────────▼───────────────────────────┐ │
│  │  EntityDefinitions API (Metadata endpoint)       │ │
│  │  IDataverseConnectionPool                        │ │
│  └──────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────┘
```

**Design decision:** Two RPC endpoints, not five. `metadata/entities` returns the lightweight list. `metadata/entity` returns everything for a selected entity in one call. Avoids chatty round-trips while keeping initial load fast.

### Components

| Component | Responsibility |
|-----------|----------------|
| `IMetadataService` / `DataverseMetadataService` | Domain service — entity list, entity detail with all sub-collections |
| `ICachedMetadataProvider` | Caching layer — TTL-based entity list cache |
| `MetadataBrowserPanel.ts` | VS Code webview panel — split pane with search and 5-tab detail |
| `MetadataExplorerScreen.cs` | TUI screen — split pane, tab cycling, search dialog |
| `MetadataEntitiesListTool.cs` | MCP tool — entity list for AI discovery |
| `MetadataEntityTool.cs` | MCP tool — full metadata with relationships |

### Dependencies

- Depends on: [connection-pooling.md](./connection-pooling.md) for pooled Dataverse clients
- Depends on: [architecture.md](./architecture.md) for Application Service boundary
- Uses patterns from: [CONSTITUTION.md](./CONSTITUTION.md) — A1, A2, D1

---

## Specification

### Service Layer

**RPC Endpoints:**

| Method | Request | Response |
|--------|---------|----------|
| `metadata/entities` | `{ environmentUrl? }` | `{ entities: EntitySummary[] }` |
| `metadata/entity` | `{ logicalName, environmentUrl? }` | `{ entity: EntityDetail }` |

**EntitySummary fields:** logicalName, schemaName, displayName, isCustomEntity, isManaged, ownershipType

**EntityDetail fields:** all of EntitySummary + attributes[], relationships[] (oneToMany + manyToMany), keys[], privileges[]

### Extension Surface

- **viewType:** `ppds.metadataBrowser`
- **Layout:** Split pane — left: entity list with search/filter, right: 5-tab detail view
- **Left pane:** Flat entity list (sortable), search box (client-side filter as you type), custom vs system entity icons
- **Right pane tabs:** Attributes (type-specific metadata), Relationships (1:N and N:N with cascade config), Keys (alternate keys with fields), Privileges (access rights), Choices (option set values for selected choice attribute)
- **Caching:** Entity list cached with configurable TTL (default 5 minutes); entity details cached on first load, invalidated on refresh
- **Actions:** Refresh (clears cache), search, Open in Maker, environment picker with theming

### TUI Surface

- **Class:** `MetadataExplorerScreen` extending `TuiScreenBase`
- **Layout:** Split pane — left: entity list, right: tabbed detail
- **Hotkeys:** Ctrl+R (refresh), Ctrl+F (search), Tab (cycle tabs), Enter (select entity), Ctrl+O (open in Maker)
- **Dialogs:** `EntitySearchDialog` (quick pick for large schemas), `ChoiceValuesDialog`

### MCP Surface

| Tool | Input | Output |
|------|-------|--------|
| `ppds_data_schema` | `{ entityName }` | Entity schema |
| `ppds_metadata_entity` | `{ entityName }` | Full metadata with relationships |
| `ppds_metadata_entities` | `{ environmentUrl? }` | Entity list for discovery |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-MB-01 | `metadata/entities` returns all entity definitions with summary fields | TBD | ✅ |
| AC-MB-02 | `metadata/entity` returns full detail (attributes, relationships, keys, privileges) in one call | TBD | ✅ |
| AC-MB-03 | VS Code panel displays entity list with search/filter and tabbed detail pane | TBD | ✅ |
| AC-MB-04 | Search/filter box filters entity list as user types (client-side) | TBD | ✅ |
| AC-MB-05 | Attributes tab shows type-specific metadata | TBD | ✅ |
| AC-MB-06 | Relationships tab shows 1:N and N:N with cascade configuration | TBD | ✅ |
| AC-MB-07 | Entity list cached with configurable TTL; refresh clears cache | TBD | ✅ |
| AC-MB-08 | TUI MetadataExplorerScreen provides equivalent split pane with tab cycling | TBD | ✅ |
| AC-MB-09 | MCP ppds_metadata_entities returns entity list for AI discovery | TBD | ✅ |
| AC-MB-10 | Handles large schemas (500+ entities) without UI lag | TBD | ✅ |
| AC-MB-11 | `openMetadataBrowserForEnv` context menu command opens panel scoped to selected environment | TBD | 🔲 |
| AC-MB-12 | Open in Maker uses `buildMakerUrl()` (not inline URL construction) | TBD | 🔲 |

---

## Design Decisions

### Why two endpoints instead of five?

**Context:** Could expose separate endpoints for attributes, relationships, keys, privileges, and choices — or bundle entity detail into one call.

**Decision:** Two endpoints: lightweight `metadata/entities` list, and `metadata/entity` that returns everything for one entity.

**Rationale:** Avoids chatty round-trips when navigating between tabs in the detail view. Entity detail payload is moderate (KB, not MB) even for entities with many attributes. Initial list load stays fast because it returns only summary fields. Single-entity detail call is cached on first load.

### Why client-side search instead of server-side?

**Context:** Entity list can contain 500+ entries. Could filter server-side via metadata API or client-side.

**Decision:** Client-side substring filtering with debounced input.

**Rationale:** The full entity list is already cached locally (necessary for the list view). Client-side filtering provides instant feedback without additional API calls. The metadata API does not have efficient partial-match filtering — it's designed for exact logical name lookups.

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-18 | Extracted from panel-parity.md per SL1 |

---

## Related Specs

- [architecture.md](./architecture.md) — Application Service boundary
- [connection-pooling.md](./connection-pooling.md) — Dataverse connection management
- [CONSTITUTION.md](./CONSTITUTION.md) — Governing principles
