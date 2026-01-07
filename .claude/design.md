# Design: TUI Enhancements

## Issues

| # | Title | Type | Priority | Size |
|---|-------|------|----------|------|
| 204 | TUI: Add search/filter within loaded results | feature | P2-Medium | M |
| 205 | TUI: Add multi-cell selection with Shift+Arrow | feature | P2-Medium | M |
| 206 | TUI: Export selection as CSV/TSV | feature | P2-Medium | S |
| 207 | TUI: Add mouse support for table navigation | feature | P3-Low | S |
| 208 | TUI: Query history as selectable list | feature | P2-Medium | M |
| 234 | refactor(tui): Abstract SQL table pattern for reuse across all data tables | refactor | P2-Medium | L |

## Context

The TUI (Terminal User Interface) provides an interactive SQL query experience for Dataverse. Current implementation works but lacks ergonomic features that would improve daily workflow.

### Current TUI Location
```
src/PPDS.Cli/Tui/
├── SqlQueryScreen.cs      # Main query interface
├── DataTableView.cs       # Table display component
├── QueryInputView.cs      # SQL input component
└── ...
```

## Feature Dependencies

```
#234 (Abstract table pattern) ─┬─► #204 (Search/filter)
                               ├─► #205 (Multi-cell selection)
                               ├─► #206 (Export CSV)
                               └─► #207 (Mouse support)

#208 (Query history) ──► Independent
```

**Recommendation:** Do #234 first to establish the abstraction, then others can build on it.

## Suggested Implementation Order

1. **#234** - Abstract SQL table pattern (foundation for others)
2. **#208** - Query history (independent, can be parallel)
3. **#204** - Search/filter within results
4. **#205** - Multi-cell selection
5. **#206** - Export CSV (needs #205 for selection)
6. **#207** - Mouse support (nice-to-have)

## Key Files

```
src/PPDS.Cli/Tui/SqlQueryScreen.cs
src/PPDS.Cli/Tui/DataTableView.cs
src/PPDS.Cli/Tui/QueryInputView.cs
src/PPDS.Cli/Services/QueryHistoryService.cs (new)
```

## Technical Notes

### #234 - Table Abstraction
- Extract generic `DataTableView<T>` from current SQL-specific implementation
- Should support: column definitions, row data, selection, scrolling
- Reusable for: query results, metadata listings, import previews

### #204 - Search/Filter
- `/` key to enter search mode (vim-style)
- Filter rows by text match across all visible columns
- `n`/`N` to navigate between matches
- `Esc` to clear filter

### #205 - Multi-Cell Selection
- `Shift+Arrow` to extend selection
- `Ctrl+A` to select all
- Visual highlight for selected cells
- Store selection state for export

### #206 - Export CSV
- `Ctrl+E` or command to export
- Export current selection or all if no selection
- Prompt for file path or use clipboard
- Support both CSV and TSV formats

### #207 - Mouse Support
- Terminal.Gui supports mouse events
- Click to select cell
- Drag to select range
- Scroll wheel for navigation

### #208 - Query History
- Store last N queries (configurable, default 100)
- `Ctrl+R` for reverse search (like bash)
- Arrow up/down in query input to cycle history
- Persist to `~/.ppds/query-history.json`

## Acceptance Criteria

### #234
- [ ] Generic `DataTableView<T>` component extracted
- [ ] Current SqlQueryScreen uses the abstraction
- [ ] No regression in existing TUI functionality

### #204
- [ ] `/` enters filter mode
- [ ] Filter text shown in status bar
- [ ] Rows filtered in real-time
- [ ] `Esc` clears filter

### #205
- [ ] Shift+Arrow extends selection
- [ ] Selection visually highlighted
- [ ] Selection state accessible programmatically

### #206
- [ ] Export hotkey works
- [ ] CSV format correct (quoted strings, escaped commas)
- [ ] TSV option available
- [ ] Clipboard support if no file specified

### #207
- [ ] Click selects cell
- [ ] Drag selects range
- [ ] Scroll wheel scrolls table

### #208
- [ ] History persisted across sessions
- [ ] Arrow keys cycle through history
- [ ] Ctrl+R reverse search works
- [ ] Duplicate queries collapsed
