# Implement — Reference

## §1 - No-Spec Fallback Chain (SKILL.md Input step)

When `$ARGUMENTS` is absent and no spec is found on the branch, read `specs/CONSTITUTION.md` FIRST — every fallback path below generates a plan, and a plan generated without the Constitution loaded produces non-compliant phases. Then:

1. **Check `.plans/context.md`** (if present, e.g. written from an issue body): read it, generate a plan from the issue context and constraints it contains, save to `.plans/`, proceed.
2. **No context.md**: prompt the user — "(1) Run `/design` to create a spec, or (2) describe the work for plan generation."
3. **User chooses `/design`**: stop; instruct user to run `/design` first.
4. **User chooses continue**: ask user to describe the work; generate a plan from their description.

## §3 - Model Selection

- **Opus (primary):** >3 sub-steps, complex UI (timelines, query builders, virtual scroll), cross-cutting refactors >10 files, Constitution-sensitive Dataverse changes.
- **Sonnet (lighter):** Mechanical phases — one-liner fixes, find-and-replace, boilerplate, CSS-only, doc updates.

When in doubt: use Opus. Re-dispatch cost > model cost difference.

## §4 - Spec Context Block (injected into every subagent)

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

## §5 - Agent Dispatch Rules

- Maximize parallelism: 4 independent tasks → 4 simultaneous agents.
- Every agent prompt must include: task from plan, full file paths, read-before-write instruction, build verification command, test command, spec context block, no-shell-redirections reminder, self-check gate (typecheck/lint/build before reporting done).
- **Shared-file guard:** If multiple parallel agents modify the same file, serialize them OR designate one as file owner.
- **Test quality:** Tests must be behavioral — call real functions, assert return values or side effects. Never `inspect.getsource()` or string-match source code. Include a negative case for boundary ACs.

## §6 - Phase Gate Sequence

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

## §7 - Cross-Agent Consistency Check

After collecting results from parallel agents that implement the same concept across surfaces:
- Same field names/types (e.g., `HasOverride` logic in MCP and RPC)
- Nullable/non-nullable agreement between C# DTOs and TypeScript
- Error codes defined in one layer used in other layers
- Default values consistent across surfaces

## §8 - Commit Format

```
feat(scope): Phase N - concise description

Bullet points of what was added/changed.

Co-Authored-By: {format from system prompt}
```

One commit per phase. Parallel streams within a phase group share one commit.

## §11 - Orchestrator Rules

1. YOU are the orchestrator — agents do the work, you review and coordinate.
2. Minimize context drain — trust agent summaries; don't read output files unless there's a failure.
3. Parallel by default — if tasks don't depend on each other, run them simultaneously.
4. Sequential when required — respect phase gates and dependency chains.
5. One commit per phase — each phase gate produces exactly one commit.
6. Review before commit — invoke `/review` before committing phase work.
7. Fix before advancing — dispatch fix agents; don't debug yourself.
8. Never skip verification — build + test + review before declaring a phase complete.
9. Continue until done — execute ALL phases; don't stop early.
