# ADR-0010: Published vs Unpublished Default

**Status:** Accepted
**Date:** 2026-01-04
**Authors:** Josh, Claude

## Context

Several Dataverse entities support dual-state content:
- **Published** - What end users see in the application
- **Unpublished** - Pending changes not yet published

Affected entities include:
- Web resources
- Forms
- Views
- Sitemap
- Ribbons

When querying these entities, the CLI must decide which version to return by default.

## Decision

**Default to published content**, with an `--unpublished` flag to access draft/pending changes.

```bash
# Default: published content
ppds webresources get myfile.js

# Explicit: unpublished/draft content
ppds webresources get myfile.js --unpublished

# Diff against published (default)
ppds webresources diff myfile.js

# Diff against unpublished
ppds webresources diff myfile.js --unpublished
```

## Rationale

### 1. Principle of Least Surprise
"Show me what's live" is the safe, expected default. Users querying data typically want to see what's actually deployed.

### 2. Scripting Safety
Automation scripts should operate on stable, published content. If a script unexpectedly gets unpublished changes, it could cause confusion or errors.

### 3. Explicit Intent
Developers actively working on changes know they want the draft version. Adding `--unpublished` explicitly signals that intent.

### 4. Consistency with Data Queries
When querying entity data (`ppds query`), you see data against the published schema. The same principle applies to metadata and resources.

## Consequences

### Positive
- Safe default for automation
- Clear semantic meaning
- Explicit flag prevents accidental draft access
- Matches user expectations ("show me what users see")

### Negative
- Developers must remember to add `--unpublished` when working on drafts
- Two round-trips if you need both versions

## Alternatives Considered

### Default to unpublished
Rejected because:
- Unsafe for scripting/automation
- Violates principle of least surprise
- Users might not realize they're seeing uncommitted changes

### Force explicit choice (no default)
Rejected because:
- Adds friction for the common case
- Most queries want published content
- Extra typing for every command

### Environment-based default
(e.g., development environments default to unpublished)

Rejected because:
- Inconsistent behavior across environments
- Harder to reason about
- Scripts may behave differently per environment
