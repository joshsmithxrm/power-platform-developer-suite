# SQL IntelliSense & Syntax Highlighting Design

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Full SQL language service for the TUI — syntax highlighting, context-aware autocomplete, and validation with error indicators. Built on the existing `SqlLexer`/`SqlParser` AST, reusable across TUI and VSCode via Application Services.

**Tech Stack:** C# (.NET 8/9/10), Terminal.Gui 1.19, xUnit

**Worktree:** `.worktrees/tui-polish` on branch `fix/tui-colors`

**Build & test commands:**
```bash
dotnet build src/PPDS.Cli/PPDS.Cli.csproj --nologo --verbosity quiet
dotnet test tests/PPDS.Cli.Tests/PPDS.Cli.Tests.csproj --filter "Category=TuiUnit" --nologo --verbosity quiet
```

**Important context for implementors:**
- Terminal.Gui 1.19 — NOT v2. No built-in autocomplete or syntax highlighting.
- `SqlLexer` produces `SqlToken(Type, Value, Position)` — already has keyword map, position tracking
- `SqlParser` produces `SqlSelectStatement` AST with `From`, `Joins`, `Columns`, `Where`, `OrderBy`, `GroupBy`
- `IMetadataService` exists but has no caching layer — every call hits Dataverse live
- `TuiColorPalette` provides all color schemes — 16 ANSI colors via `TuiTerminalPalette` overrides
- Per-character coloring proven via `SplashView.Redraw()` using `Driver.SetAttribute()`/`Driver.AddStr()`
- `InteractiveSession` caches `ServiceProvider` per environment URL

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│                     TUI Layer                           │
│                                                         │
│  SqlQueryScreen                                         │
│    └── SyntaxHighlightedTextView : TextView             │
│          ├── ISourceTokenizer (produces colored tokens)  │
│          ├── IAutocompleteProvider (produces suggestions)│
│          └── AutocompletePopup (floating ListView)       │
│                                                         │
├─────────────────────────────────────────────────────────┤
│                Application Services                     │
│                                                         │
│  ISqlLanguageService                                    │
│    ├── Tokenize(sql) → SourceToken[]                    │
│    ├── GetCompletions(sql, cursorPos) → Completion[]    │
│    └── Validate(sql) → Diagnostic[]                     │
│                                                         │
├─────────────────────────────────────────────────────────┤
│                  PPDS.Dataverse                          │
│                                                         │
│  Sql/Intellisense/                                      │
│    ├── SqlSourceTokenizer : ISourceTokenizer             │
│    ├── SqlCompletionEngine                               │
│    │     ├── AST-based cursor context detection          │
│    │     ├── Keyword completions (context-filtered)      │
│    │     ├── Entity completions (from metadata cache)    │
│    │     ├── Attribute completions (alias-aware)         │
│    │     └── Relationship JOIN suggestions               │
│    └── SqlValidator                                      │
│          ├── Parse error surfacing                        │
│          └── Metadata-aware column/table validation       │
│                                                         │
│  Metadata/                                               │
│    └── CachedMetadataProvider : ICachedMetadataProvider   │
│          ├── Entity list: eager load on connect           │
│          └── Attributes: lazy per-entity, 5-min TTL      │
│                                                         │
│  Sql/Parsing/ (existing)                                 │
│    ├── SqlLexer → SqlToken(Type, Value, Position)        │
│    └── SqlParser → SqlSelectStatement AST                │
└─────────────────────────────────────────────────────────┘
```

---

## 1. Syntax Highlighting

### Abstraction Layer

**`ISourceTokenizer`** — generic interface, not SQL-specific:

```csharp
// src/PPDS.Dataverse/Sql/Intellisense/ISourceTokenizer.cs
public interface ISourceTokenizer
{
    IReadOnlyList<SourceToken> Tokenize(string text);
}

public readonly record struct SourceToken(int Start, int Length, SourceTokenType Type);

public enum SourceTokenType
{
    Keyword,
    Function,
    StringLiteral,
    NumericLiteral,
    Comment,
    Operator,
    Identifier,
    Error
}
```

**`SqlSourceTokenizer`** — wraps existing `SqlLexer`, maps `SqlTokenType` → `SourceTokenType`:

| SqlTokenType | SourceTokenType |
|---|---|
| Select, From, Where, And, Or, Join, etc. | Keyword |
| Count, Sum, Avg, Min, Max | Function |
| StringLiteral | StringLiteral |
| NumericLiteral | NumericLiteral |
| LineComment, BlockComment | Comment |
| Equals, NotEquals, LessThan, etc. | Operator |
| Identifier, QuotedIdentifier | Identifier |
| (parse failures) | Error |

### Color Palette (SSMS-Inspired for Dark Background)

| SourceTokenType | Color | SSMS Reference |
|---|---|---|
| Keyword | Blue | Blue in SSMS |
| Function | Magenta | Magenta in SSMS |
| StringLiteral | Red | Red in SSMS |
| Comment | Green | Green in SSMS |
| NumericLiteral | Cyan | Distinct from strings |
| Operator | Gray | Neutral |
| Identifier | White | Black in SSMS, inverted for dark bg |
| Error | Red background | Validation indicator |

Defined in `TuiColorPalette` as `Dictionary<SourceTokenType, Attribute>`.

### SyntaxHighlightedTextView

Subclass of `TextView`. Overrides `Redraw(Rect bounds)`:

1. Call `ISourceTokenizer.Tokenize(Text)` to get token list
2. For each visible line, walk tokens that intersect the line
3. `Driver.SetAttribute(colorMap[token.Type])` before each token segment
4. `Driver.AddStr(tokenText)` to render
5. Handle selection highlighting (overlay selection colors on top of syntax colors)
6. Handle cursor rendering

This view is language-agnostic. SQL-specific behavior comes from the tokenizer injected at construction.

---

## 2. Autocomplete

### Trigger Rules (SSMS-Style with Smart Triggers)

**Auto-popup triggers:**
- After `.` (alias dot) → column completions for that alias
- After `FROM ` / `JOIN ` → entity name completions
- After `= ` on a known picklist column → option set value completions

**Manual trigger:**
- `Ctrl+Space` anywhere → context-appropriate suggestions

**Suppressed contexts:**
- Inside string literals (`'...'`)
- Inside comments (`--`, `/* */`)

**Hint update:** FrameView label becomes `"Query (Ctrl+Enter to execute, Ctrl+Space for suggestions, F6 to toggle focus)"`

### Context Detection (AST-Based)

**Not regex-based.** Uses the existing `SqlParser` AST to determine cursor context:

1. Parse the SQL (tolerant — partial SQL should produce partial AST)
2. Map cursor offset to AST region using token positions
3. Determine completion context from AST node type:

| Cursor Position | AST Context | Completion Type |
|---|---|---|
| After `FROM` / `JOIN` keyword | Before `SqlTableRef` | Entity names |
| In SELECT column list | `ISqlSelectColumn` region | Attributes (all in-scope tables) |
| After `alias.` | Column ref with table qualifier | Attributes (specific table) |
| After `WHERE` / `AND` / `OR` | `ISqlCondition` region | Attributes + operators |
| After `ORDER BY` | `SqlOrderByItem` region | Attributes |
| After `ON` in JOIN | Join condition | Attributes (both tables) |
| After `=` on picklist | Comparison value | Option set values |
| Statement start / after keyword | Keyword position | Context-filtered keywords |

**Alias resolution:** The AST's `SqlTableRef` and `SqlJoin` nodes carry table names and aliases. The completion engine builds an alias→entity map from the AST, then resolves which entity's attributes to suggest.

**Partial parse handling:** When the SQL is incomplete (user is mid-typing), the parser may fail. Fall back to lexer tokens + heuristic context detection (reverse word scan from cursor) when the AST is unavailable.

### Completion Engine

```csharp
// src/PPDS.Dataverse/Sql/Intellisense/SqlCompletionEngine.cs
public class SqlCompletionEngine
{
    public SqlCompletionEngine(ICachedMetadataProvider metadataProvider) { }

    public async Task<IReadOnlyList<SqlCompletion>> GetCompletionsAsync(
        string sql,
        int cursorOffset,
        CancellationToken cancellationToken = default);
}

public record SqlCompletion(
    string Label,
    string InsertText,
    SqlCompletionKind Kind,
    string? Description = null,
    string? Detail = null,
    int SortOrder = 0);

public enum SqlCompletionKind
{
    Keyword,
    Entity,
    Attribute,
    Function,
    OptionSetValue,
    JoinClause
}
```

**Keyword groups** (context-filtered, from legacy `SqlContextDetector`):
- Statement start: SELECT, INSERT, UPDATE, DELETE
- After SELECT: DISTINCT, TOP, FROM, COUNT, SUM, AVG, MIN, MAX
- After FROM entity: WHERE, ORDER BY, JOIN, INNER JOIN, LEFT JOIN, GROUP BY, AS
- After JOIN entity: ON, AS
- After WHERE condition: AND, OR, ORDER BY, GROUP BY
- WHERE operators: IS, IS NOT, IN, NOT IN, LIKE, NOT LIKE, BETWEEN, NULL
- After ORDER BY attr: ASC, DESC
- After ORDER BY complete: LIMIT

**Relationship-aware JOIN suggestions**:
- After `JOIN`, in addition to entity names, suggest related entities based on relationships from tables already in the FROM clause
- Each suggestion includes the full ON clause: `contact ON account.primarycontactid = contact.contactid`
- Uses `IMetadataService.GetRelationshipsAsync()` via the cached provider

### Autocomplete Popup UI

**`AutocompletePopup`** — a floating `ListView` overlay:

- Positioned below cursor (or above if near bottom edge)
- Max 8 visible items, scrollable
- Each item shows: icon prefix (K=keyword, T=table, C=column, F=function) + label
- Filter-as-you-type: starts-with match, fallback to contains
- Accept: Tab or Enter (inserts `InsertText`, closes popup)
- Dismiss: Escape, cursor movement away, clicking elsewhere
- Navigation: Up/Down arrows, Page Up/Down

---

## 3. Validation & Error Indicators

### Validation Sources

1. **Parse errors** — `SqlParser` fails to parse → mark error token region
2. **Unknown entity** — Entity name not found in metadata cache
3. **Unknown attribute** — Column name not found for the resolved entity
4. **Type mismatches** — Comparing string column with numeric literal (stretch goal)

### Error Display

**Red background** on error tokens — `Application.Driver.SetAttribute(new Attribute(Color.White, Color.Red))` for tokens flagged as errors. Unambiguous, works in every terminal, doesn't conflict with string literal red (which is red foreground on black background).

### Validator

```csharp
// src/PPDS.Dataverse/Sql/Intellisense/SqlValidator.cs
public class SqlValidator
{
    public SqlValidator(ICachedMetadataProvider metadataProvider) { }

    public async Task<IReadOnlyList<SqlDiagnostic>> ValidateAsync(
        string sql,
        CancellationToken cancellationToken = default);
}

public record SqlDiagnostic(
    int Start,
    int Length,
    SqlDiagnosticSeverity Severity,
    string Message);

public enum SqlDiagnosticSeverity { Error, Warning, Info }
```

---

## 4. Metadata Caching

### ICachedMetadataProvider

```csharp
// src/PPDS.Dataverse/Metadata/ICachedMetadataProvider.cs
public interface ICachedMetadataProvider
{
    Task<IReadOnlyList<EntitySummary>> GetEntitiesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AttributeMetadataDto>> GetAttributesAsync(string entityLogicalName, CancellationToken ct = default);
    Task<EntityRelationshipsDto> GetRelationshipsAsync(string entityLogicalName, CancellationToken ct = default);
    Task PreloadAsync(CancellationToken ct = default);
    void InvalidateAll();
    void InvalidateEntity(string entityLogicalName);
}
```

### CachedMetadataProvider

- Wraps `IMetadataService`
- **Entity list:** Loaded eagerly on `PreloadAsync()` (called on environment connect). Cached indefinitely per session.
- **Attributes per entity:** Lazy-loaded on first access. Cached with 5-minute TTL.
- **Relationships per entity:** Same as attributes — lazy, 5-min TTL.
- **Thread-safe:** `ConcurrentDictionary` for cache storage, `SemaphoreSlim` for load coordination.
- **DI registration:** Singleton per environment (scoped to `ServiceProvider` in `InteractiveSession`).

---

## 5. Resizable Query Editor

### Keyboard Resize

- `Ctrl+Shift+Up` — Shrink editor (min 3 rows)
- `Ctrl+Shift+Down` — Grow editor (max 80% of screen height)
- Size persisted to user settings via `IProfileStore`

### Mouse Drag (Stretch Goal)

- Horizontal splitter bar between query editor and results
- Drag to resize both panels

---

## 6. Application Service

### ISqlLanguageService

Thin wrapper that brokers between the TUI and the engine components:

```csharp
// src/PPDS.Cli/Services/ISqlLanguageService.cs
public interface ISqlLanguageService
{
    IReadOnlyList<SourceToken> Tokenize(string sql);
    Task<IReadOnlyList<SqlCompletion>> GetCompletionsAsync(string sql, int cursorOffset, CancellationToken ct = default);
    Task<IReadOnlyList<SqlDiagnostic>> ValidateAsync(string sql, CancellationToken ct = default);
}
```

This is also the interface that `ppds serve` RPC will expose for VSCode extension consumption later.

---

## Implementation Plan

### Phase 1: Foundation (Syntax Highlighting)

**1.1 — ISourceTokenizer abstraction + SqlSourceTokenizer**
- Create `ISourceTokenizer`, `SourceToken`, `SourceTokenType` in `PPDS.Dataverse/Sql/Intellisense/`
- Implement `SqlSourceTokenizer` wrapping `SqlLexer`
- Map all `SqlTokenType` values to `SourceTokenType`
- Tests: tokenize various SQL strings, verify token types and positions
- **Files:** `src/PPDS.Dataverse/Sql/Intellisense/ISourceTokenizer.cs`, `SqlSourceTokenizer.cs`
- **Tests:** `tests/PPDS.Cli.Tests/Tui/SqlSourceTokenizerTests.cs`

**1.2 — SyntaxHighlightedTextView**
- Create `SyntaxHighlightedTextView : TextView` in `PPDS.Cli/Tui/Views/`
- Override `Redraw(Rect bounds)` for per-token coloring
- Accept `ISourceTokenizer` and color map via constructor
- Handle: multi-line, horizontal scroll, selection overlay, cursor
- **Files:** `src/PPDS.Cli/Tui/Views/SyntaxHighlightedTextView.cs`

**1.3 — SQL color palette in TuiColorPalette**
- Add `SqlSyntax` dictionary mapping `SourceTokenType` → `Attribute`
- SSMS colors: Blue keywords, Magenta functions, Red strings, Green comments, Cyan numbers, Gray operators, White identifiers
- Add `SqlError` attribute (White on Red background)
- **Files:** `src/PPDS.Cli/Tui/Infrastructure/TuiColorPalette.cs`

**1.4 — Wire into SqlQueryScreen**
- Replace `_queryInput = new TextView` with `new SyntaxHighlightedTextView(sqlTokenizer, sqlColorMap)`
- Verify all existing keyboard shortcuts still work
- **Files:** `src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs`

### Phase 2: Metadata Caching

**2.1 — ICachedMetadataProvider + implementation**
- Create interface in `PPDS.Dataverse/Metadata/`
- Implement with `ConcurrentDictionary`, `SemaphoreSlim`, TTL logic
- Entity list: indefinite cache. Attributes/relationships: 5-min TTL.
- Tests: verify caching behavior, TTL expiry, thread safety
- **Files:** `src/PPDS.Dataverse/Metadata/ICachedMetadataProvider.cs`, `CachedMetadataProvider.cs`
- **Tests:** `tests/PPDS.Cli.Tests/Tui/CachedMetadataProviderTests.cs`

**2.2 — DI registration + preload on environment connect**
- Register `CachedMetadataProvider` in `ServiceCollectionExtensions`
- Call `PreloadAsync()` when `InteractiveSession` creates a new `ServiceProvider`
- **Files:** `src/PPDS.Dataverse/DependencyInjection/ServiceCollectionExtensions.cs`, `src/PPDS.Cli/Tui/InteractiveSession.cs`

### Phase 3: Autocomplete Engine

**3.1 — AST-based cursor context detection**
- Create `SqlCursorContext` class that analyzes AST + cursor offset
- Returns: context type (entity, attribute, keyword, operator, none) + in-scope aliases + current entity
- Fallback to lexer-based heuristic when parse fails
- Tests: verify context detection for all clause positions
- **Files:** `src/PPDS.Dataverse/Sql/Intellisense/SqlCursorContext.cs`
- **Tests:** `tests/PPDS.Cli.Tests/Tui/SqlCursorContextTests.cs`

**3.2 — SqlCompletionEngine**
- Takes `ICachedMetadataProvider` for entity/attribute/relationship lookups
- Implements keyword completions (context-filtered groups)
- Implements entity completions (filtered by prefix)
- Implements attribute completions (alias-aware, per-entity)
- Implements relationship-aware JOIN suggestions
- Tests: verify completions for each context type
- **Files:** `src/PPDS.Dataverse/Sql/Intellisense/SqlCompletionEngine.cs`
- **Tests:** `tests/PPDS.Cli.Tests/Tui/SqlCompletionEngineTests.cs`

**3.3 — ISqlLanguageService**
- Application service wrapping tokenizer + completion engine + validator
- Register in DI
- **Files:** `src/PPDS.Cli/Services/ISqlLanguageService.cs`, `SqlLanguageService.cs`

### Phase 4: Autocomplete UI

**4.1 — AutocompletePopup view**
- Floating `ListView` overlay with filtering, navigation, accept/dismiss
- Positioned relative to cursor in the text view
- Icon prefixes: K/T/C/F for kind indication
- Max 8 items visible, scrollable
- **Files:** `src/PPDS.Cli/Tui/Views/AutocompletePopup.cs`

**4.2 — Wire autocomplete into SyntaxHighlightedTextView**
- Add trigger detection (`.`, space after FROM/JOIN, Ctrl+Space)
- On trigger: call `ISqlLanguageService.GetCompletionsAsync()`, show popup
- On accept: insert completion text, close popup
- On dismiss: close popup, return focus to text view
- Update FrameView hint text to include Ctrl+Space
- **Files:** `src/PPDS.Cli/Tui/Views/SyntaxHighlightedTextView.cs`, `src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs`

### Phase 5: Validation

**5.1 — SqlValidator**
- Parse error detection (from `SqlParser` failures)
- Entity validation (table name exists in metadata)
- Attribute validation (column exists for resolved entity)
- Returns `SqlDiagnostic` list with positions
- **Files:** `src/PPDS.Dataverse/Sql/Intellisense/SqlValidator.cs`
- **Tests:** `tests/PPDS.Cli.Tests/Tui/SqlValidatorTests.cs`

**5.2 — Error rendering in SyntaxHighlightedTextView**
- Overlay red background on tokens that intersect diagnostics
- Run validation on text change (debounced — 500ms after last keystroke)
- Show diagnostic message in status bar or tooltip on cursor hover
- **Files:** `src/PPDS.Cli/Tui/Views/SyntaxHighlightedTextView.cs`

### Phase 6: Resizable Editor

**6.1 — Keyboard resize**
- Ctrl+Shift+Up/Down to grow/shrink query editor
- Min 3 rows, max 80% screen height
- Persist preferred size
- **Files:** `src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs`

**6.2 — Mouse drag splitter (stretch)**
- Horizontal splitter bar between editor and results
- Drag to resize
- **Files:** `src/PPDS.Cli/Tui/Views/SplitterView.cs`, `src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs`

---

## Testing Strategy

- All tests: `[Trait("Category", "TuiUnit")]`
- `SqlSourceTokenizer` — pure unit tests, no GUI context needed
- `SqlCursorContext` — pure unit tests against SQL strings
- `SqlCompletionEngine` — unit tests with mock `ICachedMetadataProvider`
- `SqlValidator` — unit tests with mock metadata
- `CachedMetadataProvider` — unit tests with mock `IMetadataService`
- `SyntaxHighlightedTextView` — `CaptureState()` pattern if feasible, otherwise manual verification
- `AutocompletePopup` — manual TUI testing (complex GUI interaction)

## File Summary

### New Files
| File | Layer |
|------|-------|
| `src/PPDS.Dataverse/Sql/Intellisense/ISourceTokenizer.cs` | Abstraction |
| `src/PPDS.Dataverse/Sql/Intellisense/SourceToken.cs` | Model |
| `src/PPDS.Dataverse/Sql/Intellisense/SourceTokenType.cs` | Enum |
| `src/PPDS.Dataverse/Sql/Intellisense/SqlSourceTokenizer.cs` | Implementation |
| `src/PPDS.Dataverse/Sql/Intellisense/SqlCursorContext.cs` | Context detection |
| `src/PPDS.Dataverse/Sql/Intellisense/SqlCompletionEngine.cs` | Completions |
| `src/PPDS.Dataverse/Sql/Intellisense/SqlCompletion.cs` | Model |
| `src/PPDS.Dataverse/Sql/Intellisense/SqlValidator.cs` | Validation |
| `src/PPDS.Dataverse/Sql/Intellisense/SqlDiagnostic.cs` | Model |
| `src/PPDS.Dataverse/Metadata/ICachedMetadataProvider.cs` | Abstraction |
| `src/PPDS.Dataverse/Metadata/CachedMetadataProvider.cs` | Implementation |
| `src/PPDS.Cli/Services/ISqlLanguageService.cs` | App Service |
| `src/PPDS.Cli/Services/SqlLanguageService.cs` | App Service |
| `src/PPDS.Cli/Tui/Views/SyntaxHighlightedTextView.cs` | TUI View |
| `src/PPDS.Cli/Tui/Views/AutocompletePopup.cs` | TUI View |

### Modified Files
| File | Change |
|------|--------|
| `src/PPDS.Cli/Tui/Infrastructure/TuiColorPalette.cs` | Add SQL syntax color map |
| `src/PPDS.Cli/Tui/Screens/SqlQueryScreen.cs` | Use SyntaxHighlightedTextView, wire autocomplete, resizable editor |
| `src/PPDS.Dataverse/DependencyInjection/ServiceCollectionExtensions.cs` | Register CachedMetadataProvider |
| `src/PPDS.Cli/Tui/InteractiveSession.cs` | Preload metadata on connect |
