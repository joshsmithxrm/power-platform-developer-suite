# Design Session

Prepare a structured design session with mandatory discovery of existing patterns and ADRs.

## Usage

`/design-session [feature-name]`

Examples:
- `/design-session plugin-traces` - Design CLI commands for plugin traces
- `/design-session web-resources` - Design web resource management
- `/design-session connection-refs` - Design connection reference workflow

## Arguments

`$ARGUMENTS` - Feature or epic name (optional, will prompt if not provided)

## Process

### 1. Discovery Phase (MANDATORY)

Before any design work, search for existing context:

**Search for ADRs:**
```bash
# Find all ADRs in the target repo
find . -path "*/docs/adr/*.md" -type f | head -20
```

**Search for existing patterns in CLAUDE.md:**
```bash
# Look for relevant sections
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
- (none found)

### Related Issues
- #N - Title
- (none found)

### Existing Implementations
- [file path] - What it does
- (none found)
```

### 2. Context Gathering

If user hasn't provided a feature inventory, request one:

```
Before designing, please provide a feature inventory:

For each feature/panel/command, document:
- Current capabilities (what it does)
- UI elements (if applicable)
- Data considerations (large fields, relationships)
- Open questions

See docs/AGENTIC_WORKFLOW.md "Feature Inventory Pattern" for template.
```

Identify:
- Constraints from existing architecture
- Dependencies on other features
- Existing implementations to reference

### 3. Scope Confirmation

Present findings and confirm scope:

```
## Design Session Scope

**Feature:** [name]

**Context Found:**
- [ADRs, patterns, issues discovered]

**Proposed Scope:**
- [What will be designed in this session]

**Questions to Resolve:**
1. [Key question]
2. [Key question]

**Session Split Recommendation:**
- Single session (proceed) | Split into N sessions (why)

Confirm scope before proceeding?
```

**STOP for user confirmation.**

### 4. Generate Design Prompt

After confirmation, create `.claude/design-session.md`:

```markdown
# Design Session: [Feature Name]

## Problem Statement
[What we're designing and why]

## Existing Context

### Relevant ADRs
- [ADR-000X](path) - [summary]

### Related Patterns
- [pattern from CLAUDE.md or existing code]

### Related Issues
- #N - [title]

## Feature Inventory
[User-provided breakdown or link to source]

## Key Questions to Resolve
1. [Question from scope confirmation]
2. [Question from scope confirmation]

## Deliverables Checklist
- [ ] ROADMAP.md update (if multi-phase)
- [ ] ADR for [decision] (if architectural)
- [ ] Design session prompts for follow-up (if splitting)
- [ ] GitHub issues for implementation
- [ ] CLAUDE.md update (if new patterns)

## References
- `docs/adr/` - Existing ADRs
- `CLAUDE.md` - Project conventions
- [Relevant code paths - relative references only]

## Session Start Instructions
1. Read this prompt and referenced materials
2. Explore existing patterns before proposing new ones
3. Ask clarifying questions before finalizing design
4. Create ADRs for architectural decisions
5. File issues with consistent format
```

### 5. Output Summary

```
## Design Session Prepared

**Feature:** [name]
**Prompt:** .claude/design-session.md

### Discovery Summary
- [N] ADRs reviewed
- [N] related issues found
- [Key patterns identified]

### Recommended Next Steps
1. Read the generated prompt
2. Start design conversation (no agents for design phase)
3. Create artifacts per checklist

### Session Split
- [Single session] | [Split into N sessions - prompts needed for...]

To begin: Read .claude/design-session.md and start designing.
```

## Behavior Summary

1. **Always search first** - Never skip discovery
2. Parse feature name from arguments or prompt for it
3. Search for ADRs, patterns, and issues
4. Report what was found
5. Request feature inventory if not provided
6. Confirm scope with user
7. **STOP for confirmation**
8. Generate design prompt file
9. Output summary with next steps

## Edge Cases

**No ADRs found:**
```
No existing ADRs found in docs/adr/.
This may be the first architectural decision for this area.
```

**No CLAUDE.md patterns found:**
```
No related patterns found in CLAUDE.md.
Consider documenting patterns discovered during design.
```

**Feature already has issues:**
```
Found existing issues:
- #N - [title] (status)

Should this session:
1. Design implementation for existing issues?
2. Create new scope beyond existing issues?
```

## When to Use

- Starting feature parity work (CLI ↔ extension)
- Designing new command groups
- Planning multi-phase epics
- Before filing implementation issues
- When architectural decisions are needed

## Related Commands

| Command | Purpose |
|---------|---------|
| `/plan-work` | Triage existing issues into worktrees |
| `/retrospective` | Analyze completed sessions |
| `/handoff` | Generate context for next session |

## Notes

- `.claude/design-session.md` is gitignored (ephemeral)
- Use relative paths in prompts (not hardcoded absolute paths)
- Reference AGENTIC_WORKFLOW.md for patterns
