# Create Issue

Create a GitHub issue with automatic project triage in the PPDS ecosystem.

## Usage

`/create-issue [options]`

Examples:
- `/create-issue` - Interactive mode
- `/create-issue --type feature --title "Add X feature"` - Quick mode
- `/create-issue --parent 210` - Create child of epic #210

## Options

| Option | Description |
|--------|-------------|
| `--type` | feature, bug, chore, docs, refactor |
| `--title` | Issue title (imperative, concise) |
| `--priority` | P0-Critical, P1-High, P2-Medium, P3-Low |
| `--size` | XS, S, M, L, XL |
| `--target` | This Week, Next, CLI v1.0.0, Q1 2026 |
| `--parent` | Parent issue number for epic children |
| `--labels` | Comma-separated labels |
| `--repo` | Repository (default: sdk) |

## Process

### 1. Gather Information

If options not provided, ask user for:
- Issue type: `feat`, `fix`, `chore`, `docs`
- Title (concise, imperative)
- Description (problem, solution, context)
- Priority (suggest based on type)
- Size (suggest based on scope)
- Target (suggest based on priority)

### 2. Format Issue Body

```markdown
## Summary

[1-3 sentence description of what this issue addresses]

## Background

[Context, why this matters, related issues]

## Tasks

- [ ] Task 1
- [ ] Task 2
- [ ] Task 3

## Acceptance Criteria

- [ ] Criterion 1
- [ ] Criterion 2

---

Related: [links to related issues if any]
```

### 3. Create Issue

```bash
gh issue create --repo joshsmithxrm/power-platform-developer-suite \
  --title "[type]: [title]" \
  --body "[body]" \
  --label "[labels]"
```

Capture the issue URL from output to extract issue number.

### 4. Add to Project

```bash
gh project item-add 3 --owner joshsmithxrm \
  --url https://github.com/joshsmithxrm/power-platform-developer-suite/issues/{number}
```

Capture the project item ID from response.

### 5. Set Project Fields

Use GraphQL mutations to set all fields:

**Project and Field IDs (hardcoded):**
```bash
PROJECT_ID=PVT_kwHOAGk32c4BLj-0
TYPE_FIELD_ID=PVTSSF_lAHOAGk32c4BLj-0zg7GUbM
PRIORITY_FIELD_ID=PVTSSF_lAHOAGk32c4BLj-0zg7GUbQ
SIZE_FIELD_ID=PVTSSF_lAHOAGk32c4BLj-0zg7GUbU
STATUS_FIELD_ID=PVTSSF_lAHOAGk32c4BLj-0zg7GUaE
TARGET_FIELD_ID=PVTF_lAHOAGk32c4BLj-0zg7GVcU
```

**Option IDs:**
```bash
# Type options
TYPE_FEATURE=926164fe
TYPE_BUG=3bbc2d7f
TYPE_CHORE=48a397b9
TYPE_DOCS=979a58c4
TYPE_REFACTOR=ef097e31

# Priority options
PRIORITY_P0=d88b54f7
PRIORITY_P1=549be3a3
PRIORITY_P2=7cb98b83
PRIORITY_P3=78b4c9e9

# Size options
SIZE_XS=ff10330e
SIZE_S=11435dea
SIZE_M=da30bc48
SIZE_L=00540448
SIZE_XL=ac8ac48e

# Status options
STATUS_TODO=f75ad846
STATUS_IN_PROGRESS=47fc9ee4
STATUS_DONE=98236657
```

**Set single-select fields (Type, Priority, Size, Status):**
```bash
gh api graphql -f query='
  mutation {
    updateProjectV2ItemFieldValue(
      input: {
        projectId: "PVT_kwHOAGk32c4BLj-0"
        itemId: "{item-id}"
        fieldId: "{field-id}"
        value: {
          singleSelectOptionId: "{option-id}"
        }
      }
    ) {
      projectV2Item {
        id
      }
    }
  }
'
```

**Set text field (Target):**
```bash
gh api graphql -f query='
  mutation {
    updateProjectV2ItemFieldValue(
      input: {
        projectId: "PVT_kwHOAGk32c4BLj-0"
        itemId: "{item-id}"
        fieldId: "PVTF_lAHOAGk32c4BLj-0zg7GVcU"
        value: {
          text: "{target-value}"
        }
      }
    ) {
      projectV2Item {
        id
      }
    }
  }
'
```

### 6. Link Parent Issue (if specified)

If `--parent` is provided, add comment linking to parent:

```bash
gh issue comment {number} --body "Child of #{parent-number}"
gh issue comment {parent-number} --body "Child issue: #{number}"
```

### 7. Report Success

```markdown
## Issue Created

Created: https://github.com/joshsmithxrm/power-platform-developer-suite/issues/{number}

Project fields set:
- Type: feature
- Priority: P2-Medium
- Size: M
- Status: Todo
- Target: CLI v1.0.0

Parent: #210 (linked)

Next steps:
- Move to In Progress when starting work
- Create branch: `git checkout -b feature/{short-name}`
- Reference issue in commits: "feat: add X (#number)"
```

## Repository Mapping

| Shorthand | Full Repo Name |
|-----------|----------------|
| `ppds` | `joshsmithxrm/power-platform-developer-suite` |
| `tools` | `joshsmithxrm/ppds-tools` |
| `alm` | `joshsmithxrm/ppds-alm` |
| `demo` | `joshsmithxrm/ppds-demo` |

## Labels by Type

| Type | Labels to Add |
|------|---------------|
| Feature | `enhancement` |
| Bug | `bug` |
| Chore | - |
| Docs | `documentation` |
| Refactor | - |

## Suggested Field Values

When user doesn't specify, suggest based on context:

| Type | Default Priority | Default Size |
|------|-----------------|--------------|
| bug | P1-High | S |
| feature | P2-Medium | M |
| chore | P3-Low | S |
| docs | P3-Low | XS |
| refactor | P2-Medium | M |

## Edge Cases

| Scenario | Handling |
|----------|----------|
| Parent issue not found | Warn but continue without linking |
| Project add fails | Show error, issue still created |
| Field update fails | Show partial success with what failed |
| Invalid type/priority | Show valid options, ask again |
| Duplicate title | Warn, ask to confirm or modify |

## Example Session

```
User: /create-issue

Claude: What type of issue?
- feat (new feature)
- fix (bug fix)
- chore (maintenance)
- docs (documentation)

User: feat

Claude: Title? (imperative, concise)

User: Add query result export to CSV

Claude: Description? (problem, solution, context)

User: The query command should support exporting results to CSV format for data analysis

Claude: Suggested triage values:
- Priority: P2-Medium (useful feature)
- Size: S (straightforward implementation)
- Target: CLI v1.0.0 (enhances query functionality)

Create with these values? (yes/modify)

User: yes

Claude: Creating issue...

Created: https://github.com/joshsmithxrm/power-platform-developer-suite/issues/245

Project fields set:
✓ Type: feature
✓ Priority: P2-Medium
✓ Size: S
✓ Status: Todo
✓ Target: CLI v1.0.0

Ready for development!
```

## Related

- **/triage**: Batch triage existing issues
- **/plan-work**: Create worktrees for issues
- **ROADMAP.md**: Field definitions and guidelines
