# Start — Reference

## §1 — Name Derivation Examples

Derive a kebab-case worktree name from the key nouns in the input:

- "env var auth and CSS fix" → `env-var-auth`
- "plugin registration v1 completion" → `plugin-registration-v1`
- "fix the CSS class inconsistency" → `css-fix`

Prefer the domain noun over the action verb. Drop prepositions and articles.

## §2 — Confirmation Template

Present this block before creating anything:

```
I'll create:
  Worktree:  .worktrees/<name>
  Branch:    feat/<name>
  Issues:    #N, #M
  Work type: (1) Bug fix  (2) Enhancement/refactor  (3) New feature  (4) Docs
             [pre-selected: Bug fix based on type:bug label]
  Launch:    <claude-command>

Good?
```

If no work type is pre-selected (Step 2b left it empty), show `Launch: (depends on work type)` and resolve the launch command after the user picks. If the user suggests a different name, work type, or launch command, use that. The confirmed launch command is used in Steps 5–7 — do not re-derive it from work type.

## §3 — worktree-create.py Rationale

`worktree-create.py` enforces the safety properties that `/start` kept losing in practice (fixes #799):

- Always fetches `origin main` first, so the worktree is based on the current remote tip (not a stale local `main` ref).
- Detects stranded target directories (exist on disk but not registered as worktrees) and refuses loudly rather than producing a silently broken worktree.
- Sanity-checks that `git status` is clean and HEAD matches `origin/main` before handing the worktree back.

Exit code detail:
- `0` — success, proceed to state init
- `1` — stranded directory detected. Surface the message verbatim to the user (includes the cleanup command). **Do NOT auto-delete** — the stranded directory may contain the user's in-progress work.
- `2` — fetch or creation failed. Surface stderr and stop.
- `3` — sanity check failed (dirty index or HEAD != origin/main). Surface the diagnostic and stop; a worktree on a stale base is the #799 failure mode.

Optional flags: `--branch <name>` to reuse/create a non-default branch; `--repo-root <path>` when invoking from a non-cwd repo.

## §4 — Launch Prompt Structure

Field set for the 6b prompt (Task / Branch / Worktree / Work type / Issues / Issue details / Investigation / User intent / First action / Project context):

- **Task** — one-line summary of the work.
- **Branch** — `feat/<name>`
- **Worktree** — absolute path to the worktree directory.
- **Work type** — bug fix / enhancement / feature / docs.
- **Issues** — comma-separated issue numbers.
- **Issue details** — `gh issue view <N> --json title,body` output (title + body) for each issue. Omit if `gh` is unauthenticated.
- **Investigation** — verbatim output from any prior `/investigate` session.
- **User intent** — any specific constraints, scope, or instructions from the user.
- **First action** — the skill to run immediately (e.g., `/implement`, `/design`, or none for bug fixes).
- **Project context** — any CLAUDE.md pointers or critical overrides.

Omit fields that have no content. Skip the entire 6a step if no issues and no investigation context.

## §5 — Return Summary Template

After `start-bg-spawn.py` exits 0, print:

```
  Worktree ready at .worktrees/<name> (branch feat/<name>)
  Issues linked: #N, #M
  Work type: <work type>
  Prompt: <chars> chars delivered verbatim to bg session <short>
  Session ID: <short> (full UUID: <sessionId>)

  Background session spawned. Watch in Agent View — every Claude Code
  surface (CLI, desktop, IDE, claude.ai/code, Slack) shows the new row.
  Open the session interactively any time with: claude attach <short>

  This session's job is done.
```

If the helper returned non-zero, surface its stderr verbatim and stop — no manual-fallback prose to print.
