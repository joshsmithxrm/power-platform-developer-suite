# Changelog - PPDS.Auth

All notable changes to PPDS.Auth will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- **ServiceClient org metadata not populated** - Credential providers now force eager org metadata discovery by accessing `ConnectedOrgFriendlyName` immediately after creating the ServiceClient. Discovery is lazy by default, and the connection pool clones clients before properties are accessed, resulting in empty metadata. This fix ensures `ConnectedOrgFriendlyName`, `ConnectedOrgUniqueName`, and `ConnectedOrgId` are populated. ([#86](https://github.com/joshsmithxrm/ppds-sdk/issues/86))

### Added

- **`EnvironmentResolutionService`** - Multi-layer environment resolution that tries direct Dataverse connection first (works for service principals), then falls back to Global Discovery for user authentication. Returns full org metadata. ([#89](https://github.com/joshsmithxrm/ppds-sdk/issues/89), [#88](https://github.com/joshsmithxrm/ppds-sdk/issues/88))
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
