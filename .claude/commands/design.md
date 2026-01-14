# Design

Start a design conversation for a new feature or significant change.

## Usage

`/design [feature-name]`

Examples:
- `/design plugin-traces` - Design CLI commands for plugin traces
- `/design web-resources` - Design web resource management
- `/design bulk-update` - Design bulk update feature

## Arguments

`$ARGUMENTS` - Feature or epic name (optional, will prompt if not provided)

## Process

### 1. Discovery Phase (MANDATORY)

Before any design work, search for existing context:

**Search for ADRs:**
```bash
find . -path "*/docs/adr/*.md" -type f | head -20
```

**Search for existing patterns in CLAUDE.md:**
```bash
grep -i "<feature-keyword>" CLAUDE.md
```

**Search for related issues:**
```bash
gh issue list --search "<feature-name>" --limit 10
```

**Report findings:**
```
## Discovery Results

### Relevant ADRs
- [ADR-000X](link) - Summary
- (none found)

### Related Patterns in CLAUDE.md
- [Section name] - Summary

### Related Issues
- #N - Title

### Existing Implementations
- [file path] - What it does
```

### 2. Interface Matrix

For features that touch multiple interfaces, generate:

| Interface | In Scope | Implementation |
|-----------|----------|----------------|
| CLI | [x] | `ppds [command]` |
| TUI | [x] | [Panel name] |
| RPC | [ ] | [method] (if extension needs it) |
| MCP | [x] | `ppds_[tool]` (if AI-useful) |
| Extension | [x] | [View name] |

**Development Order (ADR-0027):**
1. Application Service → Business logic
2. CLI Command → Exposes service
3. TUI Panel → Reference UI
4. RPC Method → If extension needs it
5. MCP Tool → If AI-useful
6. Extension View → Ports TUI patterns

### 3. Scope Confirmation

Present findings and confirm scope:

```
## Design Session Scope

**Feature:** [name]

**Context Found:**
- [ADRs, patterns, issues discovered]

**Interface Matrix:**
[table from step 2]

**Proposed Scope:**
- [What will be designed]

**Questions to Resolve:**
1. [Key question]
2. [Key question]

Confirm scope before proceeding?
```

**STOP for user confirmation.**

### 4. Generate Design Prompt

After confirmation, create `.claude/design.md`:

```markdown
# Design: [Feature Name]

## Problem Statement
[What we're designing and why]

## Existing Context
- [ADRs, patterns, issues]

## Interface Matrix
[from step 2]

## Key Questions
1. [Question]
2. [Question]

## Deliverables
- [ ] Application Service interface
- [ ] Implementation plan
- [ ] ADR (if architectural decision)
- [ ] GitHub issues for implementation

## References
- `docs/adr/` - Existing ADRs
- `CLAUDE.md` - Project conventions
```

### 5. Continue Design Conversation

Now engage in the design conversation:
- Explore options with the user
- Consider tradeoffs
- Create artifacts as needed
- Complete the design plan

### 6. After Plan Approval

**STOP. Do not implement directly.**

Design sessions produce **plans and issues**, not code. After the user approves your design plan:

1. **Create GitHub issues** for each implementation phase using `/create-issue`
2. **Add wave labels** (`wave:1`, `wave:2`) for parallel execution
3. **End the design session** - implementation happens separately

```
Design Session Flow:
  Explore → Analyze → Plan → [GATE] → Create Issues
                               ↑
                        You are here after plan approval
```

**Exception:** Trivial fixes (typos, single-line changes) may be implemented directly with explicit user approval.

## Scoping Session Checklist

Before proposing new work, design sessions MUST complete this checklist:

- [ ] **Check existing milestone** - Find the current milestone with `gh milestone list --state open`, then run `gh issue list --milestone "<milestone>" --state all` to see existing work
- [ ] **Search for duplicates** - Run `gh issue list --search "<feature-keywords>" --limit 20`
- [ ] **Scope alignment** - Explicitly confirm if TUI/Extension/CLI should have feature parity
- [ ] **Platform-specific vs shared** - Identify features unique to one UI layer (e.g., notebooks are VS Code-only)
- [ ] **Wave planning** - Plan wave labels for parallel execution
- [ ] **Integrate existing backlog** - Consider labeling existing open issues into wave structure

## When to Use

- Starting a new feature
- Planning multi-interface work
- Before implementation of significant changes
- When architectural decisions are needed

## MCP Tool Guidance

### Include in MCP
- Queries and data exploration
- Debugging and troubleshooting
- Metadata and schema inspection

### Exclude from MCP
- Destructive operations (delete, truncate)
- Bulk mutations (import, update, deploy)
- Credential management
- Security changes

## Related Commands

| Command | Purpose |
|---------|---------|
| `/design-ui` | Reference-driven UI design with wireframes |
| `/plan-work` | Plan work from existing issues |
| `/create-issue` | Create implementation issues |

## Notes

- `.claude/design.md` is gitignored (ephemeral during design)
- Use relative paths in prompts
- Create ADRs for architectural decisions
- File issues with consistent format
