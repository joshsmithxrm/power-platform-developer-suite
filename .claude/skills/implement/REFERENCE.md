# Implement — Reference

## §1 - No-Spec Fallback Chain (SKILL.md Input step)

When `$ARGUMENTS` is absent and no spec is found via workflow state:

1. **Check `.plans/context.md`** (written by `/start` from the issue body): if it exists, read it, generate a plan from the issue context and constraints it contains, save to `.plans/`, proceed.
2. **No context.md**: prompt the user — "(1) Run `/design` to create a spec, or (2) describe the work for plan generation."
3. **User chooses `/design`**: stop; instruct user to run `/design` first.
4. **User chooses continue**: ask user to describe the work; generate a plan from their description.

## §2 - Pipeline Mode

When `PPDS_PIPELINE=1` is set, the pipeline orchestrator invoked this skill.
- Execute Steps 1–5 only; skip Step 6 (orchestrator handles gates/verify/qa/review).
- Do NOT use `ScheduleWakeup` — pipeline monitors parent output; sleeping parent is killed.
- Dispatch foreground agents (`run_in_background: false`) or poll background agents at ≤60 s.

## §2 - Model Selection

- **Opus (primary):** >3 sub-steps, complex UI (timelines, query builders, virtual scroll), cross-cutting refactors >10 files, Constitution-sensitive Dataverse changes.
- **Sonnet (lighter):** Mechanical phases — one-liner fixes, find-and-replace, boilerplate, CSS-only, doc updates.

When in doubt: use Opus. Re-dispatch cost > model cost difference.

## §3 - Spec Context Block (injected into every subagent)

```
## Constitution (MUST comply — violations are defects)
{full CONSTITUTION.md content}

## Relevant Specifications — Acceptance Criteria
### {spec-name}.md
{AC table rows}

Your implementation MUST satisfy these criteria. If your task conflicts
with a spec or constitution principle, STOP and report the conflict
to the orchestrator — do not silently deviate.
```

Identify relevant specs by grepping `specs/*.md` for `**Code:**` frontmatter matching touched source paths. Always include `specs/architecture.md`.

## §4 - Agent Dispatch Rules

- Maximize parallelism: 4 independent tasks → 4 simultaneous agents.
- Every agent prompt must include: task from plan, full file paths, read-before-write instruction, build verification command, test command, spec context block, no-shell-redirections reminder, self-check gate (typecheck/lint/build before reporting done).
- **Shared-file guard:** If multiple parallel agents modify the same file, serialize them OR designate one as file owner.
- **Test quality:** Tests must be behavioral — call real functions, assert return values or side effects. Never `inspect.getsource()` or string-match source code. Include a negative case for boundary ACs.

## §5 - Phase Gate Sequence

After each phase, in order:

1. Build: `dotnet build PPDS.sln -v q` (or appropriate) → 0 errors
2. Tests: `dotnet test --filter "Category!=Integration" -v q --no-build` → 0 failures
3. AC coverage (Constitution I6): every spec AC has a referenced test that passes
4. Extension changes → `/verify extension` then `/qa extension`
5. TUI changes → `/verify tui`
6. MCP changes → `/verify mcp` then `/qa mcp`
7. CLI changes → `/qa cli`
8. `/review` — impartial review (reviewer sees diff + constitution + ACs only, no plan)
9. Commit the phase

## §6 - Cross-Agent Consistency Check

After collecting results from parallel agents that implement the same concept across surfaces:
- Same field names/types (e.g., `HasOverride` logic in MCP and RPC)
- Nullable/non-nullable agreement between C# DTOs and TypeScript
- Error codes defined in one layer used in other layers
- Default values consistent across surfaces

## §7 - Commit Format

```
feat(scope): Phase N - concise description

Bullet points of what was added/changed.

Co-Authored-By: {format from system prompt}
```

One commit per phase. Parallel streams within a phase group share one commit.

## §8 - Goal Loop (Step 5.5)

After all phases committed, if spec has `**Verification:**` frontmatter:

```python
from goal_loop import read_goal_from_spec, run_until_green, GoalLoopOutcome
goal = read_goal_from_spec(spec_path)
result = run_until_green(goal, attempt_fix=dispatch_fix_agent)
```

Outcomes: GREEN → tail; BLOCKED_HARD → raise to operator; ITERATION_CAP/STUCK_OUTPUT → record in PR, still proceed to Step 6. Per-phase fast feedback: probe only (max_iterations=1), log RED, continue.

See `specs/goal-driven-implement.md` for full contract (ACs 01–16).

## §9 - Supervisor Inbox Protocol (Step 5 Phase Entry)

At the start of each phase (before dispatching agents), poll for supervisor directives:

```bash
python scripts/supervisor_msg.py read --consume
```

Process messages in order:

| Kind | Action |
|------|--------|
| `abort` | Stop immediately. Surface the abort directive to the operator. Do not dispatch agents for this phase. |
| `revise` | Apply the feedback from `message`/`payload` to the current plan phase before dispatching. |
| `approve` | Continue without waiting for any user confirmation gate. |
| `note` | Log the message, continue normally. |

Empty inbox → proceed with normal phase execution. The supervisor writes inbox files via:
```bash
python scripts/supervisor_msg.py send <worktree-abs-path> <kind> [--message "text"]
```

## §10 - Orchestrator Rules

1. YOU are the orchestrator — agents do the work, you review and coordinate.
2. Minimize context drain — trust agent summaries; don't read output files unless there's a failure.
3. Parallel by default — if tasks don't depend on each other, run them simultaneously.
4. Sequential when required — respect phase gates and dependency chains.
5. One commit per phase — each phase gate produces exactly one commit.
6. Review before commit — invoke `/review` before committing phase work.
7. Fix before advancing — dispatch fix agents; don't debug yourself.
8. Never skip verification — build + test + review before declaring a phase complete.
9. Continue until done — execute ALL phases; don't stop early.
