# PPDS SDK Roadmap

## Epic: Extension Feature Parity

**Goal:** SDK CLI parity with VS Code extension to enable CLI daemon migration (Extension issues #51-54).

**Status:** In Progress

---

## Phase 1: Core Commands (Design Session 1)

**Status:** In Design

| Feature | Command | Issue | Status |
|---------|---------|-------|--------|
| Solutions | `ppds solutions` | [#137](https://github.com/joshsmithxrm/ppds-sdk/issues/137) | Planned |
| Import Jobs | `ppds importjobs` | [#138](https://github.com/joshsmithxrm/ppds-sdk/issues/138) | Planned |
| Environment Variables | `ppds envvars` | [#139](https://github.com/joshsmithxrm/ppds-sdk/issues/139) | Planned |
| Users | `ppds users` | [#119](https://github.com/joshsmithxrm/ppds-sdk/issues/119) | Planned |
| Roles | `ppds roles` | [#119](https://github.com/joshsmithxrm/ppds-sdk/issues/119) | Planned |

**Scope:**
- Solution list, get, export, import, components, publish, url
- Import job list, get, data (XML), wait, url
- Environment variable list, get, set, export
- User/role management per issue #119

---

## Phase 2: Connection Management (Design Session 2)

**Status:** Design Complete

| Feature | Command | Issue | Status |
|---------|---------|-------|--------|
| Early-bound Entities | - | [#149](https://github.com/joshsmithxrm/ppds-sdk/issues/149) | Planned |
| Power Apps API Auth | - | [#150](https://github.com/joshsmithxrm/ppds-sdk/issues/150) | Planned |
| Flows | `ppds flows` | [#142](https://github.com/joshsmithxrm/ppds-sdk/issues/142) | Planned |
| Connection References | `ppds connrefs` | [#143](https://github.com/joshsmithxrm/ppds-sdk/issues/143) | Planned |
| Environment Variables | `ppds envvars` | [#151](https://github.com/joshsmithxrm/ppds-sdk/issues/151) | Planned |
| Connections | `ppds connections` | [#144](https://github.com/joshsmithxrm/ppds-sdk/issues/144) | Planned |
| Deployment Settings | `ppds deployment-settings` | [#145](https://github.com/joshsmithxrm/ppds-sdk/issues/145) | Planned |

**Scope:**
- SDK `QueryExpression` pattern for Dataverse entities (flows, connrefs, envvars)
- Power Apps Admin API for connections (different from Dataverse)
- Orphaned connection reference detection (port from extension)
- Deployment settings sync with value preservation and deterministic sorting
- PAC-compatible format (see [ADR-0011](adr/0011_DEPLOYMENT_SETTINGS_FORMAT.md))

**Design Session:** 2026-01-04 - Completed

---

## Phase 3: Plugin Traces (Design Session 3)

**Status:** Design Complete

| Feature | Command | Issue | Status |
|---------|---------|-------|--------|
| Plugin Traces | `ppds plugintraces` | [#140](https://github.com/joshsmithxrm/ppds-sdk/issues/140) | Planned |
| List with filters | `ppds plugintraces list` | [#152](https://github.com/joshsmithxrm/ppds-sdk/issues/152) | Planned |
| Get single trace | `ppds plugintraces get` | [#153](https://github.com/joshsmithxrm/ppds-sdk/issues/153) | Planned |
| Timeline correlation | `ppds plugintraces related` | [#154](https://github.com/joshsmithxrm/ppds-sdk/issues/154) | Planned |
| Filter file support | `--filter-file` | [#155](https://github.com/joshsmithxrm/ppds-sdk/issues/155) | Planned |
| Delete traces | `ppds plugintraces delete` | [#156](https://github.com/joshsmithxrm/ppds-sdk/issues/156) | Planned |
| Trace settings | `ppds plugintraces settings` | [#157](https://github.com/joshsmithxrm/ppds-sdk/issues/157) | Planned |
| CSV export | `--format csv` | [#158](https://github.com/joshsmithxrm/ppds-sdk/issues/158) | Planned |

**Scope:**
- Full filtering (25 fields, 11 operators, 8 quick filters)
- Hybrid filter approach: inline flags + filter file (see [ADR-0012](adr/0012_HYBRID_FILTER_DESIGN.md))
- Timeline correlation view
- Trace level management (off/exception/all)
- Export/delete operations

**Design Session:** 2026-01-04 - Completed

---

## Phase 4: Web Resources (Design Session 4)

**Status:** Design Complete

| Feature | Command | Issue | Status |
|---------|---------|-------|--------|
| Web Resources (Epic) | `ppds webresources` | [#141](https://github.com/joshsmithxrm/ppds-sdk/issues/141) | Planned |
| Foundation | Service + Entity | [#159](https://github.com/joshsmithxrm/ppds-sdk/issues/159) | Planned |
| List & Get | `list`, `get` | [#160](https://github.com/joshsmithxrm/ppds-sdk/issues/160) | Planned |
| Pull | `pull` | [#161](https://github.com/joshsmithxrm/ppds-sdk/issues/161) | Planned |
| Push | `push` | [#162](https://github.com/joshsmithxrm/ppds-sdk/issues/162) | Planned |
| Diff/Publish/URL | `diff`, `publish`, `url` | [#163](https://github.com/joshsmithxrm/ppds-sdk/issues/163) | Planned |

**Scope:**
- Published vs unpublished content (default: published per [ADR-0010](adr/0010_PUBLISHED_UNPUBLISHED_DEFAULT.md))
- Conflict detection on push (timestamp-based with hash tracking)
- Efficient filtering for 60K+ resources
- Hierarchical pull with `--strip-prefix` option

**Design Session:** 2026-01-04 - Completed

---

## Phase 5: Extension CLI Migration

**Status:** Blocked by Phase 1-4

| Feature | Issue | Status |
|---------|-------|--------|
| CLI binary bundling | [Extension #51](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/51) | Blocked |
| DaemonCliService | [Extension #52](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/52) | Blocked |
| Feature migration | [Extension #53](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/53) | Blocked |
| TypeScript cleanup | [Extension #54](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/54) | Blocked |

---

## Separate Track: Plugin Registration

**Status:** Needs Design Session

Full plugin lifecycle:
- Assemblies, packages, steps, images
- Service endpoints, webhooks
- Data providers, custom APIs
- View modes (by assembly, entity, message)

---

## Architecture Decision Records

| ADR | Decision |
|-----|----------|
| [ADR-0009](adr/0009_CLI_COMMAND_TAXONOMY.md) | Use `ppds plugintraces` (not `traces` or `plugins traces`) |
| [ADR-0010](adr/0010_PUBLISHED_UNPUBLISHED_DEFAULT.md) | Default to published, `--unpublished` flag |
| [ADR-0011](adr/0011_DEPLOYMENT_SETTINGS_FORMAT.md) | Use PAC-compatible deployment settings format |
| [ADR-0012](adr/0012_HYBRID_FILTER_DESIGN.md) | Hybrid filter design: inline flags + filter file for complex queries |

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
