# Devcontainer Git State Sync

**Date:** 2026-02-11
**Branch:** fix/push-divergence-handling
**File:** scripts/devcontainer.ps1

## Problem

After initial clone, the container has no mechanism to sync git state from origin. This causes:
- Stale tracking refs (container never learns about origin changes)
- Main falls behind (only set at clone time)
- Stale/deleted branches accumulate (no pruning)
- Worktree history divergence after host-side rebases
- Push failures when origin was force-pushed

## Design Decisions

1. **Sync only updates `refs/remotes/origin/*`** — never touches local branches or worktree working trees
2. **Main checkout fast-forwarded** — only if clean, on main, and fast-forwardable
3. **No auto-rebase of worktrees** — push already handles rebase-on-divergence
4. **Full fetch scope** — all origin branches bundled, enables pruning
5. **Sync on every `up`** — except fresh clones (already current)
6. **Conflict → Claude session** — push launches Claude Code in mid-rebase state instead of aborting

## New: `sync` Command

Updates the container's knowledge of origin. Equivalent to a credentialless `git fetch --prune`.

### Mechanism

```
Host                              Container
-----                             ---------
git fetch origin --prune
bundle create (all origin refs)
        ---- docker cp ---->
                                  delete all refs/remotes/origin/*
                                  git fetch from bundle
                                  (recreates only current origin refs)

                                  if main checkout is clean + FF-able:
                                    git merge --ff-only origin/main

                                  report worktree status
```

### Output

```
  Fetching origin state...
  Bundling 12 refs from origin...
  Updated remote tracking refs (12 refs, pruned 2 stale).
  Fast-forwarded main (abc1234 -> def5678).

  Worktree status:
    query-engine-v3   fix/query-perf     2 ahead, 0 behind
    auth-refactor     auth-refactor      0 ahead, 5 behind
    old-feature       old-feature        ! branch deleted on origin
```

## Changed: `up` Command

Calls sync after container start, before worktree operations.

```
up flow (existing volume):
  1. Ensure volumes exist
  2. Start container
  3. NEW: Sync-ContainerFromOrigin (skip if fresh clone)
  4. Repair host worktree .git files
  5. Repair container worktree .git files
  6. Create missing worktrees (now using fresh refs)
```

## Changed: `push` Command — Conflict Handling

Current: rebase fails -> abort -> print error -> exit.
New: rebase fails -> leave mid-rebase -> launch Claude Code -> exit.

### Plan mode toggle

```powershell
param(
    ...
    [switch]$NoPlanMode
)
```

Default (plan mode on): Claude analyzes conflicts and presents a plan before resolving.
With -NoPlanMode: Claude resolves conflicts directly.

### Flow

```
push (divergence detected):
  1. Bundle origin state, send to container     (existing)
  2. Container rebases onto origin              (existing)
  3. If rebase conflicts:
     a. DON'T abort the rebase
     b. Print what happened
     c. Launch Claude Code with conflict prompt
     d. After Claude exits, exit push
     e. User re-runs push when ready
```

## New Helper: `Sync-ContainerFromOrigin`

Extracted as a function because both `sync` and `up` call it.

```
function Sync-ContainerFromOrigin {
    1. Host: git fetch origin --prune
    2. Host: collect refs/remotes/origin/* list
    3. Host: git bundle create <all origin refs>
    4. docker cp bundle into container
    5. Container: delete all refs/remotes/origin/*
    6. Container: git fetch from bundle
    7. Container: ff main if clean
    8. Container: report worktree status
    9. Clean up bundles
}
```

## Scope

- ~80-100 new lines
- ~5 lines modified in existing push block
- One new function, one new switch case
- Minor edits to up and push
- No changes to: shell, claude, ppds, down, status, send, reset
