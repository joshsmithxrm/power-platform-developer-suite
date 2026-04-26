# Two-File Skill Pattern

**Purpose:** keep `SKILL.md` <=150 lines so the executor reads only the
procedure it needs to run. Rationale, taxonomies, and worked examples
live in a sibling `REFERENCE.md` and are loaded on demand.

The `skill-line-cap.py` hook (PreToolUse Edit/Write) blocks any edit
that pushes a `SKILL.md` past 150 lines. The cap is mechanical; this
file is the procedure for getting under it.

---

## Heuristic - what stays vs. what moves

`SKILL.md` (procedure - what to execute):

- frontmatter (`name`, `description`)
- numbered steps with concrete commands
- input / output contract
- "Continue with X" pointers
- workflow-state writes
- skip / branch conditions

`REFERENCE.md` (rationale - why and when):

- design decisions and tradeoffs
- taxonomies (label sets, finding tiers, version-bump rules)
- worked examples
- domain glossary
- failure-mode catalogues
- meta-retro / historical context

Test: if a future caller can run the skill correctly without reading
the bullet, it belongs in `REFERENCE.md`.

---

## Reference-loading syntax

Inside `SKILL.md`, when a step needs context from `REFERENCE.md`,
write:

```
Read REFERENCE.md §<N> "<Section Name>" before <action>.
```

(Where `§` is the section symbol; UTF-8 is fine.)

Examples:

- `Read REFERENCE.md §3 "Version-bump matrix" before tagging.`
- `Read REFERENCE.md §5 "Finding taxonomy" before classifying.`

The numbered section markers in `REFERENCE.md` are stable anchors -
keep the order even if you add new sections at the end.

---

## REFERENCE.md section conventions

```markdown
# <Skill> - Reference

## §1 - <Title>
...rationale, examples, taxonomies...

## §2 - <Title>
...
```

- `§N` prefix is mandatory (the SKILL.md citation depends on it).
- Numbering is dense and stable - never renumber existing sections;
  append new ones.
- Each section opens with a one-line summary so a quick scan is enough.

---

## Worked example - `release` skill

`release/SKILL.md` (procedure, ~120 lines):

1. Read `Cargo.toml` / `package.json` for current version.
2. `Read REFERENCE.md §3 "Version-bump matrix" before tagging.`
3. Run `dotnet pack` and `npm publish --dry-run`.
4. Tag and push.
5. Confirm CI green.
6. Write `state.json` release block.

`release/REFERENCE.md` (rationale, no line cap):

- §1 - Channel layout (stable / prerelease / preview)
- §2 - Signing matrix (which keys, which surfaces)
- §3 - Version-bump matrix (semver decision rules with examples)
- §4 - CHANGELOG format and the empty-section rule
- §5 - Platform-specific notes (Windows code-signing certs, Linux
  reproducible builds, macOS notarization)
- §6 - NEVER list (with rationale for each)

The same shape applies to `backlog` and `retro`: the SKILL.md
runs the dispatch / phase loop; REFERENCE.md holds the label
taxonomy, finding tiers, rule-change templates, and HTML artifact
spec.

---

## When to split a new skill

Default to single-file. Split only when one of the following is true:

- `SKILL.md` is approaching 150 lines and the surplus is rationale,
  not procedure.
- A taxonomy or worked example is referenced from multiple steps and
  inlining it would duplicate context.
- The skill has a "playbook" of long examples (3+) that would dwarf
  the procedural steps.

If you are under 150 lines, do not pre-emptively split. The hook
catches the real cases.
