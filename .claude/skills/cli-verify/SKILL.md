---
name: cli-verify
description: How to verify CLI commands — build, run, check stdout/stderr, validate exit codes. Use when testing CLI changes or verifying command output.
---

# CLI Verify

Supporting knowledge for `/verify cli` and `/qa cli`. Documents how to build, run, and verify CLI commands.

## Safety pre-check

These verification flows talk to live Dataverse. Two PreToolUse hooks gate
`ppds *` invocations to keep an agent from writing to a non-dev env:

- `dev-env-check.py` -- always on. Blocks `ppds *` unless the active env is
  in the allowlist (`$PPDS_SAFE_ENVS` or `.claude/state/safe-envs.json`).
- `shakedown-readonly.py` -- active when `PPDS_SHAKEDOWN=1` is set (the
  shakedown skill exports this in its Phase 0). Blocks write verbs
  (`create`, `update`, `delete`, `plugins deploy` without `--dry-run`,
  `solutions import`, etc.).

Before running any of the patterns below:

1. Confirm `ppds env who` shows a safe env (in the allowlist).
2. If invoked from `/shakedown`, verify `PPDS_SHAKEDOWN=1` is set so write
   commands fail closed.
3. Read `docs/SAFE-SHAKEDOWN.md` for the full model and bypass procedure.

## Build

```bash
dotnet build src/PPDS.Cli/PPDS.Cli.csproj -f net10.0
```

## Run Commands

```bash
.\src\PPDS.Cli\bin\Debug\net10.0\ppds.exe <command> <args>
```

## Output Conventions (Constitution I1)

- **stdout** is for data only — pipeable, machine-readable
- **stderr** is for status messages, progress, diagnostics
- When verifying: check stdout for correct data format, stderr for appropriate status messages

## Verification Checklist

For each command:

1. **Executes without error** — exit code is 0
2. **Output format is correct** — JSON, table, or expected text
3. **Pipe-friendly** — stdout can be piped to `jq`, `grep`, etc.
4. **Error handling** — bad input produces clear error message on stderr, non-zero exit code
5. **Help text** — `ppds <command> --help` shows usage

## Common Commands

| Command | Purpose | Expected Output |
|---------|---------|----------------|
| `ppds auth list` | List profiles | JSON array of profiles |
| `ppds auth login` | Interactive login | Status on stderr, profile on stdout |
| `ppds query sql "<sql>"` | Execute SQL | Results on stdout |
| `ppds data export` | Export data | File path on stdout |
| `ppds env list` | List environments | JSON array |
| `ppds serve` | Start daemon | Status on stderr (long-running) |
| `ppds interactive` | Launch TUI | Terminal UI (interactive) |

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Success |
| 1 | General error |
| 2 | Invalid arguments |

## Error Verification

Test error paths:
- Invalid command: `ppds nonexistent` → non-zero exit, error message
- Bad arguments: `ppds query sql` (no query) → non-zero exit, usage hint
- No profile: `ppds query sql "SELECT 1"` (without auth) → clear error about missing profile
