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

## §6 — Shipped-Source Check

When `$ARGUMENTS` references an existing branch as a source of work to finalize/resume, that source can already be on `main` (squash merge). Spawning an agent with a "remaining work" brief in that case wastes a session and risks bad rework. Detect and abort.

Detection signal — kebab branch refs in `$ARGUMENTS` like `fix/<x>`, `feat/<x>`, `chore/<x>`, `bug/<x>`, etc., that are **not** the new branch being created. If none, skip the check.

For each detected source branch `<src>`:

```bash
git rev-parse --verify "<src>" >/dev/null 2>&1 || continue   # not a real branch, skip
files=$(git diff origin/main.."<src>" --name-only)
[ -z "$files" ] && continue                                   # no touched paths -> nothing to check
diff=$(git diff "<src>"..origin/main -- $files)
```

If `diff` is empty (no output for ANY of the files the branch touched), `<src>` is fully reflected on `origin/main`. **Abort** the /start with this message:

> Source branch `<src>` appears to have already shipped — `git diff <src>..origin/main` is empty across all <N> files it touched. Likely squash-merged. Re-check the launch brief before retrying — if this branch's work is done, drop it from the brief; if there is genuinely unshipped follow-up work, name it explicitly and re-invoke /start.

Do not create a worktree or spawn. If the user replies that the check is wrong (e.g. the branch ref was incidental, not a "finalize this" reference), they can re-invoke /start with a brief that no longer names the shipped branch as remaining work.

## §7 — Design-Gate Handoff Procedure

When a worker reaches Step 3 of its workflow contract (spec + plan authored, phase=blocked):

1. **Worker presents and stops** — the worker's last message in Agent View summarizes the
   spec and plan. Workflow state is `phase=blocked`, `needs="spec ready for review"`.

2. **Operator attaches** — `claude attach <short>` from any terminal, or click the session
   row in Agent View. The worker resumes from the operator's reply.

3. **Operator approval forms:**
   - Approve: any affirmative reply ("approved", "looks good", "proceed") → worker runs
     `python scripts/pipeline.py`
   - Request changes: worker incorporates them, re-presents spec, waits again
   - Abort: worker sets `phase=abandoned` and stops

4. **No interruptions after approval** — pipeline.py runs unattended:
   /implement → /gates → /verify → /qa → /review → /converge → /pr.
   pr_monitor.py handles Gemini triage and CI-fix rounds automatically.

5. **Final summary** — when pr_monitor exits, Claude Code re-engages the worker via
   Bash run_in_background=true completion. The worker reads
   `.workflow/pr-monitor-result.json` and produces one final message with actual PR state.

**Operator touch-points per worker:** design approval (one reply) + final PR review.
All automation runs between those two touch-points.

## §8 — Prompt File Writing (Why Write Tool, Not Heredoc)

Write the launch prompt to a temp file via the Write tool, not a shell heredoc. A heredoc terminator (`PROMPT_EOF`) appearing as a line inside the prompt would close the heredoc early; the Write tool is byte-exact and immune to terminator collisions.

After `start-bg-spawn.py` exits 0, delete the temp file. The daemon has already read the prompt at that point — keeping it on disk leaks task content into temp.
