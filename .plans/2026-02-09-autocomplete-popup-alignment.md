# Autocomplete Popup Alignment Fix

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix the autocomplete popup so it aligns with the word being completed — position at word start column and remove kind-icon prefix characters.

**Architecture:** Two targeted changes in the autocomplete subsystem. The popup X position is changed from cursor column to word-start column in `SyntaxHighlightedTextView`. The `FormatItem` method in `AutocompletePopup` drops the icon prefix so popup text aligns 1:1 with the text being replaced. Both changes are required together for correct visual alignment.

**Tech Stack:** C# (.NET 8/9/10), Terminal.Gui 1.19+, xUnit

---

### Task 1: Remove Kind Icon Prefix from AutocompletePopup

The `FormatItem` method prepends a kind icon character (e.g. `T `, `K `) to each completion label. This shifts the displayed text 2 characters right, causing misalignment with the word being typed. Remove the prefix so the popup shows just the label.

**Files:**
- Modify: `src/PPDS.Cli/Tui/Views/AutocompletePopup.cs:193-196`
- Modify: `src/PPDS.Cli/Tui/Views/AutocompletePopup.cs:14-18` (doc comment)

**Step 1: Update FormatItem to return just the label**

In `AutocompletePopup.cs`, replace `FormatItem` (lines 193-196):

```csharp
internal static string FormatItem(SqlCompletion item)
{
    return item.Label;
}
```

**Step 2: Update the class XML doc comment**

Replace lines 14-18:

```csharp
/// <para>
/// The popup contains a <see cref="ListView"/> sized to show at most <see cref="MaxVisibleItems"/> items.
/// </para>
```

**Step 3: Build to verify**

Run: `dotnet build src/PPDS.Cli -f net9.0 --no-restore -v q --nologo`
Expected: Build succeeded

**Step 4: Commit**

```
refactor(tui): remove kind icon prefix from autocomplete popup items
```

---

### Task 2: Position Popup at Word Start Column

The popup X is set to `cursorPos.X` (the cursor's current column). It should be set to the column where the word being completed starts, so the popup text overlays the partially typed text.

**Files:**
- Modify: `src/PPDS.Cli/Tui/Views/SyntaxHighlightedTextView.cs:254-256`

**Step 1: Calculate word start column and use it for popupX**

In `SyntaxHighlightedTextView.cs`, replace lines 254-256:

```csharp
            // Position the popup at the word start, not the cursor
            var cursorPos = CursorPosition;
            var wordLength = cursorOffset - _completionWordStart;
            var popupX = cursorPos.X - wordLength - LeftColumn;
```

The logic: `cursorPos.X` is the cursor's visual column. `wordLength` is how many characters have been typed since the word start. Subtracting gives us the column where the word starts. `LeftColumn` accounts for horizontal scrolling.

**Step 2: Build to verify**

Run: `dotnet build src/PPDS.Cli -f net9.0 --no-restore -v q --nologo`
Expected: Build succeeded

**Step 3: Commit**

```
fix(tui): position autocomplete popup at word start column
```

---

### Task 3: Run TUI Tests

Verify no regressions in existing TUI tests.

**Step 1: Run TUI unit tests**

Run: `dotnet test tests/PPDS.Cli.Tests -f net9.0 --filter Category=TuiUnit --nologo -v q`
Expected: ALL PASS, 0 failures

**Step 2: If any test failures, fix and recommit**

---

## Summary Table

| Task | Change | File |
|------|--------|------|
| 1 | Remove kind icon prefix from FormatItem | `AutocompletePopup.cs` |
| 2 | Position popup at word start column | `SyntaxHighlightedTextView.cs` |
| 3 | Run TUI tests | — |
