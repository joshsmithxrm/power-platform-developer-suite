# Copy/Paste Redesign

## Problem

- Ctrl+C copies only a single cell value, ignoring multi-cell selection
- `QueryResultsTableView` lacks multi-row copy entirely
- No way to copy with or without headers depending on context
- Column selection is ignored — multi-row copy always includes all columns

## Design

### Smart Ctrl+C with Self-Teaching Hints

One shortcut that does the right thing based on selection size, with a modifier for the opposite, and status bar hints that teach the modifier in context.

### Behavior Matrix

| Selection | Ctrl+C | Ctrl+Shift+C |
|-----------|--------|--------------|
| Single cell | Raw value only | Value with column header |
| Multi-cell block | Selected cols/rows WITH headers | Selected cols/rows WITHOUT headers |

### Column Scoping

Uses Terminal.Gui `MultiSelectedRegions` Rect to determine both rows AND columns. Only copies the columns within the selection rectangle, not all columns in the row.

- `Rect.X` / `Rect.Width` determine column range
- `Rect.Y` / `Rect.Height` determine row range
- `GetAllSelectedCells()` available as fallback for non-contiguous selections

### Output Format

Tab-separated values (TSV). Newlines and tabs within cell values replaced with spaces.

Single cell example (Ctrl+C):
```
Contoso
```

Single cell with header (Ctrl+Shift+C):
```
Name
Contoso
```

Multi-cell example (Ctrl+C):
```
Name	City	Revenue
Contoso	Seattle	1000000
Fabrikam	Portland	500000
```

Multi-cell without headers (Ctrl+Shift+C):
```
Contoso	Seattle	1000000
Fabrikam	Portland	500000
```

### Status Bar Hints

After every copy, the status bar shows a contextual hint about the alternative shortcut:

- Single cell Ctrl+C: `"Copied value (Ctrl+Shift+C to include header)"`
- Single cell Ctrl+Shift+C: `"Copied with header (Ctrl+C for value only)"`
- Multi-cell Ctrl+C: `"Copied 4 rows x 3 cols with headers (Ctrl+Shift+C for values only)"`
- Multi-cell Ctrl+Shift+C: `"Copied 4 rows x 3 cols (Ctrl+C to include headers)"`

### Consistency

Both `QueryResultsTableView` and `DataTableView` get identical copy behavior. Shared logic extracted to avoid duplication.

## Implementation

### Step 1: Extract shared copy helper

Create a static helper method (or small class) that both views call:

```
CopyHelper.CopySelection(TableView tableView, DataTable sourceTable, bool includeHeaders)
```

Returns the formatted string and a status message. Both views call this from their key handlers.

### Step 2: Update QueryResultsTableView

- Add Ctrl+Shift+C handler
- Replace `CopySelectedCell()` with call to shared helper
- Wire up status bar hints

### Step 3: Update DataTableView

- Replace `CopySelectedCell()` and `CopySelectedRows()` with call to shared helper
- Wire up status bar hints
- Update context menu labels

### Step 4: Update keyboard shortcuts dialog

- Add Ctrl+Shift+C entry
- Update Ctrl+C description to mention smart behavior

## Files to Modify

- `src/PPDS.Cli/Tui/Views/QueryResultsTableView.cs`
- `src/PPDS.Cli/Tui/Views/DataTableView.cs`
- `src/PPDS.Cli/Tui/Dialogs/KeyboardShortcutsDialog.cs`
- New: `src/PPDS.Cli/Tui/Helpers/TableCopyHelper.cs`
