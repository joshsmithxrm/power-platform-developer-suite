# VS Code Configuration for Extension Development

This folder contains VS Code configuration for **standalone extension development**.

## Two Development Modes

### 1. Monorepo Development (Recommended)

Open the workspace file at the repo root: `ppds.code-workspace`

- Provides unified debugging for both .NET CLI and TypeScript extension
- Launch configs reference `${workspaceFolder:Extension}` for correct paths
- All build/test tasks available in one place
- Compound configs for integration testing (daemon + extension)

### 2. Standalone Extension Development

Open the `extension/` folder directly in VS Code

- Uses these local `.vscode/` configurations
- Focused TypeScript-only environment
- F5 debugging works as expected (uses `${workspaceFolder}` = extension/)

## Configuration Files

| File | Purpose |
|------|---------|
| `launch.json` | Extension debugging configs (F5) |
| `tasks.json` | npm build, watch, lint, package tasks |
| `settings.json` | TypeScript SDK, formatting, exclusions |
| `extensions.json` | Recommended extensions for TypeScript dev |

## When to Use Which

| Scenario | Recommended Approach |
|----------|---------------------|
| Working on extension only | Open `extension/` folder directly |
| Working on CLI + Extension | Open `ppds.code-workspace` |
| Debugging RPC integration | Open `ppds.code-workspace` (use compound config) |
| Testing extension with local CLI | Open `ppds.code-workspace` |

## F5 Launch Configurations

**Available when opening this folder:**

- **Run Extension** - Compile and launch extension host
- **Run Extension (Watch Mode)** - Launch with tsc watch
- **Run Extension (No Build)** - Quick launch without compile
- **Run Extension Tests** - Run extension test suite
- **Run Extension (Open Folder)** - Launch with specific folder
