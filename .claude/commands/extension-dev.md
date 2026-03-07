# Extension Dev

Immediately build and install the VS Code extension locally for testing.

## Steps — execute these NOW, do not ask questions

1. Find the extension directory. It is `extension/` relative to the repo root or worktree root. Use `git rev-parse --show-toplevel` to find the root.

2. Run automated checks and install:
```bash
cd <root>/extension && npm run lint && npm run compile && npm run test && npm run local
```

3. If all pass, tell the user: "Extension installed. Reload VS Code (Ctrl+Shift+P → Reload Window) and test."

4. If any step fails, show the error and fix it.

## Other commands the user may ask for

| Request | Command |
|---------|---------|
| "revert" / "marketplace" / "go back" | `npm run marketplace` |
| "uninstall" | `npm run uninstall-local` |
| "test release" / "production build" | `npm run test-release` |
| "just tests" / "run tests" | `npm run test` |
| "e2e" / "playwright" | `npm run test:e2e` |
| "watch" | `npm run watch` |

Run these from the same `extension/` directory. Do not ask for confirmation — just run them.
