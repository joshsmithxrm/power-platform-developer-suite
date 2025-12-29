# Changelog - PPDS.Auth

All notable changes to PPDS.Auth will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/joshsmithxrm/ppds-sdk/compare/Auth-v1.0.0-beta.1...HEAD
[1.0.0-beta.1]: https://github.com/joshsmithxrm/ppds-sdk/releases/tag/Auth-v1.0.0-beta.1
