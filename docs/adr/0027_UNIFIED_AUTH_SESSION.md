# ADR-0027: Unified Authentication Session

**Status:** Accepted
**Date:** 2026-01-07
**Authors:** Josh, Claude

## Context

PPDS is a multi-interface platform with CLI commands, TUI, and future VS Code extension all needing authentication. ADR-0024 established that all UIs share `~/.ppds/` for local state, but authentication session management across interfaces wasn't explicitly defined.

During TUI development, we discovered that switching profiles or restarting the TUI often prompted for re-authentication, even though valid tokens existed in the MSAL cache. This violated the principle: "Login from ANY interface = available in ALL interfaces."

### Root Cause

MSAL token cache (`~/.ppds/msal_token_cache.bin`) stores tokens keyed by account identifier (`HomeAccountId`). When creating a new MSAL client:

1. MSAL loads the token cache file
2. `AcquireTokenSilent()` tries to find the account
3. Account lookup uses `HomeAccountId` for precise matching
4. If not found, falls back to `TenantId` and `Username`
5. If no match, forces interactive authentication

The problem: `AuthProfile.HomeAccountId` was not being persisted after successful authentication. Each new session created a new MSAL client that couldn't find the cached account.

## Decision

### Persist HomeAccountId After Authentication

When a credential provider successfully authenticates, persist `HomeAccountId` back to the profile:

```csharp
// In ProfileConnectionSource.TryUpdateHomeAccountId()
if (_provider.HomeAccountId != null &&
    _provider.HomeAccountId != _profile.HomeAccountId)
{
    _profile.HomeAccountId = newHomeAccountId;
    _onProfileUpdated(_profile);  // Callback persists to profiles.json
}
```

### Callback Pattern

`ProfileConnectionSource` accepts an optional `Action<AuthProfile>? onProfileUpdated` callback. The caller (`ProfileServiceFactory`) wires this to `ProfileStore.UpdateProfileAsync()`. This keeps the auth layer decoupled from storage.

### Token Cache Sharing

All UIs share the same token infrastructure:

| Component | Location |
|-----------|----------|
| MSAL token cache | `~/.ppds/msal_token_cache.bin` |
| Profile metadata | `~/.ppds/profiles.json` (includes `HomeAccountId`) |
| Encrypted secrets | `~/.ppds/ppds.credentials.dat` |

## Consequences

### Positive

- **Single sign-on across all interfaces** - Authenticate once in CLI, TUI, or VS Code
- **Profile switching works** - TUI can switch profiles without re-auth if tokens are cached
- **Session persistence** - Close TUI, reopen, no auth prompt
- **VS Code ready** - `ppds serve` daemon will use same pattern

### Negative

- **Slightly more file I/O** - Profile is saved after each authentication
- **Best-effort persist** - If profile save fails, connection still works

### Neutral

- **No breaking changes** - Existing profiles work; `HomeAccountId` populated on next auth

## Implementation

### Files Modified

| File | Change |
|------|--------|
| `ProfileStore.cs` | Added `UpdateProfileAsync()` for partial profile updates |
| `ProfileConnectionSource.cs` | Added `onProfileUpdated` callback, `TryUpdateHomeAccountId()` |
| `ProfileServiceFactory.cs` | Wired callback to persist HomeAccountId |

### Verification

1. Delete `~/.ppds/msal_token_cache.bin`
2. Run `ppds auth who` - prompts for auth
3. Check `profiles.json` - `homeAccountId` now populated
4. Run `ppds auth who` again - no prompt (uses cached token)
5. Start TUI, switch profiles - no prompt if token cached
6. Close TUI, reopen - no prompt

## References

- ADR-0024: Shared Local State Architecture
- ADR-0007: Unified CLI and Auth Profiles
- [MSAL Token Cache Documentation](https://learn.microsoft.com/en-us/entra/msal/dotnet/how-to/token-cache-serialization)
