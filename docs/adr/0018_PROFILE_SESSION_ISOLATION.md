# ADR-0018: Profile Session Isolation

**Status:** Accepted
**Date:** 2026-01-07
**Authors:** Josh, Claude

## Context

PPDS is a multi-interface platform with CLI commands, TUI, and future VS Code extension. Users may have multiple terminals or tools open simultaneously, potentially working with different Dataverse environments.

### Problem

Originally, TUI profile/environment switching updated the global `profiles.json`. This caused issues:

1. **Cross-session interference** - Switching profile in TUI affected CLI commands in other terminals
2. **Script disruption** - Automated scripts could be affected by manual TUI actions
3. **Multi-environment workflows blocked** - Couldn't work on DEV in TUI while CLI ran against PROD

### User Scenarios

| Persona | Need |
|---------|------|
| Single-env dev | Just works with defaults |
| Multi-env dev | Switch environments in TUI without affecting CLI |
| Consultant | Multiple profiles, multiple environments, independent sessions |
| CI/CD | Predictable behavior, not affected by interactive sessions |

## Decision

### Session Isolation Model

Each consumer type maintains its own session state:

| Consumer | Profile Source | Updates Global? |
|----------|---------------|-----------------|
| CLI commands | Flag → env var → global | No (reads only) |
| `ppds auth select` | N/A | **Yes** (explicit default change) |
| `ppds env select` | N/A | **Yes** (explicit default change) |
| TUI | Session-only switching | **No** |
| `ppds serve` RPC | Per-client session | **No** |
| VS Code extension | VS Code settings | **No** |

### Profile Resolution Order

```
1. Explicit (--profile flag, API parameter)    [highest]
2. PPDS_PROFILE environment variable
3. Global active profile from profiles.json   [lowest]
```

### Implementation

**ProfileResolver class** (`PPDS.Auth/Profiles/ProfileResolver.cs`):

```csharp
public static string? GetEffectiveProfileName(string? explicitProfile = null)
{
    // 1. Explicit (CLI flag, API parameter)
    if (!string.IsNullOrWhiteSpace(explicitProfile))
        return explicitProfile;

    // 2. Environment variable
    var envProfile = Environment.GetEnvironmentVariable("PPDS_PROFILE");
    if (!string.IsNullOrWhiteSpace(envProfile))
        return envProfile;

    // 3. Global default (null = use ActiveProfile)
    return null;
}
```

**TUI session-only switching** (no persistence):

```csharp
// MainWindow.SetActiveProfileAsync - session-only
private async Task SetActiveProfileAsync(ProfileSummary profile)
{
    // Note: TUI profile switching is session-only (ADR-0018)
    // We don't update the global active profile in profiles.json
    // Use 'ppds auth select' to change the global default

    _profileName = profile.DisplayIdentifier;
    _environmentName = profile.EnvironmentName;
    _environmentUrl = profile.EnvironmentUrl;

    await _session.SetActiveProfileAsync(
        profile.DisplayIdentifier,
        profile.EnvironmentUrl,
        profile.EnvironmentName);
}
```

## Consequences

### Positive

- **Independent sessions** - TUI, CLI, VS Code can use different profiles simultaneously
- **Script safety** - CI/CD unaffected by interactive sessions
- **Clear mental model** - `ppds auth select` = change default, TUI switch = this session only
- **Environment variable override** - Per-shell profile selection

### Negative

- **TUI doesn't remember** - Restarts TUI, back to global default
- **Learning curve** - Users must understand session vs global

### Mitigations

- **Future: TUI persistence** (Issue #288) - Optional `~/.ppds/tui-state.json` for session memory
- **VS Code settings** - Extension persists to VS Code's native settings system

## Usage Examples

```bash
# Terminal 1: CLI uses PROD
export PPDS_PROFILE=PROD
ppds data export account  # Uses PROD

# Terminal 2: TUI uses DEV (session-only)
ppds
# Switch to DEV profile in TUI menu
# CLI in Terminal 1 still uses PROD

# Terminal 3: Change global default
ppds auth select PROD  # Now all new sessions default to PROD
```

## Files Modified

| File | Change |
|------|--------|
| `src/PPDS.Auth/Profiles/ProfileResolver.cs` | New - resolution logic |
| `src/PPDS.Cli/Tui/MainWindow.cs` | Session-only profile/env switching |
| `src/PPDS.Cli/Tui/InteractiveSession.cs` | Caller tracking for debugging |

## Related

- ADR-0024: Shared Local State Architecture
- ADR-0027: Unified Authentication Session
- Issue #288: TUI session persistence (future)
- Issue #289: Environment theming and safety warnings
