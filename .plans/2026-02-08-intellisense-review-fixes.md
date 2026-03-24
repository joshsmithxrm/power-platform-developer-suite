# SQL IntelliSense Code Review Fixes Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix all Critical and Important issues from the SQL IntelliSense code review.

**Architecture:** Targeted fixes across 6 files — exception handling, nullable contracts, PpdsException wrapping, resource disposal, performance caching, and popup robustness.

**Tech Stack:** C# (.NET 8/9/10), Terminal.Gui 1.19, xUnit

**Worktree:** `.worktrees/tui-polish` on branch `fix/tui-colors`

**Build & test commands:**
```bash
dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo --verbosity quiet
dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet
```

**Important context for implementors:**
- Terminal.Gui 1.19 (NOT v2)
- `PpdsException(string errorCode, string userMessage, Exception? inner)` is the constructor pattern
- `ErrorCodes.Query.*` has existing query-related codes — add `CompletionFailed` for IntelliSense
- `TuiDebugLog.Log(string message)` is the standard debug logging call
- All test classes must have `[Trait("Category", "TuiUnit")]`
- Run tests with `dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet`

---

### Task 1: Fix bare catch clauses in SqlCursorContext (C1 partial)

**Files:**
- Modify: `src/PPDS.Dataverse/Sql/Intellisense/SqlCursorContext.cs:64-77,660-672`

**Step 1: Fix the AST parse try/catch at line 64-77**

Replace the bare `catch` with typed exception handling:

```csharp
        // Try AST-based analysis first
        try
        {
            var parser = new SqlParser(sql);
            var statement = parser.ParseStatement();

            if (statement is SqlSelectStatement select)
            {
                return AnalyzeSelectStatement(sql, cursorOffset, select);
            }
        }
        catch (OperationCanceledException)
        {
            throw; // Never swallow cancellation
        }
        catch (Exception)
        {
            // Parse failed on partial SQL — fall through to lexer-based heuristic
        }
```

**Step 2: Fix the TokenizeSafe method at line 660-672**

Replace bare `catch` with typed exception handling:

```csharp
    private static IReadOnlyList<SqlToken> TokenizeSafe(string sql)
    {
        try
        {
            var lexer = new SqlLexer(sql);
            var result = lexer.Tokenize();
            return result.Tokens;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Array.Empty<SqlToken>();
        }
    }
```

**Step 3: Build and run tests**

```bash
dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo --verbosity quiet
dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet
```

Expected: All tests pass. No behavior change — these catches only fire on malformed SQL.

**Step 4: Commit**

```bash
git add src/PPDS.Dataverse/Sql/Intellisense/SqlCursorContext.cs
git commit -m "fix: replace bare catch clauses with typed Exception in SqlCursorContext"
```

---

### Task 2: Fix bare catch clauses in SqlSourceTokenizer and SqlCompletionEngine (C1 complete)

**Files:**
- Modify: `src/PPDS.Dataverse/Sql/Intellisense/SqlSourceTokenizer.cs:20-31`
- Modify: `src/PPDS.Dataverse/Sql/Intellisense/SqlCompletionEngine.cs:147-155`

**Step 1: Fix SqlSourceTokenizer.Tokenize at line 20-31**

Replace bare `catch`:

```csharp
        try
        {
            var lexer = new SqlLexer(text);
            var result = lexer.Tokenize();
            return BuildSourceTokens(text, result.Tokens, result.Comments);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Lexer threw (e.g. unterminated string) — fall back to error token for entire input
            return new[] { new SourceToken(0, text.Length, SourceTokenType.Error) };
        }
```

**Step 2: Fix SqlCompletionEngine attribute lookup at line 147-155**

Replace bare `catch` and re-throw cancellation:

```csharp
                try
                {
                    var attributes = await _metadataProvider.GetAttributesAsync(entityName, ct);
                    completions.AddRange(attributes.Select(attr => CreateAttributeCompletion(attr)));
                }
                catch (OperationCanceledException)
                {
                    throw; // Respect cancellation
                }
                catch (Exception)
                {
                    // Skip entities where metadata lookup fails (e.g. network error)
                }
```

**Step 3: Build and run tests**

```bash
dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo --verbosity quiet
dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet
```

Expected: All tests pass.

**Step 4: Commit**

```bash
git add src/PPDS.Dataverse/Sql/Intellisense/SqlSourceTokenizer.cs src/PPDS.Dataverse/Sql/Intellisense/SqlCompletionEngine.cs
git commit -m "fix: replace bare catch clauses in SqlSourceTokenizer and SqlCompletionEngine"
```

---

### Task 3: Fix null! hack and PpdsException wrapping in SqlLanguageService (C2, C3)

**Files:**
- Modify: `src/PPDS.Cli/Services/SqlLanguageService.cs` (entire file)
- Modify: `src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs:143-171`

**Step 1: Add IntelliSense error code to ErrorCodes.Query**

In `src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs`, add inside the `Query` class after the last existing entry (line 170, before the closing `}`):

```csharp
        /// <summary>IntelliSense completion lookup failed.</summary>
        public const string CompletionFailed = "Query.CompletionFailed";
```

**Step 2: Fix SqlLanguageService — nullable field and PpdsException wrapping**

Rewrite `src/PPDS.Cli/Services/SqlLanguageService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Sql.Intellisense;

namespace PPDS.Cli.Services;

/// <summary>
/// Default implementation of <see cref="ISqlLanguageService"/>.
/// Composes <see cref="SqlSourceTokenizer"/> for syntax highlighting,
/// <see cref="SqlCompletionEngine"/> for IntelliSense completions,
/// and <see cref="SqlValidator"/> for diagnostics.
/// </summary>
public sealed class SqlLanguageService : ISqlLanguageService
{
    private readonly SqlSourceTokenizer _tokenizer = new();
    private readonly SqlCompletionEngine? _completionEngine;
    private readonly SqlValidator _validator;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlLanguageService"/> class.
    /// </summary>
    /// <param name="metadataProvider">
    /// Cached metadata provider for entity/attribute lookups.
    /// May be null if no environment is connected (completions will return keywords only).
    /// </param>
    public SqlLanguageService(ICachedMetadataProvider? metadataProvider)
    {
        _validator = new SqlValidator(metadataProvider);
        _completionEngine = metadataProvider != null
            ? new SqlCompletionEngine(metadataProvider)
            : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<SourceToken> Tokenize(string sql)
    {
        return _tokenizer.Tokenize(sql);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SqlCompletion>> GetCompletionsAsync(
        string sql, int cursorOffset, CancellationToken ct = default)
    {
        if (_completionEngine == null)
        {
            // No metadata available — return keyword-only completions
            var context = SqlCursorContext.Analyze(sql, cursorOffset);
            if (context.Kind == SqlCompletionContextKind.Keyword && context.KeywordSuggestions != null)
            {
                var keywords = new List<SqlCompletion>();
                var sortOrder = 0;
                foreach (var kw in context.KeywordSuggestions)
                {
                    keywords.Add(new SqlCompletion(kw, kw, SqlCompletionKind.Keyword, SortOrder: sortOrder++));
                }
                return keywords;
            }
            return Array.Empty<SqlCompletion>();
        }

        try
        {
            return await _completionEngine.GetCompletionsAsync(sql, cursorOffset, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PpdsException(
                ErrorCodes.Query.CompletionFailed,
                "Failed to retrieve IntelliSense completions.",
                ex);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SqlDiagnostic>> ValidateAsync(string sql, CancellationToken ct = default)
    {
        return _validator.ValidateAsync(sql, ct);
    }
}
```

**Step 3: Build and run tests**

```bash
dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo --verbosity quiet
dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet
```

Expected: All tests pass.

**Step 4: Commit**

```bash
git add src/PPDS.Cli/Services/SqlLanguageService.cs src/PPDS.Cli/Infrastructure/Errors/ErrorCodes.cs
git commit -m "fix: use nullable field and wrap exceptions in PpdsException in SqlLanguageService"
```

---

### Task 4: Fix dot trigger magic number and add PageUp/PageDown to popup (I1, S5)

**Files:**
- Modify: `src/PPDS.Cli/Tui/Views/SyntaxHighlightedTextView.cs:160`
- Modify: `src/PPDS.Cli/Tui/Views/AutocompletePopup.cs:140-166`

**Step 1: Replace magic number `(Key)46` with character cast**

In `src/PPDS.Cli/Tui/Views/SyntaxHighlightedTextView.cs` at line 160, replace:

```csharp
        if (keyEvent.Key == (Key)46) // '.' character
```

with:

```csharp
        if (keyEvent.Key == (Key)'.')
```

**Step 2: Add PageUp/PageDown handling in AutocompletePopup**

In `src/PPDS.Cli/Tui/Views/AutocompletePopup.cs`, in `ProcessKeyEvent` method, add cases before the `default:` at line 163:

```csharp
            case Key.PageUp:
                MoveSelection(-8); // Page size matches max visible items
                return true;

            case Key.PageDown:
                MoveSelection(8);
                return true;
```

**Step 3: Build and run tests**

```bash
dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo --verbosity quiet
dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet
```

Expected: All tests pass.

**Step 4: Commit**

```bash
git add src/PPDS.Cli/Tui/Views/SyntaxHighlightedTextView.cs src/PPDS.Cli/Tui/Views/AutocompletePopup.cs
git commit -m "fix: replace dot trigger magic number and add PageUp/PageDown to autocomplete popup"
```

---

### Task 5: Guard empty items in AutocompletePopup.Redraw (I3)

**Files:**
- Modify: `src/PPDS.Cli/Tui/Views/AutocompletePopup.cs:275-281`

**Step 1: Add early return guard**

In `src/PPDS.Cli/Tui/Views/AutocompletePopup.cs`, in the `Redraw` method, add an empty-items guard after the `Driver == null` check (after line 281, before the "Draw items manually" comment):

```csharp
        if (_filteredItems.Count == 0)
        {
            base.Redraw(bounds);
            return;
        }
```

**Step 2: Build and run tests**

```bash
dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo --verbosity quiet
dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet
```

Expected: All tests pass.

**Step 3: Commit**

```bash
git add src/PPDS.Cli/Tui/Views/AutocompletePopup.cs
git commit -m "fix: guard empty items in AutocompletePopup.Redraw to prevent index errors"
```

---

### Task 6: Clean semaphore dictionaries in CachedMetadataProvider.InvalidateAll (I4)

**Files:**
- Modify: `src/PPDS.Dataverse/Metadata/CachedMetadataProvider.cs:171-176`

**Step 1: Clear and dispose semaphore dictionaries in InvalidateAll**

Replace the `InvalidateAll` method at line 171-176:

```csharp
    /// <inheritdoc />
    public void InvalidateAll()
    {
        _entities = null;
        _attributeCache.Clear();
        _relationshipCache.Clear();

        // Dispose and clear per-entity semaphores to prevent unbounded growth
        foreach (var semaphore in _attributeLocks.Values)
            semaphore.Dispose();
        _attributeLocks.Clear();

        foreach (var semaphore in _relationshipLocks.Values)
            semaphore.Dispose();
        _relationshipLocks.Clear();
    }
```

**Step 2: Build and run tests**

```bash
dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo --verbosity quiet
dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet
```

Expected: All tests pass.

**Step 3: Commit**

```bash
git add src/PPDS.Dataverse/Metadata/CachedMetadataProvider.cs
git commit -m "fix: dispose and clear semaphore dictionaries in CachedMetadataProvider.InvalidateAll"
```

---

### Task 7: Pass CancellationToken to metadata preload (I5)

**Files:**
- Modify: `src/PPDS.Cli/Tui/InteractiveSession.cs:271-288`

**Step 1: Pass cancellation token from calling context**

The `GetServiceProviderAsync` method already receives a `CancellationToken cancellationToken` parameter. Pass it to `PreloadAsync`. Replace lines 276-287:

```csharp
                TuiDebugLog.Log($"Starting metadata preload for {environmentUrl}");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await cachedMetadata.PreloadAsync(cancellationToken).ConfigureAwait(false);
                        TuiDebugLog.Log($"Metadata preload completed for {environmentUrl}");
                    }
                    catch (OperationCanceledException)
                    {
                        TuiDebugLog.Log($"Metadata preload cancelled for {environmentUrl}");
                    }
                    catch (Exception ex)
                    {
                        TuiDebugLog.Log($"Metadata preload failed for {environmentUrl}: {ex.Message}");
                    }
                }, cancellationToken);
```

**Step 2: Build and run tests**

```bash
dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo --verbosity quiet
dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet
```

Expected: All tests pass.

**Step 3: Commit**

```bash
git add src/PPDS.Cli/Tui/InteractiveSession.cs
git commit -m "fix: pass CancellationToken to metadata preload in InteractiveSession"
```

---

### Task 8: Dispose CancellationTokenSource in SyntaxHighlightedTextView (I6)

**Files:**
- Modify: `src/PPDS.Cli/Tui/Views/SyntaxHighlightedTextView.cs:206-208,430-433`

**Step 1: Dispose old CTS before creating new one in TriggerCompletionsAsync**

At line 206-208, replace:

```csharp
        // Cancel any in-flight request
        _completionCts?.Cancel();
        _completionCts = new CancellationTokenSource();
```

with:

```csharp
        // Cancel and dispose any in-flight request
        _completionCts?.Cancel();
        _completionCts?.Dispose();
        _completionCts = new CancellationTokenSource();
```

**Step 2: Do the same in RunValidationAsync**

At line 430-432, replace:

```csharp
        // Cancel any in-flight validation
        _validationCts?.Cancel();
        _validationCts = new CancellationTokenSource();
```

with:

```csharp
        // Cancel and dispose any in-flight validation
        _validationCts?.Cancel();
        _validationCts?.Dispose();
        _validationCts = new CancellationTokenSource();
```

**Step 3: Build and run tests**

```bash
dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo --verbosity quiet
dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet
```

Expected: All tests pass.

**Step 4: Commit**

```bash
git add src/PPDS.Cli/Tui/Views/SyntaxHighlightedTextView.cs
git commit -m "fix: dispose CancellationTokenSource before replacement in SyntaxHighlightedTextView"
```

---

### Task 9: Cache tokenization results to avoid re-tokenizing on every Redraw (I7)

**Files:**
- Modify: `src/PPDS.Cli/Tui/Views/SyntaxHighlightedTextView.cs`

**Step 1: Add cache fields**

After the `_diagnostics` field (line 43), add:

```csharp
    /// <summary>
    /// Cached tokenization results to avoid re-tokenizing on every Redraw cycle.
    /// Invalidated when text content changes.
    /// </summary>
    private string? _cachedText;
    private IReadOnlyList<SourceToken>? _cachedTokens;
    private Terminal.Gui.Attribute[]? _cachedColorMap;
```

**Step 2: Add a helper method to get or compute tokens**

Add this method before the `Redraw` override:

```csharp
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
```

**Step 3: Update Redraw to use the cache**

Replace lines 473-489 in the `Redraw` method (the tokenization block):

```csharp
        var fullText = Text?.ToString() ?? string.Empty;

        // Tokenize once per redraw
        IReadOnlyList<SourceToken> tokens;
        try
        {
            tokens = _tokenizer.Tokenize(fullText);
        }
        catch
        {
            // If tokenizer fails, fall back to base rendering
            base.Redraw(bounds);
            return;
        }

        // Build a flat array mapping each character offset → Attribute
        var charColors = BuildColorMap(fullText, tokens);
```

with:

```csharp
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
```

**Step 4: Build and run tests**

```bash
dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo --verbosity quiet
dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet
```

Expected: All tests pass.

**Step 5: Commit**

```bash
git add src/PPDS.Cli/Tui/Views/SyntaxHighlightedTextView.cs
git commit -m "perf: cache tokenization results to avoid re-tokenizing on every Redraw cycle"
```

---

### Task 10: Final verification

**Step 1: Full build**

```bash
dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo --verbosity quiet
```

Expected: Clean build, no errors.

**Step 2: Full test suite**

```bash
dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet
```

Expected: All 62+ tests pass.

**Step 3: Verify no remaining bare catches**

Search for bare `catch` (without `Exception` type) across the IntelliSense files:

```bash
grep -rn "catch$\|catch {" src/PPDS.Dataverse/Sql/Intellisense/ src/PPDS.Cli/Services/SqlLanguageService.cs src/PPDS.Cli/Tui/Views/SyntaxHighlightedTextView.cs src/PPDS.Cli/Tui/Views/AutocompletePopup.cs
```

Expected: No matches (all bare catches have been replaced).

---

## Summary of Changes

| Task | Issue | File(s) | Fix |
|------|-------|---------|-----|
| 1 | C1 | SqlCursorContext.cs | Typed `catch (Exception)` + re-throw `OperationCanceledException` |
| 2 | C1 | SqlSourceTokenizer.cs, SqlCompletionEngine.cs | Same pattern |
| 3 | C2, C3 | SqlLanguageService.cs, ErrorCodes.cs | Nullable `SqlCompletionEngine?` field + `PpdsException` wrapping |
| 4 | I1, S5 | SyntaxHighlightedTextView.cs, AutocompletePopup.cs | `(Key)'.'` + PageUp/PageDown |
| 5 | I3 | AutocompletePopup.cs | Empty items guard in Redraw |
| 6 | I4 | CachedMetadataProvider.cs | Clear + dispose semaphore dicts in InvalidateAll |
| 7 | I5 | InteractiveSession.cs | Pass CancellationToken to PreloadAsync |
| 8 | I6 | SyntaxHighlightedTextView.cs | Dispose CTS before replacement |
| 9 | I7 | SyntaxHighlightedTextView.cs | Cache tokenization results |
| 10 | — | All | Final verification |
