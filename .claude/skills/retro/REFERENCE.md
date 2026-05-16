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

## §7 - Mechanical signal detector reference

### Correction pattern taxonomy

`extract_transcript_signals` matches two classes of corrections in user-typed text:

| Class | Examples | Why included |
|-------|---------|--------------|
| Direct | `"no,"`, `"no "`, `"wrong"`, `"try again"`, `"that's not"`, `"not what i"` | Imperative corrections — operator says what was wrong |
| Question-form | `"why "`, `"shouldn't "`, `"didn't you "`, `"weren't you supposed "`, `"isn't this "` | Interrogative corrections — operator asks why the wrong thing happened. PR #1095 example: *"why is the PR ready but the monitor hasn't been run?"* — missed by old detector |

Both classes increment `user_corrections` and set `needs_manual_review: true`.

### Escalation flags

Written to `.workflow/retro-findings.json` via `write_session_flags`:

| Flag | Trigger |
|------|---------|
| `needs_manual_review` | `user_corrections > 0` OR `tool_failures > 2` OR `repeated_commands > 3` OR `frustration_hits > 0` |
| `signal_extractor_suspect` | All detector counts zero AND `tool_call_count > 50` — a non-trivial session with no signals is suspicious |

### Tool failure detection

Primary: `is_error: true` on a `tool_result` block inside a `user` event.
Fallback: content-substring patterns — `"old_string not found"` (Edit), `"file not found"` / `"no such file"` (Read).

Note: Claude Code transcripts carry tool results in `user`-type events (not `tool_result`-type events), and failures are signalled via `is_error: true`, not `"Exit code: N"` strings.

## §8 - HTML artifact spec

Pipeline retros write .workflow/retro-findings.json (machine-readable JSON). Interactive retros write .retros/YYYY-MM-DD-summary.md only. HTML artifacts (.retros/*.html) are reserved for tooling that consumes them - the retro-html-guard.py hook blocks HTML writes outside PPDS_PIPELINE=1. The conversation IS the analysis in interactive mode.

If HTML output is needed (e.g., audit dashboards), generate via python scripts/retro_html_generator.py from inside a pipeline-mode invocation.

## §9 - Phase 5 one-at-a-time pattern

Evidence: PR #1051 retro (2026-05-14) — operator demanded per-finding explanation before approval: *"go back through one by one you explain to me the thing you found what you suggest and i will tell you do it"* and *"why didn't you make a recommendation and provide rationale and then ask for my opinion and decision?"*

**Template (repeated F1–FN):**

```
## F<N> — <title>

**What I found:** <one paragraph: observation, evidence, impact>

**My recommendation:** <DO NOW / DEFER / DROP / RESEARCH-FIRST> — <one-sentence lean>

**Rationale:**
- <bullet 1>
- <bullet 2>

**Your call?**
```

One finding per turn. AI lean stated. Rationale as bullets. Wait for operator response (go / change / drop / defer) before the next finding. If the operator redirects, return with a different recommendation — not a menu of alternatives. For sub-questions inside a finding, apply `interaction-patterns.md §5` per sub-question.

After all findings are decided: ONE confirmation turn restating the final plan. Then ask "Want the meta now, or done?" before surfacing meta-observations.

**Why retro differs from §4 bulk plan:** Retro findings carry significant rationale and sub-questions; bulk approval is high-risk. The operator's ground truth: individual explanation before approval. Falsification: an operator who explicitly prefers bulk approval → revert this rule.

**Consumers that retain §4 (bulk plan):** `/backlog` triage, `/pr` Gemini comment triage, `/dependabot-triage`, `/review` suggestion acceptance — these involve larger N with simpler per-item decisions and have not been disproven.

## §10 - Phase 6 envelope dispatch details

### DEFER routing
Invoke `Skill(skill="backlog")` for each DEFER finding. Pass the finding description and recommended labels. NEVER draft the issue body inline — the backlog skill owns the artifact.

### DO NOW Lane B/C grouping and envelope
Group by PR boundary (interaction-patterns.md §2): same file(s) → same group; same subsystem tightly coupled → same group; otherwise separate groups.

For each group, prepare a plan file if none exists: write `.plans/retro-<YYYY-MM-DD>-<finding_id>.md` with the finding description, recommendation, and rationale as implementation guidance.

Write `.workflow/goal-envelope.json` (schema v1.1, per specs/feat-1069-supervisor-pattern.md). Required fields per group entry: `id`, `title`, `branch_suffix`, `plan`, `files`, `size_estimate`, `depends_on`, `ac_refs`. Then spawn:

```bash
python scripts/goal_supervisor.py spawn .workflow/goal-envelope.json --supervisor-worktree .
```

### Rule-drift kind
File as a standalone Lane B PR. Bundle with related code fixes only when they touch the same files.

### Handoff discipline — retro complete boundary
After Phase 9 commit, the retro context is closed. New-artifact requests (issue drafting, design sessions, investigation) start a new task. Invoke the receiving skill rather than synthesizing inline:

| Request | Skill to invoke |
|---------|----------------|
| Issue filing | `Skill(skill="backlog")` |
| Design / spec | `Skill(skill="design")` |
| Investigation | `Skill(skill="investigate")` |
| PR creation | `Skill(skill="pr")` |

Do NOT draft artifact content from retro context; the receiving skill loads its own context.
