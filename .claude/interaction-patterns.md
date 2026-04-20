# Interaction Patterns

Durable conventions for how AI agents work in this repo. Referenced from `CLAUDE.md` so every skill inherits them.

## 1. Agent topology — three lanes

**Lane A — read-only / research.** `Agent` tool in the calling session, no isolation. Returns findings inline. Use for verification, investigation, analysis.

**Lane B — small code change.** `Agent` tool with `isolation: "worktree"`. Agent edits in its own worktree, returns a PR or patch. Calling session reads the diff via `gh`.

**Lane C — larger code change.** New Claude Code session in a dedicated worktree (`/dispatch-worker` skill when available; manual `git worktree add` + `claude` otherwise). Calling session monitors via `gh pr view`.

### Lane assignment (countable, no time estimates)

- **Lane A**: no file writes; output is analysis.
- **Lane B**: ≤3 files AND ≤200 LOC net change; no new abstraction / skill / config surface; clearly correct fix (no design decision); unit-test additions/tweaks only.
- **Lane C**: >3 files OR >200 LOC; OR introduces new abstraction / skill / config surface; OR requires design decision; OR cross-cutting (public API or multiple subsystems).

## 2. Grouping agents by PR boundary

One agent or session = one PR. Priority rules:

1. Same file(s) → same agent, always.
2. Same subsystem, tightly coupled → same agent.
3. Same subsystem, independent → separate agents, parallel.
4. Different subsystems → separate agents, parallel.

**Rationale**: the PR is the atom of quality signal (review, checks, rollback). Matching agent scope to PR scope gives each agent one clean signal.

## 3. DO NOW / DEFER / DROP heuristic

**DO NOW when any apply:**
- Lane A — always.
- Lane B — when standalone.
- Clearly correct fix, no design choice needed.
- Blocks or compounds another DO NOW.

**DEFER to backlog when any apply:**
- Lane C — typically.
- Blocked on unlanded work.
- Needs spec / design session.
- Fundamentally separate concern from current session.

**DROP with reason:**
- `wontfix` — state why.
- `already-fixed` — link PR / commit.

**Quality floor when doing:** do it right, not the quick version. If a DO NOW item turns out bigger than expected, surface it — don't paper over.

**Bias:** prefer DO NOW when the item is simple. Backlog items rot.

## 4. Plan-with-defaults UX for N-decision interactions

When presenting N findings or decisions to the user, the AI:

1. **Pre-triages every item** with a recommended verb (DO NOW / DEFER / DROP / RESEARCH-FIRST) and a **confidence tag** (HIGH / LOW).
2. **Sends ONE message with two blocks:**
   - **Bulk plan** — HIGH-confidence items, one line each, grouped by verb. User replies `accept` to ratify, or calls out IDs to override.
   - **Contested** — LOW-confidence items, **capped at 5**. Each gets 2–3 sentences of framing + the AI's lean + per-item ask.
3. **Serializes meta-recommendations.** Does not co-present meta-observations with findings. After findings are settled: "Want the meta now, or done?"
4. **Closes on confirmation.** One summary turn restating the final plan before writing any durable state.

**If more than 5 are genuinely contested, the pre-triage isn't strong enough.** Think harder and move items into defensible defaults.

**Applies to**: `/retro` findings triage, `/backlog` issue triage, `/pr` Gemini comment triage, `/review` suggestion acceptance, `/converge` fix iterations, `/cleanup` worktree pruning, dependabot reviews, permission-batch prompts, spec grooming — anywhere N decisions exist.

**Rationale**: Cowan 4±1 working-memory capacity; Hick's Law on choice overload; smart-default approval-workflow pattern; Terraform plan/apply idiom; Anthropic guidance on surfacing uncertainty.

## 5. Communication style for AI-asked questions

When the AI needs a decision:

1. **Recommendation** — one sentence, no hedging.
2. **Why** — 2–5 bullets of rationale, evidence when available.
3. **Where it changes behavior** — when material.
4. **Agree or redirect?** — one question. Not a menu of options.

If a recommendation is rejected, return with a different recommendation — not "here are the other options." The user should not weigh unweighted options.

## 6. HTML artifacts vs. chat interaction

**Chat interaction** (decisions, dialogue): ASCII + markdown only. No HTML, no forms, no browser detours. Chat is synchronous text.

**Persistent artifacts** (reports, dashboards, flow diagrams): HTML + Mermaid where it adds clarity. Generated alongside markdown. Examples:

- `.retros/YYYY-MM-DD-summary.html` — retro summary dashboard
- `.plans/*-flow.html` — process-flow diagrams
- `.retros/findings-index.html` — navigable history

Generated at synthesis/persist phases, not during interactive decision making.

## 7. Decision records — how rules evolve

**Skills are the normative surface.** `.claude/skills/*/SKILL.md`, `CLAUDE.md`, `specs/CONSTITUTION.md`, `.claude/hooks/**` encode what the current rule is.

**PR bodies are the episodic rationale.** When a PR touches any rule surface, the body must include a `## Rule change` section:
- Context — what wasn't working
- Old rule → new rule
- Why
- Falsification — what evidence would revise this

Gemini review blocks the PR if that section is missing from a rule-touching diff.

**Retros are the pruning loop.** `/retro` generates `rule-drift` findings when observed behavior diverges from the rule. Auto-proposes SKILL.md edits + PR body templates.

**Breadcrumbs are optional.** Where a rule is non-obvious, the SKILL.md line may carry `<!-- since: PR#NNN rationale -->`. Not required — required fields rot first.

**Trigger for adding a breadcrumb:**
- Reverses or amends a prior rule → yes.
- Safety-critical (shakedown, permissions, branch protection) → yes.
- Resolves a known contradiction → yes.
- Typo, wording cleanup, additive example → no.

**No separate ADR or decisions directory.** Skills are the current rule; PRs are the change record; retros mine drift. Graveyard prevented by absence of a cemetery.

**Labels**: `type:rule-change` on issues that propose rule changes. `decision-record` label is NOT used (dropped after research showed label drift).

## 8. Rule evolution for this file

To change a rule in this document:

1. File an observation in a `/retro` (tier: observation) with transcript / PR / metric evidence.
2. Or cite external research that contradicts the rule.
3. Open a PR with a `## Rule change` section per §7.
4. Main-session judgment (not subagent) decides.

## References

- `.plans/2026-04-19-retro-flow.md` — retro flow diagram (gitignored, local only)
- `CLAUDE.md` — repo-level rules, references this file
- `specs/CONSTITUTION.md` — non-negotiable principles
