# Fix: Credential Store Persistence Bug

**Status:** Completed (superseded by ADR-0032)
**Branch:** `fix/credential-store-persistence` (merged)
**Date:** 2026-01-12

## Summary

Credentials are not being persisted to disk for `ClientSecret`, `CertificateFile`, and `UsernamePassword` auth methods. Profiles work initially but fail after token expiration because secrets cannot be re-read from the credential store.

## Root Cause

`SecureCredentialStore` misuses `MsalCacheHelper`. The helper is designed ONLY for MSAL token caches via `RegisterCache()` - it does NOT automatically encrypt files written with `File.WriteAllBytesAsync`. The current code:

1. Creates `MsalCacheHelper` with storage properties
2. Calls `VerifyPersistence()` (only tests if encryption is available - doesn't enable it)
3. Writes raw unencrypted JSON with `File.WriteAllBytesAsync` (bypasses MsalCacheHelper entirely)

## Bugs Identified

| Bug | Severity | Location | Description |
|-----|----------|----------|-------------|
| MsalCacheHelper Misuse | CRITICAL | Lines 296-308, 319-359 | Credentials written as raw JSON, not encrypted |
| Dictionary Case Sensitivity | MEDIUM | Line 283 | Deserialized dictionary loses case-insensitive comparer |
| Temp File Cleanup | LOW | Lines 305-307 | Temp file left behind if `File.Move` fails |
| Misleading Comments | DOC | Lines 342-343 | Comments claim encryption happens but it doesn't |

## Affected Auth Methods

| Auth Method | What's Lost | Impact |
|-------------|-------------|--------|
| ClientSecret | Client secret | Cannot re-authenticate after token expires |
| CertificateFile | Certificate password | Cannot re-authenticate if password-protected |
| UsernamePassword | User password | Cannot re-authenticate after token expires |

## Proposed Solution

### Use Platform-Native Encryption Directly

Remove MsalCacheHelper misuse and use platform APIs:

**Windows:** `System.Security.Cryptography.ProtectedData` (DPAPI)
```csharp
var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
```

**macOS:** Keep MsalCacheHelper for Keychain integration OR use Security framework P/Invoke

**Linux:** Keep libsecret integration with cleartext fallback warning

### Security Properties

| Platform | Encryption | Protection Scope |
|----------|-----------|------------------|
| Windows | DPAPI | CurrentUser - only current Windows user can decrypt |
| macOS | Keychain | User Keychain - protected by login password |
| Linux | libsecret | GNOME Keyring/KWallet - requires running keyring daemon |

## Implementation Plan

### Phase 1: Create Platform Encryption Abstraction

Create `IPlatformEncryption` interface:
```csharp
public interface IPlatformEncryption
{
    byte[] Encrypt(byte[] data);
    byte[] Decrypt(byte[] data);
    bool IsAvailable { get; }
}
```

Implementations:
- `WindowsEncryption` - DPAPI via `ProtectedData`
- `MacEncryption` - Keychain (reuse MsalCacheHelper correctly or P/Invoke)
- `LinuxEncryption` - libsecret or cleartext fallback

### Phase 2: Fix SecureCredentialStore

1. Remove `EnsureCacheHelperAsync()` approach
2. Add platform encryption to `LoadCacheAsync`/`SaveCacheAsync`
3. Fix dictionary case sensitivity:
   ```csharp
   var deserialized = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
   return deserialized == null
       ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
       : new Dictionary<string, string>(deserialized, StringComparer.OrdinalIgnoreCase);
   ```
4. Add temp file cleanup in try/finally
5. Fix misleading comments

### Phase 3: Add Unit Tests

1. Credential persistence round-trip (store -> restart -> retrieve)
2. Case-insensitive ApplicationId lookup
3. Encryption/decryption (mock platform encryption)
4. Cleartext fallback behavior on Linux
5. Temp file cleanup on error

### Phase 4: Integration Testing

1. Create SPN profile: `ppds auth create -id <id> -cs <secret> -t <tenant> -env <url>`
2. Verify `ppds.credentials.dat` exists and is encrypted
3. Delete `msal_token_cache.bin` to force re-auth
4. Run `ppds plugins list` - should re-authenticate using stored secret

### Phase 5: Documentation

1. Update CLAUDE.md if patterns change
2. Add ADR if warranted
3. Update error messages

## Files to Modify

| File | Changes |
|------|---------|
| `src/PPDS.Auth/Credentials/SecureCredentialStore.cs` | Replace MsalCacheHelper with platform encryption |
| `src/PPDS.Auth/Credentials/IPlatformEncryption.cs` | New - platform encryption interface |
| `src/PPDS.Auth/Credentials/WindowsEncryption.cs` | New - DPAPI implementation |
| `src/PPDS.Auth/Credentials/MacEncryption.cs` | New - Keychain implementation |
| `src/PPDS.Auth/Credentials/LinuxEncryption.cs` | New - libsecret/cleartext implementation |
| `tests/PPDS.Auth.Tests/Credentials/SecureCredentialStoreTests.cs` | New/update tests |

## Acceptance Criteria

- [ ] Credentials persist to `ppds.credentials.dat` after `ppds auth create`
- [ ] File is encrypted (not readable JSON) on Windows
- [ ] Credentials survive process restart
- [ ] Re-authentication works after token expiration
- [ ] Case-insensitive ApplicationId lookup works
- [ ] All existing tests pass
- [ ] New tests for credential persistence pass

## References

- `src/PPDS.Auth/Credentials/SecureCredentialStore.cs` - Current broken implementation
- `src/PPDS.Auth/Credentials/CredentialProviderFactory.cs` - Credential retrieval logic
- `src/PPDS.Cli/Commands/Auth/AuthCommandGroup.cs` - Profile creation flow
