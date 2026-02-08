# Power Platform Developer Suite - VS Code Extension

VS Code extension for Power Platform development. Communicates with the `ppds` CLI via JSON-RPC for all operations.

## Development

```bash
cd extension
npm install
npm run compile
```

## Publishing

The extension is published to the VS Code Marketplace under:
- **Publisher:** JoshSmithXRM
- **Extension ID:** JoshSmithXRM.power-platform-developer-suite

To publish a new version:
1. Update version in `package.json`
2. Run `vsce publish`

## Architecture

The extension is a thin UI layer that delegates to `ppds serve` for all operations:

```
VS Code Extension (TypeScript)
        │
        ▼
   JSON-RPC over stdio
        │
        ▼
   ppds serve (CLI daemon)
        │
        ▼
   Application Services
```

All business logic lives in Application Services, keeping the extension UI-agnostic.
