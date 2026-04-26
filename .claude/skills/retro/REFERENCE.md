# Retrospective - Reference

Rationale, taxonomies, and worked details for /retro. Loaded on demand from .claude/skills/retro/SKILL.md. The procedure lives there; the rationale lives here.

## §1 - Tier definitions

Every finding is assigned a tier. Tier governs whether it lands in code, an issue, or only the retro report.

- **auto-fix**: stale references, typos, hardcoded values. Safe to auto-implement; no human judgment needed.
- **draft-fix**: skill wording, checklist gaps, doc smell. Auto-PR but flag for review.
- **issue-only**: specific code bug with a concrete fix. Reproducible. Litmus: "Can someone open this issue, make a code change, and close it?"
- **observation**: run metrics, single-run anomalies, patterns without fixes. Stays in retro report - NOT filed as a GitHub issue.

### Issue-worthiness litmus

Apply before marking issue-only: "Can someone open this issue, make a code change, and close it?" If the answer is "this is just how that run went" or "there is no specific code to change," use observation.

observation examples (do NOT file): high fix ratio (11 fix / 2 feat), 109-min stage gap, most-thrashed file (6 touches), 900s converge timeout, "consider adding a checklist for X."

issue-only examples (DO file): "Agent frontmatter uses allowedTools instead of tools", "Pipeline resumes while previous stage still running", "MSYS converts /path to C:/path in issue titles", "Duplicate PR stage runs when PR already exists."

## §2 - Finding taxonomy (kind)

Orthogonal to tier. Describes the *nature* of the change the finding proposes.

- code - default; fix lives in source/test/config.
- rule-drift - observed behavior diverges from a documented rule. Proposes a SKILL.md / CLAUDE.md / interaction-patterns.md edit. Fed into the rule-evolution loop per .claude/interaction-patterns.md §7.
- tooling - gap in hooks, scripts, or CI.
- process - who-does-what / sequencing issue; typically fixed by routing changes.

### Contributing factors

Per incident, list ALL contributing factors tagged {tooling | behavior | skill | setup}. This replaces single-cause 5-Whys analysis - real incidents almost always have more than one factor.

## §3 - Decision phase rules

The decision UX must obey .claude/interaction-patterns.md §4 (plan-with-defaults). Four rules:

1. Pre-triage every finding with a verb (DO NOW / DEFER / DROP / RESEARCH-FIRST) and a confidence tag (HIGH / LOW). Heuristic: .claude/interaction-patterns.md §3.
2. Send ONE message with two blocks:
   - Bulk plan - HIGH-confidence items, one line each, grouped by verb. User replies accept or calls out IDs to override.
   - Contested - LOW-confidence items, capped at 5. Each gets 2-3 sentences of framing + AI lean + per-item ask.
3. Serialize meta-recommendations. Do NOT co-present meta-observations with findings. After findings settle, ask: "Want the meta now, or done?"
4. Close on confirmation. One summary turn restating the final plan before writing durable state.

If more than 5 are genuinely contested, the pre-triage is not strong enough - think harder and move items into defensible defaults.

## §4 - Routing matrix

| Verb | Hand-off | Notes |
|------|----------|-------|
| DEFER | /backlog create | Apply type: and area: labels per docs/BACKLOG.md. Link the retro: Source: .retros/YYYY-MM-DD-summary.md#R-NN. |
| DO NOW (Lane A) | Inline in main session | Read-only analysis / verification. |
| DO NOW (Lane B) | Dispatch worker via Agent with isolation: worktree | <=3 files, <=200 LOC, no new abstraction. |
| DO NOW (Lane C) | New Claude Code session in dedicated worktree | See .claude/interaction-patterns.md §1. |
| DO NOW / PR lifecycle | /pr | Rebase, draft, Gemini review, triage. Retro does NOT bypass /pr. |
| DROP | Record in summary | Reason required (wontfix / already-fixed / not-reproducible). No issue filed. |
| RESEARCH-FIRST | /investigate | Bounded question; returns inline; loop back to Phase 5 per row. |
| Cleanup / status | /cleanup, /status | After routing or before durable writes. |

Rule-drift kind additionally generates a SKILL.md / rule-surface patch proposal with a draft "## Rule change" section. The patch is filed as a standalone PR (Lane B if small) - never bundled with unrelated code fixes.

## §5 - Persist schemas

### .workflow/retro-findings.json (pipeline mode only)

Schema fields:

- session_date (string, YYYY-MM-DD)
- pr (string, #NNN)
- stats (object): total_commits, feat_fix_ratio, thrashing_incidents, sessions_to_success, user_interrupts, severity (rough|clean)
- findings (array): each finding has id (R-NN), tier (auto-fix|draft-fix|issue-only|observation), kind (code|rule-drift|tooling|process), description, files, fix_description, confidence (HIGH|LOW), verb (DO_NOW|DEFER|DROP|RESEARCH_FIRST), routed_to, status (landed|open|blocked|dropped), contributing_factors (array of {factor, type}).

### .retros/summary.json (persistent rolling store)

1. Read .retros/summary.json from repo root. If missing: seed with schema_version: 1, total_retros: 0, empty findings_by_category, zeroed metrics. If invalid JSON or wrong schema: log warning, recreate.
2. Append each finding to findings_by_category[category] with {date, branch, finding_id}. Append-only - never mutate or delete in this step.
3. Increment total_retros.
4. Update metrics: avg_fix_ratio (mean across all retros), pipeline_success_rate (fraction with severity clean), avg_convergence_rounds (from pipeline retros).
5. Trim entries older than 6 months AND beyond the last 20 retros (remove only when BOTH thresholds exceed).
6. Update last_updated to today.
7. Write the file.

## §6 - Lane discipline rationale

Why mechanical extraction can be a subprocess but transcript judgment cannot:

| Work | Who does it | Why |
|------|-------------|-----|
| Regex scans, counters, tool-failure detection, git metrics, stop-hook counts | Python subprocess (scripts/retro_helpers.py) | Deterministic, non-LLM, no judgment. Subprocess is fine. |
| Reading extracted quotes and deciding what they mean | Main session (Claude) | Judgment lives with the user-facing loop; avoids self-preference-bias (arxiv 2410.21819). |
| Deep research on a contested finding | Lane A subagent via /investigate | Read-only, bounded, returns inline. |
| Implementing a DO-NOW fix | Lane B/C agent via /pr | Owns its own PR; retro watches, not drives. |

The hard rule: dispatch mechanical signal extraction (Python/subprocess) only; never dispatch LLM-based transcript judgment to a subagent. Agents previously reported "zero user corrections" when the user was furious because they were asked to *judge* what they had read instead of just extracting signals.

## §7 - HTML artifact spec

Pipeline retros write .workflow/retro-findings.json (machine-readable JSON). Interactive retros write .retros/YYYY-MM-DD-summary.md only. HTML artifacts (.retros/*.html) are reserved for tooling that consumes them - the retro-html-guard.py hook blocks HTML writes outside PPDS_PIPELINE=1. The conversation IS the analysis in interactive mode.

If HTML output is needed (e.g., audit dashboards), generate via python scripts/retro_html_generator.py from inside a pipeline-mode invocation.
