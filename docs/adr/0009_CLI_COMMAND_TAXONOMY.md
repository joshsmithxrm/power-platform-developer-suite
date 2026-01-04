# ADR-0009: CLI Command Taxonomy

**Status:** Accepted
**Date:** 2026-01-04
**Authors:** Josh, Claude

## Context

As we add CLI commands for extension feature parity, we need consistent naming conventions. Specifically, the plugin trace viewer feature raised a naming question:

1. `ppds traces` - Short but ambiguous (traces of what?)
2. `ppds plugins traces` - Grouped under plugins namespace
3. `ppds plugintraces` - Specific, maps to entity name

The `plugins` command group currently handles plugin registration:
- `ppds plugins extract` - Extract from assembly
- `ppds plugins deploy` - Deploy registrations
- `ppds plugins list` - List registered plugins
- `ppds plugins diff` - Compare config vs environment
- `ppds plugins clean` - Remove orphaned registrations

Plugin traces are an observability/debugging concern, not a registration concern. Mixing them under `plugins` conflates two different domains.

## Decision

Use `ppds plugintraces` as a standalone command group.

### Naming Pattern

| Domain | Command | Entity | Notes |
|--------|---------|--------|-------|
| Registration | `ppds plugins` | pluginassembly, plugintype, sdkmessageprocessingstep | Plugin lifecycle management |
| Observability | `ppds plugintraces` | plugintracelog | Debugging, monitoring |
| Solutions | `ppds solutions` | solution | Solution management |
| Import Jobs | `ppds importjobs` | importjob | Specific to avoid "jobs" ambiguity |
| Web Resources | `ppds webresources` | webresource | Direct entity match |
| Connection Refs | `ppds connrefs` | connectionreference | Abbreviated for clarity |
| Flows | `ppds flows` | workflow (cloudflow) | Cloud flow management |
| Connections | `ppds connections` | connection | Connector instances |

### Principles

1. **Entity alignment** - Command names should map to Dataverse entity logical names where practical
2. **Specificity over brevity** - `plugintraces` is longer than `traces` but unambiguous
3. **Domain separation** - Don't nest unrelated features (traces ≠ registration)
4. **Future-proofing** - Clear naming allows adding related features (e.g., `ppds flowruns` for flow execution history)

## Consequences

### Positive
- Clear, unambiguous command names
- Easy to discover via `ppds --help`
- Entity-aligned naming aids documentation
- Separation of concerns in command structure

### Negative
- Slightly longer command names
- Users must learn domain-specific terms

## Alternatives Considered

### `ppds plugins traces`
Rejected because:
- Conflates registration and observability domains
- Makes `plugins` a catch-all for anything plugin-related
- Inconsistent with other feature groupings (we don't have `ppds solutions components` as a nested group)

### `ppds traces`
Rejected because:
- Ambiguous - could mean application traces, telemetry, etc.
- Doesn't indicate the feature domain
- May conflict if we add other trace types later
