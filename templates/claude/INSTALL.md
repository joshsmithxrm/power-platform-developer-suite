# Installing PPDS Claude Code Templates

These templates provide Claude Code with PPDS-specific guidance and recommended permission settings.

---

## Option 1: User-Wide Installation (Recommended)

Copy templates to your home directory for use across all projects:

**macOS/Linux:**
```bash
# Create PPDS directory in Claude config
mkdir -p ~/.claude/ppds

# Copy templates (from SDK repo)
cp -r path/to/power-platform-developer-suite/templates/claude/* ~/.claude/ppds/
```

**Windows (PowerShell):**
```powershell
# Create PPDS directory in Claude config
New-Item -ItemType Directory -Force -Path "$env:USERPROFILE\.claude\ppds"

# Copy templates (from SDK repo)
Copy-Item -Path "C:\path\to\power-platform-developer-suite\templates\claude\*" -Destination "$env:USERPROFILE\.claude\ppds" -Recurse
```

Then in any project's `CLAUDE.md`, add:
```markdown
@~/.claude/ppds/CONSUMER_GUIDE.md
```

---

## Option 2: Project-Specific Installation

Copy directly into your project:

```bash
# Copy as a rule file
cp templates/claude/CONSUMER_GUIDE.md .claude/rules/ppds.md

# Copy settings to merge with your settings
cp templates/claude/settings-recommended.json .claude/
```

Then merge the permissions from `settings-recommended.json` into your `.claude/settings.json`.

---

## Option 3: Symlink (Advanced)

Link to SDK repo for automatic updates when you pull new SDK versions:

**macOS/Linux:**
```bash
ln -s /path/to/power-platform-developer-suite/templates/claude/CONSUMER_GUIDE.md .claude/rules/ppds.md
```

**Windows (requires admin):**
```powershell
New-Item -ItemType SymbolicLink -Path ".claude\rules\ppds.md" -Target "C:\path\to\power-platform-developer-suite\templates\claude\CONSUMER_GUIDE.md"
```

---

## File Descriptions

| File | Purpose |
|------|---------|
| `CONSUMER_GUIDE.md` | Best practices for PPDS development |
| `settings-recommended.json` | Permission configuration for PPDS commands |
| `commands/ppds-help.md` | Quick reference slash command |

---

## Verifying Installation

Run `/memory` in Claude Code to see loaded context files.

If using `@~/.claude/ppds/CONSUMER_GUIDE.md`, you should see it listed in the imported files.
