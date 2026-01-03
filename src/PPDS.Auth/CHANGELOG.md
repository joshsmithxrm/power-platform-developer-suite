# Changelog - PPDS.Auth

All notable changes to PPDS.Auth will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.0.0-beta.4] - 2026-01-03

### Changed

- **BREAKING: Profile storage schema v2** - Modernized profile storage format. Profiles now stored as array with name-based active profile instead of dictionary with numeric index. v1 profiles are automatically deleted on first load (pre-release breaking change). ([#107](https://github.com/joshsmithxrm/ppds-sdk/issues/107))
- **BREAKING: Secure credential storage** - Secrets (client secrets, certificate passwords, user passwords) are now stored in platform-native secure storage using MSAL.Extensions (Windows DPAPI, macOS Keychain, Linux libsecret) instead of in profile JSON. New `SecureCredentialStore` class manages encrypted credential storage. ([#107](https://github.com/joshsmithxrm/ppds-sdk/issues/107))
- **BREAKING: Removed `EnvironmentInfo.Id`** - Redundant field removed; use `OrganizationId` or `EnvironmentId` instead. ([#107](https://github.com/joshsmithxrm/ppds-sdk/issues/107))

### Added

- **`SecureCredentialStore`** - Cross-platform encrypted credential storage using MSAL.Extensions.Msal. Credentials keyed by ApplicationId. ([#107](https://github.com/joshsmithxrm/ppds-sdk/issues/107))
- **`CredentialProviderFactory.CreateAsync()`** - Async factory method that retrieves secrets from secure store. Supports `PPDS_SPN_SECRET` environment variable for CI/CD scenarios. ([#107](https://github.com/joshsmithxrm/ppds-sdk/issues/107))
- **`AuthProfile.Authority`** - Stores full authority URL for authentication. ([#107](https://github.com/joshsmithxrm/ppds-sdk/issues/107))

### Removed

- **`AuthProfile.ClientSecret`** - Moved to secure credential store
- **`AuthProfile.CertificatePassword`** - Moved to secure credential store
- **`AuthProfile.Password`** - Moved to secure credential store
- **`AuthProfile.UserCountry`** - Removed (optional JWT claims not available without app manifest configuration)
- **`AuthProfile.TenantCountry`** - Removed (optional JWT claims not available without app manifest configuration)

### Fixed

- **Linux cleartext storage uses exclusive MSAL config** - On Linux without libsecret, the SecureCredentialStore now uses an isolated MSAL configuration path to prevent conflicts with system keyring detection. ([#107](https://github.com/joshsmithxrm/ppds-sdk/issues/107))
- **ServiceClient org metadata not populated** - Credential providers now force eager org metadata discovery by accessing `ConnectedOrgFriendlyName` immediately after creating the ServiceClient. Discovery is lazy by default, and the connection pool clones clients before properties are accessed, resulting in empty metadata. This fix ensures `ConnectedOrgFriendlyName`, `ConnectedOrgUniqueName`, and `ConnectedOrgId` are populated. ([#86](https://github.com/joshsmithxrm/ppds-sdk/issues/86))
- **Service principal identity display truncated** - Credential providers now return full Application ID (GUID) instead of truncated `app:xxxxxxxx...` format. Affects `ClientSecretCredentialProvider`, `CertificateFileCredentialProvider`, `CertificateStoreCredentialProvider`, `GitHubFederatedCredentialProvider`, `AzureDevOpsFederatedCredentialProvider`, and `ManagedIdentityCredentialProvider`. ([#100](https://github.com/joshsmithxrm/ppds-sdk/issues/100))
- **Missing Environment ID for direct connections** - `EnvironmentResolutionService.TryDirectConnectionAsync` now populates `EnvironmentId` from `ServiceClient.EnvironmentId` property. Previously only Global Discovery paths returned this value. ([#101](https://github.com/joshsmithxrm/ppds-sdk/issues/101))

### Added

- **`EnvironmentResolutionService`** - Multi-layer environment resolution that tries direct Dataverse connection first (works for service principals), then falls back to Global Discovery for user authentication. Returns full org metadata. ([#89](https://github.com/joshsmithxrm/ppds-sdk/issues/89), [#88](https://github.com/joshsmithxrm/ppds-sdk/issues/88))
- **`TokenCacheManager.ClearAllCachesAsync()`** - Public method to clear all MSAL token caches including file-based cache and platform-specific secure storage (macOS Keychain, Linux keyring). ([#90](https://github.com/joshsmithxrm/ppds-sdk/issues/90))
- **Integration tests for credential providers** - Live tests for `ClientSecretCredentialProvider`, `CertificateFileCredentialProvider`, and `GitHubFederatedCredentialProvider` ([#55](https://github.com/joshsmithxrm/ppds-sdk/issues/55))
- Manual test procedures documentation for interactive browser and device code authentication


## [1.0.0-beta.3] - 2026-01-02

### Fixed

- **Race condition in ProfileConnectionSource** - Added `SemaphoreSlim` for proper async synchronization instead of mixed lock/async patterns ([#81](https://github.com/joshsmithxrm/ppds-sdk/pull/81))
- **Memory leaks in credential providers** - Added cache unregistration in `Dispose()` for `DeviceCodeCredentialProvider`, `InteractiveBrowserCredentialProvider`, `UsernamePasswordCredentialProvider`, and `GlobalDiscoveryService` ([#81](https://github.com/joshsmithxrm/ppds-sdk/pull/81))
- **Sync-over-async deadlock risk** - Wrapped blocking calls in `Task.Run()` to prevent deadlocks in UI/ASP.NET contexts ([#81](https://github.com/joshsmithxrm/ppds-sdk/pull/81))
- **Copy-paste bug in CertificateStoreCredentialProvider** - Fixed `StoreName=` → `StoreLocation=` parameter ([#81](https://github.com/joshsmithxrm/ppds-sdk/pull/81))

### Added

- `AuthenticationOutput` static class for configurable authentication message output - library consumers can redirect or suppress console messages ([#81](https://github.com/joshsmithxrm/ppds-sdk/pull/81))
- `Clone()` methods on `AuthProfile`, `EnvironmentInfo`, and `ProfileCollection` for proper deep copying ([#82](https://github.com/joshsmithxrm/ppds-sdk/pull/82))
- Input validation for required fields in `CredentialProviderFactory` ([#81](https://github.com/joshsmithxrm/ppds-sdk/pull/81))

### Changed

- Extracted common MSAL client setup into `MsalClientBuilder` - reduces code duplication across credential providers ([#82](https://github.com/joshsmithxrm/ppds-sdk/pull/82))

## [1.0.0-beta.2] - 2026-01-01

### Fixed

- **Cross-tenant token cache issue** - Fixed bug where `ppds env list` would return environments from wrong tenant when user had profiles for multiple tenants. Root cause was MSAL account lookup using `FirstOrDefault()` instead of filtering by tenant. Now uses `HomeAccountId` for precise account lookup with tenant filtering fallback. ([#59](https://github.com/joshsmithxrm/ppds-sdk/issues/59))

### Added

- `HomeAccountId` property on `AuthProfile` and `ICredentialProvider` to track MSAL account identity across sessions

## [1.0.0-beta.1] - 2025-12-29

### Added

- Authentication profile storage with encrypted secrets (DPAPI on Windows)
- Profile management: create, list, select, delete, update, rename, clear
- Multiple credential providers:
  - `InteractiveBrowserCredentialProvider`: Opens browser for OAuth login
  - `DeviceCodeCredentialProvider`: Device code flow for headless environments
  - `ClientSecretCredentialProvider`: Service principal with client secret
  - `CertificateFileCredentialProvider`: Service principal with certificate file
  - `CertificateStoreCredentialProvider`: Service principal with Windows certificate store
  - `ManagedIdentityCredentialProvider`: Azure Managed Identity (system or user-assigned)
  - `GitHubFederatedCredentialProvider`: GitHub Actions OIDC federation
  - `AzureDevOpsFederatedCredentialProvider`: Azure Pipelines OIDC federation
  - `UsernamePasswordCredentialProvider`: Username/password authentication
- Global Discovery Service integration for environment enumeration
- Environment resolution by ID, URL, unique name, or partial friendly name
- Multi-cloud support: Public, GCC, GCC High, DoD, China, USNat, USSec
- `ICredentialProvider` abstraction for custom authentication methods
- Platform-native token caching via MSAL
- JWT claims parsing for identity information
- Targets: `net8.0`, `net10.0`

[Unreleased]: https://github.com/joshsmithxrm/ppds-sdk/compare/Auth-v1.0.0-beta.3...HEAD
[1.0.0-beta.3]: https://github.com/joshsmithxrm/ppds-sdk/compare/Auth-v1.0.0-beta.2...Auth-v1.0.0-beta.3
[1.0.0-beta.2]: https://github.com/joshsmithxrm/ppds-sdk/compare/Auth-v1.0.0-beta.1...Auth-v1.0.0-beta.2
[1.0.0-beta.1]: https://github.com/joshsmithxrm/ppds-sdk/releases/tag/Auth-v1.0.0-beta.1
