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
- **Secure credential storage** — `SecureCredentialStore` (MSAL.Extensions: Windows DPAPI, macOS Keychain, Linux libsecret) keyed by ApplicationId. Replaced in beta.6 by `NativeCredentialStore` using platform-native APIs directly (Windows Credential Manager, macOS Keychain, Linux Secret Service) ([#485](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/485)). `TokenCacheManager.ClearAllCachesAsync()` wipes all caches.
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
- **`ProfileEncryption` cleartext opt-in for CI/CD** — On non-Windows platforms, `Encrypt()` now supports explicit opt-in to base64-encoded cleartext storage via `PPDS_ALLOW_CLEARTEXT=1` environment variable. Without opt-in, throws `AuthenticationException` with error code `Auth.SecureStorageUnavailable` instead of silently using insecure XOR obfuscation ([#858](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/858)).
- **Linux MSAL token cache hardening** — Token cache on Linux now prefers libsecret (GNOME Keyring / KWallet) and only falls back to unprotected file storage when the keyring is unavailable. Fallback files are pre-created at mode `0600` ([#858](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/858)).
- **Credential store failure error code** — OS-level credential store failures now surface as `AuthenticationException` with error code `Auth.CredentialStoreFailure` instead of propagating raw interop exceptions ([#803](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/803)).

### Changed

- **BREAKING — Profile storage schema v2** — Array storage, name-based active profile, secure storage for secrets. v1 profiles auto-deleted ([#107](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/107)).
- **BREAKING — `AuthProfile` secret removal** — `ClientSecret`, `CertificatePassword`, and `Password` moved to secure credential store; `UserCountry` and `TenantCountry` removed (optional JWT claims unavailable without app-manifest configuration).
- **BREAKING — Removed `EnvironmentInfo.Id`** — Redundant; use `OrganizationId` or `EnvironmentId` ([#107](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/107)).
- **`EnvironmentConfig.Type` uses `EnvironmentType` enum** — Changed from `string` for compile-time safety.
- **Native OS credential storage** — Replaced custom `SecureCredentialStore` with OS-native APIs (Windows Credential Manager, macOS Keychain, Linux Secret Service) for better security and reliability ([#485](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/485)).
- **Deprecated Azure.Identity APIs replaced** — `ManagedIdentityCredentialProvider` updated to `ManagedIdentityId.SystemAssigned` / `ManagedIdentityId.FromUserAssignedClientId()` (Azure.Identity 1.19.0+).
- **BREAKING — `AuthenticationOutput` defaults to stderr** — `AuthenticationOutput.Writer` now writes to `Console.Error` (stderr) by default instead of `Console.Out` (stdout). Callers that parsed stdout for data will no longer see authentication status messages mixed in ([#868](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/868)).
- **BREAKING — `ProfileEncryption.Decrypt` on non-Windows** — Throws `AuthenticationException` with error code `Auth.LegacyEncryptedProfileUnsupported` when given an `ENCRYPTED:`-prefixed value on macOS/Linux. Previously returned `string.Empty`, which silently cascaded into "wrong credentials" UX ([#881](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/881)).
- **Vendored credential store backends** — Replaced `Devlooped.CredentialManager` NuGet dependency with vendored git-credential-manager interop code (MIT-licensed from Microsoft). No user-facing behavior change ([#803](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/803)).
- **`AddAuthServices()` uses TryAdd semantics** — DI registration methods now use `TryAddSingleton` instead of `AddSingleton`, allowing callers to pre-register specialized implementations ([#858](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/858)).
- **Linux plaintext credential fallback is double-gated** — `NativeCredentialStore` plaintext fallback on Linux now requires BOTH the `allowCleartextFallback` constructor parameter AND the `GCM_CREDENTIAL_STORE=plaintext` environment variable ([#803](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/803)).
- **`AddAuthServices()` uses TryAdd semantics** — DI registration methods now use `TryAddSingleton` instead of `AddSingleton`, allowing callers to pre-register specialized implementations ([#858](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/858)).

### Fixed

- **Cross-tenant token cache issue** — `ppds env list` returned environments from wrong tenant when a user had multi-tenant profiles; fixed by using `HomeAccountId` for precise MSAL account lookup with tenant-filtering fallback ([#59](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/59)).
- **Race condition in `ProfileConnectionSource`** — Added `SemaphoreSlim` for proper async synchronization ([#81](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/81)).
- **Memory leaks in credential providers** — Cache unregistration in `Dispose()` for `DeviceCodeCredentialProvider`, `InteractiveBrowserCredentialProvider`, `UsernamePasswordCredentialProvider`, `GlobalDiscoveryService` ([#81](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/81)).
- **Sync-over-async deadlock risk** — Wrapped blocking calls in `Task.Run()` to prevent deadlocks in UI/ASP.NET contexts ([#81](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/81)).
- **`CertificateStoreCredentialProvider` copy-paste bug** — Fixed `StoreName=` → `StoreLocation=` parameter ([#81](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/81)).
- **Token cache scope mismatch in browser auth** — Fixed scope mismatch during interactive browser profile creation.
- **Disposal guards and typed catches** — Added across credential providers.
- **Unnamed profile selection silently failed** — Active profile tracking now uses index-based lookup with backwards compatibility.
- **Linux cleartext storage uses isolated MSAL config path** — Prevents conflicts with system keyring detection when libsecret is unavailable.
- **ServiceClient org metadata populated** — Credential providers force eager discovery via `ConnectedOrgFriendlyName` access immediately after `ServiceClient` creation so metadata survives pool cloning ([#86](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/86)).
- **Service principal identity display** — Returns full Application ID (GUID) instead of truncated `app:xxxxxxxx...` format across all SPN providers ([#100](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/100)).
- **Missing `EnvironmentId` for direct connections** — `EnvironmentResolutionService.TryDirectConnectionAsync` now populates `EnvironmentId` from `ServiceClient.EnvironmentId` ([#101](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/101)).
- **Windows Credential Manager URI validation** — Service identifier changed to `https://ppds.credentials` to satisfy GCM's `CreateTargetName()` URI-format requirement ([#763](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/763)).
- **`PowerPlatformTokenProvider` error handling** — Throws `AuthenticationException` (was `ArgumentException`) for credential validation failures ([#676](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/676)).
- **MSAL token-state query** — Token validity checked against MSAL's actual cache rather than stale profile metadata ([#491](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/491)).
- **Credential store bypass for test scenarios** — `PPDS_TEST_CLIENT_SECRET` env var supported for test/CI scenarios ([#488](https://github.com/joshsmithxrm/power-platform-developer-suite/issues/488)).
- **Input validation in `CredentialProviderFactory`** — Added for required fields.
- **Repeated login prompts eliminated** — `InteractiveBrowserCredentialProvider` now uses the "organizations" (multi-tenant) MSAL authority with per-request `.WithTenantId()` overrides, aligning cache keys with `GlobalDiscoveryService` ([#868](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/868)).
- **CVE pin for `System.Security.Cryptography.Xml`** — Explicit package reference pins to patched 8.0.3, preventing transitive resolution from pulling 8.0.2 with two High CVEs ([#858](https://github.com/joshsmithxrm/power-platform-developer-suite/pull/858)).

[Unreleased]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Auth-v1.0.0...HEAD
[1.0.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/releases/tag/Auth-v1.0.0
