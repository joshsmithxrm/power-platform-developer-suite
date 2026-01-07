# Create Issue

Create a GitHub issue in any PPDS ecosystem repository.

## Usage

`/create-issue [repo]`

Where `[repo]` is one of:
- `extension` - power-platform-developer-suite
- `sdk` - ppds-sdk
- `tools` - ppds-tools
- `alm` - ppds-alm
- `demo` - ppds-demo

## Process

### 1. Gather Information
Ask user for:
- Issue type: `feat`, `fix`, `chore`, `docs`
- Title (concise, imperative)
- Description (problem, solution, context)
- Labels (if known)

### 2. Format Issue

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
gh issue create --repo joshsmithxrm/[repo-name] --title "[type]: [title]" --body "[body]"
```

### 4. Link Cross-Repo Issues

If this issue relates to issues in other repos:
- Add "Related:" links in body
- Comment on related issues with link to new issue

## Repository Mapping

| Shorthand | Full Repo Name |
|-----------|----------------|
| `extension` | `joshsmithxrm/power-platform-developer-suite` |
| `sdk` | `joshsmithxrm/ppds-sdk` |
| `tools` | `joshsmithxrm/ppds-tools` |
| `alm` | `joshsmithxrm/ppds-alm` |
| `demo` | `joshsmithxrm/ppds-demo` |

## Labels by Type

| Type | Labels to Add |
|------|---------------|
| Feature | `enhancement` |
| Bug | `bug` |
| Tech Debt | `tech-debt` |
| Documentation | `documentation` |
| Cross-repo | `cross-repo` |

## Example

```
User: /create-issue sdk

Claude: What type of issue?
- feat (new feature)
- fix (bug fix)
- chore (maintenance)
- docs (documentation)

User: feat

Claude: Title? (imperative, concise)

User: Add interactive plugin registration CLI commands

Claude: Description? (problem, solution, context)

User: [provides description]

Claude: Creating issue...
[Creates issue with gh CLI]

Created: https://github.com/joshsmithxrm/ppds-sdk/issues/70
```
