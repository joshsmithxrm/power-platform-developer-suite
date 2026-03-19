# Environment Variables

**Status:** Implemented
**Last Updated:** 2026-03-18
**Code:** [src/PPDS.Dataverse/Services/IEnvironmentVariableService.cs](../src/PPDS.Dataverse/Services/IEnvironmentVariableService.cs) | [src/PPDS.Extension/src/panels/EnvironmentVariablesPanel.ts](../src/PPDS.Extension/src/panels/EnvironmentVariablesPanel.ts) | [src/PPDS.Cli/Tui/Screens/EnvironmentVariablesScreen.cs](../src/PPDS.Cli/Tui/Screens/EnvironmentVariablesScreen.cs)
**Surfaces:** CLI, TUI, Extension, MCP

---

## Overview

View environment variable definitions and their current values, update values with type-aware validation, and export deployment settings. Key tool for deployment troubleshooting — surfaces the gap between default and current values, highlights missing required variables, and enables AI agents to fix misconfigurations.

### Goals

- **Configuration visibility:** Show environment variable definitions with default vs current values across all surfaces
- **Type-aware editing:** Validate input by type (String, Number, Boolean, JSON, DataSource) before writing
- **Deployment support:** Export `.deploymentsettings.json` for CI/CD pipeline configuration
- **Multi-surface consistency:** Same data and operations via VS Code, TUI, MCP, and CLI (Constitution A1, A2)

### Non-Goals

- Environment variable definition creation (managed through solutions)
- DataSource type editing (complex reference type, view-only)
- Bulk value updates across environments (deployment pipeline concern)

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
│  │         IEnvironmentVariableService              │ │
│  │   ListAsync, GetAsync, SetValueAsync, ExportAsync│ │
│  └─────────────────────┬───────────────────────────┘ │
│                        │                              │
│  ┌─────────────────────▼───────────────────────────┐ │
│  │         IDataverseConnectionPool                 │ │
│  └──────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────┘
```

**Data model note:** List is two Dataverse queries (definitions + values) joined client-side by definition ID.

### Components

| Component | Responsibility |
|-----------|----------------|
| `IEnvironmentVariableService` | Domain service — ListAsync, GetAsync, SetValueAsync, ExportAsync |
| `EnvironmentVariablesPanel.ts` | VS Code webview panel — table, inline edit, solution filter, export |
| `EnvironmentVariablesScreen.cs` | TUI screen — data table, detail/edit dialog, hotkeys |
| `EnvironmentVariablesListTool.cs` | MCP tool — structured listing with current vs default values |
| `EnvironmentVariablesGetTool.cs` | MCP tool — full detail including description and type |
| `EnvironmentVariablesSetTool.cs` | MCP tool — AI agents can fix misconfigurations |

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
| `environmentVariables/list` | `{ solutionId?, environmentUrl? }` | `{ variables: EnvironmentVariableInfo[] }` |
| `environmentVariables/get` | `{ schemaName, environmentUrl? }` | `{ variable: EnvironmentVariableDetail }` |
| `environmentVariables/set` | `{ schemaName, value, environmentUrl? }` | `{ success: boolean }` |

**EnvironmentVariableInfo fields:** schemaName, displayName, type (String/Number/Boolean/JSON/DataSource), defaultValue, currentValue, isManaged, isRequired, description, modifiedOn

### Extension Surface

- **viewType:** `ppds.environmentVariables`
- **Layout:** Three-zone with virtual table + inline edit capability
- **Table columns:** Schema Name, Display Name, Type, Default Value, Current Value, Managed, Modified On
- **Default sort:** schemaName ascending
- **Solution filter:** Dropdown in toolbar (persisted across sessions)
- **Visual indicators:** Override highlight (current differs from default), missing value warning (required + no value + no default)
- **Edit flow:** Edit action, type-aware input validation (boolean toggle, numeric validation, JSON syntax validation), calls `environmentVariables/set`, refreshes row
- **Actions:** Refresh, solution filter, edit value, export deployment settings (`.deploymentsettings.json`), Open in Maker, environment picker with theming

### TUI Surface

- **Class:** `EnvironmentVariablesScreen` extending `TuiScreenBase`
- **Hotkeys:** Ctrl+R (refresh), Enter (detail/edit dialog), Ctrl+E (export deployment settings), Ctrl+F (filter by solution), Ctrl+O (open in Maker)
- **Dialogs:** `EnvironmentVariableDetailDialog` — type-aware edit with validation

### MCP Surface

| Tool | Input | Output |
|------|-------|--------|
| `ppds_environment_variables_list` | `{ solutionId?, environmentUrl? }` | List with current vs default values |
| `ppds_environment_variables_get` | `{ schemaName }` | Full detail including description and type |
| `ppds_environment_variables_set` | `{ schemaName, value }` | Set value — AI agents can fix misconfigurations |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-EV-01 | `environmentVariables/list` returns definitions joined with current values | TBD | ✅ |
| AC-EV-02 | `environmentVariables/set` updates value record (creates if none exists) | TBD | ✅ |
| AC-EV-03 | VS Code panel shows visual differentiation between default, overridden, and missing values | TBD | ✅ |
| AC-EV-04 | Solution filter persists selection to globalState with panel-specific key | TBD | 🔲 |
| AC-EV-05 | Edit action validates input by type (String/Number/Boolean/JSON) | TBD | ✅ |
| AC-EV-06 | Export deployment settings writes correct .deploymentsettings.json format | TBD | ✅ |
| AC-EV-07 | TUI EnvironmentVariablesScreen displays same data and supports edit via dialog | TBD | ✅ |
| AC-EV-08 | MCP ppds_environment_variables_set can update a variable value | TBD | ✅ |
| AC-EV-09 | Required variables with no value and no default show warning indicator | TBD | ✅ |
| AC-EV-10 | All surfaces handle zero environment variables gracefully | TBD | ✅ |
| AC-EV-11 | Open in Maker uses `buildMakerUrl()` (not inline URL construction) | TBD | 🔲 |
| AC-EV-12 | Panel unit tests cover message handling, environment switching, data loading | TBD | 🔲 |

---

## Design Decisions

### Why client-side join for definitions and values?

**Context:** Environment variable definitions and values are separate Dataverse entities. Could use a FetchXml join, separate queries, or an expand.

**Decision:** Two separate queries joined client-side by definition ID.

**Rationale:** Dataverse `environmentvariabledefinition` and `environmentvariablevalue` have a 1:N relationship. Separate queries allow independent caching and are simpler to reason about than FetchXml outer joins. The result set is typically small (tens to low hundreds).

### Why type-aware validation on edit?

**Context:** Could accept any string value and let Dataverse reject invalid input, or validate client-side.

**Decision:** Client-side validation by type: boolean toggle, numeric range check, JSON syntax validation.

**Rationale:** Immediate feedback is better UX than a round-trip error. The type system is simple (5 types) and validation rules are straightforward. DataSource type is excluded from editing entirely (complex reference type).

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
