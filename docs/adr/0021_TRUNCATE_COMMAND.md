# ADR-0021: Truncate Command

## Status

Accepted

## Context

Users need to delete all records from entities for dev/test scenarios:
- Resetting environments before re-testing imports
- Cleaning up test data after experiments
- Preparing environments for fresh data loads

Alternative approaches considered:
1. **Hidden command** (require `PPDS_INTERNAL=1` env var) - Rejected: Security through obscurity doesn't work
2. **Same confirmation as delete** (`delete N`) - Rejected: Truncate is more dangerous, needs stronger signal
3. **No confirmation at all with `--force`** - Kept: Required for CI/CD scripting, explicit opt-in

## Decision

Add visible `ppds data truncate` command with elevated safeguards that exceed the standard `delete` command.

### Command Syntax

```bash
ppds data truncate --entity <logical_name> [options]
```

| Option | Required | Description |
|--------|----------|-------------|
| `--entity`, `-e` | Yes | Entity logical name to truncate |
| `--profile`, `-p` | No | Auth profile(s) |
| `--environment`, `-env` | No | Target environment URL |
| `--dry-run` | No | Preview record count without deleting |
| `--force` | No | Skip confirmation (non-interactive mode) |
| `--batch-size` | No | Records per batch (default: 1000) |
| `--bypass-plugins` | No | Bypass plugins: sync, async, all |
| `--bypass-flows` | No | Bypass Power Automate triggers |
| `--continue-on-error` | No | Continue on individual failures (default: true) |

### Elevated Confirmation

The confirmation prompt requires typing:
```
TRUNCATE <entity> <count>
```

This is elevated compared to delete's `delete N` because:
1. **ALL CAPS "TRUNCATE"** - Visual distinction, harder to type accidentally
2. **Entity name included** - Confirms WHAT you're deleting from
3. **Record count included** - Confirms HOW MUCH you're deleting

### Environment Context Display

Before confirmation, the command always displays:
```
Connected as: admin@contoso.onmicrosoft.com
Environment: Andromeda Dev (https://andromeda.crm.dynamics.com)

Entity: account
Records to delete: 43,710

WARNING: This will permanently delete ALL 43,710 records from 'account'.
         This operation cannot be undone.

Type 'TRUNCATE account 43710' to confirm, or Ctrl+C to cancel:
```

This prevents "wrong environment" accidents - users see exactly WHERE they're executing.

### Non-Interactive Mode

Non-interactive mode (piped input) requires explicit `--force`:
```bash
# Fails without --force
ppds data truncate --entity account < /dev/null
# Error: CONFIRMATION_REQUIRED

# Works with --force
ppds data truncate --entity account --force
```

### Claude Code Deny Rule

The command is denied in Claude Code settings to prevent AI-initiated truncations:
```json
{
  "permissions": {
    "deny": [
      "Bash(ppds data truncate:*)"
    ]
  }
}
```

Claude can help prepare the command, but humans must execute it.

## Consequences

### Positive

- Users can reset environments for testing without manual record deletion
- Elevated safeguards ensure conscious choice before mass deletion
- Environment display prevents wrong-environment accidents
- Consistent with existing CLI patterns (`--dry-run`, `--force`)
- Visible in help - no hidden footguns

### Negative

- Users can still accidentally delete data if they ignore warnings and type confirmation
- This is acceptable: admins are trusted, safeguards ensure conscious choice

### Neutral

- Command follows same bulk operation patterns as delete
- Uses existing `BulkOperationExecutor.DeleteMultipleAsync` infrastructure

## Related ADRs

- **ADR-0009**: CLI Command Taxonomy (command naming conventions)
- **ADR-0013**: CLI --dry-run Convention (preview pattern)
