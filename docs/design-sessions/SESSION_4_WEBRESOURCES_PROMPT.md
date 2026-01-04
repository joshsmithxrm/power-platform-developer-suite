# Design Session 4: Web Resources (Advanced)

## Session Prompt

Use this prompt to start the design session:

---

Design session for advanced Web Resources CLI features (`ppds webresources`).

## Context

The extension has a comprehensive Web Resources panel with editing, conflict detection, and publish management. We need CLI parity with some advanced features.

### Extension Features (17 files)
- Browse and edit web resources in VS Code
- Syntax highlighting for JS, CSS, HTML, XML
- **Conflict detection:** On-save diff if server content changed
- **Unpublished detection:** Show pending changes
- VS Code file system provider integration
- Solution component tracking
- Publish/Publish All

### Key Design Challenges
1. **Published vs unpublished:** Query either version (default: published per ADR-0010)
2. **Conflict detection:** Track server hash, warn on push if changed
3. **Large environments:** Default solution has 60K+ resources
4. **Content handling:** Base64 encoded, exclude from list

### Current SDK Status
- No `ppds webresources` command
- Entity: `webresource`

## Deliverables

1. **Published/unpublished query pattern** (`--unpublished` flag)
2. **Conflict detection storage schema** (`.ppds/webresources.json`)
3. **Efficient listing** for large environments (filtering, pagination)
4. **Command structure** with all subcommands
5. **Issue breakdown** for implementation

## Expected Commands

```bash
# List (excludes content field, default: published)
ppds webresources list [--solution <name>]
                       [--type js|css|html|xml|png|jpg|gif|ico|svg|xsl|xslt|resx]
                       [--type text]           # Shorthand for js|css|html|xml
                       [--unpublished]
                       [--name <pattern>]
                       [--top <n>]

# Get single resource (downloads content)
ppds webresources get <name> [--output <path>] [--unpublished]

# Pull multiple to folder
ppds webresources pull <folder> [--solution <name>]
# Tracks hashes in .ppds/webresources.json

# Push (with conflict detection)
ppds webresources push <path> [--solution <name>] [--force]
# If server changed since pull: warn, offer --force

# Diff local vs server
ppds webresources diff <local-path> [--unpublished]

# Publish
ppds webresources publish [--all | --name <name>]

# Maker URL
ppds webresources url
```

## Conflict Detection Schema

`.ppds/webresources.json`:
```json
{
  "version": 1,
  "environment": "https://org.crm.dynamics.com",
  "resources": {
    "new_/scripts/myfile.js": {
      "id": "guid",
      "hash": "sha256:abc123...",
      "pulledAt": "2024-01-04T12:00:00Z",
      "modifiedOn": "2024-01-04T11:00:00Z"
    }
  }
}
```

## Published vs Unpublished Query

Web resources store content in two places:
- `content` - Published version
- `content` with unpublished layer - Draft version

Need to determine the correct Dataverse query pattern for each.

## References

- [ROADMAP.md](../ROADMAP.md) - Phase 4 overview
- [ADR-0010](../adr/0010_PUBLISHED_UNPUBLISHED_DEFAULT.md) - Default to published
- [ADR-0008](../adr/0008_CLI_OUTPUT_ARCHITECTURE.md) - Output architecture
- Extension source: `C:\VS\ppds\extension\src\features\webResources`
