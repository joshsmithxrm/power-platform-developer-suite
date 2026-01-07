# Setup PPDS Ecosystem

Set up PPDS repositories on a new machine or update an existing setup.

## Usage

`/setup-ecosystem`

## What It Does

Interactive wizard to clone PPDS repositories and configure the development environment.

## Flow

### 1. Choose Base Path

Ask user where to put repos:
- Default: `C:\VS\ppds` (Windows) or `~/dev/ppds` (macOS/Linux)
- User can specify any path

```
Where do you want the PPDS repos? [default: C:\VS\ppds]
```

### 2. Select Repositories

Ask which repos to clone (use AskUserQuestion with multiSelect):
- `sdk` - NuGet packages & CLI (core)
- `extension` - VS Code extension
- `tools` - PowerShell module
- `alm` - CI/CD templates
- `demo` - Reference implementation

### 3. VS Code Workspace

Ask if they want a VS Code workspace file created.

### 4. Execute Setup

For each selected repo:
1. Check if folder exists
2. If exists, verify it's a git repo and pull latest
3. If not, clone from GitHub

```bash
# Clone pattern
git clone https://github.com/joshsmithxrm/ppds-sdk.git {base}/sdk
git clone https://github.com/joshsmithxrm/power-platform-developer-suite.git {base}/extension
git clone https://github.com/joshsmithxrm/ppds-tools.git {base}/tools
git clone https://github.com/joshsmithxrm/ppds-alm.git {base}/alm
git clone https://github.com/joshsmithxrm/ppds-demo.git {base}/demo
```

### 5. Create Workspace File (if requested)

Generate `{base}/ppds.code-workspace`:

```json
{
    "folders": [
        { "path": "sdk" },
        { "path": "extension" },
        { "path": "tools" },
        { "path": "alm" },
        { "path": "demo" }
    ],
    "settings": {}
}
```

Only include folders that were cloned.

### 6. Recommend Terminal Setup

After setup, suggest running `/setup-terminal` to install the PowerShell profile with the `ppds` function (runs CLI from worktree without global install).

## Idempotent Behavior

| Scenario | Action |
|----------|--------|
| Folder doesn't exist | Clone repo |
| Folder exists, is git repo | `git pull` to update |
| Folder exists, not git repo | Warn and skip |
| Workspace file exists | Ask to overwrite or skip |

## Example Session

```
User: /setup-ecosystem

Claude: Where do you want the PPDS repos?
[Uses AskUserQuestion with text input option]

User: D:\Projects\ppds

Claude: Which repos do you need?
[Uses AskUserQuestion with multiSelect]
- sdk (NuGet packages & CLI)
- extension (VS Code extension)
- tools (PowerShell module)
- alm (CI/CD templates)
- demo (Reference implementation)

User: [selects sdk, extension]

Claude: Create VS Code workspace file?
[Uses AskUserQuestion]

User: Yes

Claude: Setting up D:\Projects\ppds...

Creating directory...
Cloning sdk... done
Cloning extension... done
Creating ppds.code-workspace... done

Setup complete!

To set up terminal helpers, run: /setup-terminal
To open in VS Code: code D:\Projects\ppds\ppds.code-workspace
```

## Repository URLs

| Shorthand | GitHub URL |
|-----------|------------|
| `sdk` | `https://github.com/joshsmithxrm/ppds-sdk.git` |
| `extension` | `https://github.com/joshsmithxrm/power-platform-developer-suite.git` |
| `tools` | `https://github.com/joshsmithxrm/ppds-tools.git` |
| `alm` | `https://github.com/joshsmithxrm/ppds-alm.git` |
| `demo` | `https://github.com/joshsmithxrm/ppds-demo.git` |
