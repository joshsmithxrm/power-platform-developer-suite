---
name: retro
description: Conduct a structured retrospective on recent work sessions. Use when the user asks to review what happened, analyze session quality, identify patterns, or do a post-mortem. Triggers include "retrospective", "retro", "what went well", "session review", "post-mortem".
---

# Retrospective

Structured, nine-phase analysis of recent work sessions. Splits the job into **mechanical extraction** (non-LLM, safe to dispatch as a subprocess) and **main-session judgment** (LLM reasoning over the extracted evidence — never delegated).

Artifacts vary by mode:

**Interactive mode** (user present):
- `.retros/YYYY-MM-DD-summary.md` — executive synthesis for humans.
- `.retros/summary.json` — rolling cross-retro metrics (append-only).

**Pipeline mode** (headless via `claude -p`):
- `.workflow/retro-findings.json` — machine-readable findings, consumed by `scripts/pipeline.py`.
- `.retros/summary.json` — rolling cross-retro metrics (append-only).

## Phase Registration (MUST run first) <!-- enforcement: T3 -->

```bash
python scripts/workflow-state.py set phase retro
```

Enables the stop-hook bypass for the retro phase.

## Core Quality Bar (READ BEFORE STARTING)

- Commit messages are the AI's self-reported summary — exactly the thing that needs verification.
- Do NOT grade sessions (no letter grades, no "successful" labels). Present evidence, let the user decide.
- **Mechanical extraction MAY run as a subprocess; LLM transcript judgment MUST stay in the main session.** See "Lane discipline" below. Agents previously reported "zero user corrections" when the user was furious, because they were asked to judge what they'd read instead of just extracting signals. <!-- enforcement: T3 -->
- Do NOT use `grep` on JSONL transcripts — lines are too long on Windows and results get `[Omitted]`. Use the Python extraction helpers in `scripts/retro_helpers.py`.

## Lane discipline — who reads what

| Work | Who does it | Why |
|------|-------------|-----|
| Regex scans, counters, tool-failure detection, git metrics, stop-hook counts | Python subprocess (`scripts/retro_helpers.py`) | Deterministic, non-LLM, no judgment |
| Reading extracted quotes and deciding what they mean | Main session (Claude) | Judgment lives with the user-facing loop; avoids self-preference-bias (arxiv 2410.21819) |
| Deep research on a specific contested finding | Lane A subagent via `/investigate` | Read-only, bounded, returns inline |
| Implementing a DO-NOW fix | Lane B/C agent via `/pr` | Owns its own PR; retro watches, not drives |

**Rule:** Dispatch mechanical signal extraction (Python/subprocess) only; never dispatch LLM-based transcript judgment to a subagent.

## Mode Detection

Detect automatically based on context:

**Pipeline mode** (CWD is a worktree AND `.workflow/pipeline.log` exists AND running via `claude -p`):
→ Jump to the **Pipeline Retro** section below. Mechanical only — no decision phase, no HTML.

**Interactive mode** (user is present):
→ Follow the full 9-phase flow below.

---

## Interactive Retro — 9 phases

```
1. INVOKE /retro [scope]
2. SCOPE + CARRYOVER
3. MECHANICAL EXTRACTION        [subprocess — non-LLM]
4. MAIN-SESSION ANALYSIS        [main Claude only]
5. DECISION PHASE               [plan-with-defaults UX]
6. ROUTING                      [/backlog, /pr, /investigate]
7. MONITOR & CONFIRM
8. EXECUTIVE SYNTHESIS          [md only in interactive; skipped in pipeline]
9. PERSIST                      [summary.json only in interactive; + findings JSON in pipeline]
```

### Phase 1. Invoke

Parse `$ARGUMENTS`:

- `/retro latest` or `/retro` (no args) → find the latest session (most recent 30+ minute gap in git activity).
- `/retro 6h` or `/retro 2d` → explicit time window.
- `/retro abc123..def456` → explicit commit range.
- `/retro the last 2 PRs` → find by PR number; use `gh pr list --state merged --limit N`.

### Phase 2. Scope + Carryover

**Determine scope** via `git log --since="<window>" --format="%H %ai" --no-merges`. Commit-count guard: if scope >25 commits, warn and suggest narrowing.

**Carryover check** (merged from the old Phase 1): read prior retro state before analyzing anything new.

1. Read `.workflow/retro-findings.json` (current worktree) if present.
2. Read `.retros/summary.json` (repo root) for the last 3 retros.
3. For each prior finding, classify:
   - `auto-fix` / `draft-fix` → check whether the referenced files were changed since the finding's date (`git log --since=<date> -- <file>`).
   - `issue-only` → check if the GitHub issue is still open (`gh issue view <N> --json state`).
   - `observation` → no carryover check; was informational.
4. Report: `Last N retros found X findings. Y resolved, Z still open: [list with IDs]`.

If no prior retro data is found, skip silently.

### Phase 3. Mechanical Extraction (subprocess)

Run non-LLM extractors. Safe to dispatch as a subprocess / Bash call; returns raw data only, no interpretation.

**3a. Transcript signals** — for each discovered transcript, run:

```bash
python -c "
import json, sys
sys.path.insert(0, 'scripts')
from retro_helpers import extract_transcript_signals, extract_enforcement_signals, discover_transcripts
transcripts = discover_transcripts('.')
for t in transcripts:
    s = extract_transcript_signals(t)
    print(json.dumps({'transcript': t, 'signals': s}))
es = extract_enforcement_signals('.workflow/state.json')
print(json.dumps({'enforcement': es}))
"
```

Produces per-transcript: user-correction counts, tool failures, repeated commands, stop-hook counts.

**3b. User-message excerpts** — extract raw quotes (no synthesis). Do NOT interpret in this phase; emit text for Phase 4:

```bash
python -c "
import json
path = r'<transcript-path>'
with open(path, 'r', encoding='utf-8') as f:
    for i, line in enumerate(f, 1):
        try:
            obj = json.loads(line)
            if obj.get('type') == 'user' and 'message' in obj:
                msg = obj['message']
                content = msg.get('content', '')
                if isinstance(content, str) and len(content) < 2000:
                    print(f'LINE {i}: {content[:500]}')
                    print('---')
                elif isinstance(content, list):
                    for item in content:
                        if isinstance(item, dict) and item.get('type') == 'text' and len(item.get('text','')) < 2000:
                            print(f'LINE {i}: {item[\"text\"][:500]}')
                            print('---')
        except Exception:
            pass
"
```

**3c. Frustration regex** — quick scan over the extracted quotes:

```bash
python -c "
import re, sys
patterns = re.compile(r'\b(no,|wrong|stop|why are you|why did you|that\'s not|thats not|furious|wtf|goddamn|please stop|interrupted by user)\b', re.IGNORECASE)
for line in sys.stdin:
    if patterns.search(line):
        print(line.rstrip())
"
```

**3d. Git metrics:**

- Commit count, feat/fix ratio.
- Thrashing: any file touched by 3+ commits in the window.
- Feat→fix chains: `git log --grep='^fix' --after=<feat-date>`.
- Stage timing from `.workflow/pipeline.log` (if present).

**3e. Transcript discovery** — find both:

- **Main-repo transcripts** (real user interaction):

  ```bash
  ls -d ~/.claude/projects/*ppdsw-ppds/
  grep -l "#NNN\|branch-name" ~/.claude/projects/*ppdsw-ppds/*.jsonl
  ```

- **Worktree transcripts** (headless pipeline sessions):

  ```bash
  ls -d ~/.claude/projects/*ppdsw-ppds*worktrees-<name>*
  ```

Output a single structured blob for Phase 4. **Do NOT synthesize yet.**

### Phase 4. Main-Session Analysis (no delegation)

Main Claude reads the extracted quotes + metrics **directly**. Builds a findings table.

Row schema:

```
# | issue | rec | rationale | confidence (HIGH/LOW) | tier | kind
```

**Tier taxonomy** (unchanged from prior except the added `rule-drift` kind):

- **auto-fix**: stale references, typos, hardcoded values. Safe to auto-implement.
- **draft-fix**: skill wording, checklist gaps. Auto-PR but flag for review.
- **issue-only**: specific code bug with a concrete fix. Reproducible. Litmus: "Can someone open this issue, make a code change, and close it?"
- **observation**: run metrics, single-run anomalies, patterns without fixes. Stays in retro report — NOT filed as a GitHub issue.

**Kind (new):** Orthogonal to tier. Describes the *nature* of the change the finding proposes.

- `code` — default; fix lives in source/test/config.
- `rule-drift` — observed behavior diverges from a documented rule. Proposes a SKILL.md / CLAUDE.md / interaction-patterns.md edit. Fed into the rule-evolution loop per `.claude/interaction-patterns.md §7`.
- `tooling` — gap in hooks, scripts, or CI.
- `process` — who-does-what / sequencing issue; typically fixed by routing changes.

**Contributing factors** (replaces single 5-Whys): per incident, list ALL contributing factors tagged `{tooling | behavior | skill | setup}`.

**Severity heuristic**: if any of `user_interrupts > 0`, `frustration_hits > 0`, `session crashes (< 10 lines or very short)`, `wrong-branch incidents` → **rough session**, analyze at full depth. Else **clean session**, lighter analysis.

### Phase 5. Decision Phase — plan-with-defaults UX

Follow `.claude/interaction-patterns.md §4` exactly. This is the normative UX for N-decision interactions.

1. **Pre-triage** every finding with a verb (`DO NOW` / `DEFER` / `DROP` / `RESEARCH-FIRST`) and a confidence tag (HIGH / LOW), using the DO NOW / DEFER / DROP heuristic in `.claude/interaction-patterns.md §3`.
2. **Send ONE message with two blocks:**
   - **Bulk plan** — HIGH-confidence items, one line each, grouped by verb. User replies `accept` to ratify, or calls out IDs to override.
   - **Contested** — LOW-confidence items, **capped at 5**. Each gets 2–3 sentences of framing + the AI's lean + per-item ask.
3. **Serialize meta-recommendations.** Do NOT co-present meta-observations with findings. After findings are settled, ask: "Want the meta now, or done?"
4. **Close on confirmation.** One summary turn restating the final plan before writing any durable state.

**If more than 5 are genuinely contested, the pre-triage isn't strong enough** — think harder and move items into defensible defaults.

**Per-row branches:**

- `DO NOW` → flows to Phase 6 routing (Lane A/B/C).
- `DEFER` → flows to Phase 6 routing (`/backlog create`).
- `DROP` → recorded in `retro-summary.md` with reason (`wontfix` / `already-fixed` / `not-reproducible`).
- `RESEARCH-FIRST` → dispatch Lane A agent via `/investigate` with the specific question; on return, re-decide the row (still HIGH or LOW?).

### Phase 6. Routing

One-way flow per decision. Every handoff is named, with the skill that owns it:

| Verb | Hand-off | Notes |
|------|----------|-------|
| `DEFER` | `/backlog create` | Follow `docs/BACKLOG.md`. Apply `type:` and `area:` labels. Link the retro: `Source: .retros/YYYY-MM-DD-summary.md#R-NN`. |
| `DO NOW` (Lane A) | Inline in main session | Read-only analysis / verification. |
| `DO NOW` (Lane B) | Dispatch worker via `Agent` with `isolation: "worktree"` | ≤3 files, ≤200 LOC, no new abstraction. |
| `DO NOW` (Lane C) | New Claude Code session in dedicated worktree | See `.claude/interaction-patterns.md §1`. |
| `DO NOW` / PR lifecycle | `/pr` | Rebase, draft, Gemini review, triage. Retro does NOT bypass the PR skill. |
| `DROP` | Record in summary | Reason required. No issue filed. |
| `RESEARCH-FIRST` | `/investigate` | Bounded question, returns inline; loop back to Phase 5 per row. |
| Worktree cleanup | `/cleanup` | After routing complete. |
| Verify workflow state | `/status` | If anything looks wrong before writing durable state. |

**Rule-drift kind** additionally generates a SKILL.md / rule-surface patch proposal with a draft `## Rule change` section per `.claude/interaction-patterns.md §7`. The patch is filed as a standalone PR (Lane B if small) — never bundled with unrelated code fixes.

### Phase 7. Monitor & Confirm

For each `DO NOW` row: verify it actually landed.

- Lane A: confirm the analysis was produced and recorded.
- Lane B/C: `gh pr view <N> --json state,mergedAt` — confirm merged, not just opened.
- `/backlog create` DEFERs: `gh issue view <N>` — confirm issue exists with correct labels.

Record per-row status (`landed` / `open` / `blocked`) into the decision log.

### Phase 8. Executive Synthesis

**Interactive mode: write 8a only. Skip 8b and 8c** — the conversation is the analysis; HTML artifacts have no consumer.

**8a. `.retros/YYYY-MM-DD-summary.md`** — human-readable narrative. Sections:

- **Pattern narrative** — 1-3 paragraphs describing what the retro actually found. Themes, not bullet regurgitation.
- **Top-N prioritized** — the ranked verbs + findings, including the final decision.
- **Decisions log** — per-row: AI lean, user override if any, final verb, hand-off target, monitor status.
- **What worked** — genuine positives.
- **Meta** (if surfaced in Phase 5) — observations about the retro process itself.

**8b. `.retros/YYYY-MM-DD-summary.html`** — navigable dashboard. Generated by:

```bash
python scripts/retro_html_generator.py \
  --findings .workflow/retro-findings.json \
  --summary .retros/YYYY-MM-DD-summary.md \
  --out .retros/YYYY-MM-DD-summary.html
```

The generator produces self-contained HTML (no build step, no JS framework):

- Embedded Mermaid rendering of the 9-phase flow.
- Searchable / filterable findings table (vanilla JS, no deps).
- Decision log with status badges.
- Back-link to `.retros/findings-index.html`.

**8c. `.retros/findings-index.html`** — regenerated on every retro. Cross-retro navigation; each retro links to its md + html; each finding links to its routed artifact (issue / PR / skill patch).

Generated by:

```bash
python scripts/retro_html_generator.py --index --retros-dir .retros --out .retros/findings-index.html
```

### Phase 9. Persist

**Interactive mode: run 9b only. Skip 9a and 9c** — `retro-findings.json` is consumed by `pipeline.py` (headless only), and `findings-index.html` depends on it.

**9a. Write `.workflow/retro-findings.json`** (pipeline mode only). Schema:

```json
{
  "session_date": "YYYY-MM-DD",
  "pr": "#NNN",
  "stats": {
    "total_commits": 0,
    "feat_fix_ratio": "N/M",
    "thrashing_incidents": 0,
    "sessions_to_success": 0,
    "user_interrupts": 0,
    "severity": "rough|clean"
  },
  "findings": [
    {
      "id": "R-01",
      "tier": "auto-fix|draft-fix|issue-only|observation",
      "kind": "code|rule-drift|tooling|process",
      "description": "What is wrong",
      "files": ["path/to/affected/file"],
      "fix_description": "What to do about it",
      "confidence": "HIGH|LOW",
      "verb": "DO_NOW|DEFER|DROP|RESEARCH_FIRST",
      "routed_to": "issue #NNN | PR #NNN | SKILL.md | dropped",
      "status": "landed|open|blocked|dropped",
      "contributing_factors": [
        {"factor": "description", "type": "tooling|behavior|skill|setup"}
      ]
    }
  ]
}
```

**9b. Update `.retros/summary.json`** (persistent store):

1. **Read** `.retros/summary.json` from repo root (not worktree `.workflow/`).
   - If file doesn't exist: create with seed schema (`schema_version: 1`, `total_retros: 0`, empty `findings_by_category`, zeroed `metrics`).
   - If invalid JSON or `schema_version` differs from 1: log warning, create fresh.
2. **Append** each finding to `findings_by_category[category]` with `{date, branch, finding_id}`. Append-only; never mutate or delete existing entries during this step. New categories become new keys.
3. **Increment** `total_retros`.
4. **Update** `metrics` (rolling averages):
   - `avg_fix_ratio` — average feat/fix ratio across all retros.
   - `pipeline_success_rate` — fraction of retros with severity `clean`.
   - `avg_convergence_rounds` — average convergence rounds (from pipeline retros).
5. **Trim** entries older than 6 months AND beyond the last 20 retros (remove only when BOTH thresholds exceed).
6. **Update** `last_updated` to today.
7. **Write** `.retros/summary.json`.

**9c. Regenerate `.retros/findings-index.html`** with the new retro linked (see Phase 8c command).

---

## Pipeline Retro (headless, no user)

When running as a pipeline stage via `claude -p`:

1. Run Phase 3 (mechanical extraction) unchanged.
2. Run a stripped Phase 4 — mechanical metrics ONLY, no judgment, no decision-table, no routing, no HTML.
3. Write `.workflow/retro-findings.json` with mechanical findings.
4. Run Phase 9b (persistent-store update) so `.retros/summary.json` stays current.
5. **Crash detection:** if this retro runs <5 minutes AND produces zero findings, log a warning `{"warning": "possible retro crash"}` into `retro-findings.json`.

No grading, no root-cause analysis, no HTML generation in pipeline mode. The orchestrator reads `retro-findings.json` for auto-heal decisions (see `scripts/pipeline.py:process_retro_findings`).

### Issue-worthiness litmus test (reminder)

Before marking a finding `issue-only`, apply: "Can someone open this issue, make a code change, and close it?" If the answer is "this is just how that run went" or "there's no specific code to change," use `observation` instead.

**`observation` examples** (do NOT file as issue):
- "High fix ratio (11 fix / 2 feat)" — run metric.
- "109-minute gap between stages" — single-run timing.
- "Most-thrashed file (6 touches)" — pattern without a fix.
- "Converge timed out at 900s" — single-run anomaly.
- "Consider adding a checklist for X" — behavioral recommendation.

**`issue-only` examples** (DO file):
- "Agent frontmatter uses `allowedTools` instead of `tools`" — code bug, concrete fix.
- "Pipeline resumes while previous stage still running" — reproducible logic bug.
- "MSYS converts `/path` to `C:/path` in issue titles" — specific, fixable.
- "Duplicate PR stage runs when PR already exists" — missing guard, concrete fix.

---

## References

- `.claude/interaction-patterns.md` §1 (lanes), §3 (DO NOW / DEFER / DROP), §4 (plan-with-defaults), §6 (HTML artifacts), §7 (rule evolution).
- `docs/BACKLOG.md` — labels & milestones for DEFER routing.
- `specs/CONSTITUTION.md` — non-negotiable principles.
- `scripts/retro_helpers.py` — mechanical extraction helpers.
- `scripts/retro_html_generator.py` — synthesis HTML generator.
- `scripts/pipeline.py:process_retro_findings` — auto-heal consumer.
