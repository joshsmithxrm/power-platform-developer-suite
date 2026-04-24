# Changelog - PPDS.Auth

All notable changes to PPDS.Auth will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0] - 2026-04-18

First stable release. Consolidates features developed across the `1.0.0-beta.1` through `1.0.0-beta.8` series. Targets `net8.0`, `net9.0`, `net10.0`.

### Added

- **Credential providers** — `InteractiveBrowserCredentialProvider`, `DeviceCodeCredentialProvider`, `ClientSecretCredentialProvider`, `CertificateFileCredentialProvider`, `CertificateStoreCredentialProvider`, `ManagedIdentityCredentialProvider` (system and user-assigned), `GitHubFederatedCredentialProvider`, `AzureDevOpsFederatedCredentialProvider`, `UsernamePasswordCredentialProvider`.
- **`ICredentialProvider` abstraction** — Custom authentication methods via plug-in implementation.
- **Profile management** — Create, list, select, delete, update, rename, clear profiles. `AuthProfile.Authority` stores full authority URL; `AuthProfile.HomeAccountId` tracks MSAL account identity across sessions ([#59](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/59)).
- **Profile storage v2** — Array-based storage with name-based active profile (migration from v1 auto-deletes on first load) ([#107](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/107)).
- **Secure credential storage** — Platform-native credential storage via `NativeCredentialStore` (Windows Credential Manager, macOS Keychain, Linux Secret Service). `TokenCacheManager.ClearAllCachesAsync()` wipes all caches ([#485](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/485)).
- **Environment-variable authentication** — `EnvironmentVariableAuth` static class for stateless CI/CD via `PPDS_CLIENT_ID`, `PPDS_CLIENT_SECRET`, `PPDS_TENANT_ID`, `PPDS_ENVIRONMENT_URL` (plus optional `PPDS_CLOUD`). All four required variables read together; partial configuration throws. `ConnectionResolver` attempts env-var auth before profile-store lookup ([#706](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/706)).
- **Global Discovery Service integration** — Environment enumeration; resolution by ID, URL, unique name, or partial friendly name. Multi-cloud support: Public, GCC, GCC High, DoD, China, USNat, USSec.
- **`EnvironmentResolutionService`** — Multi-layer environment resolution that tries direct Dataverse connection first (works for service principals) before falling back to Global Discovery. Returns full org metadata ([#88](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/88), [#89](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/89)).
- **`IPowerPlatformTokenProvider` / `PowerPlatformTokenProvider`** — Token acquisition for Power Apps and Power Automate management APIs using interactive browser, device code, or client credentials. Shares MSAL token cache with existing credential providers ([#150](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/150)).
- **`CloudEndpoints.GetPowerAppsApiUrl()` / `GetPowerAutomateApiUrl()`** — Cloud-specific Power Platform API URLs.
- **`EnvironmentConfig` and `EnvironmentConfigStore`** — Per-environment configuration (label, type, color). `EnvironmentType` enum (`Development`, `Test`, `Production`, `Sandbox`, `Default`). `QuerySafetySettings` and `ProtectionLevel` for DML thresholds and production protection, with cross-environment DML policy enforcement.
- **Profile-linked environment tracking** — `EnvironmentConfig.Profiles` list tracks which profile(s) have accessed each environment, enabling per-profile filtering in UI/TUI. `EnvironmentConfigStore.SaveConfigAsync()` accepts optional `profileName` ([#656](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/656)).
- **`TuiStateFile` in `ProfilePaths`** — Enables TUI screen-state and filter persistence across sessions ([#656](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/656)).
- **DI registration** — `AddAuthServices()` extension method registers `ProfileStore`, `EnvironmentConfigStore`, and `NativeCredentialStore`.
- **`CredentialProviderFactory.CreateAsync()`** — Async factory that retrieves secrets from the secure store; supports `PPDS_SPN_SECRET` environment variable; accepts `clientSecretOverride` parameter with precedence over env-var/store lookups ([#107](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/107), [#706](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/706)).
- **`AuthenticationOutput`** — Configurable authentication-message output (consumers can redirect or suppress).
- **`Clone()` methods** — Deep copying on `AuthProfile`, `EnvironmentInfo`, and `ProfileCollection`.
- **`MsalClientBuilder`** — Shared MSAL client setup extracted from credential providers to reduce duplication.
- **Platform-native token caching via MSAL**, JWT claims parsing, and integration tests for service-principal credential providers ([#55](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/55)).
- **`ProfileEncryption` cleartext opt-in for CI/CD** — On non-Windows platforms, `Encrypt()` supports explicit opt-in to base64-encoded cleartext storage via `PPDS_ALLOW_CLEARTEXT=1` environment variable. Without opt-in, throws `AuthenticationException` with error code `Auth.SecureStorageUnavailable` when secure storage is unavailable.
- **Linux MSAL token cache hardening** — Token cache on Linux prefers libsecret (GNOME Keyring / KWallet) and only falls back to unprotected file storage when the keyring is unavailable. Fallback files are pre-created at mode `0600`.
- **Credential store failure error code** — OS-level credential store failures surface as `AuthenticationException` with error code `Auth.CredentialStoreFailure` instead of propagating raw interop exceptions.
- **`AuthenticationOutput` writes to stderr** — Authentication status messages are written to `Console.Error` (stderr) by default, keeping `stdout` clean for data consumers.
- **`ProfileEncryption.Decrypt` error on non-Windows** — On macOS/Linux, attempting to decrypt a legacy `ENCRYPTED:`-prefixed profile value throws `AuthenticationException` with error code `Auth.LegacyEncryptedProfileUnsupported` rather than silently returning empty credentials.

[Unreleased]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Auth-v1.0.0...HEAD
[1.0.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/releases/tag/Auth-v1.0.0
