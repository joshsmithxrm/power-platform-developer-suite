---
name: retro
description: Conduct a structured retrospective on recent work sessions. Use when the user asks to review what happened, analyze session quality, identify patterns, or do a post-mortem. Triggers include "retrospective", "retro", "what went well", "session review", "post-mortem".
---

# Retrospective

Structured, nine-phase analysis of recent work sessions. Splits the job into mechanical extraction (subprocess, non-LLM) and main-session judgment (LLM reasoning, never delegated).

Artifacts:
- Interactive mode: `.retros/YYYY-MM-DD-summary.md` + `.retros/summary.json`.
- Pipeline mode: `.workflow/retro-findings.json` + `.retros/summary.json`.

## Phase Registration (MUST run first) <!-- enforcement: T3 -->

```bash
python scripts/workflow-state.py set phase retro
```

Read REFERENCE.md SS6 "Lane discipline rationale" before deciding what to dispatch and what to keep main-session.

## Mode Detection

- Pipeline mode (CWD is a worktree AND `.workflow/pipeline.log` exists AND running via `claude -p`) -> Pipeline Retro section.
- Interactive mode -> 9-phase flow below.

---

## Interactive Retro - 9 phases

```
1. INVOKE /retro [scope]
2. SCOPE + CARRYOVER
3. MECHANICAL EXTRACTION   [subprocess - non-LLM]
4. MAIN-SESSION ANALYSIS   [main Claude only]
5. DECISION PHASE          [plan-with-defaults UX]
6. ROUTING                 [/backlog, /pr, /investigate]
7. MONITOR & CONFIRM
8. EXECUTIVE SYNTHESIS     [md only in interactive]
9. PERSIST                 [summary.json + findings JSON]
```

### Phase 1. Invoke

Parse `$ARGUMENTS`: `latest` / `<window>` / `<commit-range>` / "the last N PRs". No args -> last session (30+ min git gap).

### Phase 2. Scope + Carryover

```bash
git log --since="<window>" --format="%H %ai" --no-merges
```

If scope > 25 commits, warn and suggest narrowing. Then read `.workflow/retro-findings.json` (worktree) and `.retros/summary.json` (last 3) and classify carryover. Report `Last N retros found X findings. Y resolved, Z still open: [list with IDs]`.

### Phase 3. Mechanical Extraction (subprocess)

Run non-LLM extractors via `scripts/retro_helpers.py`. NEVER use `grep` on JSONL transcripts (Windows lines too long). <!-- enforcement: T3 --> Capture per-transcript: user-correction counts, tool failures, repeated commands, stop-hook counts, raw user-message excerpts (no synthesis), frustration regex hits, git metrics (commit count, feat/fix ratio, thrashing, feat->fix chains, stage timing).

```bash
python -c "
import json, sys
sys.path.insert(0, 'scripts')
from retro_helpers import extract_transcript_signals, extract_enforcement_signals, discover_transcripts
for t in discover_transcripts('.'):
    print(json.dumps({'transcript': t, 'signals': extract_transcript_signals(t)}))
print(json.dumps({'enforcement': extract_enforcement_signals('.workflow/state.json')}))
"
```

Output a single structured blob for Phase 4. Do NOT synthesize yet.

### Phase 4. Main-Session Analysis (no delegation)

Read REFERENCE.md SS1 "Tier definitions" and SS2 "Finding taxonomy" before classifying. Build the findings table; row schema: `# | issue | rec | rationale | confidence | tier | kind`. Tag every incident with contributing factors (`tooling | behavior | skill | setup`). Apply the severity heuristic: any of user_interrupts > 0, frustration_hits > 0, very-short crashes, wrong-branch incidents -> rough session, full depth.

### Phase 5. Decision Phase - plan-with-defaults UX

Read REFERENCE.md SS3 "Decision phase rules" before authoring the bulk-plan / contested split. Follow `.claude/interaction-patterns.md` SS4 verbatim. Pre-triage every finding (`DO NOW` / `DEFER` / `DROP` / `RESEARCH-FIRST`) with confidence (HIGH / LOW). Send ONE message with a bulk plan + contested block (cap 5). Serialize meta-recommendations.

### Phase 6. Routing

Read REFERENCE.md SS4 "Routing matrix" before dispatching. One-way flow per decision. Rule-drift kind additionally generates a SKILL.md / rule-surface patch proposal per `.claude/interaction-patterns.md SS7`.

### Phase 7. Monitor & Confirm

For each `DO NOW` row: verify it actually landed.
- Lane A: confirm analysis recorded.
- Lane B/C: `gh pr view <N> --json state,mergedAt`.
- DEFER: `gh issue view <N>` confirms label set.
Record per-row status into the decision log.

### Phase 8. Executive Synthesis

Interactive mode: write `.retros/YYYY-MM-DD-summary.md` only. Skip HTML artifacts (the conversation IS the analysis).

Sections: pattern narrative, top-N prioritized, decisions log, what worked, meta (if surfaced).

### Phase 9. Persist

Read REFERENCE.md SS5 "Persist schemas" for the JSON shapes.

Interactive mode: run 9b only (update `.retros/summary.json`). Append findings, increment `total_retros`, update rolling metrics, trim entries older than 6 months AND beyond the last 20 retros.

---

## Pipeline Retro (headless, no user)

When running via `claude -p`:

1. Phase 3 (mechanical extraction) unchanged.
2. Stripped Phase 4 - metrics only, no judgment, no decisions, no HTML.
3. Write `.workflow/retro-findings.json` with mechanical findings.
4. Phase 9b (persistent-store update).
5. Crash detection: retro <5 min AND zero findings -> log `{"warning": "possible retro crash"}`.

No grading, no root-cause analysis, no HTML in pipeline mode.

---

## References

- `.claude/skills/retro/REFERENCE.md` - tier defs, taxonomies, decision rules, routing matrix, schemas.
- `.claude/interaction-patterns.md` SS1, SS3, SS4, SS6, SS7.
- `docs/BACKLOG.md` - labels for DEFER routing.
- `scripts/retro_helpers.py` - mechanical extraction helpers.
- `scripts/pipeline.py:process_retro_findings` - auto-heal consumer.
