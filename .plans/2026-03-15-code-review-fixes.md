# Code Review Fixes Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix 3 CRITICAL and 5 IMPORTANT findings from the impartial code review of the `feature/vscode-extension-mvp` branch.

**Architecture:** All fixes are isolated, low-risk changes — 1-line path fixes, string corrections, additive CI steps, and a small memory leak fix. No architectural changes.

**Tech Stack:** TypeScript (VS Code extension), GitHub Actions YAML

---

## Chunk 1: Critical Path Fixes and Bug Fixes

### Task 1: Fix SPN case-sensitivity bug in profileCommands.ts

**Files:**
- Modify: `src/PPDS.Extension/src/commands/profileCommands.ts:398`

- [ ] **Step 1: Fix the casing**

Change the camelCase array values to match the PascalCase `authMethodId` values defined at lines 357-378:

```typescript
// Line 398 — change from:
const isSPN = ['clientSecret', 'certificateFile', 'certificateStore'].includes(
    selectedMethod.authMethodId,
);

// To:
const isSPN = ['ClientSecret', 'CertificateFile', 'CertificateStore'].includes(
    selectedMethod.authMethodId,
);
```

- [ ] **Step 2: Verify typecheck passes**

Run: `cd src/PPDS.Extension && npx tsc --noEmit`
Expected: No errors

- [ ] **Step 3: Commit**

```bash
git add src/PPDS.Extension/src/commands/profileCommands.ts
git commit -m "fix(extension): correct SPN case-sensitivity in profile create wizard"
```

---

### Task 2: Fix bundle-cli.js path after directory move

**Files:**
- Modify: `src/PPDS.Extension/scripts/bundle-cli.js:17`

- [ ] **Step 1: Fix the relative path**

`EXTENSION_DIR` is `src/PPDS.Extension`. Going up one `..` yields `src/`. The CLI project is at `src/PPDS.Cli/PPDS.Cli.csproj`, so the path from `src/` should NOT include another `src/` segment.

```javascript
// Line 17 — change from:
const CLI_PROJECT = join(EXTENSION_DIR, '..', 'src', 'PPDS.Cli', 'PPDS.Cli.csproj');

// To:
const CLI_PROJECT = join(EXTENSION_DIR, '..', 'PPDS.Cli', 'PPDS.Cli.csproj');
```

- [ ] **Step 2: Verify the path resolves correctly**

Run: `cd src/PPDS.Extension && node -e "const{join}=require('path');console.log(join(__dirname,'..','PPDS.Cli','PPDS.Cli.csproj'))"`
Expected: Path ending in `src/PPDS.Cli/PPDS.Cli.csproj`

- [ ] **Step 3: Commit**

```bash
git add src/PPDS.Extension/scripts/bundle-cli.js
git commit -m "fix(extension): correct CLI project path in bundle-cli.js after directory move"
```

---

### Task 3: Fix webview-cdp.mjs repo root resolution

**Files:**
- Modify: `src/PPDS.Extension/tools/webview-cdp.mjs:512`

- [ ] **Step 1: Fix the repo root path**

`extDir` is `src/PPDS.Extension`. One `..` yields `src/`, but we need the repo root (two levels up).

```javascript
// Line 512 — change from:
const repoRoot = resolve(extDir, '..');

// To:
const repoRoot = resolve(extDir, '..', '..');
```

- [ ] **Step 2: Commit**

```bash
git add src/PPDS.Extension/tools/webview-cdp.mjs
git commit -m "fix(extension): correct repo root resolution in webview-cdp.mjs after directory move"
```

---

### Task 4: Fix ExplainDocumentProvider memory leak

**Files:**
- Modify: `src/PPDS.Extension/src/providers/explainDocumentProvider.ts`

- [ ] **Step 1: Add document close listener to clean up the contents map**

The `contents` Map grows without bound because entries are never removed. Add a listener for document close events that cleans up entries with the matching scheme.

```typescript
import * as vscode from 'vscode';

/**
 * Virtual document provider for EXPLAIN output.
 * Documents are read-only and close without a save prompt.
 * URI format: ppds-explain:{counter}.{ext}
 */
export class ExplainDocumentProvider implements vscode.TextDocumentContentProvider {
    static readonly scheme = 'ppds-explain';
    static instance: ExplainDocumentProvider | undefined;

    private readonly _onDidChange = new vscode.EventEmitter<vscode.Uri>();
    readonly onDidChange = this._onDidChange.event;

    private readonly contents = new Map<string, string>();
    private counter = 0;
    private readonly _closeListener: vscode.Disposable;

    constructor() {
        ExplainDocumentProvider.instance = this;
        this._closeListener = vscode.workspace.onDidCloseTextDocument((doc) => {
            if (doc.uri.scheme === ExplainDocumentProvider.scheme) {
                this.contents.delete(doc.uri.toString());
            }
        });
    }

    provideTextDocumentContent(uri: vscode.Uri): string {
        return this.contents.get(uri.toString()) ?? '';
    }

    /**
     * Creates a new virtual document with the given content and opens it.
     * Returns the document URI.
     */
    async show(content: string, languageId: string): Promise<void> {
        const ext = languageId === 'xml' ? 'xml' : 'txt';
        const uri = vscode.Uri.parse(`${ExplainDocumentProvider.scheme}:Execution Plan ${++this.counter}.${ext}`);
        this.contents.set(uri.toString(), content);
        this._onDidChange.fire(uri);
        const doc = await vscode.workspace.openTextDocument(uri);
        await vscode.languages.setTextDocumentLanguage(doc, languageId);
        await vscode.window.showTextDocument(doc, { viewColumn: vscode.ViewColumn.Beside, preview: true });
    }

    dispose(): void {
        this._closeListener.dispose();
        this._onDidChange.dispose();
    }
}
```

- [ ] **Step 2: Verify typecheck passes**

Run: `cd src/PPDS.Extension && npx tsc --noEmit`
Expected: No errors

- [ ] **Step 3: Commit**

```bash
git add src/PPDS.Extension/src/providers/explainDocumentProvider.ts
git commit -m "fix(extension): clean up ExplainDocumentProvider contents on document close"
```

---

## Chunk 2: CI/CD and Build Infrastructure Fixes

### Task 5: Fix build.yml — remove unreliable push filter and add test step

**Files:**
- Modify: `.github/workflows/build.yml:108-132`

- [ ] **Step 1: Remove the unreliable `if` condition and add test step**

The `if` condition on the extension job uses `contains(github.event.head_commit.modified, ...)` which only checks the HEAD commit of a push, missing files changed in earlier commits. The workflow-level `paths` filter already handles change detection correctly.

Also add `npm test` after the build step so PR test failures are caught in CI, not just at publish time.

Replace lines 105-132:

```yaml
  extension:
    runs-on: ubuntu-latest

    steps:
      - uses: actions/checkout@v6

      - name: Setup Node.js
        uses: actions/setup-node@v6
        with:
          node-version: '20'
          cache: 'npm'
          cache-dependency-path: src/PPDS.Extension/package-lock.json

      - name: Install dependencies
        working-directory: src/PPDS.Extension
        run: npm ci

      - name: Lint
        working-directory: src/PPDS.Extension
        run: npm run lint

      - name: Build
        working-directory: src/PPDS.Extension
        run: npm run compile

      - name: Test
        working-directory: src/PPDS.Extension
        run: npm test
```

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/build.yml
git commit -m "fix(ci): remove unreliable push filter and add extension test step to build workflow"
```

---

### Task 6: Add missing .vscodeignore exclusions

**Files:**
- Modify: `src/PPDS.Extension/.vscodeignore`

- [ ] **Step 1: Add exclusions for dev-only directories and config files**

Add after the `scripts/**` line (line 108):

```
dev/**
tools/**
knip.json
.stylelintrc.json
```

- [ ] **Step 2: Commit**

```bash
git add src/PPDS.Extension/.vscodeignore
git commit -m "fix(extension): exclude dev/, tools/, and config files from VSIX package"
```

---

### Task 7: Fix stale path comment in webview-cdp.test.mjs

**Files:**
- Modify: `src/PPDS.Extension/tools/webview-cdp.test.mjs:1`

- [ ] **Step 1: Update the comment**

```javascript
// Line 1 — change from:
// extension/tools/webview-cdp.test.mjs

// To:
// src/PPDS.Extension/tools/webview-cdp.test.mjs
```

- [ ] **Step 2: Commit**

```bash
git add src/PPDS.Extension/tools/webview-cdp.test.mjs
git commit -m "fix(extension): update stale path comment in webview-cdp.test.mjs"
```

---

### Task 8: Run gates to verify all fixes

- [ ] **Step 1: Run typecheck**

Run: `cd src/PPDS.Extension && npx tsc --noEmit`
Expected: No errors

- [ ] **Step 2: Run extension unit tests**

Run: `cd src/PPDS.Extension && npm test`
Expected: All tests pass

- [ ] **Step 3: Run lint**

Run: `cd src/PPDS.Extension && npm run lint`
Expected: No errors
