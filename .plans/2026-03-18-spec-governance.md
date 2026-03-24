# Spec Governance — Implementation Plan

**Spec:** [specs/spec-governance.md](../../specs/spec-governance.md)
**Branch:** `feature/spec-governance`
**Scope:** Session 1 — governance infrastructure only. Session 2 (spec restructuring) is a separate plan.

---

## Phase 1: Constitution Amendment

**Goal:** Add Spec Laws (SL1–SL5) to the Constitution.

**Tasks:**

1.1. Add `## Spec Laws` section to `specs/CONSTITUTION.md` after Resource Laws, with laws SL1–SL5 as specified in the spec.

1.2. Update `CLAUDE.md` Specs section:
- Remove `Index: specs/README.md` reference
- Add reference to `specs/CONSTITUTION.md` Spec Laws
- Keep existing pointers to Constitution and Template

**Verification:** Read `specs/CONSTITUTION.md` and confirm SL1–SL5 present. Read `CLAUDE.md` and confirm updated references.

**ACs covered:** AC-01, AC-17

---

## Phase 2: Spec Template Update

**Goal:** Update the template with Surfaces frontmatter, Changelog, and stronger Code: guidance.

**Tasks:**

2.1. Update `specs/SPEC-TEMPLATE.md` frontmatter:
- Remove `**Version:** 1.0` line
- Add `**Surfaces:** CLI | TUI | Extension | MCP | All | N/A` line after `**Code:**`
- Update `**Code:**` line with guidance comment requiring grep-friendly path prefixes
- Update `**Status:**` to show `Draft | Implemented`

2.2. Add `## Changelog` section to template (before Roadmap):
```markdown
## Changelog

| Date | Change |
|------|--------|
| YYYY-MM-DD | Initial spec |
```

2.3. Add surface section guidance within the Specification section, showing the pattern for CLI/TUI/Extension/MCP subsections.

**Verification:** Read template, confirm Version removed, Surfaces present, Changelog section present, Code: guidance strengthened.

**ACs covered:** AC-02, AC-03, AC-04

---

## Phase 3: Skill Updates — README Migration

**Goal:** Migrate 6 skills from `specs/README.md` lookup to frontmatter grep. Remove hardcoded mappings from `/implement`.

**Tasks are independent — can be parallelized.**

3.1. Update `.claude/commands/spec.md`:
- Step 1: Replace `specs/README.md` reference with frontmatter grep instruction
- Step 3: Replace hardcoded cross-reference rules with frontmatter-based discovery
- Add Step 6 instruction: regenerate `specs/README.md` after create/update/reconcile

3.2. Update `.claude/commands/spec-audit.md`:
- Step 1: Replace `specs/README.md` reference with frontmatter grep
- Step 2 (full audit): Replace "Read specs/README.md to get list of all specs" with glob `specs/*.md`

3.3. Update `.claude/commands/gates.md`:
- Replace `Read specs/README.md to map changed files to specs` with frontmatter grep instruction

3.4. Update `.claude/commands/review.md`:
- Step 2: Replace `Map changed files to specs via specs/README.md` with frontmatter grep

3.5. Update `.claude/commands/implement.md`:
- Step 2B: Replace README reference and hardcoded code-path-to-spec mappings with frontmatter grep
- Add instruction: when creating new code paths for a spec, update the spec's `**Code:**` frontmatter

3.6. Update `.claude/skills/ext-panels/SKILL.md`:
- Replace `specs/README.md` reference with frontmatter grep

**Verification:** Grep all updated skills for "README" — zero matches for spec discovery (README may still appear in the context of "regenerate README" in /spec). Grep for hardcoded path mappings in /implement — zero matches.

**ACs covered:** AC-07, AC-08, AC-09, AC-10, AC-11, AC-12, AC-16

---

## Phase 4: Enhanced /spec — Reconcile Mode

**Goal:** Add reconcile mode to the /spec skill.

**Tasks:**

4.1. Update `.claude/commands/spec.md` Step 2 (Determine Mode):
- Add third mode: reconcile. Triggered when spec exists and user requests alignment with code, or when significant code divergence is detected.

4.2. Add reconcile mode behavior to `/spec`:
- Read spec ACs, Code paths, type descriptions
- Read code at Code: paths
- Enumerate public types, methods, behaviors
- Compare against existing ACs (missing, stale, status drift)
- Member-count verification: count public surface area vs AC coverage, warn if <90%
- Present proposed changes for user approval
- After writing: regenerate README, append to Changelog

**Verification:** Read `/spec` skill, confirm three modes documented with reconcile behavior including member-count verification step.

**ACs covered:** AC-13, AC-14

---

## Phase 5: Auto-Generated README

**Goal:** Generate the initial README from frontmatter of all existing specs.

**Tasks:**

5.1. Write a generation script (`scripts/generate-spec-readme.py`) that:
- Globs `specs/*.md` (excluding CONSTITUTION.md, SPEC-TEMPLATE.md, README.md)
- Extracts from each: H1 title, Status, Surfaces (or "—" if missing), Code paths, first sentence of Overview
- Generates markdown table sorted alphabetically
- Writes to `specs/README.md` with auto-generated comment header

5.2. Run the script to generate the initial README.

5.3. Add instruction to `/spec` skill (Step 6) to run the generation script after spec operations.

**Note:** Existing specs don't yet have `**Surfaces:**` frontmatter — the generated README will show "—" for that column until session 2 adds it. This is expected and acceptable.

**Verification:** Run the script. Count specs in generated README vs `ls specs/*.md` count. Verify all match. Confirm auto-generated comment present.

**ACs covered:** AC-05, AC-06, AC-15

---

## Phase 6: Commit & Gates

After all phases complete:

6.1. Commit all changes.
6.2. Run `/gates` to verify nothing is broken (build, lint, test).

---

## Dependency Graph

```
Phase 1 (Constitution) ──┐
                          ├──▶ Phase 3 (Skill Updates) ──▶ Phase 4 (/spec Reconcile)
Phase 2 (Template)  ──────┘                                        │
                                                                   ▼
                                                          Phase 5 (README Gen)
                                                                   │
                                                                   ▼
                                                          Phase 6 (Commit & Gates)
```

Phases 1 and 2 are independent (can be parallelized).
Phase 3 tasks (3.1–3.6) are independent (can be parallelized).
Phase 4 depends on Phase 3.1 (the /spec skill must be migrated before adding reconcile mode).
Phase 5 depends on Phase 2 (template must be updated so the script knows what to scrape) and Phase 4 (the /spec regeneration instruction must be in place).
