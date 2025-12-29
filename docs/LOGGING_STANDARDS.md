# LOGGING_STANDARDS.md

Console output standards for PPDS CLI commands.

---

## Section Headers

Use `[brackets]` for section headers in normal output. This provides clear visual separation without dated decoration.

```
[Environments]

  PPDS Demo - Dev *
      Type: Developer
      URL: https://orgcabef92d.crm.dynamics.com

[Schema Analysis]

  Entities: 5
  Dependencies: 12
```

**Do NOT use:**
- `======` (dated, DOS-era style)
- `******` (too decorative)
- Box-drawing characters for headers

**For error messages:** Use plain headers without brackets:
```
Dataverse Configuration Error

Missing required property: Url
```

---

## Display Patterns

### Card Format

Use cards when displaying detailed information with multiple fields per item:

```
[Environments]

  PPDS Demo - Dev *
      Type: Developer
      URL: https://orgcabef92d.crm.dynamics.com
      Unique Name: unq3a504f4385d7f01195c7000d3a5cc
      Region: NA

  PPDS Demo - Prod
      Type: Developer
      URL: https://org45808e40.crm.dynamics.com

3 environment(s) found. * = active
```

**When to use cards:**
- Items have many fields (4+)
- Items have variable/optional fields
- Readability matters more than density
- Examples: `env list`, `auth list`

### Table Format

Use tables for homogeneous data with fixed columns:

```
Logical Name                             Display Name                             Custom
------------------------------------------------------------------------------------------
account                                  Account
contact                                  Contact
new_customentity                         Custom Entity                            Yes

Total: 3 entities
```

**When to use tables:**
- Fixed columns across all items
- Scanning/comparing items matters
- Grep-parseability is important
- Examples: `schema list`, user mapping results

**Table underlines:** Use `-----` (dashes) for column header underlines.

---

## Required Options

Required options display `[Required]` at the start of their description:

```
Options:
  -env, --environment <environment>  [Required] Default environment (ID, url, unique name, or partial name)
  -?, -h, --help                     Show help and usage information
```

This keeps the required indicator inline with the description for consistent scannability regardless of terminal width.

---

## Elapsed Timestamp Format

All progress messages are prefixed with an elapsed timestamp:

```
[+00:00:08.123]
```

**Format:** `[+hh:mm:ss.fff]`
- `hh` - Hours (always 2 digits)
- `mm` - Minutes (always 2 digits)
- `ss` - Seconds (always 2 digits)
- `fff` - Milliseconds (always 3 digits)

**Implementation:**
```csharp
var elapsed = _stopwatch.Elapsed;
var prefix = $"[+{elapsed:hh\\:mm\\:ss\\.fff}]";
```

---

## Progress Format

### Entity Progress

```
[+00:00:02.456] [Export] account: 1,234/5,000 (25%) @ 523.4 rec/s
[+00:00:04.789] [Import] contact (Tier 2): 500/1,000 (50%) @ 104.2 rec/s [480 ok, 20 failed]
```

**Format:** `[+elapsed] [Phase] entity(tier): current/total (pct%) @ throughput rec/s [success/failure]`

| Component | Description | Example |
|-----------|-------------|---------|
| `[+elapsed]` | Time since operation start | `[+00:00:02.456]` |
| `[Phase]` | Current phase | `[Export]`, `[Import]` |
| `entity` | Entity logical name | `account`, `contact` |
| `(Tier N)` | Tier number (import only) | `(Tier 2)` |
| `current/total` | Progress count (comma-separated) | `1,234/5,000` |
| `(pct%)` | Percentage complete | `(25%)` |
| `@ throughput rec/s` | Records per second | `@ 523.4 rec/s` |
| `[success ok, failure failed]` | Error breakdown (if failures) | `[480 ok, 20 failed]` |

### Deferred Fields

```
[+00:00:10.123] [Deferred] account.parentaccountid: 50/100 (25 updated)
```

### M2M Relationships

```
[+00:00:12.456] [M2M] accountcontact: 150/300
```

### General Messages

```
[+00:00:00.123] Parsing schema...
[+00:00:01.456] Exporting 5 entities...
[+00:00:15.789] Writing output file...
```

---

## Completion Format

### Success

```
Export succeeded.
    42,366 record(s) in 00:00:08 (4,774.5 rec/s)
    0 Error(s)
```

### Failure

```
Import completed with errors.
    1,234 record(s) in 00:00:15 (82.3 rec/s)
    56 Error(s)

Error Pattern: 50 of 56 errors share the same cause:
  Referenced systemuser (owner/createdby/modifiedby) does not exist in target environment

Suggested fixes:
  -> Use --strip-owner-fields to remove ownership references and let Dataverse assign the current user
  -> Or provide a --user-mapping file to remap user references to valid users in the target
```

**Components:**

| Line | Success | Failure |
|------|---------|---------|
| Header | `{Operation} succeeded.` | `{Operation} completed with errors.` |
| Summary | `N record(s) in HH:MM:SS (X.X rec/s)` | Same |
| Errors | `0 Error(s)` | `N Error(s)` (in red) |

---

## Color Usage

| Color | Usage |
|-------|-------|
| Green | Success messages (`succeeded.`) |
| Yellow | Warning, partial success (`completed with errors.`) |
| Red | Error messages, error counts |
| Cyan | Suggestions, hints |
| Default | Progress messages, informational output |

---

## JSON Output Mode

When `--json` is specified, output follows this structure:

```json
{
  "phase": "exporting",
  "entity": "account",
  "current": 1234,
  "total": 5000,
  "percentComplete": 24.68,
  "recordsPerSecond": 523.4,
  "elapsedMs": 2456
}
```

Completion:
```json
{
  "phase": "complete",
  "success": true,
  "recordsProcessed": 42366,
  "successCount": 42366,
  "failureCount": 0,
  "durationMs": 8867,
  "recordsPerSecond": 4774.5
}
```

---

## Implementation Reference

- `ConsoleProgressReporter` - Human-readable console output
- `JsonProgressReporter` - Machine-readable JSON output
- `IProgressReporter` - Interface for custom reporters
- `ProgressEventArgs` - Progress event data

See `src/PPDS.Migration/Progress/` for implementation details.
