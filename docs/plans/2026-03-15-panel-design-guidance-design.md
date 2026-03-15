# Panel Design Guidance — Design Spec

**Issue:** #307 (TUI/VS Code Design System)
**Scope:** Enhance the `webview-panels` skill with design guidance for consistent panel development across extension and TUI.

## Problem

The `webview-panels` skill provides excellent technical scaffolding (file layout, message protocols, esbuild, quality gates) but zero design guidance. When a session designs a new panel, there's no instruction on:

- What the standard panel layout looks like
- Which CSS patterns already exist and should be reused
- How environment theming should work in extension panels
- What keyboard shortcuts panels should support
- How to ensure TUI and extension panels expose equivalent functionality

The TUI has a mature color/theming system (`TuiColorPalette.cs`, `TuiThemeService.cs`) but the extension doesn't layer environment awareness beyond the env picker dropdown text.

## Approach

Enhance the existing `webview-panels` skill with a new "Design Guidance" section (~100 lines). This keeps technical structure and design guidance in one place — sessions invoke one skill and get everything.

Additionally, implement the environment theming CSS pattern in `shared.css` and the `data-env-type` attribute in `environmentPicker.ts` so that the guidance references existing code, not aspirational patterns.

## Design Sections

### 1. Panel Anatomy

Document the standard three-zone layout (toolbar → content → status bar) that's defined in `shared.css`. Rules:
- Toolbar always contains environment picker
- Content area is `flex: 1` with own scrolling
- Status bar shows contextual info, defaults to "Ready"
- Use existing empty/error/loading states from `shared.css`

### 2. Reusable CSS Patterns

Catalog of existing patterns with reference panels:

| Pattern | Source | Reference |
|---------|--------|-----------|
| Data table | `query-panel.css` `.results-table` | QueryPanel |
| Tree/list | `solutions-panel.css` `.solution-list` | SolutionsPanel |
| Detail card (standalone) | `solutions-panel.css` `.detail-card` | SolutionsPanel |
| Detail card (inline/nested) | `solutions-panel.css` `.component-detail-card` | SolutionsPanel |
| Filter bar | `query-panel.css` `.filter-bar` | QueryPanel |
| Dropdown menu | `query-panel.css` `.dropdown-menu` | QueryPanel |
| Context menu | `query-panel.css` `.context-menu` | QueryPanel |

Rules: `@import './shared.css'`, copy patterns don't cross-import, use `var(--vscode-*)` tokens only, follow established spacing scale (4/6/8/12/16/40px) and font sizes (11/12/13px).

### 3. Environment Theming

Implement a `data-env-type` attribute on the toolbar element, set when environment is selected. Add CSS rules to `shared.css` for colored top-border accents:

- Production: red (`--vscode-testing-iconFailed`)
- Sandbox: yellow (`--vscode-editorWarning-foreground`)
- Development: green (`--vscode-testing-iconPassed`)
- Test: yellow (`--vscode-editorWarning-foreground`) — same color as Sandbox, separate `data-env-type` value
- Trial: blue (`--vscode-editorInfo-foreground`)
- Unknown/null: no attribute set, no accent border (natural CSS default)

This maps to TUI's five-value `StatusBar_Production/Sandbox/Development/Test/Trial` scheme. The accent is subtle (3px top border) — panels don't get repainted.

Implementation touches:
- `shared.css`: `[data-env-type]` selectors for all five types
- `environmentPicker.ts`: extend `showEnvironmentPicker` return type to include `type` from `EnvironmentInfo.type`, set `data-env-type` attribute on toolbar when env is selected. Remove attribute when type is null/Unknown.
- Host panels: pass environment type from daemon response

### 4. Keyboard Shortcuts

Standard shortcuts all panels support:

| Shortcut | Action |
|----------|--------|
| `Ctrl/Cmd+R` | Refresh |
| `Ctrl/Cmd+F` | Focus filter |
| `Escape` | Close filter / deselect |
| `Ctrl/Cmd+Shift+E` | Export |
| `Ctrl/Cmd+C` | Copy selection |

### 5. TUI Functional Parity Checklist

Before marking a panel complete, verify TUI screen exposes same:
- Data fields, filter/search, sort options
- Export formats, drill-down paths
- Refresh behavior, environment scoping

## Implementation Summary

1. Add ~6 lines of env theming CSS to `shared.css`
2. Update `environmentPicker.ts` to set `data-env-type` attribute on toolbar
3. Add ~100 lines of "Design Guidance" section to `webview-panels` SKILL.md
4. Close issue #307

## Non-Goals

- Shared design tokens file between TUI and extension (different rendering engines, unnecessary abstraction)
- Documenting TUI-specific patterns (TUI's `TuiColorPalette.cs` is self-contained and mature)
- Component-level specs duplicating what's already in the CSS files
- Accessibility/WCAG audit (follow VS Code's built-in theme tokens which handle this)
