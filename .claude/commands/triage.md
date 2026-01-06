# Triage GitHub Issues

Systematically review and categorize GitHub issues for the PPDS Roadmap project.

## Usage

`/triage [options] [issue-numbers...]`

Examples:
- `/triage` - Triage all untriaged open issues (up to 50)
- `/triage --state all` - Include closed issues
- `/triage --limit 20` - Only first 20 issues
- `/triage 224 223 222` - Triage specific issues

## Process

### 1. Fetch Project Metadata

Fetch project field IDs and option IDs once per session (hardcoded for performance):

**Note:** These IDs can change if project fields are modified. To update them, use:
```bash
# Get project ID and field IDs
gh api graphql -f query='query { node(id: "PVT_kwHOAGk32c4BLj-0") { ... on ProjectV2 { fields(first: 20) { nodes { ... on ProjectV2Field { id name } ... on ProjectV2SingleSelectField { id name options { id name } } } } } } }'
```

```bash
PROJECT_NUMBER=3
PROJECT_ID=PVT_kwHOAGk32c4BLj-0
REPO_OWNER=joshsmithxrm
REPO_NAME=ppds-sdk

# Field IDs
TYPE_FIELD_ID=PVTSSF_lAHOAGk32c4BLj-0zg7GUbM
PRIORITY_FIELD_ID=PVTSSF_lAHOAGk32c4BLj-0zg7GUbQ
SIZE_FIELD_ID=PVTSSF_lAHOAGk32c4BLj-0zg7GUbU
STATUS_FIELD_ID=PVTSSF_lAHOAGk32c4BLj-0zg7GUaE
TARGET_FIELD_ID=PVTF_lAHOAGk32c4BLj-0zg7GVcU

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

### 2. Fetch Issues and Project Items

**Fetch all issues:**
```bash
gh issue list --repo joshsmithxrm/ppds-sdk \
  --limit 1000 \
  --state ${STATE:-open} \
  --json number,title,state,url,labels,createdAt
```

**Fetch project items with field values:**

Use GraphQL API to get project items and their field values. This is complex - need to:
1. Query project items
2. Extract field values for Type, Priority, Size, Status, Target
3. Cross-reference with issue list

```bash
gh api graphql --paginate -f query='
  query($endCursor: String) {
    node(id: "PVT_kwHOAGk32c4BLj-0") {
      ... on ProjectV2 {
        items(first: 100, after: $endCursor) {
          pageInfo {
            hasNextPage
            endCursor
          }
          nodes {
            id
            content {
              ... on Issue {
                number
              }
            }
            fieldValues(first: 20) {
              nodes {
                ... on ProjectV2ItemFieldSingleSelectValue {
                  field {
                    ... on ProjectV2SingleSelectField {
                      name
                    }
                  }
                  name
                }
                ... on ProjectV2ItemFieldTextValue {
                  field {
                    ... on ProjectV2Field {
                      name
                    }
                  }
                  text
                }
              }
            }
          }
        }
      }
    }
  }
'
```

### 3. Identify Untriaged Issues

An issue needs triage if:
- NOT in project yet, OR
- In project but missing ANY of: Type, Priority, Size, Status, Target

Filter issues to those needing triage.

### 4. Present Summary Table

Show summary of issues needing triage:

```markdown
## Issues Needing Triage

Found 15 open issues (10 in project, 5 not in project)

### Not in Project (5)

| Issue | Title | Labels | Created |
|-------|-------|--------|---------|
| #224 | feat(security): PII detection and handling | enhancement, epic:data-migration | 2026-01-06 |
| #223 | feat(security): Data masking | enhancement, epic:data-migration | 2026-01-06 |

### In Project - Missing Fields (10)

| Issue | Title | Type | Priority | Size | Status | Target | Labels |
|-------|-------|------|----------|------|--------|--------|--------|
| #220 | Field transformation | ✓ | ✓ | - | ✓ | - | enhancement, epic:data-migration |
| #210 | Enterprise Data Migration Platform | - | - | - | - | - | epic:data-migration |

Legend: ✓ = has value, - = missing
```

**STOP HERE - Ask user if they want to proceed with triage**

### 5. Generate Triage Template

If user confirms, generate an editable markdown table:

```markdown
## Triage Input

Instructions:
1. Fill in missing fields for each issue
2. Valid values shown below
3. Leave cell empty to skip that field
4. Delete rows you don't want to triage

| Issue | Type | Priority | Size | Status | Target | Parent Issue | Suggested Area |
|-------|------|----------|------|--------|--------|--------------|----------------|
| #224 | feature | | | Todo | | #210 | area:data |
| #223 | feature | | | Todo | | #210 | area:data |
| #220 | | | M | | Next | | area:data |

Valid values:
- Type: feature, bug, chore, docs, refactor
- Priority: P0-Critical, P1-High, P2-Medium, P3-Low
- Size: XS, S, M, L, XL
- Status: Todo, In Progress, Done
- Target: This Week, Next, Q1 2026, CLI v1.0.0, Blocked, or (empty)
- Parent Issue: Issue number (e.g., #210) to link to epic/parent, or (empty)
- Suggested Area: Suggestions based on title/labels (apply manually if desired)

Paste the completed table when ready.
```

**Label suggestions logic:**
- Title contains "auth" or "authentication" → suggest `area:auth`
- Title contains "data" or "import" or "export" or "migration" → suggest `area:data`
- Title contains "plugin" → suggest `area:plugins`
- Title contains "cli" or "command" → suggest `area:cli`
- Title contains "tui" or "interactive" → suggest `area:tui`
- Title contains "pool" or "connection" → suggest `area:pooling`
- Title contains "daemon" or "serve" → suggest `area:daemon`
- Has label `epic:*` or `phase:*` → suggest as parent issue candidate

### 6. Parse and Validate Input

When user pastes completed table:

1. Parse markdown table
2. Validate each field value:
   - Type: must be one of: feature, bug, chore, docs, refactor
   - Priority: must be one of: P0-Critical, P1-High, P2-Medium, P3-Low
   - Size: must be one of: XS, S, M, L, XL
   - Status: must be one of: Todo, In Progress, Done
   - Target: any text value or empty

3. Map to option IDs:
   - feature → 926164fe
   - bug → 3bbc2d7f
   - chore → 48a397b9
   - docs → 979a58c4
   - refactor → ef097e31
   - P0-Critical → d88b54f7
   - P1-High → 549be3a3
   - P2-Medium → 7cb98b83
   - P3-Low → 78b4c9e9
   - XS → ff10330e
   - S → 11435dea
   - M → da30bc48
   - L → 00540448
   - XL → ac8ac48e
   - Todo → f75ad846
   - In Progress → 47fc9ee4
   - Done → 98236657

4. Show validation errors if any

### 7. Show Confirmation Summary

Present summary of changes:

```markdown
## Triage Summary

Will update 3 issues:

| Issue | Actions |
|-------|---------|
| #224 | Add to project, set Type=feature, Priority=P2-Medium, Size=M, Status=Todo |
| #223 | Add to project, set Type=feature, Priority=P3-Low, Size=L, Status=Todo |
| #220 | Update Size=M, Target=Next (already in project) |

Proceed with these changes? (yes/no)
```

**Best Practice Reminder:**
```
💡 Tip: When you move an issue to Status=In Progress, remember to assign yourself!
```

### 8. Execute Updates

For each issue:

**If not in project:**
```bash
gh project item-add 3 --owner joshsmithxrm --url https://github.com/joshsmithxrm/ppds-sdk/issues/{number}
```

**Update fields via GraphQL API:**

For single-select fields (Type, Priority, Size, Status):
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

For text fields (Target):
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

**Important:** Need to get the project item ID first. When adding to project, the response includes the item ID.

### 9. Report Results

```markdown
## Triage Complete

Successfully triaged 3 issues:

✓ #224 - Added to project, set 4 fields
✓ #223 - Added to project, set 4 fields
✓ #220 - Updated 2 fields

View in project: https://github.com/users/joshsmithxrm/projects/3

Next steps:
- Apply suggested area labels manually if desired
- Link epic children to parent issues via Parent Issue field
- When you start work, move Status to In Progress and assign yourself
```

## Edge Cases

| Scenario | Handling |
|----------|----------|
| Issue not found | Skip with warning "Issue #999 not found" |
| Issue already fully triaged | Don't include in summary table |
| Invalid field value | Show error with valid options, ask to re-enter |
| API rate limit | Show error with retry suggestion |
| Project not found | Fatal error with helpful message |
| No issues need triage | "All issues are fully triaged! 🎉" |
| User cancels at confirmation | "Triage cancelled, no changes made" |
| Partial success | Report which succeeded and which failed |
| Network error | Show error, suggest retry |

## Arguments

```
Usage: /triage [options] [issue-numbers...]

Options:
  --state <open|closed|all>   Filter by issue state (default: open)
  --limit <number>            Max issues to check (default: 50)

Examples:
  /triage                     # Triage up to 50 open issues
  /triage --state all         # Include closed issues
  /triage 224 223 222         # Triage specific issues
  /triage --limit 20          # First 20 issues
```

## Implementation Notes

**Key challenges:**
1. **GraphQL complexity** - Projects V2 uses GraphQL, not REST API
2. **Item ID lookup** - Need to fetch item ID before updating fields
3. **Pagination** - Handle 100+ items
4. **Field value extraction** - Parse nested GraphQL response

**Performance:**
- Hardcode field/option IDs (don't query metadata each time)
- Batch issue fetches
- Use --paginate for large projects

**User experience:**
- Show progress for batch operations
- Validate before making changes
- Clear error messages with resolution steps
- Link to ROADMAP.md for field definitions

## When to Use

- After new issues are created
- When issues are missing triage data
- Before sprint/milestone planning
- To clean up backlog
- When priorities shift and need re-evaluation

## Related

- **ROADMAP.md**: Field definitions, sizing guidelines, priority criteria
- **Project**: https://github.com/users/joshsmithxrm/projects/3
