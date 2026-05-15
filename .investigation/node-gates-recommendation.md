# Workflow Defects: Node Absence + Pipeline Exit-Code Masking

**Branch:** `fix/node-gates-investigation`
**Date:** 2026-05-15
**Status:** Investigation complete; awaiting operator approval before any fix.

---

## TL;DR

Two independent bugs combined to produce a false-pass on `/gates`:

- **Defect A** is environmental — `fnm` (Fast Node Manager) installs `node`/`npm` under a per-shell PID-keyed directory that is only on `PATH` if `fnm env` ran in the launching shell. Some Claude Code sessions get spawned from a shell context where it didn't, so the captured shell snapshot has no node on `PATH`. About **12.5%** of recent shell snapshots on this machine are in this broken state (5 out of last 40).
- **Defect B** is procedural — the parent session improvised `npm run X 2>&1 | tail -10` to truncate output. Bash's last-segment exit-code semantics meant every npm failure was reported as a pass. The `/gates` skill text does **not** prescribe `| tail`; this was Claude-side improvisation.

The `/gates` skill currently has no node-availability gate and no guidance on safe output truncation, so the bug is fully reproducible by any session with either an unlucky snapshot or a habit of piping through `tail`.

---

## Defect A — Node/npm not findable from Bash tool

### Root cause

Node.js on this machine is installed via **fnm** (managed via WinGet — `Schniz.fnm`). fnm's runtime convention:

1. Each interactive shell calls `fnm env` (typically in a `.bashrc`/`.bash_profile`).
2. `fnm env` mints a fresh directory at `%LOCALAPPDATA%\fnm_multishells\<PID>_<unix-ms>\` containing symlinks to the currently-active node toolchain.
3. That directory is appended to `PATH` for **that shell only**.

Claude Code's `Bash` tool does **not** start a fresh interactive shell per call. Instead it captures a one-time **shell snapshot** at session start (`~/.claude/shell-snapshots/snapshot-bash-<unix-ms>-<rand>.sh`) and `source`s that snapshot for every Bash invocation thereafter. The snapshot bakes in the `PATH` that was active in the launching shell.

**Consequence:** if the shell that launched Claude Code had not yet sourced `fnm env`, the snapshot is missing the `fnm_multishells/<...>` entry, and **no Bash tool call in that session will ever see `node` or `npm`**, period — until the user starts a new session.

### Evidence

| Source | Finding |
|---|---|
| This session's snapshot (`snapshot-bash-1778825851785-05uqe6.sh`) | Contains `/c/Users/josh_/AppData/Local/fnm_multishells/12176_1778723526665` in `PATH`. `npm --version` ⇒ `10.9.4`. ✅ |
| Parent-session-era snapshot (`snapshot-bash-1778815770953-pnlbd7.sh`, May 14 22:29) | **No** `fnm_multishells` entry. Otherwise identical PATH. ❌ |
| Recent snapshots (40 most recent) | 5 of 40 missing fnm (12.5%). Scattered, not clustered — consistent with "sometimes the launching shell hadn't initialized fnm yet." |
| `Schniz.fnm_Microsoft.Winget.Source_8wekyb3d8bbwe` | Present in PATH on every snapshot — fnm binary itself is reachable, only its activated environment isn't. |
| `.github/workflows/*.yml` | All 4 TS-touching workflows use `actions/setup-node@v6`. CI provisions node deterministically, so PRs that touch TS never hit this bug there. |
| `/gates` skill text | Has no preflight check for node availability and no opinion on `tail`. |

### Why it doesn't show up everywhere

- Foreground Claude Code sessions: usually launched from a terminal the user has been working in, where `fnm env` has already run. Snapshot has node. /gates works.
- Background sessions launched by `/start`: `claude --bg` inherits from the daemon's environment, which inherits from whichever shell spawned Claude originally. If `/start` got run from a session that itself had no fnm, the bg session won't either.
- The 5/40 broken snapshots correspond to specific historical Claude launches that happened from PATH-incomplete shells.

### Fix recommendation (operator-actionable)

Pick one of the three; #1 is the cleanest.

1. **Make node user-global, not per-shell** *(recommended)*

   Add fnm activation to PowerShell `$PROFILE` AND bash `~/.bashrc` so every interactive shell gets a multishell PATH entry before Claude Code is launched. Currently neither profile exists (`cat ~/.bashrc` ⇒ "No such file or directory"), which is why activation is sporadic.

   ```powershell
   # In $PROFILE
   fnm env --use-on-cd | Out-String | Invoke-Expression
   ```

   ```bash
   # In ~/.bashrc
   eval "$(fnm env --use-on-cd --shell bash)"
   ```

   After this, every snapshot Claude captures will have fnm in PATH.

2. **Pin the node version system-wide.** Install node directly from nodejs.org (puts it at `C:\Program Files\nodejs\`) and uninstall fnm. PATH entry becomes static and survives any shell context. Trades flexibility (fnm's multi-version support) for reliability.

3. **Make `/gates` defensive.** Add a preflight check in the skill text:

   ```bash
   if ! command -v npm >/dev/null 2>&1; then
       echo "FAIL (preflight): npm not on PATH — node toolchain unavailable in this shell"
       # SKIP all TS gates with explicit FAIL verdict, do NOT mark them PASS
       exit 1
   fi
   ```

   This doesn't fix the environment, but it converts a silent false-pass into a loud, actionable failure. Worth doing **even if #1 is also adopted**, since it costs ~5 lines and protects against unknown future PATH regressions.

**My recommendation:** do **#1 + #3**. #1 fixes the root cause. #3 hardens the skill against any future PATH-shape we haven't anticipated.

---

## Defect B — `npm run X 2>&1 | tail -10` masks the exit code

### Root cause

Confirmed exactly as suspected. POSIX shells (including Git Bash) propagate the exit status of the **last** command in a pipeline by default:

```bash
$ false | tail -10; echo "exit=$?"
exit=0                              # tail succeeded reading stdin → 0

$ set -o pipefail; false | tail -10; echo "exit=$?"
exit=1                              # pipefail propagates the worst exit code

$ false | tail -10; echo "PIPESTATUS=${PIPESTATUS[@]}"
PIPESTATUS=1 0                      # original codes recoverable per-stage
```

So when the parent session ran:

```bash
npm run compile --prefix src/PPDS.Extension 2>&1 | tail -10
```

…and `npm` printed `npm: command not found` to stderr (which was redirected to stdout, then piped to `tail`), `tail` happily read those 10 lines, returned 0, and Claude saw "exit=0 → PASS." The actual `npm: command not found` text was sitting right in the tail output, but Claude didn't read it as a failure indicator because the exit code said otherwise.

### Where the bug was introduced

- **Not in the `/gates` skill text.** I read `.claude/skills/gates/SKILL.md` end-to-end — every command is bare (e.g. `npm run compile --prefix src/PPDS.Extension`). No `| tail`, no `| head`, no `2>&1 |` patterns anywhere in the skill.
- **In Claude's improvisation.** The parent session decided to truncate output with `| tail -10` to keep its context window manageable. This is a reasonable instinct but a foot-gun without `set -o pipefail`.
- **Compounded by the lack of any project-level shell discipline.** There's no `set -euo pipefail` boilerplate in skill commands, no hook that flags suspicious pipelines.

### Fix recommendation

Three layers, in order of importance:

1. **Add explicit guidance to `.claude/skills/gates/SKILL.md`** — a short "Output handling" section near the top:

   > **Never pipe gate commands through `tail`/`head` without `set -o pipefail`.** Bash returns the last pipeline command's exit code, so `npm run compile | tail -10` reports PASS even when npm errored out. If you must truncate output, use `set -o pipefail` first, or check `${PIPESTATUS[0]}` explicitly, or capture to a file and `tail` the file separately:
   >
   > ```bash
   > npm run compile --prefix src/PPDS.Extension >.gates.log 2>&1 || { tail -20 .gates.log; exit 1; }
   > ```

2. **Generalize to a workflow-wide convention** in `CLAUDE.md` under NEVER:

   > Pipe a gate/test command through `tail`/`head`/`grep` without `set -o pipefail` — the trailing command's exit code masks the real one. Capture to a log file and `tail` the file instead.

3. **Optional hook (T2)** — `scripts/hooks/check-pipefail.py` that scans tool-call shell strings for `| tail`/`| head`/`| grep` without a preceding `pipefail` and warns. Probably overkill for now, but cheap to add later if the pattern recurs.

**My recommendation:** do **#1 + #2**. #3 only if we see the pattern again in retros.

---

## Decisions (resolved with operator, captured in this PR)

1. **fnm vs system-node**: keep fnm. Root-cause fix is to make fnm activation user-global by adding it to `~/.bashrc` (PowerShell `$PROFILE` already has it).
2. **PR scope**: this PR ships all fixes — the `start-bg-spawn.py` pass-through, the `/gates` SKILL.md preflight + pipefail guidance, REFERENCE.md §8 with rationale and safe patterns, and the past-PR audit results.
3. **Shell profile**: `~/.bashrc` is a machine-local change applied outside the repo. PowerShell `$PROFILE` already activates fnm — no edit there.
4. **Past-PR audit**: run (see "Past-PR audit" section below) — verdict was that no merged PR shipped broken code, because CI's `actions/setup-node` matrix was authoritative throughout the affected period.
5. **GitHub issues**: defer. The two follow-up candidates (T2 hook for tail-without-pipefail enforcement; snapshot inspection tool for PATH-shape drift detection) are not needed now. File only when a retro surfaces the pattern recurring.

---

## Past-PR audit (2026-05-13 onward)

Per operator request, audited every merged PR since the broken-snapshot pattern first appears in the snapshot record (earliest = 2026-05-13 12:36 = `1778693736381-5xs18t.sh`).

### Scope

24 PRs merged into `main` since `2026-05-13T00:00:00Z`. Classified by TS touch:

| Category | Count | PRs |
|---|---|---|
| .NET-only / Python-only / config / CI workflow | 12 | #1059, #1057, #1056, #1053, #1051, #1050, #1049, #1046, #1045, #1041, #1038, #1035, #1034, #1022, #1018, #1005, #1004 — no TS gates run, immune to Defect A |
| TS lockfile/dependabot only | 8 | #1044, #1043, #1042, #1019, #1003, #1002, #1001, #1000, #999 — generated by dependabot, all passed CI's `actions/setup-node` matrix |
| **Real-code TS changes** | **2** | **#1040** (plugin trace Status filter), **#1016** (one `types.ts` line) |

### Findings on the 2 real-code TS PRs

**#1040 — `fix(extension): plugin trace Status filter ignored`**
- Merge SHA: `57d316ee9b9eed29020b81484bbb5f10d06d36e8`
- CI status at merge: `build: SUCCESS`, `extension: SUCCESS`, `Pre-Merge Gate: SUCCESS`. The `build` workflow runs `npm ci`, `npm run typecheck:all`, `npm run lint`, `npm run dead-code`, `npm run compile`, `npm test` (via `actions/setup-node@v6`).
- Verdict: **clean**. Whatever local /gates said during PR authoring, CI's TS matrix is authoritative and passed.

**#1016 — `fix(metadata): surface publish requirement on Update*Async`**
- Merge SHA: `9f68a9af37ef1b5ba7d9bb5821453620adade735`
- TS touch: 3 added lines in `src/PPDS.Extension/src/types.ts`. No `.ts` logic changes.
- CI status at merge: `build: SUCCESS`, `test: SUCCESS`. Same TS matrix as above.
- Verdict: **clean**. Single-type-export edit; impossible for it to ship broken even if local /gates was a no-op.

### What the audit *can't* answer

- Whether any of the 18 PRs above had local /gates report PASS while CI ran the TS gates that actually caught regressions during review. There's no preserved per-PR /gates session log to compare against. Closed-without-merge PRs were not enumerated.
- Whether implementers spent time chasing false-PASS state (a workflow-quality cost, not a code-correctness cost). Not recoverable without retros.

### Overall verdict

**No merged PR shipped broken code due to Defect A.** CI is authoritative on the TS gate matrix and has run reliably (`actions/setup-node@v6` is deterministic). The bug's blast radius is workflow-velocity (false-PASS sessions wasted time and produced misleading status) rather than code-correctness (merges were gated by CI either way).

Defect A is therefore a **process** bug, not a **product** bug. The 12.5% snapshot breakage rate did real damage to AI session reliability but did not corrupt the codebase. Fix priority remains high (workflow-friction multiplier) but no rollbacks or hot-fixes needed on prior PRs.

---

## Out of scope (intentionally not touched)

- No edits to `.claude/skills/gates/SKILL.md` — recommendation only, per task constraints.
- No installation/uninstallation of node, fnm, or anything else.
- No edits to other scripts.
- The only code change in this branch is the `--permission-mode` pass-through commit on `scripts/start-bg-spawn.py`, which is what enabled bypass-spawning for this investigation in the first place and is already tested (12/12 unit tests pass on the patched file).
