# TUI Polish Tracker

## How to Use

This file tracks incremental UX improvements during TUI development iterations.

- Add feedback items under "Open" with `- [ ]` checkbox and date
- Mark items complete by moving to "Done" section
- For bugs/features needing discussion, create a GitHub Issue instead
- **This file is deleted during pre-PR** - it's iteration scaffolding, not permanent docs

## When to Use Tracker vs Issues

| Scope | Tool | Examples |
|-------|------|----------|
| Quick polish (<30min) | Tracker | Colors, spacing, text |
| Small UX fix (<1hr) | Tracker | Shortcuts, tooltips |
| Bug with impact | GitHub Issue | Crashes, data loss |
| New feature | GitHub Issue | New screens |
| Architecture | GitHub Issue | Service refactors |

**Rule:** Can fix in current session without discussion? Use Tracker. Otherwise create Issue.

## Status

**Phase:** MVP Debugging
**Last Updated:** 2026-01-07

## Open Feedback

_No open items_

## Done

- [x] Connection pool warming - InitializeAsync() warms pool at TUI startup (fixed: 2026-01-07)
- [x] Environment change events - SetEnvironmentAsync() + EnvironmentChanged event (fixed: 2026-01-07)
- [x] Debug logging - TuiDebugLog writes to ~/.ppds/tui-debug.log (fixed: 2026-01-07)
- [x] Deadlock fix - Replaced nested Application.Run() with MessageBox.Query() (fixed: 2026-01-07)
- [x] SQL query error - Removed PageNumber=1 that conflicted with TOP (fixed: 2026-01-07)
- [x] Status updates - Granular status ("Connecting...", "Executing...") (fixed: 2026-01-07)
- [x] DI consistency - All services obtained via InteractiveSession (fixed: 2026-01-07)
- [x] Silent failures - History save errors now show status bar warning (fixed: 2026-01-07)
- [x] Error messages - PpdsException.UserMessage used for user-facing errors (fixed: 2026-01-07)
- [x] Dark theme - Modern dark theme with cyan accents (fixed: 2026-01-07)
- [x] Environment-aware status bar - Production=red, Dev=green, Sandbox=yellow (fixed: 2026-01-07)
- [x] Disabled menu items - Coming Soon suffix, grayed out (fixed: 2026-01-07)
- [x] Theme service tests - TuiThemeServiceTests with environment detection (fixed: 2026-01-07)
- [x] Session tests - InteractiveSessionTests for lifecycle/services (fixed: 2026-01-07)

## Design Decisions

- **Primary accent:** Cyan (stands out on dark, not harsh)
- **Status bar colors:** Environment-aware for safety (prod=red warning, dev=green safe)
- **Menu items:** Disabled with "(Coming Soon)" suffix rather than showing error dialogs
- **Local services:** Transient instances per call, but share underlying ProfileStore singleton
