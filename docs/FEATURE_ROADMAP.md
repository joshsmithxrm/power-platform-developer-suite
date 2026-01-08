# PPDS Feature Roadmap

## Epic: Extension Feature Parity

**Goal:** CLI parity with VS Code extension to enable CLI daemon migration (Extension issues #51-54).

**Status:** In Progress

**View Issues:** [Filter by phase label](https://github.com/joshsmithxrm/power-platform-developer-suite/issues?q=is%3Aopen+label%3Aphase%3A1-core%2Cphase%3A2-connections%2Cphase%3A3-traces%2Cphase%3A4-webresources%2Cphase%3A5-migration)

---

## Phases Overview

| Phase | Label | Status | Scope |
|-------|-------|--------|-------|
| 1 | `phase:1-core` | Design | Solutions, Import Jobs, Env Vars, Users/Roles |
| 2 | `phase:2-connections` | Design Complete | Flows, ConnRefs, Connections, Deployment Settings |
| 3 | `phase:3-traces` | Design Complete | Plugin Traces (full filtering, timeline, settings) |
| 4 | `phase:4-webresources` | Design Complete | Web Resources (pull, push, diff, publish) |
| 5 | `phase:5-migration` | Blocked | Extension CLI daemon migration |

**Filter by phase:** `label:phase:2-connections`

---

## Phase 1: Core Commands

**Epic:** [#137](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/137) (Solutions)

**Scope:**
- `ppds solutions` - list, get, export, import, components, publish, url
- `ppds importjobs` - list, get, data (XML), wait, url
- `ppds envvars` - list, get, set, export
- `ppds users`/`ppds roles` - per issue #119

---

## Phase 2: Connection Management

**Design Session:** 2026-01-04 - Completed

**Scope:**
- SDK `QueryExpression` pattern for Dataverse entities (flows, connrefs, envvars)
- Power Apps Admin API for connections (different from Dataverse)
- Orphaned connection reference detection (port from extension)
- Deployment settings sync with value preservation and deterministic sorting
- PAC-compatible format (see [ADR-0011](adr/0011_DEPLOYMENT_SETTINGS_FORMAT.md))

---

## Phase 3: Plugin Traces

**Design Session:** 2026-01-04 - Completed

**Scope:**
- Full filtering (25 fields, 11 operators, 8 quick filters)
- Hybrid filter approach: inline flags + filter file (see [ADR-0012](adr/0012_HYBRID_FILTER_DESIGN.md))
- Timeline correlation view
- Trace level management (off/exception/all)
- Export/delete operations

---

## Phase 4: Web Resources

**Design Session:** 2026-01-04 - Completed

**Scope:**
- Published vs unpublished content (default: published per [ADR-0010](adr/0010_PUBLISHED_UNPUBLISHED_DEFAULT.md))
- Conflict detection on push (timestamp-based with hash tracking)
- Efficient filtering for 60K+ resources
- Hierarchical pull with `--strip-prefix` option

---

## Phase 5: Extension CLI Migration

**Status:** Blocked by Phase 1-4

Depends on SDK phases being complete. See Extension issues #51-54.

---

## Separate Track: Plugin Registration

**Status:** Needs Design Session

Full plugin lifecycle: assemblies, packages, steps, images, service endpoints, webhooks, data providers, custom APIs.

---

## Architecture Decision Records

| ADR | Decision |
|-----|----------|
| [ADR-0009](adr/0009_CLI_COMMAND_TAXONOMY.md) | Use `ppds plugintraces` (not `traces` or `plugins traces`) |
| [ADR-0010](adr/0010_PUBLISHED_UNPUBLISHED_DEFAULT.md) | Default to published, `--unpublished` flag |
| [ADR-0011](adr/0011_DEPLOYMENT_SETTINGS_FORMAT.md) | Use PAC-compatible deployment settings format |
| [ADR-0012](adr/0012_HYBRID_FILTER_DESIGN.md) | Hybrid filter design: inline flags + filter file |

---

## Key Design Decisions

### JSON Output Strategy
- `list`: Core fields only, expensive fields excluded
- `get`: All fields included
- Dedicated commands for large blobs (`ppds importjobs data`)

### Maker URL Pattern
- JSON output always includes `makerUrl` field
- `url` subcommand for human convenience
- Table output excludes URL (too long)

### Three Audiences
Every command serves Extension (JSON-RPC), Humans (tables), AI/Tooling (structured errors).

---

## References

- [CLI Output Architecture (ADR-0008)](adr/0008_CLI_OUTPUT_ARCHITECTURE.md)
- [Extension CLI Migration Issues](https://github.com/joshsmithxrm/power-platform-developer-suite/issues?q=is%3Aissue+label%3Aepic%3Acli-daemon)
