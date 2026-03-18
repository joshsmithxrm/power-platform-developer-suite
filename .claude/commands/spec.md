# Spec

Create or update a specification following PPDS conventions. Ensures consistency, cross-references related specs, and enforces numbered acceptance criteria.

## Input

$ARGUMENTS = spec name (e.g., `connection-pooling` for existing, `new-feature` for new)

## Process

### Step 1: Load Foundation

Read these files before doing anything else:
- `specs/CONSTITUTION.md` — non-negotiable principles (includes Spec Laws SL1–SL5)
- `specs/SPEC-TEMPLATE.md` — structural template
- Glob `specs/*.md` and grep each for `**Code:**` lines to build a code-path-to-spec map. This replaces the README index.

### Step 2: Determine Mode

**If spec exists** (`specs/$ARGUMENTS.md` found):
- Read the existing spec
- Read the code files referenced in the spec's `Code:` header line
- Identify drift: does the code match what the spec describes?
- Identify missing ACs: does the spec have numbered acceptance criteria?
- Present findings to user before making changes

**If spec is new** (`specs/$ARGUMENTS.md` not found):
- Search existing specs for overlapping scope — check that this isn't already covered
- If overlap found, ask user: update existing spec or create new one?
- Proceed to authoring

### Step 3: Cross-Reference Related Specs

Use the code-path-to-spec map from Step 1 to find related specs. Match the spec's Code: paths against the directories being touched. Always include `specs/architecture.md` and `specs/CONSTITUTION.md`.

### Step 4: Author/Update Spec

**For new specs:** Walk through the template section by section with the user. One section at a time, ask if it looks right before moving to the next. Follow the brainstorming pattern — multiple choice questions when possible, open-ended when needed.

**For existing specs:** Present the drift analysis and proposed changes. Get user approval before modifying.

**For both:**
- Ensure every section from the template is addressed (even if just "N/A" for optional sections)
- Cross-check against constitution — hard stop on violations
- Cross-check against related specs — flag contradictions

### Step 5: Enforce Acceptance Criteria

This is a HARD GATE. The spec is not complete without:
- Numbered AC IDs (AC-01, AC-02, ...)
- Each criterion is specific and testable (not vague prose)
- Test column populated where tests exist (can be "TBD" for new specs)
- Status column accurate

If the user tries to skip ACs, remind them: Constitution I3 requires numbered acceptance criteria before implementation begins.

### Step 6: Finalize

1. Write/update the spec file at `specs/$ARGUMENTS.md`
2. Regenerate `specs/README.md` by running `python scripts/generate-spec-readme.py` (or manually scraping frontmatter from all specs if the script doesn't exist yet)
3. Commit:
   ```
   docs(specs): add/update {spec-name} specification

   Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
   ```

## Rules

1. **Always read foundation first** — constitution and spec template. No exceptions.
2. **Always cross-reference** — no spec exists in isolation.
3. **ACs are mandatory** — the spec is incomplete without them.
4. **One section at a time** — don't dump the entire spec at once.
5. **Constitution violations are hard stops** — if a spec proposes something that violates the constitution, flag it immediately.
6. **Don't invent requirements** — ask the user. The spec captures their intent, not your assumptions.
