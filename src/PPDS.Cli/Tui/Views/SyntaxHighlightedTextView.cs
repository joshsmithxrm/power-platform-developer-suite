using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Cli.Services;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Dataverse.Sql.Intellisense;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Views;

/// <summary>
/// A <see cref="TextView"/> subclass that renders per-token syntax highlighting.
/// Language-agnostic: the <see cref="ISourceTokenizer"/> and color map are injected.
/// Overrides <see cref="Redraw"/> to apply token-based coloring while preserving
/// all standard <see cref="TextView"/> behavior (selection, cursor, scrolling, undo).
/// </summary>
/// <remarks>
/// Also supports SQL IntelliSense autocomplete via an <see cref="AutocompletePopup"/>
/// overlay when an <see cref="ISqlLanguageService"/> is set.
/// </remarks>
internal sealed class SyntaxHighlightedTextView : TextView
{
    private readonly ISourceTokenizer _tokenizer;
    private readonly Dictionary<SourceTokenType, Terminal.Gui.Attribute> _colorMap;
    private readonly Terminal.Gui.Attribute _defaultAttr;
    private readonly AutocompletePopup _autocompletePopup;

    /// <summary>
    /// Tracks the start offset of the word being completed so we can replace it on accept.
    /// </summary>
    private int _completionWordStart;

    /// <summary>
    /// Cancellation source for in-flight completion requests. Replaced on each new trigger.
    /// </summary>
    private CancellationTokenSource? _completionCts;

    /// <summary>
    /// Latest validation diagnostics. Updated asynchronously after debounce.
    /// </summary>
    private IReadOnlyList<SqlDiagnostic> _diagnostics = Array.Empty<SqlDiagnostic>();

    /// <summary>
    /// Cached tokenization results to avoid re-tokenizing on every Redraw cycle.
    /// Invalidated when text content changes.
    /// </summary>
    private string? _cachedText;
    private IReadOnlyList<SourceToken>? _cachedTokens;
    private Terminal.Gui.Attribute[]? _cachedColorMap;

    /// <summary>
    /// Cancellation source for in-flight validation requests.
    /// </summary>
    private CancellationTokenSource? _validationCts;

    /// <summary>
    /// Token returned by <see cref="Application.MainLoop.AddTimeout"/> for debounced validation.
    /// Null when no timer is pending.
    /// </summary>
    private object? _validationTimerToken;

    /// <summary>
    /// Debounce delay for validation after keystrokes (milliseconds).
    /// </summary>
    private const int ValidationDebounceMs = 500;

    /// <summary>
    /// Gets or sets the SQL language service used for IntelliSense completions.
    /// Set this after construction when the environment connection is established.
    /// </summary>
    public ISqlLanguageService? LanguageService { get; set; }

    /// <summary>
    /// Raised when IntelliSense completions are requested but <see cref="LanguageService"/> is not yet available.
    /// </summary>
    internal event Action? IntelliSenseUnavailable;

    /// <summary>
    /// Gets the autocomplete popup (for testing / external access).
    /// </summary>
    internal AutocompletePopup AutocompletePopupView => _autocompletePopup;

    /// <summary>
    /// Gets the latest validation diagnostics (for testing / status display).
    /// </summary>
    internal IReadOnlyList<SqlDiagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// Initializes a new syntax-highlighted text view.
    /// </summary>
    /// <param name="tokenizer">Produces tokens for the current text content.</param>
    /// <param name="colorMap">Maps token types to Terminal.Gui color attributes.</param>
    public SyntaxHighlightedTextView(
        ISourceTokenizer tokenizer,
        Dictionary<SourceTokenType, Terminal.Gui.Attribute> colorMap)
    {
        _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
        _colorMap = colorMap ?? throw new ArgumentNullException(nameof(colorMap));

        // Default text color (used for gaps between tokens and unknown types)
        _defaultAttr = colorMap.TryGetValue(SourceTokenType.Identifier, out var id)
            ? id
            : new Terminal.Gui.Attribute(Color.White, Color.Black);

        // Create and add the autocomplete popup as a subview (initially hidden)
        _autocompletePopup = new AutocompletePopup();
        _autocompletePopup.CompletionAccepted += OnCompletionAccepted;
        Add(_autocompletePopup);
    }

    /// <inheritdoc />
    public override bool ProcessKey(KeyEvent keyEvent)
    {
        // If the autocomplete popup is showing, route keys to it first
        if (_autocompletePopup.IsShowing)
        {
            if (_autocompletePopup.ProcessKeyEvent(keyEvent))
            {
                return true;
            }
        }

        // Ctrl+Space: manual trigger for completions
        // Note: Some terminals send NUL (0x00) for Ctrl+Space instead of CtrlMask|Space
        if (keyEvent.Key == (Key.CtrlMask | Key.Space) || (int)keyEvent.Key == 0)
        {
            TuiDebugLog.Log($"Ctrl+Space detected (key=0x{(int)keyEvent.Key:X8}), triggering completions");
            _ = TriggerCompletionsAsync();
            return true;
        }

        // Let the base TextView handle the key
        var handled = base.ProcessKey(keyEvent);

        // After the key is processed, check for autocomplete triggers
        if (handled)
        {
            CheckAutocompleteTrigger(keyEvent);

            // Schedule debounced validation on text-changing keys
            if (IsTextChangingKey(keyEvent))
            {
                ScheduleDebouncedValidation();
            }
        }

        return handled;
    }

    /// <summary>
    /// Checks whether the just-processed key should trigger or update autocomplete.
    /// </summary>
    private void CheckAutocompleteTrigger(KeyEvent keyEvent)
    {
        // If popup is showing, update filter on printable character, backspace, or delete
        if (_autocompletePopup.IsShowing)
        {
            if (IsPrintableChar(keyEvent) || keyEvent.Key == Key.Backspace || keyEvent.Key == Key.DeleteChar)
            {
                UpdatePopupFilter();
                return;
            }

            // Non-navigation, non-printable key: dismiss the popup
            if (!IsNavigationKey(keyEvent))
            {
                _autocompletePopup.Hide();
            }
            return;
        }

        // Dot trigger: after typing '.'
        if (keyEvent.Key == (Key)'.')
        {
            TuiDebugLog.Log("Dot trigger: triggering completions");
            _ = TriggerCompletionsAsync();
            return;
        }

        // Space after FROM or JOIN: trigger completions
        if (keyEvent.Key == Key.Space)
        {
            CheckFromJoinTrigger();
        }
    }

    /// <summary>
    /// Checks if the cursor is positioned right after a FROM or JOIN keyword
    /// followed by the space that was just typed, and triggers completions if so.
    /// </summary>
    private void CheckFromJoinTrigger()
    {
        var fullText = Text?.ToString() ?? string.Empty;
        var offset = GetCursorFlatOffset();

        // We need at least 5 characters before cursor for "FROM " or "JOIN "
        if (offset < 5) return;

        // Look at the text before the cursor (the space was already inserted)
        var textBefore = fullText.Substring(0, offset).TrimEnd();
        var upper = textBefore.ToUpperInvariant();

        if (upper.EndsWith("FROM") || upper.EndsWith("JOIN") ||
            upper.EndsWith("INNER JOIN") || upper.EndsWith("LEFT JOIN") ||
            upper.EndsWith("RIGHT JOIN") || upper.EndsWith("CROSS JOIN") ||
            upper.EndsWith("FULL JOIN") || upper.EndsWith("LEFT OUTER JOIN") ||
            upper.EndsWith("RIGHT OUTER JOIN") || upper.EndsWith("FULL OUTER JOIN"))
        {
            _ = TriggerCompletionsAsync();
        }
    }

    /// <summary>
    /// Triggers an asynchronous completion request and shows the popup with results.
    /// </summary>
    internal async Task TriggerCompletionsAsync()
    {
        TuiDebugLog.Log($"TriggerCompletionsAsync called, LanguageService={(LanguageService != null ? "set" : "NULL")}");

        if (LanguageService == null)
        {
            TuiDebugLog.Log("IntelliSense unavailable — LanguageService is null, firing event");
            IntelliSenseUnavailable?.Invoke();
            return;
        }

        // Cancel and dispose any in-flight request
        _completionCts?.Cancel();
        _completionCts?.Dispose();
        _completionCts = new CancellationTokenSource();
        var ct = _completionCts.Token;

        var fullText = Text?.ToString() ?? string.Empty;
        var cursorOffset = GetCursorFlatOffset();

        // Determine the word start (for replacement on accept)
        _completionWordStart = FindWordStart(fullText, cursorOffset);

        TuiDebugLog.Log($"Requesting completions: offset={cursorOffset}, wordStart={_completionWordStart}, textLen={fullText.Length}");

        try
        {
            var completions = await LanguageService.GetCompletionsAsync(fullText, cursorOffset, ct);

            TuiDebugLog.Log($"Completions returned: count={completions.Count}, cancelled={ct.IsCancellationRequested}");

            if (ct.IsCancellationRequested || completions.Count == 0)
            {
                return;
            }

            // Position the popup at the word start, not the cursor
            var cursorPos = CursorPosition;
            var wordLength = cursorOffset - _completionWordStart;
            var popupX = cursorPos.X - wordLength - LeftColumn;
            var popupY = cursorPos.Y - TopRow + 1; // below the cursor line

            // If near the bottom of the view, position above
            var viewHeight = Bounds.Height;
            var popupHeight = Math.Min(completions.Count, AutocompletePopup.MaxVisibleItems);
            if (popupY + popupHeight > viewHeight)
            {
                popupY = cursorPos.Y - TopRow - popupHeight;
                if (popupY < 0) popupY = 0;
            }

            TuiDebugLog.Log($"Showing popup: x={popupX}, y={popupY}, items={completions.Count}, viewHeight={viewHeight}");

            // Show on the main thread (we may already be on it, but invoke to be safe)
            Application.MainLoop?.Invoke(() =>
            {
                if (ct.IsCancellationRequested) return;
                _autocompletePopup.Show(completions, popupX, popupY);
                SetNeedsDisplay();
                TuiDebugLog.Log($"Popup shown, IsShowing={_autocompletePopup.IsShowing}");
            });
        }
        catch (OperationCanceledException)
        {
            // Expected when a new trigger replaces the previous one
            TuiDebugLog.Log("Completion request cancelled");
        }
        catch (Exception ex)
        {
            TuiDebugLog.Log($"Autocomplete error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates the popup filter based on text typed since the completion word start.
    /// </summary>
    private void UpdatePopupFilter()
    {
        var fullText = Text?.ToString() ?? string.Empty;
        var cursorOffset = GetCursorFlatOffset();

        if (cursorOffset < _completionWordStart)
        {
            _autocompletePopup.Hide();
            return;
        }

        var currentWord = fullText.Substring(_completionWordStart, cursorOffset - _completionWordStart);
        _autocompletePopup.UpdateFilter(currentWord);
    }

    /// <summary>
    /// Handles a completion being accepted: inserts the completion text at the cursor.
    /// </summary>
    private void OnCompletionAccepted(SqlCompletion completion)
    {
        var fullText = Text?.ToString() ?? string.Empty;
        var cursorOffset = GetCursorFlatOffset();

        // Replace the partial word (from _completionWordStart to cursorOffset) with InsertText
        var wordEnd = cursorOffset;
        var before = fullText.Substring(0, _completionWordStart);
        var after = fullText.Substring(wordEnd);

        var newText = before + completion.InsertText + after;
        Text = newText;

        // Position cursor after inserted text
        var newCursorOffset = _completionWordStart + completion.InsertText.Length;
        SetCursorFromFlatOffset(newCursorOffset);

        SetNeedsDisplay();
    }

    /// <summary>
    /// Gets the flat character offset of the current cursor position.
    /// </summary>
    internal int GetCursorFlatOffset()
    {
        var fullText = Text?.ToString() ?? string.Empty;
        var lines = fullText.Split('\n');
        var pos = CursorPosition;
        return FlatOffset(lines, pos.Y, pos.X);
    }

    /// <summary>
    /// Sets the cursor position from a flat character offset.
    /// </summary>
    private void SetCursorFromFlatOffset(int offset)
    {
        var fullText = Text?.ToString() ?? string.Empty;
        var lines = fullText.Split('\n');
        var remaining = offset;
        var row = 0;
        foreach (var line in lines)
        {
            if (remaining <= line.Length)
            {
                CursorPosition = new Point(remaining, row);
                return;
            }
            remaining -= line.Length + 1; // +1 for newline
            row++;
        }
        // If past end, set to end of last line
        if (lines.Length > 0)
        {
            CursorPosition = new Point(lines[lines.Length - 1].Length, lines.Length - 1);
        }
    }

    /// <summary>
    /// Finds the start of the word at/before the given offset.
    /// A "word" is a contiguous run of identifier characters (letters, digits, underscore, dot).
    /// </summary>
    private static int FindWordStart(string text, int offset)
    {
        var pos = offset - 1;
        while (pos >= 0 && IsWordChar(text[pos]))
        {
            pos--;
        }
        return pos + 1;
    }

    private static bool IsWordChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == '_';
    }

    private static bool IsPrintableChar(KeyEvent keyEvent)
    {
        // A printable character is one without Ctrl or Alt modifiers (other than shift)
        // and the key value maps to a visible ASCII character
        var key = keyEvent.Key;
        if ((key & Key.CtrlMask) != 0 || (key & Key.AltMask) != 0)
            return false;

        var rawKey = (int)(key & ~Key.ShiftMask);
        return rawKey >= 32 && rawKey < 127;
    }

    private static bool IsNavigationKey(KeyEvent keyEvent)
    {
        return keyEvent.Key == Key.CursorUp || keyEvent.Key == Key.CursorDown ||
               keyEvent.Key == Key.CursorLeft || keyEvent.Key == Key.CursorRight ||
               keyEvent.Key == Key.Home || keyEvent.Key == Key.End ||
               keyEvent.Key == Key.PageUp || keyEvent.Key == Key.PageDown;
    }

    /// <summary>
    /// Determines if a key event could modify the text content.
    /// </summary>
    private static bool IsTextChangingKey(KeyEvent keyEvent)
    {
        if (IsPrintableChar(keyEvent)) return true;
        if (keyEvent.Key == Key.Backspace || keyEvent.Key == Key.DeleteChar) return true;
        if (keyEvent.Key == Key.Enter) return true;
        // Ctrl+V (paste), Ctrl+X (cut), Ctrl+Z (undo), Ctrl+Y (redo)
        if (keyEvent.Key == (Key.CtrlMask | Key.V) ||
            keyEvent.Key == (Key.CtrlMask | Key.X) ||
            keyEvent.Key == (Key.CtrlMask | Key.Z) ||
            keyEvent.Key == (Key.CtrlMask | Key.Y))
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Schedules a debounced validation run. Resets any previously scheduled timer.
    /// After <see cref="ValidationDebounceMs"/> of inactivity, runs validation asynchronously.
    /// </summary>
    private void ScheduleDebouncedValidation()
    {
        if (LanguageService == null) return;

        // Cancel any pending timer
        if (_validationTimerToken != null && Application.MainLoop != null)
        {
            Application.MainLoop.RemoveTimeout(_validationTimerToken);
            _validationTimerToken = null;
        }

        // Schedule a new timer
        if (Application.MainLoop != null)
        {
            _validationTimerToken = Application.MainLoop.AddTimeout(
                TimeSpan.FromMilliseconds(ValidationDebounceMs),
                timerArgs =>
                {
                    _validationTimerToken = null;
                    _ = RunValidationAsync();
                    return false; // do not repeat
                });
        }
    }

    /// <summary>
    /// Runs validation asynchronously and updates the diagnostics field.
    /// </summary>
    internal async Task RunValidationAsync()
    {
        if (LanguageService == null) return;

        // Cancel and dispose any in-flight validation
        _validationCts?.Cancel();
        _validationCts?.Dispose();
        _validationCts = new CancellationTokenSource();
        var ct = _validationCts.Token;

        var fullText = Text?.ToString() ?? string.Empty;

        // Clear stale diagnostics immediately so old errors don't linger
        // while the new async validation runs
        _diagnostics = Array.Empty<SqlDiagnostic>();
        _cachedColorMap = null;
        Application.MainLoop?.Invoke(SetNeedsDisplay);

        try
        {
            var diags = await LanguageService.ValidateAsync(fullText, ct);

            if (ct.IsCancellationRequested) return;

            _diagnostics = diags;

            // Refresh display to show/hide error highlights
            Application.MainLoop?.Invoke(() =>
            {
                if (!ct.IsCancellationRequested)
                {
                    SetNeedsDisplay();
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Expected when text changes during validation
        }
        catch (Exception ex)
        {
            TuiDebugLog.Log($"Validation error: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns cached tokens and color map, recomputing only when text has changed.
    /// </summary>
    private (IReadOnlyList<SourceToken> tokens, Terminal.Gui.Attribute[] colorMap)? GetCachedTokenization(string fullText)
    {
        if (_cachedText == fullText && _cachedTokens != null && _cachedColorMap != null)
        {
            return (_cachedTokens, _cachedColorMap);
        }

        try
        {
            var tokens = _tokenizer.Tokenize(fullText);
            var colorMap = BuildColorMap(fullText, tokens);
            _cachedText = fullText;
            _cachedTokens = tokens;
            _cachedColorMap = colorMap;
            return (tokens, colorMap);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _cachedText = null;
            _cachedTokens = null;
            _cachedColorMap = null;
            return null;
        }
    }

    /// <inheritdoc />
    public override void Redraw(Rect bounds)
    {
        if (Driver == null)
        {
            base.Redraw(bounds);
            return;
        }

        var fullText = Text?.ToString() ?? string.Empty;

        // Use cached tokenization to avoid re-tokenizing on every paint cycle
        var cached = GetCachedTokenization(fullText);
        if (cached == null)
        {
            // Tokenizer failed — fall back to base rendering
            base.Redraw(bounds);
            return;
        }

        var charColors = cached.Value.colorMap;

        // Render line by line
        var lines = fullText.Split('\n');
        var topRow = TopRow;
        var leftCol = LeftColumn;
        var isSelecting = Selecting;
        var selStartCol = SelectionStartColumn;
        var selStartRow = SelectionStartRow;
        var cursorPos = CursorPosition;

        // Compute selection bounds as flat offsets for easy comparison
        int selStart = -1, selEnd = -1;
        if (isSelecting)
        {
            selStart = FlatOffset(lines, selStartRow, selStartCol);
            selEnd = FlatOffset(lines, cursorPos.Y, cursorPos.X);
            if (selStart > selEnd)
                (selStart, selEnd) = (selEnd, selStart);
        }

        // Selection color: inverted (black on cyan, matching TuiColorPalette.Selected)
        var selectionAttr = Driver.MakeAttribute(Color.Black, Color.Cyan);
        var hasFocus = HasFocus;
        var cursorScreenCol = cursorPos.X - leftCol;
        var cursorScreenRow = cursorPos.Y - topRow;

        var screenRow = 0;
        var flatPos = 0; // track flat offset into fullText

        // Advance flatPos to account for lines before topRow
        for (int i = 0; i < topRow && i < lines.Length; i++)
        {
            flatPos += lines[i].Length + 1; // +1 for \n
        }

        for (int lineIdx = topRow; lineIdx < lines.Length && screenRow < bounds.Height; lineIdx++)
        {
            var line = lines[lineIdx];
            // Strip trailing \r for accurate rendering (Windows line endings)
            if (line.Length > 0 && line[line.Length - 1] == '\r')
                line = line.Substring(0, line.Length - 1);

            var lineStart = flatPos;

            var screenCol = 0;
            for (int charIdx = leftCol; charIdx < line.Length && screenCol < bounds.Width; charIdx++)
            {
                var ch = line[charIdx];
                var absPos = lineStart + charIdx;

                // Selection always wins over syntax colors
                Terminal.Gui.Attribute attr;
                if (isSelecting && absPos >= selStart && absPos < selEnd)
                {
                    attr = selectionAttr;
                }
                else if (absPos < charColors.Length)
                {
                    attr = charColors[absPos];
                }
                else
                {
                    attr = _defaultAttr;
                }

                // Highlight cursor position with high-contrast inverted block
                if (hasFocus && screenRow == cursorScreenRow && screenCol == cursorScreenCol)
                {
                    attr = Driver.MakeAttribute(Color.Black, Color.White);
                }

                Driver.SetAttribute(attr);

                // Handle tabs
                if (ch == '\t')
                {
                    var tabWidth = TabWidth > 0 ? TabWidth : 4;
                    var spaces = tabWidth - (screenCol % tabWidth);
                    for (int t = 0; t < spaces && screenCol < bounds.Width; t++)
                    {
                        AddRune(screenCol, screenRow, ' ');
                        screenCol++;
                    }
                }
                else
                {
                    AddRune(screenCol, screenRow, ch);
                    screenCol += System.Rune.ColumnWidth(ch);
                }
            }

            // Clear rest of line
            if (screenCol < bounds.Width)
            {
                for (int c = screenCol; c < bounds.Width; c++)
                {
                    if (hasFocus && screenRow == cursorScreenRow && c == cursorScreenCol)
                        Driver.SetAttribute(Driver.MakeAttribute(Color.Black, Color.White));
                    else
                        Driver.SetAttribute(_defaultAttr);
                    AddRune(c, screenRow, ' ');
                }
            }

            flatPos += lines[lineIdx].Length + 1; // +1 for \n (use original line length before \r strip)
            screenRow++;
        }

        // Clear remaining rows below text
        if (screenRow < bounds.Height)
        {
            for (int r = screenRow; r < bounds.Height; r++)
            {
                for (int c = 0; c < bounds.Width; c++)
                {
                    if (hasFocus && r == cursorScreenRow && c == cursorScreenCol)
                        Driver.SetAttribute(Driver.MakeAttribute(Color.Black, Color.White));
                    else
                        Driver.SetAttribute(_defaultAttr);
                    AddRune(c, r, ' ');
                }
            }
        }

        // Draw visible subviews (autocomplete popup) on top of text content.
        // Required because we override Redraw without calling base.Redraw(),
        // which means Terminal.Gui's default subview rendering is skipped.
        foreach (var sub in Subviews)
        {
            if (sub.Visible)
            {
                // Set clip region to the subview's frame so it draws at the right position
                Driver.SetAttribute(_defaultAttr);
                sub.Redraw(sub.Bounds);
            }
        }

        // Position cursor but hide the hardware cursor — we paint our own block cursor above.
        // Without this, the terminal's hardware cursor (often a dark block) draws on top
        // of our painted white-background cursor cell, making it invisible.
        PositionCursor();
        if (Driver != null)
            Driver.SetCursorVisibility(CursorVisibility.Invisible);
    }

    /// <summary>
    /// Builds a flat array mapping each character position in the source text
    /// to its syntax color attribute, then overlays diagnostic error highlights.
    /// </summary>
    private Terminal.Gui.Attribute[] BuildColorMap(string text, IReadOnlyList<SourceToken> tokens)
    {
        var map = new Terminal.Gui.Attribute[text.Length];
        Array.Fill(map, _defaultAttr);

        foreach (var token in tokens)
        {
            if (_colorMap.TryGetValue(token.Type, out var attr))
            {
                var end = Math.Min(token.Start + token.Length, text.Length);
                for (int i = token.Start; i < end; i++)
                    map[i] = attr;
            }
        }

        // Overlay diagnostic error highlights (red background)
        var diagnostics = _diagnostics;
        if (diagnostics.Count > 0)
        {
            var errorAttr = TuiColorPalette.SqlError;

            foreach (var diag in diagnostics)
            {
                if (diag.Severity != SqlDiagnosticSeverity.Error) continue;

                var diagEnd = Math.Min(diag.Start + diag.Length, text.Length);
                for (int i = Math.Max(0, diag.Start); i < diagEnd; i++)
                {
                    map[i] = errorAttr;
                }
            }
        }

        return map;
    }

    /// <summary>
    /// Computes the flat character offset for a (row, col) position.
    /// </summary>
    private static int FlatOffset(string[] lines, int row, int col)
    {
        var offset = 0;
        for (int i = 0; i < row && i < lines.Length; i++)
            offset += lines[i].Length + 1;
        if (row < lines.Length)
            offset += Math.Min(col, lines[row].Length);
        return offset;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autocompletePopup.CompletionAccepted -= OnCompletionAccepted;

            _completionCts?.Cancel();
            _completionCts?.Dispose();
            _completionCts = null;

            _validationCts?.Cancel();
            _validationCts?.Dispose();
            _validationCts = null;

            if (_validationTimerToken != null)
            {
                Application.MainLoop?.RemoveTimeout(_validationTimerToken);
                _validationTimerToken = null;
            }
        }
        base.Dispose(disposing);
    }
}
