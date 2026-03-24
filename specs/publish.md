# Publish

**Status:** Draft
**Last Updated:** 2026-03-23
**Code:** [src/PPDS.Cli/Commands/Publish/](../src/PPDS.Cli/Commands/Publish/)
**Surfaces:** CLI

---

## Overview

Cross-cutting publish command for Dataverse customizations. Consolidates all publish operations under a single top-level command with type-scoped subcommand aliases. Replaces the existing `ppds solutions publish` (which called `PublishAllXml` directly) with a structured command that distinguishes between publishing everything and publishing specific components.

### Goals

- **Unified publish entry point:** Single `ppds publish` command for all publish operations
- **Type safety:** `--type` flag prevents ambiguous component identification
- **Extensibility:** New component types (entities, option sets) add without breaking changes
- **Alias support:** Domain commands (`ppds webresources publish`) auto-inject `--type`

### Non-Goals

- Publish status tracking or history
- Selective publish within a component (e.g., publish only one form of an entity)
- Undo/rollback of published changes

---

## Architecture

```
ppds publish --all ──────────────────────────► PublishAllXml
ppds publish --type webresource app.js ──┐
ppds webresources publish app.js ────────┤
                                         ├──► Name Resolution ──► PublishXml(ids)
ppds publish --type webresource          │
    --solution X ────────────────────────┘

ppds solutions publish ──────────────────────► ppds publish --all (alias)
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `PublishCommandGroup.cs` | Top-level `ppds publish` command with `--all`, `--type`, `--solution` |
| `WebResourceNameResolver` | Shared name resolution for web resources (used by publish + web resources commands) |
| `IWebResourceService.PublishAsync` | Publish specific web resources via PublishXml |
| `IWebResourceService.PublishAllAsync` | Publish all customizations via PublishAllXml |

### Dependencies

- Depends on: [web-resources.md](./web-resources.md) for `IWebResourceService` and name resolution
- Depends on: [connection-pooling.md](./connection-pooling.md) for publish coordination (per-environment semaphore)

---

## Specification

### Command Syntax

```bash
ppds publish --all                                   # PublishAllXml — everything
ppds publish --type <type> <name|id>...              # Publish specific components
ppds publish --type <type> --solution <name>         # Publish all of type in solution
```

### Flags

| Flag | Required | Description |
|------|----------|-------------|
| `--all` | Exclusive | Publish all customizations via PublishAllXml. Cannot combine with `--type`, `--solution`, or positional args. |
| `--type <type>` | When specifying resources | Component type. Currently supported: `webresource`. Required when positional args or `--solution` are used. |
| `--solution <name>` | No | Scope to components within a solution. Requires `--type`. |
| `--profile`, `--environment` | No | Standard auth/environment options. |

### Supported Types

| Type Value | Component | Service Method |
|------------|-----------|----------------|
| `webresource` | Web Resources | `IWebResourceService.PublishAsync(ids)` |

Future types (entities, option sets, etc.) add rows to this table without changing the command structure.

### Flag Combination Rules

| Combination | Result |
|-------------|--------|
| `ppds publish` (bare) | Show usage/help |
| `ppds publish --all` | PublishAllXml |
| `ppds publish --all --type X` | Error: `--all publishes all customizations. Remove --type or use --solution to scope.` |
| `ppds publish --all --solution X` | Error: `--all publishes all customizations. Remove --all to scope by solution.` |
| `ppds publish --all app.js` | Error: `--all publishes all customizations. Remove --all to publish specific resources.` |
| `ppds publish app.js` (no --type) | Error: `--type is required when specifying resources. Example: ppds publish --type webresource app.js` |
| `ppds publish --solution X` (no --type) | Error: `--type is required with --solution. Supported types: webresource` |
| `ppds publish --type webresource app.js` | Resolve name → PublishXml |
| `ppds publish --type webresource --solution X` | List web resources in solution → PublishXml with all IDs |

### Aliases

Domain command groups provide thin wrappers that auto-inject `--type`:

| Alias | Equivalent |
|-------|------------|
| `ppds webresources publish app.js` | `ppds publish --type webresource app.js` |
| `ppds webresources publish --solution X` | `ppds publish --type webresource --solution X` |
| `ppds solutions publish` | `ppds publish --all` (breaking change — previously called PublishAllXml directly) |

### Name Resolution (Web Resources)

Same shared logic as `ppds webresources get/url`:

1. **GUID** — direct lookup by ID
2. **Exact name** — match on `name` field
3. **Partial match** — resources whose name ends with the argument

Multiple positional args are supported — each is resolved independently, all resolved IDs are published in a single `PublishXml` call.

On ambiguity (single arg matches multiple resources): error with list of matches, exit code 1.

### Output

**Text mode (stderr for status, stdout for data per Constitution I1):**

```
Connected as josh@contoso.com to org.crm.dynamics.com

Publishing 2 web resource(s)...
Published successfully in 3.2 seconds.
```

**JSON mode:**

```json
{
  "publishedCount": 2,
  "durationSeconds": 3.2
}
```

**PublishAllXml text mode:**

```
Connected as josh@contoso.com to org.crm.dynamics.com

Publishing all customizations...
Published successfully in 12.4 seconds.
```

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-PUB-01 | `ppds publish --all` calls `PublishAllXml` | TBD | 🔲 |
| AC-PUB-02 | `ppds publish --all` rejects combination with `--type`, `--solution`, or positional args | TBD | 🔲 |
| AC-PUB-03 | `ppds publish --type webresource <name>` resolves name and calls `PublishXml` | TBD | 🔲 |
| AC-PUB-04 | `ppds publish --type webresource --solution <name>` publishes all web resources in solution | TBD | 🔲 |
| AC-PUB-05 | `ppds publish` (bare) shows usage help | TBD | 🔲 |
| AC-PUB-06 | `ppds publish <name>` without `--type` returns error with guidance | TBD | 🔲 |
| AC-PUB-07 | `ppds publish --solution X` without `--type` returns error with guidance | TBD | 🔲 |
| AC-PUB-08 | `ppds webresources publish` alias auto-injects `--type webresource` | TBD | 🔲 |
| AC-PUB-09 | `ppds solutions publish` alias maps to `ppds publish --all` | TBD | 🔲 |
| AC-PUB-10 | Multiple positional args resolved independently, published in single `PublishXml` call | TBD | 🔲 |
| AC-PUB-11 | Ambiguous name match returns error with list of matching resources | TBD | 🔲 |
| AC-PUB-12 | JSON output includes `publishedCount` and `durationSeconds` | TBD | 🔲 |
| AC-PUB-13 | Unsupported `--type` value returns error listing supported types | TBD | 🔲 |

---

## Design Decisions

### Why a top-level publish command?

**Context:** `PublishAllXml` was under `ppds solutions publish`, but it publishes all customizations — not just solutions. Adding web resource publish under `ppds webresources publish` would create two publish commands with different scopes.

**Decision:** Single `ppds publish` command with `--all` for everything, `--type` for scoped publish. Domain commands alias into it.

**Alternatives considered:**
- Keep `ppds solutions publish` and add `ppds webresources publish` separately: Violates single code path (Constitution A2), no shared validation
- `ppds publish` with auto-detection (no `--type`): Ambiguous when multiple component types share names

**Consequences:**
- Positive: Single entry point, extensible, clear semantics
- Negative: Breaking change for `ppds solutions publish` (now alias, same behavior)

### Why --all is exclusive?

**Context:** `--all --type webresource` could mean "publish all web resources" — but that would require listing all 60K+ web resources and publishing them individually via `PublishXml`.

**Decision:** `--all` means `PublishAllXml` exclusively. No combination with other flags.

**Rationale:** `PublishAllXml` is a single API call that publishes everything. Scoping it by type would require a fundamentally different code path (list + publish), defeats the purpose of "all", and could be catastrophically slow in large environments.

### Why require --type?

**Context:** Could auto-detect component type from the name (e.g., `app.js` is probably a web resource).

**Decision:** Require `--type` when specifying resources by name.

**Rationale:** Auto-detection is fragile and will break when multiple component types are supported. Explicit is better than implicit. Domain command aliases (`ppds webresources publish`) eliminate the verbosity for the common case.

---

## Extension Points

### Adding a New Publishable Type

1. **Create resolver**: Implement name resolution for the component type (e.g., `EntityNameResolver`)
2. **Add service method**: Ensure the domain service has a `PublishAsync(ids)` method
3. **Register type**: Add the type string to the supported types table in `PublishCommandGroup.cs`
4. **Create alias**: Add a `publish` subcommand to the domain command group that auto-injects `--type`

---

## Related Specs

- [web-resources.md](./web-resources.md) — Web resource service and name resolution
- [connection-pooling.md](./connection-pooling.md) — Publish coordination (per-environment semaphore)
- [CONSTITUTION.md](./CONSTITUTION.md) — A1 (services), A2 (single code path), I1 (stdout for data)

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-23 | Initial spec |
