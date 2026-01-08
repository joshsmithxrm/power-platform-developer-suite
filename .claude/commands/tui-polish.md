# TUI Polish Session

You are starting a TUI polish session. This command helps iterate on Terminal.Gui UX improvements.

## Instructions

1. **Read the tracker file** at `docs/tui/POLISH_TRACKER.md` to understand current state
2. **Check debug log** at `~/.ppds/tui-debug.log` if troubleshooting issues
3. **Check for open items** - work on these first
4. **Review the current TUI** by examining:
   - `src/PPDS.Cli/Tui/MainWindow.cs` - Main window and navigation
   - `src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs` - SQL query experience
   - `src/PPDS.Cli/Tui/Infrastructure/TuiColorPalette.cs` - Color schemes
   - `src/PPDS.Cli/Tui/Infrastructure/TuiThemeService.cs` - Environment detection
5. **Gather feedback** - Ask what aspect needs polish if no open items exist
6. **Update tracker** - Add new items or mark items complete as you work

## When to Use Tracker vs Issues

| Scope | Tool | Examples |
|-------|------|----------|
| Quick polish (<30min) | Tracker | Colors, spacing, text |
| Small UX fix (<1hr) | Tracker | Shortcuts, tooltips |
| Bug with impact | GitHub Issue | Crashes, data loss |
| New feature | GitHub Issue | New screens |
| Architecture | GitHub Issue | Service refactors |

## Key Files

### Theme Infrastructure
- `TuiColorPalette.cs` - Static color constants (dark theme)
- `TuiThemeService.cs` - Environment detection, color scheme selection
- `ITuiThemeService.cs` - Theme service interface
- `EnvironmentType.cs` - Production/Sandbox/Dev/Trial/Unknown enum

### Main TUI Components
- `MainWindow.cs` - Application shell with menu and navigation
- `SqlQueryScreen.cs` - Query input, execution, results display
- `InteractiveSession.cs` - Session state, service lifecycle

### Tests
- `TuiThemeServiceTests.cs` - Environment detection tests
- `InteractiveSessionTests.cs` - Session lifecycle tests
- `TuiOperationProgressTests.cs` - Progress reporter tests

## Design Principles

- **Dark background** (Black) - easier on eyes, modern dev aesthetic
- **Cyan primary accent** - stands out, not harsh
- **Environment-aware colors** - Production=red (danger), Dev=green (safe), Sandbox=yellow (warning)
- **Status bar reflects environment** - Visual safety indicator
- **No "Windows 95 blue"** - Replace with DarkGray or themed colors

## Important Notes

- The tracker file (`docs/tui/POLISH_TRACKER.md`) is deleted during `/pre-pr`
- This slash command persists to document the process
- All design decisions that matter go in code comments or ADRs
