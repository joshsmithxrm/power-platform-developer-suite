# ADR-0032: Native OS Credential Storage

**Status:** Accepted
**Date:** 2026-01-12
**Authors:** Josh, Claude

## Context

The original `SecureCredentialStore` implementation had a critical bug: it misused `MsalCacheHelper` and wrote credentials as **plaintext JSON** to `ppds.credentials.dat`. The `MsalCacheHelper.VerifyPersistence()` call only checks if encryption is available - it doesn't actually encrypt anything. `File.WriteAllBytesAsync()` bypassed MSAL entirely.

**Impact:** ClientSecret, CertificateFile, and UsernamePassword auth methods failed after token expiration because credentials weren't properly persisted.

**Root Cause:** `MsalCacheHelper` is designed specifically for MSAL token caches via `RegisterCache()`. Using it for arbitrary data encryption doesn't work - calling `VerifyPersistence()` and then writing bytes directly to a file bypasses the encryption mechanism entirely.

## Decision

Replace file-based credential storage with native OS credential managers using `Devlooped.CredentialManager` - a .NET Standard 2.0 wrapper around the Git Credential Manager's credential store infrastructure.

### Credential Store Backends

| Platform | Store | Security |
|----------|-------|----------|
| Windows | Credential Manager | DPAPI with CurrentUser scope |
| macOS | Keychain Services | Secure Enclave integration |
| Linux | libsecret | GNOME Keyring / KWallet |

### Storage Model

Each credential is stored as a separate entry:
- **Service Name:** `ppds.credentials`
- **Account:** Application ID (lowercase for case-insensitivity)
- **Password:** JSON-serialized credential data

A manifest entry (`_manifest`) tracks all stored application IDs to support enumeration and `ClearAsync()`.

### CI/CD Fallback

For headless Linux environments without libsecret, the `--accept-cleartext-caching` flag (parity with PAC CLI) configures GCM to use its plaintext file store backend:

```csharp
if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && allowCleartextFallback)
{
    Environment.SetEnvironmentVariable("GCM_CREDENTIAL_STORE", "plaintext");
}
```

## Consequences

### Positive

- **Actually encrypted** - OS manages encryption, not our code
- **No custom crypto** - Uses proven implementations (GCM has millions of users)
- **Cross-platform** - Single codebase for Windows, macOS, Linux
- **CI/CD support** - `--accept-cleartext-caching` maps to GCM plaintext backend
- **Clean break** - No migration needed (pre-release; old implementation never persisted correctly)

### Negative

- **Linux requires keyring** - libsecret must be installed for secure storage
- **New dependency** - `Devlooped.CredentialManager` v2.6.1.1

### Neutral

- **API unchanged** - `ISecureCredentialStore` interface preserved
- **Breaking removal** - `SecureCredentialStore` deleted (pre-release cleanup)

## Implementation

### Files Changed

| Action | File |
|--------|------|
| Create | `src/PPDS.Auth/Credentials/NativeCredentialStore.cs` |
| Delete | `src/PPDS.Auth/Credentials/SecureCredentialStore.cs` |
| Modify | All `new SecureCredentialStore()` → `new NativeCredentialStore()` |

### Testing Strategy

Unit tests use a mock `ICredentialStore` to avoid OS credential store access:

```csharp
internal NativeCredentialStore(bool allowCleartextFallback, ICredentialStore? store)
{
    _store = store ?? CredentialManager.Create(ServiceName);
}
```

## References

- [Devlooped.CredentialManager](https://github.com/devlooped/CredentialManager)
- [Git Credential Manager Credential Stores](https://github.com/git-ecosystem/git-credential-manager/blob/main/docs/credstores.md)
- ADR-0024: Shared Local State Architecture
- `docs/internal/CREDENTIAL_STORE_FIX.md` - Original implementation plan
