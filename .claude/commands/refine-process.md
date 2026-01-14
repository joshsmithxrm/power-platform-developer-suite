# Refine Process

Lightweight process refinement when signals indicate the workflow needs adjustment.

## When to Use

Run this command when you observe these signals appearing 3+ times:

| Signal | Indicates |
|--------|-----------|
| Workers frequently stuck at same point | Workflow gap or unclear handoff |
| PRs need major rework after review | Design phase inadequate |
| You check on workers frequently | Trust not calibrated |
| Claude does unexpected things | Terminology/expectation mismatch |
| Context-switching more than expected | Session boundaries wrong |
| Same feedback given on multiple PRs | Missing pattern or rule |

Also run after major milestones (e.g., v1 shipped) or when you feel friction but can't name it.

## Usage

`/refine-process`

No arguments. This command guides a conversational review.

## Process

### 1. Collect Signals

Review recent work to identify friction:

```bash
# Recent PRs and their review cycles
gh pr list --state merged --limit 10 --json number,title,reviews

# Recent issues that had scope problems
gh issue list --label blocked --state closed --limit 5
```

### 2. Categorize Findings

| Category | Examples | Fix Type |
|----------|----------|----------|
| **Pattern gap** | Same code feedback 3x | Add to `docs/patterns/` |
| **Rule gap** | Claude keeps doing X | Add to CLAUDE.md |
| **Workflow gap** | Workers stuck at same point | Update workflow doc |
| **Terminology gap** | Claude misinterprets term | Add to terminology.md |
| **Gate calibration** | Too tight or loose oversight | Adjust gate criteria |

### 3. Propose Changes

For each identified gap, propose a minimal fix:

**Pattern gap:**
```markdown
Add `docs/patterns/example-pattern.cs`:
- Demonstrates: [what]
- Related: [ADRs]
- Source: [existing code to extract]
```

**Rule gap:**
```markdown
Add to CLAUDE.md NEVER/ALWAYS:
| Rule | Why |
```

**Workflow gap:**
```markdown
Update `.claude/workflows/[file].md`:
- Section: [which]
- Change: [what to add/modify]
```

### 4. Apply Changes

After discussion and approval:
1. Make the changes
2. Test with one issue to verify improvement
3. Monitor for the signal to stop appearing

## Output

At the end of refinement, summarize:

```
## Process Refinement Summary

### Signals Observed
- [Signal 1]: appeared X times
- [Signal 2]: appeared Y times

### Changes Made
1. Added pattern: `docs/patterns/X.cs`
2. Added CLAUDE.md rule: "Never do Y"
3. Updated workflow: added Z section

### Verification Plan
- [ ] Test with issue #NNN
- [ ] Monitor for 5 PRs
- [ ] Check if signal stops
```

## Related

- [Meta-Monitoring](../workflows/terminology.md#meta-monitoring)
- [Trust Calibration](../workflows/terminology.md#trust-calibration)
