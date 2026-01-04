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

**Status:** Not Started

| Feature | Command | Issue | Status |
|---------|---------|-------|--------|
| Flows | `ppds flows` | [#142](https://github.com/joshsmithxrm/ppds-sdk/issues/142) | Planned |
| Connection References | `ppds connrefs` | [#143](https://github.com/joshsmithxrm/ppds-sdk/issues/143) | Planned |
| Connections | `ppds connections` | [#144](https://github.com/joshsmithxrm/ppds-sdk/issues/144) | Planned |
| Deployment Settings | `ppds deployment-settings` | [#145](https://github.com/joshsmithxrm/ppds-sdk/issues/145) | Planned |

**Scope:**
- Flow/ConnRef/Connection entity relationships
- Orphaned connection reference detection
- Deployment settings generation (Microsoft format)

**Session Prompt:** [SESSION_2_CONNREFS_PROMPT.md](design-sessions/SESSION_2_CONNREFS_PROMPT.md)

---

## Phase 3: Plugin Traces (Design Session 3)

**Status:** Not Started

| Feature | Command | Issue | Status |
|---------|---------|-------|--------|
| Plugin Traces | `ppds plugintraces` | [#140](https://github.com/joshsmithxrm/ppds-sdk/issues/140) | Planned |

**Scope:**
- Full filtering (25 fields, 11 operators, 8 quick filters)
- Timeline correlation view
- Trace level management
- Export/delete operations

**Session Prompt:** [SESSION_3_PLUGINTRACES_PROMPT.md](design-sessions/SESSION_3_PLUGINTRACES_PROMPT.md)

---

## Phase 4: Web Resources (Design Session 4)

**Status:** Not Started

| Feature | Command | Issue | Status |
|---------|---------|-------|--------|
| Web Resources | `ppds webresources` | [#141](https://github.com/joshsmithxrm/ppds-sdk/issues/141) | Planned |

**Scope:**
- Published vs unpublished content
- Conflict detection on push
- Efficient filtering for 60K+ resources

**Session Prompt:** [SESSION_4_WEBRESOURCES_PROMPT.md](design-sessions/SESSION_4_WEBRESOURCES_PROMPT.md)

---

## Phase 5: Extension CLI Migration

**Status:** Blocked by Phase 1-4

| Feature | Issue | Status |
|---------|-------|--------|
| CLI binary bundling | Extension #51 | Blocked |
| DaemonCliService | Extension #52 | Blocked |
| Feature migration | Extension #53 | Blocked |
| TypeScript cleanup | Extension #54 | Blocked |

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
