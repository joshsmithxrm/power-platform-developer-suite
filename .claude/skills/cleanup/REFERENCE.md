# Cleanup — Reference

Rationale, taxonomies, and worked templates for the `cleanup` skill. SKILL.md cites sections here by `§N`.

## §1 — Framing rationale

The earlier `cleanup` was too conservative: it stopped at `git branch --merged main`, leaving drift-merged, squash-shipped, superseded, and abandoned branches for the user to investigate. In a 2026-05-15 session the user redirected the skill six times before the work got done (retro at `.retros/2026-05-15-cleanup-session.md`). Once the user escalated to "dispatch parallel investigators", 14 of 16 ambiguous branches turned out to be safe deletes.

This rewrite encodes that pattern as the default. The skill enumerates everything first, classifies in parallel, and only surfaces to the user (a) the SAFE batch in one approval and (b) genuinely undecidable items as per-item prompts. Reduces user back-and-forth from ~6 redirects to 1 bulk approval + up-to-5 per-item prompts.

The single most important phrase: **"when in doubt, classify and act on the safe bucket — only surface what genuinely needs judgment."** Investigators bias toward DELETE-HIGH for clearly-shipped-elsewhere work and DISCUSS-LOW only when unsure.

## §2 — Rule-filter table (SKILL.md §4)

Apply in order; first match wins. Bypasses investigator dispatch.

| Signal | Bucket |
|--------|--------|
| Branch is `main` | PROTECTED (root) |
| Worktree path == current cleanup session's worktree | PROTECTED (current) |
| Branch has an OPEN PR in `pr_by_branch` | PROTECTED (open PR) |
| Branch is the head of an `isRunning: true` session's `cwd` worktree | PROTECTED (active session) |
| Locked worktree, PID alive (or no PID in reason) | PROTECTED (locked) |
| Branch in `git branch --merged main` AND `git log main..<branch>` non-empty | SAFE (merged) |
| Branch in `--merged` AND `git log main..<branch>` empty | SAFE (not-started — 0 divergent commits) |
| Branch's remote tracking ref was freshly pruned this run | SAFE (squash-merged via prune) |
| Branch has a MERGED PR in `pr_by_branch` | SAFE (squash-merged via PR) |
| All divergent commits touch only `.retros/**`, `.workflow/**`, `.claude/state/**` | SAFE (drift-merged via workflow artifacts) |
| `release/*` AND tag-lineage check passes (tag in branch ancestry) | PROTECTED (shipped release) |
| `release/*` AND tag-lineage check fails | AMBIGUOUS (release-lineage-fail) |
| Otherwise | dispatch investigator (§3) |

The workflow-artifact filter is the biggest false-positive remover from the retro: half the "active" branches in that session were active only because `.retros/.workflow` commits accumulated after the real work shipped.

The release-lineage rule reflects: `release/v1.0.0` looked sacred but its commits were on a separate lineage from where `Cli-v1.0.0` actually shipped (the tag is on main; the branch tip is not in main's ancestry). Tags are the source of truth.

## §3 — Investigator brief (SKILL.md §4)

Dispatch all investigators in a single `Agent` message (Lane A, read-only). Issue them in parallel — the only place the skill runs agents concurrently. Sequential execution happens in step 8.

Prompt template:

> **Investigator brief for `<branch>`**
>
> Determine whether this branch's work has already shipped (cherry-picked, squash-merged under a different name, superseded by a later PR) or is genuinely active and unshipped.
>
> Return one structured block with all of:
>
> - `last_author_date` — `git log -1 --format=%ai <branch>`
> - `divergent_commits` — count from `git log main..<branch> --oneline | wc -l`
> - `divergent_loc` — `git diff main...<branch> --shortstat`
> - `paths_touched` — `git log main..<branch> --name-only --pretty=format: | sort -u`
> - `ships_elsewhere` — search `git log main --grep="<branch>"`, `gh pr list --search "head:<branch>"`, and `git log --all --grep="<branch-key-phrase>"`. Look for cherry-picks, squash merges, supersession.
> - `summary` — 1–2 sentences describing what this branch is.
> - `recommendation` — DELETE / KEEP / DISCUSS
> - `confidence` — HIGH / LOW
> - `rationale` — one sentence justifying the recommendation.
>
> Bias: when work clearly shipped elsewhere or the divergence is only workflow artifacts, **DELETE with HIGH**. When unsure, **DISCUSS with LOW** — do not guess. Verify "is this branch actually unshipped?" before recommending KEEP for branches with substantive code.

The investigator MUST verify the ships-elsewhere claim with `git diff` evidence before returning DELETE — the 2026-05-15 retro caught a wrong mission brief because the calling agent skipped that step.

## §4 — Bucketing rules (SKILL.md §5)

| Source | Resulting bucket |
|--------|-----------------|
| Rule-filter PROTECTED | PROTECTED |
| Rule-filter SAFE | SAFE |
| Investigator `DELETE` + `HIGH` | SAFE |
| Investigator `DELETE` + `LOW` | AMBIGUOUS |
| Investigator `DISCUSS` | AMBIGUOUS |
| Investigator `KEEP` | PROTECTED (active, leave alone) |
| Release-lineage failure (no tag in ancestry) | AMBIGUOUS |
| Investigator timeout / malformed return | AMBIGUOUS (LOW) |

Never auto-SAFE a `release/*` branch — even if the investigator says DELETE-HIGH, route to AMBIGUOUS for a human eyeball.

## §5 — Stale-lock detection (SKILL.md §5)

Lock reasons look like `locked claude agent agent-xxx (pid 43216)`. Parse the PID; ambiguous reasons (no PID, no text) stay PROTECTED.

PID-alive check:

```bash
# Windows
powershell -NoProfile -Command "Get-Process -Id <pid> -ErrorAction SilentlyContinue | Select-Object Id, ProcessName"
# Unix
kill -0 <pid> 2>/dev/null && echo running || echo dead
```

Dead PID → `git worktree unlock <path>`, then re-bucket by §2. `--dry-run` records but does not unlock.

## §6 — Zombie + daemon shutdown (SKILL.md §8)

Dead Claude agent sessions leave bash/shell processes in `until ... sleep` loops polling for task output that will never arrive. They hold filesystem locks on the worktree directory, blocking `git worktree remove` and `rm -rf` with "Permission denied" / "Device or resource busy".

Match the **full worktree path** (e.g., `.worktrees/profile-env-ux`), not just the dirname — avoids false matches on short names.

```bash
# Windows
powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*<full-worktree-path>*' } | Select-Object ProcessId, Name, CommandLine"
# Unix
pgrep -af '<full-worktree-path>'
```

Report matches, then kill each (exclude the cleanup session's own PID):

```bash
# Windows
taskkill /PID <pid> /F
# Unix
kill -9 <pid>
```

Wait 2 s after kill so file handles release before the removal command runs.

**Daemon shutdown:** for each `*-session.json` file in the worktree (e.g., `tests/PPDS.Tui.E2eTests/tools/.tui-verify-session.json`):

1. Read `daemonPort`, `daemonPid` from the JSON.
2. `curl -X POST --max-time 5 http://localhost:{port}/shutdown`.
3. On failure, kill `daemonPid` directly (`taskkill /PID {pid} /F` or `kill {pid}`).
4. Delete the session file.

## §7 — Remote-sweep classification (SKILL.md §10)

For each remote branch (`git ls-remote --heads origin`, exclude `main` / default):

| Signal | Classification |
|--------|---------------|
| Has MERGED PR in `pr_by_branch` | SAFE (remote-merged) |
| Has CLOSED-without-merge PR AND no open issue references the branch name | SAFE (closed-no-ship) |
| Tracked by a local branch in a `isRunning: true` session | PROTECTED (in flight) |
| Has an OPEN PR | PROTECTED (open PR) |
| `release/*` AND tag-lineage passes | PROTECTED (shipped release) |
| `release/*` AND tag-lineage fails | AMBIGUOUS (release-lineage-fail — confirm before delete) |
| No PR record AND last commit > 90 days old | AMBIGUOUS (stale-orphan) |
| Otherwise | AMBIGUOUS (no signal) |

Deletion command (one-at-a-time):

```bash
git push origin --delete <branch>
```

Remote-sweep gets its own bulk-approval prompt — it's a second batch, but still one prompt for many items, not per-item.

## §8 — Final report

```
## Cleanup Report

### Removed (worktrees)
| Worktree | Branch | Reason | Forced? |

### Deleted Branches (no worktree)
- <branch> — <reason> (-d / -D)

### Orphans Removed
| Directory | Status |

### Pruned Local Remote Refs
- origin/<branch>

### Remote Branches Deleted (push --delete)
| Branch | Reason |

### Rebased (active)
| Worktree | Branch | Result |

### Skipped / Protected
| Item | Reason |

### AMBIGUOUS — User Decisions
| Item | Decision |

### Stale Locks Recovered
| Worktree | PID | Action |

### Zombie Processes Killed
| PID | Worktree | Process |

### Mid-Rebase Aborted
| Worktree | Phase (pre / post) |

### Sessions Archived
| Session ID | cwd | Last active |

### Investigators
- Dispatched: N
- DELETE (HIGH): N | DELETE (LOW): N | KEEP: N | DISCUSS: N

### Summary
- Local removed: N worktrees, N branches
- Remote deleted: N
- Orphans: N removed, N failed
- Stale locks: N recovered
- Zombies killed: N
- Rebased: N OK, N conflict
- Sessions archived: N
- Protected: N
```

Prefix the title with `[DRY RUN]` when no destructive command ran.

## §9 — Error catalogue

| Error | Recovery |
|-------|----------|
| `merge --ff-only` fails on main | STOP — report; do not force-reset |
| `git remote prune origin` fails | Log warning; classification still works via `--merged` and PR cross-reference |
| Mid-rebase found in pre-scan | `rebase --abort`, record, continue |
| Investigator times out / returns malformed | Treat as DISCUSS-LOW → AMBIGUOUS |
| Worktree removal fails (Permission denied) | Zombies already killed → log "partially removed", do not retry |
| `branch -d` fails | Branch not actually merged — skip and log |
| `branch -D` fails | Unexpected — log for manual investigation |
| Rebase conflict | `rebase --abort`, record, continue |
| Locked worktree (PID alive) | PROTECTED, skip |
| Locked worktree (PID dead) | Unlock, reclassify, proceed |
| Lock reason has no PID | PROTECTED (ambiguous lock), skip |
| `rm -rf` orphan fails | Log as failed, continue |
| `.worktrees/` directory absent | Skip orphan sweep |
| Orphan is symlink/junction (Windows) | Remove link, do not follow target |
| `gh` unavailable | Skip remote sweep; warn in report |
| `mcp__ccd_session_mgmt__list_sessions` unavailable | Skip session-aware protection; warn |
| `archive_session` rejected (subagent mode) | Surface to user; do not retry from a subagent |
| Tag-lineage ambiguous (no matching tag) | Recommendation = DISCUSS → AMBIGUOUS |
| Investigator says KEEP for a branch later found shipped | Surface in report — improvement signal for next retro |

## §10 — Design constraints from the retro

- The permission classifier consistently denies batched cleanup ops (parallel rebase, `branch -d <list>`, `rm -rf` even on empty dirs). All destructive phases must be one-at-a-time by default. Do not retry batched.
- Mid-rebase states from prior cancelled batches accumulate silently. Pre- and post-scan both run; both abort any found.
- `mcp__ccd_session_mgmt__archive_session` is unavailable in unsupervised (subagent) mode. Only the main `/cleanup` session can call it — not investigators.
- The investigator pattern (Lane A, parallel, structured return) is the only agent dispatch in this skill. The executor phase remains sequential.
- The skill is the natural home for session archival proposals — main-session, one bulk-approval prompt, one archive call per session.
