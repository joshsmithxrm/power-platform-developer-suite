# Extension UX Polish & Bug Fixes Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix 10 UX bugs and polish items across Data Explorer, Solutions Panel, and extension commands before shipping the VS Code extension MVP.

**Architecture:** All changes are surgical — CSS tweaks, message protocol additions, conditional logic. The largest change is wiring FetchXML conversion in Data Explorer (Task 3) and the component type resolution via entity metadata (Task 6). No new panels, no new services.

**Tech Stack:** TypeScript (VS Code extension + webview scripts), CSS, C# (.NET 8 — SolutionService, ExportDialog, RpcMethodHandler)

**Testing:** `npm run ext:test` (Vitest unit), `dotnet test PPDS.sln --filter "Category!=Integration" -v q` (.NET unit)

---

## Chunk 1: Quick Fixes (Tasks 1–5)

These are small, independent, low-risk changes.

### Task 1: Data Explorer — Editor/Results Visual Contrast

**Files:**
- Modify: `src/PPDS.Extension/src/panels/styles/query-panel.css:5-22`

Currently the editor and results are separated by a 1px border only. The editor blends into the results. The resize handle is invisible.

- [ ] **Step 1: Add editor background tint and visible resize handle**

In `query-panel.css`, update the `.editor-container` and `.editor-wrapper` sections:

```css
/* lines 5-22 — replace entire block */
.editor-container {
    flex-shrink: 0;
    border-bottom: 2px solid var(--vscode-panel-border);
    background: var(--vscode-editor-background, var(--vscode-panel-background));
}

.editor-wrapper {
    height: 150px;
    min-height: 120px;
    max-height: 300px;
    overflow: hidden;
    resize: vertical;
    position: relative;
}

/* Visible resize grab handle */
.editor-container::after {
    content: '';
    display: block;
    height: 4px;
    cursor: ns-resize;
    background: transparent;
    transition: background 0.15s;
}
.editor-container:hover::after {
    background: var(--vscode-focusBorder);
}
```

- [ ] **Step 2: Verify visually** — @webview-cdp: Open Data Explorer, take screenshot, confirm editor area has visible separation from results.

- [ ] **Step 3: Commit**
```bash
git add src/PPDS.Extension/src/panels/styles/query-panel.css
git commit -m "fix(extension): improve editor/results visual contrast in Data Explorer"
```

---

### Task 2: Skip JSON Headers Prompt (Extension + TUI)

**Files:**
- Modify: `src/PPDS.Extension/src/panels/QueryPanel.ts:427-455` (extension export flow)
- Modify: `src/PPDS.Cli/Tui/Dialogs/ExportDialog.cs:67-97` (TUI export dialog)

The `includeHeaders` parameter is completely ignored in the JSON serialization path (`RpcMethodHandler.cs:1376-1393`). The prompt is misleading.

- [ ] **Step 1: Extension — skip headers prompt for JSON**

In `QueryPanel.ts`, move the headers prompt inside a conditional that skips it for JSON. Replace lines 447-455:

```typescript
        // Headers toggle — skip for JSON (always keyed objects)
        let includeHeaders = true;
        if (format !== 'json') {
            const headersPick = await vscode.window.showQuickPick([
                { label: 'Include column headers', includeHeaders: true },
                { label: 'Data only (no headers)', includeHeaders: false },
            ], {
                title: 'Column Headers',
                placeHolder: 'Include headers in export?',
            });
            if (!headersPick) return;
            includeHeaders = headersPick.includeHeaders;
        }
```

And update line 458 to use the local variable:
```typescript
        const exportParams = this.buildExportParams(format, includeHeaders);
```

- [ ] **Step 2: TUI — hide checkbox when JSON selected**

In `ExportDialog.cs`, add a `SelectedItemChanged` handler after line 78 to toggle the checkbox visibility:

```csharp
        _formatGroup.SelectedItemChanged += (_, args) =>
        {
            // JSON always exports keyed objects — headers toggle is meaningless
            _includeHeadersCheck.Visible = args.SelectedItem != FormatJson;
        };
```

Also add a constant near the top of the class (with the other format constants):
```csharp
    private const int FormatJson = 2;  // Index in RadioGroup: CSV=0, TSV=1, JSON=2, Clipboard=3
    private const int FormatClipboard = 3;
```

Note: `FormatClipboard` already exists at line 44 as value `3`. If it's already defined, just add `FormatJson`.

- [ ] **Step 3: Run tests**
```bash
npm run ext:test
dotnet test PPDS.sln --filter "Category!=Integration" -v q
```

- [ ] **Step 4: Commit**
```bash
git add src/PPDS.Extension/src/panels/QueryPanel.ts src/PPDS.Cli/Tui/Dialogs/ExportDialog.cs
git commit -m "fix(export): skip misleading headers prompt for JSON export"
```

---

### Task 3: Wire Up FetchXML Toggle Conversion in Data Explorer

**Files:**
- Modify: `src/PPDS.Extension/src/panels/webview/query-panel.ts:294-300` (toggle click handler)
- Modify: `src/PPDS.Extension/src/panels/webview/shared/message-types.ts:13-31` (add convertQuery message)
- Modify: `src/PPDS.Extension/src/panels/QueryPanel.ts:55-200` (add convertQuery handler in constructor)

Currently the Data Explorer's SQL/FetchXML toggle only changes the Monaco syntax highlighting mode — no transpilation. Notebooks wire up actual conversion via `daemon.queryExplain()` (SQL→FetchXML) and `FetchXmlToSqlTranspiler` (FetchXML→SQL). Port this logic.

- [ ] **Step 1: Add message type for conversion**

In `message-types.ts`, add a new message to `QueryPanelWebviewToHost` (after the `cancelQuery` entry at line 29):

```typescript
    | { command: 'convertQuery'; sql: string; fromLanguage: string; toLanguage: string }
```

Add a new host→webview message to `QueryPanelHostToWebview` (after `daemonReconnected` at line 44):

```typescript
    | { command: 'queryConverted'; content: string; language: string }
    | { command: 'conversionFailed'; error: string }
```

- [ ] **Step 2: Wire toggle to send conversion request**

In `query-panel.ts`, replace the toggle handler at lines 294-300:

```typescript
// Language toggle pill — triggers conversion
langToggle.addEventListener('click', (e) => {
    const seg = (e.target as HTMLElement).closest('.lang-seg') as HTMLElement | null;
    if (!seg || seg.classList.contains('active')) return;
    const targetLang = seg.dataset.lang!;
    const content = editor ? editor.getValue().trim() : '';
    if (!content) {
        // Empty editor — just switch mode
        manualOverride = true;
        updateLanguage(targetLang);
        return;
    }
    // Request conversion from host
    vscode.postMessage({
        command: 'convertQuery',
        sql: content,
        fromLanguage: currentLanguage,
        toLanguage: targetLang,
    });
});
```

- [ ] **Step 3: Handle conversion response in webview message handler**

In `query-panel.ts`, add cases in the message handler switch (around line 723):

```typescript
        case 'queryConverted':
            manualOverride = true;
            if (editor) {
                editor.setValue(msg.content);
                updateLanguage(msg.language);
            }
            break;
        case 'conversionFailed':
            // Conversion failed — just toggle the syntax mode anyway
            manualOverride = true;
            updateLanguage(msg.language ?? currentLanguage);
            break;
```

Note: Also add `'queryConverted'` and `'conversionFailed'` to the `assertNever` exhaustive check if present.

- [ ] **Step 4: Implement conversion in host QueryPanel**

In `QueryPanel.ts`, add the `convertQuery` handler inside the constructor's message switch. Add this case alongside the other message handlers (around line 165):

```typescript
                case 'convertQuery': {
                    const { sql, fromLanguage, toLanguage } = message;
                    try {
                        let converted: string;
                        if (toLanguage === 'xml') {
                            // SQL → FetchXML: use daemon's explain endpoint
                            const result = await this.daemon.queryExplain({
                                sql,
                                environmentUrl: this.environmentUrl ?? undefined,
                            });
                            converted = result.plan;
                        } else {
                            // FetchXML → SQL: use client-side transpiler
                            const { FetchXmlToSqlTranspiler } = await import('../utils/fetchXmlToSql.js');
                            const transpiler = new FetchXmlToSqlTranspiler();
                            const result = transpiler.transpile(sql);
                            if (!result.success) {
                                throw new Error(result.error || 'Transpilation failed');
                            }
                            converted = result.sql;
                        }
                        this.panel?.webview.postMessage({
                            command: 'queryConverted',
                            content: converted,
                            language: toLanguage,
                        });
                    } catch (error) {
                        const msg = error instanceof Error ? error.message : String(error);
                        vscode.window.showWarningMessage(`Conversion failed: ${msg}`);
                        this.panel?.webview.postMessage({
                            command: 'conversionFailed',
                            error: msg,
                        });
                    }
                    break;
                }
```

You'll need to add the import for `FetchXmlToSqlTranspiler` at the top of the file or use dynamic import as shown above.

- [ ] **Step 5: Run tests**
```bash
npm run ext:test
```

- [ ] **Step 6: Commit**
```bash
git add src/PPDS.Extension/src/panels/QueryPanel.ts src/PPDS.Extension/src/panels/webview/query-panel.ts src/PPDS.Extension/src/panels/webview/shared/message-types.ts
git commit -m "feat(extension): wire up SQL/FetchXML conversion in Data Explorer toggle"
```

---

### Task 4: Move Filter Bar Above Results Table

**Files:**
- Modify: `src/PPDS.Extension/src/panels/QueryPanel.ts:570-595` (HTML template)

The filter button is an icon-only button at the far right of the toolbar. It's not discoverable. Move the filter bar to sit between the editor and results, and remove the toolbar filter button.

- [ ] **Step 1: Remove filter button from toolbar, ensure filter-bar is between editor and results**

In `QueryPanel.ts` `getHtmlContent()`, remove the filter button from the toolbar (line 572-574) and verify the filter-bar div (lines 591-595) is already positioned between the editor-container and results-wrapper. It already is — the HTML layout is:

```
toolbar → reconnect-banner → editor-container → filter-bar → results-wrapper
```

So the filter-bar is already in the right position. The issue is that the filter button that toggles it is hidden up in the toolbar. Instead of a toggle button, make the filter bar **always visible** when there are results.

In `QueryPanel.ts`, remove lines 572-574 (the filter button):
```html
    <!-- DELETE these 3 lines -->
    <vscode-button id="filter-btn" appearance="icon" title="Filter results (/)">
        <span class="codicon codicon-filter"></span>
    </vscode-button>
```

- [ ] **Step 2: Make filter bar always visible after query results**

In `query-panel.ts`, update the `handleQueryResult` area. Find where `queryResult` is handled (around line 724) and after rendering results, show the filter bar:

```typescript
// After renderTable is called in handleQueryResult:
filterBar.classList.add('visible');
```

Also update `hideFilter()` (line 696) — instead of hiding the bar, just clear the input:

```typescript
function hideFilter(): void {
    filterInput.value = '';
    renderTable(allRows);
    filterCount.textContent = '';
}
```

Update the `/` keyboard shortcut (around line 680) to focus the filter input instead of toggling visibility:

```typescript
case '/':
    e.preventDefault();
    filterInput.focus();
    break;
```

Remove the filterBtn click handler (line 687) since the button no longer exists. Keep the Escape key handler to blur/clear the filter input.

- [ ] **Step 3: Run tests**
```bash
npm run ext:test
```

- [ ] **Step 4: Commit**
```bash
git add src/PPDS.Extension/src/panels/QueryPanel.ts src/PPDS.Extension/src/panels/webview/query-panel.ts
git commit -m "fix(extension): move filter bar above results table for discoverability"
```

---

### Task 5: Fix EXPLAIN Casing in Overflow Menu

**Files:**
- Modify: `src/PPDS.Extension/src/panels/webview/query-panel.ts:494`

- [ ] **Step 1: Change label to title case**

In `query-panel.ts` line 494, change:
```typescript
        { label: 'EXPLAIN', action: 'explain' },
```
to:
```typescript
        { label: 'Explain Query', action: 'explain' },
```

- [ ] **Step 2: Commit**
```bash
git add src/PPDS.Extension/src/panels/webview/query-panel.ts
git commit -m "fix(extension): use title case for Explain Query menu item"
```

---

## Chunk 2: Backend & Infrastructure (Tasks 6–8)

### Task 6: Resolve Custom Component Types via Entity Metadata

**Files:**
- Modify: `src/PPDS.Dataverse/Services/SolutionService.cs:21-27,138-148,330-339,465-500`

The Dataverse `componenttype` option set only goes up to 432. Types >= 10000 are ObjectTypeCodes of Dataverse entities. We can resolve them by querying the already-cached entity list.

**Tested against DEV environment:** 24 of 26 screenshot component types resolved via `EntityDefinitions` ObjectTypeCode lookup. Zero additional API calls needed — entities are already cached by `ICachedMetadataProvider`.

- [ ] **Step 1: Add ICachedMetadataProvider to SolutionService constructor**

In `SolutionService.cs`, add the dependency:

```csharp
// Line 25 — add new field
private readonly ICachedMetadataProvider _cachedMetadata;

// Lines 138-148 — update constructor signature and body
public SolutionService(
    IDataverseConnectionPool pool,
    ILogger<SolutionService> logger,
    IMetadataService metadataService,
    IComponentNameResolver nameResolver,
    ICachedMetadataProvider cachedMetadata)
{
    _pool = pool ?? throw new ArgumentNullException(nameof(pool));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
    _nameResolver = nameResolver ?? throw new ArgumentNullException(nameof(nameResolver));
    _cachedMetadata = cachedMetadata ?? throw new ArgumentNullException(nameof(cachedMetadata));
}
```

- [ ] **Step 2: Add entity ObjectTypeCode resolution to GetComponentTypeNamesAsync**

Replace `GetComponentTypeNamesAsync` (lines 472-500) with:

```csharp
    private async Task<Dictionary<int, string>> GetComponentTypeNamesAsync(
        string envUrl,
        CancellationToken cancellationToken)
    {
        var cacheKey = envUrl.TrimEnd('/').ToLowerInvariant();

        if (_componentTypeCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var dict = new Dictionary<int, string>();

        // Tier 1: componenttype global option set (covers types 1-432)
        try
        {
            _logger.LogDebug("Fetching componenttype option set metadata for cache key: {EnvUrl}", cacheKey);
            var optionSet = await _metadataService.GetOptionSetAsync("componenttype", cancellationToken);
            foreach (var option in optionSet.Options)
            {
                dict[option.Value] = option.Label;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch componenttype metadata, using hardcoded dictionary as base");
            foreach (var kvp in ComponentTypeNames)
            {
                dict[kvp.Key] = kvp.Value;
            }
        }

        // Tier 1.5: Resolve custom types (>= 10000) via entity ObjectTypeCode lookup.
        // These types correspond to Dataverse entity ObjectTypeCodes.
        // The cached entity list is already populated — zero additional API calls.
        try
        {
            var entities = await _cachedMetadata.GetEntitiesAsync(cancellationToken);
            foreach (var entity in entities)
            {
                if (entity.ObjectTypeCode >= 10000 && !dict.ContainsKey(entity.ObjectTypeCode))
                {
                    var label = !string.IsNullOrWhiteSpace(entity.DisplayName)
                        ? entity.DisplayName
                        : entity.SchemaName ?? entity.LogicalName;
                    dict[entity.ObjectTypeCode] = label;
                }
            }
            _logger.LogDebug("Resolved {Count} custom component types via entity ObjectTypeCode lookup",
                entities.Count(e => e.ObjectTypeCode >= 10000));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve custom component types via entity metadata");
        }

        _componentTypeCache.TryAdd(cacheKey, dict);
        return dict;
    }
```

- [ ] **Step 3: Update fallback label for truly unresolvable types**

In `GetComponentsAsync`, update the fallback label at lines 337-339. Change `"Custom Component ({type})"` to `"Unknown ({type})"`:

```csharp
                    : type >= 10000
                        ? $"Unknown ({type})"
                        : $"Component Type {type}";
```

- [ ] **Step 4: Verify DI registration includes ICachedMetadataProvider**

Check the DI container wiring (likely in `ServiceCollectionExtensions.cs` or wherever `SolutionService` is registered). `ICachedMetadataProvider` should already be registered since `ComponentNameResolver` uses it. If not, register it.

```bash
# Search for existing registration
grep -r "ICachedMetadataProvider" src/PPDS.Dataverse/ src/PPDS.Cli/
```

- [ ] **Step 5: Run tests**
```bash
dotnet test PPDS.sln --filter "Category!=Integration" -v q
```

Fix any test compilation failures from the updated constructor signature (add mock `ICachedMetadataProvider` parameter to test constructors).

- [ ] **Step 6: Commit**
```bash
git add src/PPDS.Dataverse/Services/SolutionService.cs
git commit -m "feat(solutions): resolve custom component types via entity ObjectTypeCode metadata"
```

---

### Task 7: Extract Shared FilterBar Utility

**Files:**
- Create: `src/PPDS.Extension/src/panels/webview/shared/filter-bar.ts`
- Modify: `src/PPDS.Extension/src/panels/webview/query-panel.ts:686-718` (use shared filter)
- Modify: `src/PPDS.Extension/src/panels/webview/solutions-panel.ts:44-85` (use shared filter)

Both panels implement client-side text filtering independently. Extract to shared utility.

- [ ] **Step 1: Create shared filter-bar utility**

Create `src/PPDS.Extension/src/panels/webview/shared/filter-bar.ts`:

```typescript
/**
 * Shared filter bar utility for webview panels.
 * Provides debounced text filtering with result count display.
 */

export interface FilterBarOptions<T> {
    /** The text input element */
    input: HTMLInputElement;
    /** The count display element */
    countEl: HTMLElement;
    /** Filter bar container (for visibility toggling, optional) */
    container?: HTMLElement;
    /** Debounce delay in ms (default: 150) */
    debounceMs?: number;
    /** Extract searchable strings from an item */
    getSearchableText: (item: T) => string[];
    /** Called with filtered results */
    onFilter: (filtered: T[], total: number) => void;
    /** Label for count display (default: 'rows') */
    itemLabel?: string;
}

export class FilterBar<T> {
    private items: T[] = [];
    private debounceTimer: ReturnType<typeof setTimeout> | null = null;
    private readonly opts: Required<Pick<FilterBarOptions<T>, 'debounceMs' | 'itemLabel'>> & FilterBarOptions<T>;

    constructor(options: FilterBarOptions<T>) {
        this.opts = {
            debounceMs: 150,
            itemLabel: 'rows',
            ...options,
        };
        this.opts.input.addEventListener('input', () => this.onInput());
    }

    /** Update the data set and re-apply current filter */
    setItems(items: T[]): void {
        this.items = items;
        this.apply();
    }

    /** Clear the filter input and reset */
    clear(): void {
        this.opts.input.value = '';
        this.opts.countEl.textContent = '';
        this.opts.onFilter(this.items, this.items.length);
    }

    /** Focus the filter input */
    focus(): void {
        this.opts.input.focus();
    }

    private onInput(): void {
        if (this.debounceTimer) clearTimeout(this.debounceTimer);
        this.debounceTimer = setTimeout(() => this.apply(), this.opts.debounceMs);
    }

    private apply(): void {
        const term = this.opts.input.value.toLowerCase();
        if (!term) {
            this.opts.countEl.textContent = '';
            this.opts.onFilter(this.items, this.items.length);
            return;
        }
        const filtered = this.items.filter(item =>
            this.opts.getSearchableText(item).some(text => text.toLowerCase().includes(term))
        );
        this.opts.countEl.textContent = `Showing ${filtered.length} of ${this.items.length} ${this.opts.itemLabel}`;
        this.opts.onFilter(filtered, this.items.length);
    }
}
```

- [ ] **Step 2: Update query-panel.ts to use shared FilterBar**

Replace the inline filter logic (lines 686-718) with:

```typescript
import { FilterBar } from './shared/filter-bar.js';

// ... in initialization section:
const resultsFilter = new FilterBar<Record<string, unknown>>({
    input: filterInput,
    countEl: filterCount,
    getSearchableText: (row) => columns.map(col => {
        const key = col.alias || col.logicalName;
        const val = row[key];
        if (val === null || val === undefined) return '';
        if (typeof val === 'object' && val !== null && 'formatted' in val) {
            return String((val as Record<string, unknown>).formatted || (val as Record<string, unknown>).value || '');
        }
        return String(val);
    }),
    onFilter: (filtered) => renderTable(filtered),
    itemLabel: 'rows',
});

// After handleQueryResult populates allRows:
resultsFilter.setItems(allRows);
```

Remove the old `filterInput.addEventListener('input', ...)` block and the `hideFilter`/`toggleFilter` functions (replaced by FilterBar methods).

- [ ] **Step 3: Update solutions-panel.ts to use shared FilterBar**

Replace the inline filter logic (lines 44-85) with usage of the shared `FilterBar` class, parameterized for solutions. The `getSearchableText` callback should extract `friendlyName`, `uniqueName`, `publisherName`.

- [ ] **Step 4: Run tests**
```bash
npm run ext:test
```

- [ ] **Step 5: Commit**
```bash
git add src/PPDS.Extension/src/panels/webview/shared/filter-bar.ts src/PPDS.Extension/src/panels/webview/query-panel.ts src/PPDS.Extension/src/panels/webview/solutions-panel.ts
git commit -m "refactor(extension): extract shared FilterBar utility for webview panels"
```

---

### Task 8: Gate Debug Commands Behind Development Mode

**Files:**
- Modify: `src/PPDS.Extension/package.json:248-266` (add enablement clause)
- Modify: `src/PPDS.Extension/src/extension.ts:331-335` (set context key)

The legacy extension used `context.extensionMode === ExtensionMode.Development` + `enablement` in package.json. Port this pattern.

- [ ] **Step 1: Set development context key in extension.ts**

Near the top of the `activate()` function (after line 52 where `extensionState` is set), add:

```typescript
    // Gate debug commands — only visible when running in dev mode (F5)
    const isDevelopment = context.extensionMode === vscode.ExtensionMode.Development;
    void vscode.commands.executeCommand('setContext', 'ppds.isDevelopment', isDevelopment);
```

- [ ] **Step 2: Add enablement clause to debug commands in package.json**

In `package.json`, add `"enablement": "ppds.isDevelopment"` to each debug command (lines 248-266):

```json
      {
        "command": "ppds.debug.daemonStatus",
        "title": "Daemon Status",
        "category": "PPDS Debug",
        "enablement": "ppds.isDevelopment"
      },
      {
        "command": "ppds.debug.extensionState",
        "title": "Extension State",
        "category": "PPDS Debug",
        "enablement": "ppds.isDevelopment"
      },
      {
        "command": "ppds.debug.treeViewState",
        "title": "Tree View State",
        "category": "PPDS Debug",
        "enablement": "ppds.isDevelopment"
      },
      {
        "command": "ppds.debug.panelState",
        "title": "Panel State",
        "category": "PPDS Debug",
        "enablement": "ppds.isDevelopment"
      }
```

- [ ] **Step 3: Run tests**
```bash
npm run ext:test
```

- [ ] **Step 4: Commit**
```bash
git add src/PPDS.Extension/package.json src/PPDS.Extension/src/extension.ts
git commit -m "fix(extension): gate debug commands behind development mode"
```

---

## Chunk 3: New Commands & Cleanup (Tasks 9–10)

### Task 9: Add "Open Documentation" Command

**Files:**
- Modify: `src/PPDS.Extension/package.json` (add command entry)
- Modify: `src/PPDS.Extension/src/commands/browserCommands.ts` (add handler)

- [ ] **Step 1: Register command in package.json**

Add to the commands array (after the `restartDaemon` entry around line 246):

```json
      {
        "command": "ppds.openDocumentation",
        "title": "Open Documentation",
        "category": "PPDS",
        "icon": "$(book)"
      },
```

- [ ] **Step 2: Implement in browserCommands.ts**

In `registerBrowserCommands()` (after line 116), add:

```typescript
    context.subscriptions.push(
        vscode.commands.registerCommand('ppds.openDocumentation', () => {
            void vscode.env.openExternal(vscode.Uri.parse('https://ppds.dev'));
        })
    );
```

Note: Replace `https://ppds.dev` with the actual ppds-docs URL if different.

- [ ] **Step 3: Commit**
```bash
git add src/PPDS.Extension/package.json src/PPDS.Extension/src/commands/browserCommands.ts
git commit -m "feat(extension): add Open Documentation command"
```

---

### Task 10: Consolidate Maker URL Construction in Solutions Panel

**Files:**
- Modify: `src/PPDS.Extension/src/panels/SolutionsPanel.ts:98-112` (use shared helper)
- Modify: `src/PPDS.Extension/src/commands/browserCommands.ts:8-13` (export helper)

The Solutions panel reimplements Maker URL building. `browserCommands.ts` already has `buildMakerUrl()`.

- [ ] **Step 1: Export buildMakerUrl from browserCommands**

In `browserCommands.ts`, the function at line 8 is already a standalone helper. Verify it's exported (has `export` keyword). If not, add it:

```typescript
export function buildMakerUrl(environmentId: string): string {
    return `https://make.powerapps.com/environments/${environmentId}/solutions`;
}
```

- [ ] **Step 2: Use shared helper in SolutionsPanel**

In `SolutionsPanel.ts`, replace the `openInMaker` handler (lines 98-112) with:

```typescript
                case 'openInMaker': {
                    if (this.environmentId) {
                        const { buildMakerUrl } = await import('../commands/browserCommands.js');
                        let url = buildMakerUrl(this.environmentId);
                        if (message.solutionId) {
                            url = url.replace('/solutions', `/solutions/${message.solutionId}`);
                        }
                        await vscode.env.openExternal(vscode.Uri.parse(url));
                    } else {
                        vscode.window.showInformationMessage('Environment ID not available. Select an environment first.');
                    }
                    break;
                }
```

- [ ] **Step 3: Run tests**
```bash
npm run ext:test
```

- [ ] **Step 4: Commit**
```bash
git add src/PPDS.Extension/src/panels/SolutionsPanel.ts src/PPDS.Extension/src/commands/browserCommands.ts
git commit -m "refactor(extension): consolidate Maker URL construction via shared helper"
```

---

## Final Verification

- [ ] **Run full test suite**
```bash
npm run ext:test
dotnet test PPDS.sln --filter "Category!=Integration" -v q
```

- [ ] **Visual verification** — @webview-cdp: Launch VS Code, open Data Explorer, open Solutions panel. Verify:
  - Editor/results contrast is visible
  - Filter bar is above results
  - FetchXML toggle converts queries
  - "Explain Query" in overflow (not "EXPLAIN")
  - Component types in Solutions show real names (Plugin Package, Custom API, etc.)
  - Debug commands are NOT visible in command palette (production mode)
  - "PPDS: Open Documentation" appears in palette

- [ ] **Final commit with all changes if needed**
