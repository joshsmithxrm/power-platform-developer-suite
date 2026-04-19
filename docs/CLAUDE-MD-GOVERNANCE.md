# CLAUDE.md Governance

CLAUDE.md is the most expensive context in the repo. It is loaded into every
session, costs tokens on every turn, and competes with the Claude Code system
prompt's existing ~50 instructions for instruction-following budget. A
bloated CLAUDE.md does not just clutter — it actively degrades Claude's
adherence to the rules that matter.

This doc is the gate: what belongs in CLAUDE.md, what does not, and how the
hook chain enforces both.

## The 4-Question Test

A line belongs in CLAUDE.md only if **all four** are true:

1. **Globally relevant** — true in EVERY session, regardless of task.
2. **Behavior-shaping** — without it, Claude does the **wrong thing**, not
   merely a suboptimal one.
3. **Not auto-discoverable** — Claude cannot reasonably find it via Read /
   Grep / Glob when the situation arises.
4. **Stable** — will not change meaningfully in the next 90 days.

If any answer is "no", the line does not belong in CLAUDE.md. Route it
elsewhere using the table below.

## Routing Rules

| Failure mode | Destination |
|--------------|-------------|
| Fails Q1 (situational) | Skill: `.claude/skills/<name>/SKILL.md` |
| Fails Q2 (cosmetic / merely-good-advice) | Delete |
| Fails Q3 (visible in code) | Code comment, XML doc comment, or delete |
| Fails Q4 (volatile) | Spec or `.plans/` doc; link from spec index |
| Must-always-happen rule | Hook: `.claude/hooks/` or `scripts/hooks/` + analyzer |
| Procedural workflow (more than 3 steps) | Skill |
| Reference data (tables, mappings) | `docs/` |
| Tech stack / file structure | `README.md` |
| Build / test / contributing procedure | `CONTRIBUTING.md` |

## Worked Examples

### KEEP — passes all 4

> Use Application Services for all persistent state — single code path for
> CLI/TUI/RPC.

- Q1 globally relevant: yes, every session touches state.
- Q2 behavior-shaping: yes, agents will create ad-hoc state plumbing without it.
- Q3 not auto-discoverable: yes, the Application Services pattern is
  conventional, not enforced by types.
- Q4 stable: yes, this has been the architecture since v0.

### DELETE — fails Q3

> Tech Stack: .NET 4.6.2, 8.0, 9.0, 10.0; Terminal.Gui 1.19+

Visible in every csproj's `TargetFramework`. Agents read the csproj before
adding code. Belongs in README.md, not CLAUDE.md.

### MOVE TO SKILL — fails Q1

> Test Conventions: Application Services use mocked deps with `Unit` trait;
> Dataverse SDK logic uses FakeXrmEasy; ...

Only relevant when writing tests. Auto-loads on demand if placed in a skill.
Lives in `.claude/skills/test-conventions/SKILL.md`.

### MOVE TO HOOK — must-always rule (fails the "advisory is enough" check)

> NEVER regenerate `.snk` files — breaks strong naming.

Categorical never. Should not depend on Claude reading and following advice.
Implemented as `.claude/hooks/snk-protect.py` (PreToolUse on Edit/Write
matching `*.snk`).

### MOVE TO ANALYZER — must-always C# rule

> Use bulk APIs (`CreateMultiple`, `UpdateMultiple`) — 5x faster than
> `ExecuteMultiple`.

Already enforced by `UseBulkOperationsAnalyzer` in PPDS.Analyzers. The build
fails if violated. Listing it in CLAUDE.md is dead code.

## Enforcement Chain

Three layers protect CLAUDE.md from drift:

| Layer | When | What |
|-------|------|------|
| `claudemd-line-cap.py` | PreToolUse on Edit/Write | Blocks an edit that would push the file past 100 lines. Fast feedback at edit time. |
| `claudemd-gate.sh` | pre-commit | Blocks commit if CLAUDE.md is in the staged diff and either (a) post-edit line count > 100 or (b) commit message lacks the `[claude-md-reviewed: YYYY-MM-DD]` marker. Catches anything that bypassed the PreToolUse layer (manual editors, other agents). |
| `snk-protect.py` | PreToolUse on Edit/Write | Adjacent enforcement — replaces the previous CLAUDE.md `.snk` NEVER rule with a deterministic block. |

If a hook misfires, run with `git commit --no-verify` only after fixing the
underlying issue (hooks are not noise to be silenced — investigate first).

## The `[claude-md-reviewed: YYYY-MM-DD]` Marker

Every commit that touches any `CLAUDE.md` must include this line in the
commit message body:

```
[claude-md-reviewed: 2026-04-18]
```

Format:

- Literal prefix `[claude-md-reviewed: ` (case-sensitive).
- ISO 8601 date `YYYY-MM-DD` — typically today.
- Closing `]`.

The marker is not a magic incantation. It is a checkpoint forcing the
committer to acknowledge the 4-question test before adding to CLAUDE.md. The
mechanical check exists because past behavior has been to fix-forward and
never subtract: the v1-prelaunch audit added analyzer rules but did not
sweep CLAUDE.md to remove the now-redundant lines. The marker defends
against that pattern.

A reviewer can ask the committer to elaborate on the marker if the change
seems to fail the test. The marker is the conversation prompt; the test is
the substance.

## Decision Tree

> "I want to add a rule."

```
Is it true in EVERY session?
  No  -> Skill (.claude/skills/<name>/SKILL.md)
  Yes -> Q2

Does removing it cause Claude to do the WRONG thing (not just suboptimal)?
  No  -> Delete or write a code comment instead
  Yes -> Q3

Can Claude figure it out from Read / Grep / Glob when relevant?
  Yes -> Delete; trust the discovery
  No  -> Q4

Will this still be true in 90 days?
  No  -> Spec or .plans/, not CLAUDE.md
  Yes -> Q5

Must this happen 100% of the time, no exceptions?
  Yes -> Hook (.claude/hooks/) or analyzer (PPDS.Analyzers/), not CLAUDE.md
  No  -> KEEP — add to CLAUDE.md
```

## Pruning Cadence

Once a quarter (or whenever a CLAUDE.md change feels coerced), apply the
4-question test to every existing line. The audit pattern:

1. Read each line.
2. Run it through the 4 questions.
3. If it fails, route per the table.
4. Commit the deletions with the marker.

This protects against drift more than it does from any individual addition.

## Pattern Library — What "Drift" Looks Like

Six anti-patterns observed in the v1-launch hygiene audit:

1. **Pseudo-Constitution Drift** — paraphrasing rules already in
   `specs/CONSTITUTION.md`. CLAUDE.md should POINT to canonical sources, not
   restate them. Every paraphrase is a future drift bug.
2. **Should-Be-An-Analyzer Rules** — performance rules (pool usage, bulk
   ops) duplicated in CLAUDE.md when the build already fails on violation.
3. **Procedural Docs Masquerading as Rules** — "the pre-commit hook runs
   dotnet build" is reference, not a rule. Belongs in CONTRIBUTING.md.
4. **Skill / Doc Pointers That Skills Already Provide** — Claude Code's
   skill system self-advertises every skill at session start. Mentioning
   `/backlog` in CLAUDE.md is double-loading.
5. **Volatile Pointers Into `.plans/` (Which Is Gitignored)** — pointing at
   ephemeral plans that don't exist for a fresh clone is a self-contradiction.
6. **Vague Aspirations** — "write for the user's goal", "use clean code".
   Vague rules degrade Claude's instruction-following per HumanLayer
   research. Prefer concrete pointers over abstract advice.

## References

- Anthropic best-practices: https://code.claude.com/docs/en/best-practices
- HumanLayer "Writing a Good claude.md":
  https://humanlayer.dev/blog/writing-a-good-claude-md
- AGENTS.md spec (Linux Foundation): https://agents.md
- Hygiene audit findings (V1-Launch retro): see `.plans/retro/findings/E-claudemd-hygiene.md`
  for the per-line verdict table that drove the v1.0 cleanup.
