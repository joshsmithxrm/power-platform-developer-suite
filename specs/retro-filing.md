# Retro Filing

**Status:** Draft
**Last Updated:** 2026-03-27
**Code:** [scripts/pipeline.py](../scripts/pipeline.py) | [.claude/skills/retro/](../.claude/skills/retro/)
**Surfaces:** N/A

---

## Overview

Pipeline retro auto-files GitHub issues for every `issue-only` finding via `process_retro_findings()`. It doesn't distinguish between actionable code bugs and observational metrics. Result: ~50% of retro-filed issues are noise (timing gaps, fix ratios, thrashing counts) that require triage effort to close. Additionally, pipeline retries file duplicate issues for the same finding.

### Goals

- **Issue-worthiness criteria**: Only file GitHub issues for findings that identify specific, fixable code defects
- **Noise reduction**: Observational metrics and single-run anomalies stay in the retro report without becoming issues
- **Duplicate prevention**: Same finding is not filed as multiple issues across pipeline retries or worktrees

### Non-Goals

- **Changing retro analysis depth**: The retro skill still analyzes all findings — this only affects which ones become GitHub issues
- **Auto-heal implementation**: `auto-fix` and `draft-fix` tiers are unchanged; auto-heal is a separate concern
- **Retro skill restructuring**: The skill prompt gets criteria additions, not a rewrite

---

## Architecture

```
Retro Skill (AI)                    process_retro_findings() (Python)
┌────────────────────┐              ┌──────────────────────────────┐
│ Analyzes session   │              │ Reads retro-findings.json    │
│ Classifies:        │──writes──▶   │ For each issue-only finding: │
│  - auto-fix        │  findings    │   1. Check for duplicates    │
│  - draft-fix       │  .json      │   2. Skip if match exists    │
│  - issue-only      │              │   3. File GitHub issue       │
│  - observation ◀── NEW           │                              │
└────────────────────┘              └──────────────────────────────┘
```

The fix is in two places:
1. **Retro skill prompt** — Add `observation` tier with clear criteria so the AI classifies correctly
2. **`process_retro_findings()`** — Add duplicate check before filing

### Components

| Component | Responsibility |
|-----------|----------------|
| Retro skill prompt | Classifies findings into tiers using issue-worthiness criteria |
| `process_retro_findings()` | Reads findings, deduplicates, files GitHub issues for `issue-only` tier |

---

## Specification

### Issue-Worthiness Criteria

The retro skill uses these criteria when assigning tiers:

**`issue-only`** — becomes a GitHub issue:

| Criterion | Rationale |
|-----------|-----------|
| Identifies a specific code bug, missing logic, or broken behavior | Issues must be actionable |
| Has a concrete fix — can point to a file and say "change this" | Vague recommendations aren't issues |
| Problem is reproducible — would happen on every run, not just this one | Single-run anomalies aren't defects |

**`observation`** — stays in retro report only:

| Criterion | Rationale |
|-----------|-----------|
| Run metric — timing, ratios, counts, percentages | Metrics are data, not bugs |
| Single-run anomaly — gap between stages, one-time timeout | Not reproducible |
| Pattern without a concrete code change — "most-thrashed file", "high fix ratio" | No actionable fix |
| Behavioral recommendation — "consider adding a checklist", "should be standard practice" | Not a code defect |

**Litmus test:** "Can someone open this issue, make a code change, and close it?" If yes → `issue-only`. If the response would be "this is just how that run went" → `observation`.

### Evidence: Correct Classification of Past Findings

| Finding | Old Tier | Correct Tier | Why |
|---------|----------|--------------|-----|
| "High fix ratio 5.5x" (#690) | issue-only | observation | Run metric, no code fix |
| "109-minute gap between QA and review" (#702) | issue-only | observation | Single-run timing anomaly |
| "converge-r1 timed out at 900s" (#701) | issue-only | observation | Single-run anomaly |
| "Most-thrashed file (6 touches)" (#693) | issue-only | observation | Pattern observation, no fix |
| "Wrong frontmatter key 'allowedTools'" (#710) | issue-only | issue-only | Specific code bug, concrete fix |
| "Duplicate process spawning" (#700) | issue-only | issue-only | Reproducible bug in pipeline.py |
| "MSYS path corruption in titles" (#721) | issue-only | issue-only | Specific code bug, concrete fix |

### Tier Definitions (Updated)

| Tier | Definition | Pipeline Action |
|------|------------|-----------------|
| `auto-fix` | Stale references, typos, hardcoded values. Safe to auto-implement. | (Future: auto-heal) |
| `draft-fix` | Skill wording, checklist gaps. Auto-PR but flag for review. | (Future: auto-PR) |
| `issue-only` | Specific code bug, missing logic, or broken behavior with a concrete fix. Reproducible. | File GitHub issue |
| `observation` | Run metrics, single-run anomalies, patterns, behavioral recommendations. No concrete code fix. | Stays in retro report only |

### Duplicate Prevention

Before filing a GitHub issue for an `issue-only` finding, `process_retro_findings()` checks for existing open issues with a matching title prefix.

**Flow:**

1. Build candidate title: `retro: {desc[:70]}`
2. Extract search prefix: first 50 characters of the title
3. Run `gh issue list --search "{prefix}" --state open --json number,title --limit 5`
4. For each returned issue, check if title starts with the same prefix
5. If match found: log `ISSUE_SKIPPED_DUPLICATE` with the existing issue number, skip filing
6. If no match: proceed with `gh issue create`

**Why 50-char prefix match:** Pipeline retries may produce the same finding with slightly different trailing text (e.g., different timing numbers). Matching on a prefix catches these while avoiding false positives from unrelated issues.

### Retro Skill Prompt Changes

Add to the tier definitions section (SKILL.md lines 197-200):

```
Tier definitions:
- **auto-fix**: Stale references, typos, hardcoded values. Safe to auto-implement.
- **draft-fix**: Skill wording, checklist gaps. Auto-PR but flag for review.
- **issue-only**: Specific code bug, missing logic, or broken behavior with a concrete
  fix. Must be reproducible (would happen on every run). Litmus test: "Can someone open
  this issue, make a code change, and close it?"
- **observation**: Run metrics (timing, ratios, counts), single-run anomalies (gaps,
  timeouts), patterns without concrete fixes ("most-thrashed file"), behavioral
  recommendations ("consider adding a checklist"). Stays in retro report — NOT filed
  as a GitHub issue.
```

Add classification guidance to the Pipeline Retro section:

```
Before classifying a finding as issue-only, apply the litmus test: "Can someone open
this issue, make a code change, and close it?" If the answer is "this is just how that
run went" or "there's no specific code to change," classify as observation instead.

Examples of observation (do NOT file as issue):
- "High fix ratio (11 fix / 2 feat)" — run metric
- "109-minute gap between stages" — single-run timing
- "Most-thrashed file (6 touches)" — pattern without a fix
- "Converge timed out at 900s" — single-run anomaly

Examples of issue-only (DO file as issue):
- "Agent frontmatter uses 'allowedTools' instead of 'tools'" — code bug, concrete fix
- "Pipeline resumes while previous stage still running" — reproducible logic bug
- "MSYS converts /path to C:/path in issue titles" — specific, fixable
```

### `process_retro_findings()` Changes

Two changes to the function at pipeline.py:563-616:

1. **Skip `observation` tier** — already implicit (function only iterates `issues` list filtered by `tier == "issue-only"`), but add `observation` to the summary log for visibility.

2. **Add duplicate check** before `gh issue create`:

```python
def _find_duplicate_issue(title, repo_root):
    """Check for existing open issue with matching title prefix."""
    prefix = title[:50]
    try:
        result = subprocess.run(
            ["gh", "issue", "list", "--search", prefix, "--state", "open",
             "--json", "number,title", "--limit", "5"],
            cwd=repo_root, capture_output=True, text=True, timeout=15,
        )
        if result.returncode != 0:
            return None
        issues = json.loads(result.stdout)
        for issue in issues:
            if issue["title"].startswith(prefix):
                return issue["number"]
    except (subprocess.TimeoutExpired, json.JSONDecodeError, OSError):
        pass
    return None
```

Updated filing loop:

```python
observations = [f for f in findings if f.get("tier") == "observation"]

log(
    logger, "retro", "FINDINGS_SUMMARY",
    auto_fix=len(auto_fixes), draft_fix=len(draft_fixes),
    issue_only=len(issues), observation=len(observations),
)

for finding in issues:
    desc = finding.get("description", "No description")
    title = f"retro: {desc[:70]}"

    existing = _find_duplicate_issue(title, repo_root)
    if existing:
        log(logger, "retro", "ISSUE_SKIPPED_DUPLICATE",
            finding=finding.get("id", "R-??"), existing=f"#{existing}")
        continue

    # ... existing gh issue create logic ...
```

### Constraints

- The retro skill prompt is the primary control — the AI classifies findings
- `process_retro_findings()` trusts the AI's classification; it does not second-guess tier assignments
- Duplicate check is best-effort — if `gh issue list` fails, file the issue anyway (prefer duplicates over lost findings)
- The `observation` tier is stored in `retro-findings.json` and `.retros/summary.json` for trend analysis — it's not discarded, just not filed

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | Retro skill prompt includes `observation` tier with clear definition and examples | `test_skill_prompt_contains_observation_tier` | 🔲 |
| AC-02 | Retro skill prompt includes litmus test guidance: "Can someone open this issue, make a code change, and close it?" | `test_skill_prompt_contains_litmus_test` | 🔲 |
| AC-03 | `process_retro_findings()` logs `observation` count in `FINDINGS_SUMMARY` | `test_findings_summary_includes_observation_count` | 🔲 |
| AC-04 | When `retro-findings.json` contains findings with `tier == "observation"`, `process_retro_findings()` does not call `gh issue create` for them | `test_observation_tier_not_filed` | 🔲 |
| AC-05 | `_find_duplicate_issue()` returns existing issue number when open issue with matching title prefix exists | `test_find_duplicate_returns_existing_issue` | 🔲 |
| AC-06 | `_find_duplicate_issue()` returns None when no matching open issue exists | `test_find_duplicate_returns_none_when_no_match` | 🔲 |
| AC-07 | `process_retro_findings()` skips filing and logs `ISSUE_SKIPPED_DUPLICATE` when duplicate exists | `test_skip_duplicate_issue_filing` | 🔲 |
| AC-08 | `process_retro_findings()` files issue when `_find_duplicate_issue()` fails (best-effort dedup) | `test_files_issue_when_dedup_check_fails` | 🔲 |
| AC-09 | `observation` findings are written to `retro-findings.json` and `.retros/summary.json` (not discarded) | `test_observations_persisted_in_store` | 🔲 |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| All findings are observations | 5 findings, all `observation` tier | Zero issues filed, summary log shows `issue_only=0, observation=5` |
| `gh issue list` times out during dedup | Network failure | File the issue anyway (best-effort dedup) |
| Duplicate check returns similar but non-matching title | "retro: Pipeline fails" vs "retro: Pipeline retry" | Not a match (prefix differs after char ~20), file both |
| Finding with empty description | `description: ""` | Title becomes `retro: `, dedup matches any other empty-desc finding |

---

## Design Decisions

### Why a Fourth Tier Instead of a Code Filter?

**Context:** ~50% of retro-filed issues are observational noise. Need to prevent them from becoming GitHub issues.

**Decision:** Add `observation` tier to the retro skill prompt. The AI classifies findings; `process_retro_findings()` trusts the classification.

**Alternatives considered:**
- Heuristic filter in `process_retro_findings()` (pattern-match on "ratio", "gap", "timed out"): Fragile, duplicates the AI's judgment, needs constant maintenance as new observation types appear
- Both prompt + code filter (belt-and-suspenders): Adds complexity for marginal benefit — if the AI classifies correctly, the filter never fires
- Reduce what retro analyzes (skip metrics entirely): Loses valuable trend data that `.retros/summary.json` captures

**Consequences:**
- Positive: Single point of control (the prompt). Easy to adjust criteria by editing skill text.
- Positive: Zero risk of filtering out real bugs — the AI applies judgment, not regex.
- Negative: Depends on AI classification quality. Mitigated by the litmus test being concrete and easy to apply.

### Why Prefix Match for Duplicate Detection?

**Context:** Pipeline retries produce the same finding with slightly different trailing text (different timing numbers, different session counts).

**Decision:** Match on first 50 characters of the `retro: {desc}` title.

**Alternatives considered:**
- Exact title match: Misses duplicates with different trailing numbers (e.g., "converge-r1 ran twice (2150s...)" vs "converge-r1 ran twice (1800s...)")
- Semantic similarity via embedding: Over-engineered for this problem; adds API dependency
- Finding ID match (search for `R-01` in issue body): Finding IDs reset per retro run, so R-01 in one run is different from R-01 in another

**Consequences:**
- Positive: Catches duplicates with minor wording differences
- Negative: Could false-positive on unrelated issues with similar 50-char prefix. Mitigated by the `retro: ` prefix — only retro-filed issues match, and the 50-char window captures enough semantic content to distinguish topics.

---

## Related Specs

- [pipeline-observability.md](./pipeline-observability.md) — Pipeline infrastructure that produces the retro stage and `process_retro_findings()`
- [workflow-enforcement.md](./workflow-enforcement.md) — Workflow state schema where retro findings are stored

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-27 | Initial spec — issue #722 |
