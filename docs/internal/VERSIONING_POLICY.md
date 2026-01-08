# PPDS Ecosystem Versioning Policy

Cross-repo versioning rules for the PPDS ecosystem.

---

## Version Sync Rules

| Rule | Description |
|------|-------------|
| Major versions sync | Breaking changes trigger coordinated major bump across affected repos |
| Minor/patch independent | Each repo releases independently for non-breaking changes |
| Document compatibility | Each repo documents minimum versions of dependencies |

---

## Dependency Flow

```
ppds-alm (CI/CD templates)
    │
    └── calls ──► ppds-tools (PowerShell module)
                      │
                      ├── reflects on ──► ppds (PPDS.Plugins NuGet)
                      │
                      └── shells to ──► ppds (ppds CLI)

ppds-demo (reference implementation)
    │
    ├── uses ──► ppds-alm workflows
    ├── uses ──► ppds-tools cmdlets
    └── references ──► ppds packages (NuGet)

ppds (monorepo)
    └── contains: CLI, TUI, MCP, NuGet libs (PPDS.*), VS Code extension
```

---

## When to Sync Major Versions

| Scenario | Action |
|----------|--------|
| ppds changes `PluginStepAttribute` properties | ppds 2.0 → ppds-tools 2.0 |
| ppds-tools changes cmdlet signatures | ppds-tools 2.0 → ppds-alm 2.0 |
| ppds-tools changes auth flow | ppds-tools 2.0 → ppds-alm 2.0 |
| ppds-alm changes workflow inputs | ppds-alm 2.0 only |

---

## Pre-release Conventions

| Repo | Mechanism | Example |
|------|-----------|---------|
| ppds | Git tag suffix | `{Package}-v1.2.0-alpha1` |
| ppds-tools | Manifest field | `Prerelease = 'alpha1'` |
| ppds-alm | Git tag suffix (optional) | `v1.1.0-beta1` |

### Stages

| Stage | Format | Purpose |
|-------|--------|---------|
| Alpha | `-alphaN` | Early testing |
| Beta | `-betaN` | Feature complete |
| RC | `-rcN` | Release candidate |
| Stable | (no suffix) | Production |

---

## Compatibility Matrix

**Last updated:** 2026-01-07

| ppds-alm | ppds-tools (min) | ppds (min) |
|----------|-----------------|------------|
| 1.0.x | 1.1.0 | N/A |
| 1.1.x | 1.2.0 | N/A |

| ppds-tools | PPDS.Plugins (min) | ppds CLI (min) |
|------------|-------------------|----------------|
| 1.1.x | 1.0.0 | N/A |
| 1.2.x | 1.0.0 | 1.0.0 |

---

## Release Order

For breaking changes, release in this order:

1. **ppds** - NuGet packages and CLI must publish first
2. **ppds-tools** - PowerShell module (depends on ppds)
3. **ppds-alm** - Templates (depend on ppds-tools)
4. **ppds-demo** - Update to use new versions
