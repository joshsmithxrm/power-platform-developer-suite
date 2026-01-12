# TUI Review

Evaluate TUI screens for UX quality and identify improvements.

## Usage

`/tui-review` - Review the main TUI experience
`/tui-review sql` - Review SQL Query screen specifically
`/tui-review profile` - Review profile management dialogs
`/tui-review [screen]` - Review a specific screen

## Setup

1. Build the CLI:
   ```bash
   dotnet build src/PPDS.Cli/PPDS.Cli.csproj -f net10.0
   ```

2. Run automated tests first to ensure baseline works:
   ```bash
   dotnet test --filter "Category=TuiUnit"
   ```

## Review Process

### Step 1: Launch TUI
```bash
.\src\PPDS.Cli\bin\Debug\net10.0\ppds.exe interactive
```

### Step 2: Explore the Target Screen

**Main Menu:** Navigation, menu items, status bar, keyboard shortcuts
**SQL Query (F2):** Query input, execution, results table, pagination, filter, export
**Profile Selector (Alt+P):** Profile list, create/delete, switching
**Environment Selector (Alt+E):** Environment discovery, selection, details
**Error Log (F12):** Error display, details, copy functionality

### Step 3: Test These Interactions
- Keyboard navigation (Tab, arrows, Enter, Escape)
- Hotkeys (F1, F2, Alt+P, Alt+E, Ctrl+Q)
- Mouse clicks (if applicable)
- Error states (what happens when things fail?)
- Empty states (no data, no profiles, etc.)
- Loading states (spinners, status messages)

### Step 4: Capture State (Optional)
Use the state capture API to inspect internal state:
```csharp
// Components implement ITuiStateCapture<T>
var state = screen.CaptureState();
// Inspect: QueryText, StatusText, ResultCount, etc.
```

## Report Format

Structure your findings as:

### What Works Well
- List polished/intuitive aspects

### UX Issues
| Issue | Severity | Description |
|-------|----------|-------------|
| ... | P1/P2/P3 | ... |

**Severity:**
- P1: Blocks core functionality
- P2: Annoying but workable
- P3: Nice to have improvement

### Missing Features
What would users expect that isn't there?

### Suggested Improvements
Specific, actionable recommendations with rationale.

### Comparison to Similar Tools
How does it compare to lazygit, k9s, or other TUI tools?

## Important

- **DO NOT make code changes** - This is evaluation only
- **DO create issues** for P1/P2 findings using `/create-issue`
- Reference `src/PPDS.Cli/Tui/` for screen locations
- Reference `docs/adr/0028_TUI_TESTING_STRATEGY.md` for testing patterns

## Screen Locations

| Screen | File |
|--------|------|
| Main Window | `src/PPDS.Cli/Tui/MainWindow.cs` |
| SQL Query | `src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs` |
| Profile Selector | `src/PPDS.Cli/Tui/Dialogs/ProfileSelectorDialog.cs` |
| Environment Selector | `src/PPDS.Cli/Tui/Dialogs/EnvironmentSelectorDialog.cs` |
| Status Bar | `src/PPDS.Cli/Tui/Views/TuiStatusBar.cs` |
| Error Details | `src/PPDS.Cli/Tui/Dialogs/ErrorDetailsDialog.cs` |
