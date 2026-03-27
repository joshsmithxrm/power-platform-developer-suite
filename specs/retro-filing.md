# Retro Filing

**Status:** Draft (v2.0 — retro overhaul: transcript reading, isolation, auto-trigger, issue updates)
**Last Updated:** 2026-03-27
**Code:** [scripts/pipeline.py](../scripts/pipeline.py) | [scripts/retro_helpers.py](../scripts/retro_helpers.py) | [.claude/skills/retro/](../.claude/skills/retro/)
**Surfaces:** N/A

---

## Overview

The retro system has two problems. First, filing quality: pipeline retro auto-files GitHub issues for every `issue-only` finding via `process_retro_findings()`, but doesn't distinguish between actionable code bugs and observational metrics — ~50% of filed issues are noise. Second, coverage: retro is blind to what actually happened in sessions. It reads mechanical metrics (pipeline.log, git log) but not transcript content. Interactive sessions skip retro entirely. When retro does run interactively, it is contaminated by implementation context.

### Goals

- **Issue-worthiness criteria**: Only file GitHub issues for findings that identify specific, fixable code defects
- **Noise reduction**: Observational metrics and single-run anomalies stay in the retro report without becoming issues
- **Duplicate prevention**: Same finding updates existing issues instead of creating duplicates
- **Transcript reading**: Retro reads both user AND assistant messages from all sessions on a branch
- **Context isolation**: Retro runs as a separate agent, not in the implementation context window
- **Automatic trigger**: Retro runs after every PR (via pr-monitor), not only in pipeline mode

### Non-Goals

- **Changing retro analysis depth**: The retro skill still analyzes all findings — this affects filing behavior and input sources
- **Auto-heal implementation**: `auto-fix` and `draft-fix` tiers are unchanged; auto-heal is a separate concern
- **Real-time session monitoring**: Retro runs after the fact, not during sessions

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

### Transcript Discovery and Reading

**Problem:** Pipeline retro reads `pipeline.log` and `git log` only — mechanical metrics. It does not read transcript content (what the AI actually did, what errors occurred, what the user corrected). Interactive retro reads user messages but NOT assistant messages. Neither reads across sessions.

**Transcript sources (in priority order):**

| Source | Location | What it contains |
|--------|----------|-----------------|
| Pipeline stage JSONL | `.workflow/stages/{stage}.jsonl` | Full assistant output — tool calls, text, errors |
| Pipeline stage logs | `.workflow/stages/{stage}.log` | Extracted assistant text (human-readable) |
| Pipeline log | `.workflow/pipeline.log` | Stage timing, heartbeats, events |
| Claude session transcripts | `~/.claude/projects/{hashed-path}/*.jsonl` (direct filesystem read) | Interactive session history |
| PR monitor log | `.workflow/pr-monitor.log` | CI status, Gemini triage, notification events |
| Workflow state | `.workflow/state.json` | Phase transitions, stop hook enforcement logging |

**Discovery flow:**

```
1. List all .workflow/stages/*.jsonl files (pipeline sessions)
2. List all .workflow/stages/*.log files (extracted text)
3. Read .workflow/pipeline.log for stage events
4. Read interactive session transcripts from Claude Code's local storage:
   Scan ~/.claude/projects/ for directories matching the repo path hash
   Read *.jsonl files, filter by timestamp (sessions during this branch's lifetime)
5. Read .workflow/state.json for enforcement events
6. Read .workflow/pr-monitor.log if exists
```

**Content extraction from JSONL:** Use the same `extract_text_from_jsonl()` logic from pipeline-observability (extracts `assistant` message text blocks). For retro purposes, also extract:
- `tool_use` events: which tools were called, which files were modified
- `tool_result` events: which tool calls failed (non-zero exit, error messages)
- User messages (in interactive transcripts): corrections, frustrations, repeated commands

**What retro should detect from transcripts:**

| Signal | Source | Example |
|--------|--------|---------|
| User corrections | User messages | "No, I said X not Y", "That's wrong", "Try again" |
| Tool failures | tool_result events | Bash exit code != 0, Read file not found |
| Repeated commands | User messages | Same command issued 3+ times ("finish the workflow") |
| Agent retries | assistant messages | Same tool call with same args issued multiple times |
| Session crashes | Session list | Short sessions (<2 min), abrupt endings |
| Skill loading failures | assistant messages | "Skill not found", "Unknown command" |
| Stop hook ignored | state.json | `stop_hook_count > 1` — agent ignored enforcement |
| Unnecessary decision points | User messages | User says "just do it", "don't ask", "proceed" |

### Context Isolation

**Problem:** When retro runs in the same context window as implementation, the retro agent has access to implementation details, tool outputs, and prior conversation. This biases the retro — it focuses on what it remembers from the session rather than systematically analyzing transcripts.

**Evidence:** PR #725: retro ran in the same session as implementation. It reported 3 findings, all from the last 30 minutes of work. It missed a pattern of 4 repeated "finish the workflow" commands from the first hour.

**Fix:** Retro always runs as a separate `claude -p` session. No shared context with implementation.

**Pipeline mode (already isolated):** Retro is a separate stage in `pipeline.py` — each stage gets a fresh context window. No change needed.

**Interactive mode (new):** When a user runs `/retro` interactively, it should spawn a subagent (`subagent_type: "general-purpose"`) with ONLY:
- The transcript files (JSONL, logs)
- The retro skill instructions
- The constitution
- The workflow state

It does NOT receive the conversation history from the implementation session. The subagent returns findings to the parent session, which writes them to state and handles filing.

**pr-monitor mode (new):** `pr_monitor.py` runs `claude -p "/retro"` in the worktree — inherently isolated (separate process, fresh context).

### Automatic Trigger

**Problem:** Interactive sessions skip retro entirely. The user has to manually run `/retro`, which rarely happens. When it does happen, it's in the same context (contaminated).

**Fix:** Retro is triggered automatically by:

| Trigger | Mechanism | Isolation |
|---------|-----------|-----------|
| Pipeline completion (success) | Last stage in pipeline.py sequence | Separate `claude -p` session |
| Pipeline failure | `except PipelineFailure` block in pipeline.py (see pipeline-observability) | Separate `claude -p` session |
| PR monitor completion | Step 7 of pr_monitor.py, before notification | Separate `claude -p` session |
| Interactive PR creation | `/pr` skill spawns retro subagent after PR is created | Subagent (isolated context) |

**No stop-hook trigger:** The stop hook is NOT a good trigger for retro. It fires unpredictably (e.g., feat/workflow-overhaul design session 2026-03-27: stop hook fired 6 times as false positives from stale local main). Retro is triggered by completion events (PR created, pipeline done), not by session lifecycle.

### Issue Updates Instead of Skip

**Problem (updated):** The v1 spec skipped duplicate issues entirely. But recurring findings carry important information — "this happened again" is a signal that the original issue is more serious than initially thought, or that a fix didn't stick.

**Fix:** When a finding matches an existing open issue, post a comment with the new occurrence instead of skipping.

**Updated `_find_duplicate_issue()` behavior:**

```python
def _handle_duplicate(finding, existing_issue_number, repo_root):
    """Post a comment on the existing issue with new occurrence details."""
    # Resolve branch from state.json or git
    branch = "unknown"
    state_path = os.path.join(repo_root, ".workflow", "state.json")
    try:
        with open(state_path) as f:
            branch = json.load(f).get("branch", "unknown")
    except (OSError, json.JSONDecodeError):
        try:
            result = subprocess.run(
                ["git", "rev-parse", "--abbrev-ref", "HEAD"],
                cwd=repo_root, capture_output=True, text=True, timeout=5)
            if result.returncode == 0:
                branch = result.stdout.strip()
        except (subprocess.TimeoutExpired, FileNotFoundError):
            pass

    body = f"""### Retro observation ({datetime.now().strftime('%Y-%m-%d')})
**Branch:** {branch}
**Session:** {finding.get('id', 'unknown')}
**Details:** {finding.get('description', 'No description')}
**Evidence:** {finding.get('contributing_factors', ['None'])[0] if finding.get('contributing_factors') else 'None'}

This finding was also observed in a previous retro. Updating existing issue rather than filing duplicate."""

    subprocess.run(
        ["gh", "issue", "comment", str(existing_issue_number),
         "--body", body],
        cwd=repo_root, capture_output=True, text=True, timeout=15,
    )
```

**Updated filing loop:**

```python
for finding in issues:
    desc = finding.get("description", "No description")
    title = f"retro: {desc[:70]}"

    existing = _find_duplicate_issue(title, repo_root)
    if existing:
        log(logger, "retro", "ISSUE_UPDATED_DUPLICATE",
            finding=finding.get("id", "R-??"), existing=f"#{existing}")
        _handle_duplicate(finding, existing, repo_root)
        continue

    # ... existing gh issue create logic ...
```

**Why update instead of skip:** A finding that appears in 3 consecutive retros is not the same as a finding that appears once. The comment trail creates a history: "first seen on feat/X, also on feat/Y, also on feat/Z." This pattern visibility helps prioritize fixes.

### Constraints

- The retro skill prompt is the primary control — the AI classifies findings
- `process_retro_findings()` trusts the AI's classification; it does not second-guess tier assignments
- Duplicate check is best-effort — if `gh issue list` fails, file the issue anyway (prefer duplicates over lost findings)
- The `observation` tier is stored in `retro-findings.json` and `.retros/summary.json` for trend analysis — it's not discarded, just not filed
- Transcript reading is additive — retro still reads pipeline.log and git log in addition to transcripts
- PPDS_SHAKEDOWN=1 suppresses all issue filing and commenting (shakedown findings stay in report only)

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
| AC-07 | `process_retro_findings()` updates existing issue (posts comment) and logs `ISSUE_UPDATED_DUPLICATE` when duplicate exists | `test_update_duplicate_issue` | 🔲 |
| AC-08 | `process_retro_findings()` files issue when `_find_duplicate_issue()` fails (best-effort dedup) | `test_files_issue_when_dedup_check_fails` | 🔲 |
| AC-09 | `observation` findings are written to `retro-findings.json` and `.retros/summary.json` (not discarded) | `test_observations_persisted_in_store` | 🔲 |
| AC-10 | `extract_transcript_signals(jsonl_path)` returns structured signals: user corrections, tool failures, repeated commands from JSONL | `test_extract_transcript_signals` (unit test with fixture JSONL containing known patterns) | 🔲 |
| AC-11 | `extract_transcript_signals()` identifies user correction patterns: messages containing "no", "wrong", "try again", "that's not", or 3+ repetitions of same command | `test_user_correction_detection` | 🔲 |
| AC-12 | `extract_transcript_signals()` identifies tool failures: Bash tool_result with non-zero exit code, Read with "file not found", Edit with "old_string not found" | `test_tool_failure_detection` | 🔲 |
| AC-13 | `extract_enforcement_signals(state_path)` returns stop hook block count and timestamps from `stop_hook_count` field in state | `test_enforcement_signal_extraction` | 🔲 |
| AC-14 | Interactive `/retro` dispatches subagent with `subagent_type: "general-purpose"` — subagent receives only transcript files, constitution, and state (no conversation history) | Manual — run `/retro` in interactive session, verify subagent dispatch in tool calls | 🔲 |
| AC-15 | `pr_monitor.py` triggers retro as penultimate step (step 7), passing worktree path for transcript access | `test_pr_monitor.py::test_retro_trigger` | 🔲 |
| AC-16 | Duplicate issue comment includes branch name, session ID, new evidence, and "also observed" preamble | `test_duplicate_comment_format` | 🔲 |
| AC-17 | `PPDS_SHAKEDOWN=1` suppresses all `gh issue create` and `gh issue comment` calls | `test_shakedown_suppresses_all_issue_ops` | 🔲 |
| AC-18 | Interactive `/pr` skill spawns retro subagent after PR is created (isolated, best-effort) | Manual — run `/pr` interactively, verify retro subagent dispatched after PR creation | 🔲 |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| All findings are observations | 5 findings, all `observation` tier | Zero issues filed, summary log shows `issue_only=0, observation=5` |
| `gh issue list` times out during dedup | Network failure | File the issue anyway (best-effort dedup) |
| Duplicate check returns similar but non-matching title | "retro: Pipeline fails" vs "retro: Pipeline retry" | Not a match (prefix differs after char ~20), file both |
| Finding with empty description | `description: ""` | Title becomes `retro: `, dedup matches any other empty-desc finding |
| No JSONL files exist (first session on branch) | Empty `.workflow/stages/` | Retro falls back to pipeline.log + git log only |
| JSONL contains only tool_use events (no text) | Agent used tools but produced no text | Extract tool names and files modified — still useful signal |
| Claude Code local storage path not found (`~/.claude/projects/` missing) | Different Claude Code version or portable install | Skip interactive transcript reading, log warning, continue with pipeline JSONL only |
| Retro subagent stalls in interactive mode | Agent takes >5 min | Stall-based timeout kills subagent. Parent session reports "retro incomplete" |
| `gh issue comment` fails | Network error | Log warning, continue with next finding. Best-effort. |
| Same finding appears in 5 consecutive retros | 5 comments on same issue | Each comment adds to the trail. Human reviewer sees pattern and can prioritize. |

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
| 2026-03-27 | v2.0 — retro overhaul: (1) transcript discovery and reading (JSONL + stage logs + interactive sessions), (2) content extraction for user corrections, tool failures, repeated commands, enforcement patterns, (3) context isolation — retro as separate agent/subagent, (4) automatic trigger via pr-monitor and /pr, (5) issue updates instead of skip on duplicates (post comment with new occurrence), (6) PPDS_SHAKEDOWN suppression. |
