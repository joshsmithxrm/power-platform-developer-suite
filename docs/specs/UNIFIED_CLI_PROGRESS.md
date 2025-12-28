# PPDS Unified CLI - Implementation Progress

**Spec:** [UNIFIED_CLI_SPEC.md](UNIFIED_CLI_SPEC.md)
**ADR:** [ADR-0008](../adr/0008_UNIFIED_CLI_AND_AUTH.md)
**Branch:** `feature/unified-cli-auth`
**Started:** 2025-01-27

---

## Phase Overview

| Phase | Description | Status | Dependencies |
|-------|-------------|--------|--------------|
| 1 | PPDS.Auth Foundation | **Complete** | - |
| 2 | CLI Restructure + Auth Commands | **Complete** | Phase 1 |
| 3 | Environment Discovery | **Complete** | Phase 1, 2 |
| 4 | Additional Auth Methods | **Complete** | Phase 2 |
| 5 | Data Command Integration | **Complete** | Phase 2, 3 |
| 6 | Pooling Support | **Complete** | Phase 5 |
| 7 | Polish & Documentation | **Complete** | All |

---

## Phase 1: PPDS.Auth Foundation - COMPLETE

**Goal:** Create the shared auth package with profile storage and core credential providers.

### Completed Items
- [x] Create `src/PPDS.Auth/PPDS.Auth.csproj`
- [x] Add to solution file
- [x] Configure target framework (net8.0)
- [x] Add package references (Azure.Identity, MSAL, Dataverse.Client)
- [x] Create folder structure (Credentials/, Profiles/, Cloud/)
- [x] Create `CloudEnvironment.cs` enum (Public, UsGov, UsGovHigh, UsGovDod, China)
- [x] Create `CloudEndpoints.cs` with authority URLs per cloud
- [x] Create `AuthProfile.cs` - profile data model
- [x] Create `EnvironmentInfo.cs` - environment binding model
- [x] Create `AuthMethod.cs` enum (DeviceCode, ClientSecret, Certificate, etc.)
- [x] Create `ProfileCollection.cs` - collection with active tracking
- [x] Create `ProfileStore.cs` - JSON file storage
- [x] Create `ProfileEncryption.cs` - DPAPI encryption for Windows
- [x] Create `ProfilePaths.cs` - platform-specific paths
- [x] Create `ICredentialProvider.cs` interface
- [x] Create `CredentialProviderFactory.cs` - creates provider from profile
- [x] Create `DeviceCodeCredentialProvider.cs` - interactive login
- [x] Create `ClientSecretCredentialProvider.cs` - app secret auth
- [x] Create `ServiceClientFactory.cs` - factory with performance settings
- [x] Create `AuthenticationException.cs` - custom exception

**Files Created:** 17 files in PPDS.Auth package

---

## Phase 2: CLI Restructure + Auth Commands - COMPLETE

**Goal:** Rename PPDS.Migration.Cli to PPDS.Cli, restructure command hierarchy, implement auth commands.

### 2.1 Project Rename - COMPLETE
- [x] `git mv src/PPDS.Migration.Cli src/PPDS.Cli`
- [x] Rename csproj: `PPDS.Migration.Cli.csproj` → `PPDS.Cli.csproj`
- [x] Update solution file references
- [x] Update csproj:
  - [x] PackageId → PPDS.Cli
  - [x] ToolCommandName → ppds
  - [x] RootNamespace → PPDS.Cli
  - [x] AssemblyName → ppds
  - [x] MinVerTagPrefix → Cli-v
- [x] Add project reference to PPDS.Auth

### 2.2 Namespace Updates - COMPLETE
- [x] Update all .cs files: `PPDS.Migration.Cli` → `PPDS.Cli`
- [x] Update using statements throughout

### 2.3 Command Folder Restructure - COMPLETE
- [x] Create `Commands/Auth/` folder
- [x] Create `Commands/Env/` folder
- [x] Create `Commands/Data/` folder
- [x] Move existing commands to Data folder:
  - [x] ExportCommand.cs → Commands/Data/
  - [x] ImportCommand.cs → Commands/Data/
  - [x] MigrateCommand.cs → Commands/Data/CopyCommand.cs (renamed)
  - [x] AnalyzeCommand.cs → Commands/Data/
- [x] Create DataCommandGroup.cs - wraps data subcommands
- [x] Update namespaces to PPDS.Cli.Commands.Data

### 2.4 Program.cs Restructure - COMPLETE
- [x] Create AuthCommandGroup with all commands
- [x] Add command groups to root command
- [x] Keep existing global auth options (--url, --auth) for compatibility
- [x] Wire up auth and data command groups

### 2.5 Test Updates - COMPLETE
- [x] Rename test project folder and csproj
- [x] Update test namespaces
- [x] Rename MigrateCommandTests.cs → CopyCommandTests.cs
- [x] Update test assertions for new command names
- [x] All 792 tests passing

### 2.6 Auth Commands - COMPLETE
- [x] `ppds auth create` - Create new profile (interactive login)
- [x] `ppds auth list` - List all profiles (text and JSON)
- [x] `ppds auth select` - Select active profile
- [x] `ppds auth delete` - Delete a profile (with confirmation)
- [x] `ppds auth update` - Re-authenticate existing profile
- [x] `ppds auth name` - Rename a profile
- [x] `ppds auth clear` - Delete all profiles and credentials
- [x] `ppds auth who` - Show current active profile

**Files Modified/Created:**
- Modified: 20+ files (namespace updates)
- Created: Commands/Auth/AuthCommandGroup.cs
- Created: Commands/Data/DataCommandGroup.cs
- Renamed: MigrateCommand.cs → CopyCommand.cs

---

## Phase 3: Environment Discovery - COMPLETE

**Goal:** Integrate Global Discovery Service for environment listing and selection.

### 3.1 Discovery Infrastructure - COMPLETE
- [x] Create `Discovery/` folder in PPDS.Auth
- [x] Create `DiscoveredEnvironment.cs` - model for GDS results
- [x] Create `IGlobalDiscoveryService.cs` interface
- [x] Create `GlobalDiscoveryService.cs` - uses ServiceClient.DiscoverOnlineOrganizationsAsync
- [x] Map OrganizationDetail to DiscoveredEnvironment
- [x] Support all cloud environments via CloudEndpoints

### 3.2 Environment Resolution - COMPLETE
- [x] Create `EnvironmentResolver.cs`
- [x] Match by GUID (environment ID)
- [x] Match by exact URL
- [x] Match by unique name
- [x] Match by friendly name (exact and partial)
- [x] Handle ambiguous matches with AmbiguousMatchException
- [x] Create `AmbiguousMatchException.cs`

### 3.3 Env Commands - COMPLETE
- [x] Create `Commands/Env/EnvCommandGroup.cs`
- [x] `ppds env list` - Discover and list environments via GDS
- [x] `ppds env select` - Resolve environment and bind to profile
- [x] `ppds env who` - Show current profile and environment
- [x] JSON output support for all commands
- [x] Active environment marker in list output

### 3.4 Profile Integration - COMPLETE
- [x] Add `EnvironmentId` property to EnvironmentInfo
- [x] Store full environment info on select
- [x] Wire up EnvCommandGroup in Program.cs

**Files Created:**
- src/PPDS.Auth/Discovery/DiscoveredEnvironment.cs
- src/PPDS.Auth/Discovery/IGlobalDiscoveryService.cs
- src/PPDS.Auth/Discovery/GlobalDiscoveryService.cs
- src/PPDS.Auth/Discovery/EnvironmentResolver.cs
- src/PPDS.Cli/Commands/Env/EnvCommandGroup.cs

---

## Phase 4: Additional Auth Methods - COMPLETE

**Goal:** Implement remaining credential providers.

### 4.1 Certificate File Auth - COMPLETE
- [x] Create `CertificateFileCredentialProvider.cs`
- [x] Load PFX/P12 from disk with optional password
- [x] Store cert temporarily in user store for ServiceClient
- [x] Add `--certificate-path` and `--certificate-password` options

### 4.2 Certificate Store Auth - COMPLETE
- [x] Create `CertificateStoreCredentialProvider.cs`
- [x] Find certificate by thumbprint in Windows store
- [x] Support StoreName and StoreLocation options
- [x] Add `--certificate-thumbprint` option
- [x] Platform check (Windows only)

### 4.3 Managed Identity Auth - COMPLETE
- [x] Create `ManagedIdentityCredentialProvider.cs`
- [x] Support system-assigned identity (no client ID)
- [x] Support user-assigned identity (with --application-id)
- [x] Add `--managed-identity` option
- [x] Use Azure.Identity.ManagedIdentityCredential

### 4.4 Auth Create Updates - COMPLETE
- [x] Update auth create with all new options
- [x] Auto-detect auth method from provided options
- [x] Validation for required fields per auth method
- [x] Platform checks for Windows-only features

### 4.5 Not Implemented (Future)
- [ ] GitHub OIDC Credential (--github-federated)
- [ ] Azure DevOps OIDC Credential (--azure-devops-federated)
- [ ] Username/Password Credential (deprecated)

**Files Created:**
- src/PPDS.Auth/Credentials/CertificateFileCredentialProvider.cs
- src/PPDS.Auth/Credentials/CertificateStoreCredentialProvider.cs
- src/PPDS.Auth/Credentials/ManagedIdentityCredentialProvider.cs

---

## Phase 5: Data Command Integration - COMPLETE

**Goal:** Wire up data commands to use new profile-based auth.

### 5.1 Infrastructure - COMPLETE
- [x] Create `ProfileConnectionSourceAdapter` in CLI (bridges PPDS.Auth → PPDS.Dataverse)
- [x] Create `ProfileServiceFactory` for creating service providers from profiles
- [x] Support single profile and multi-profile (pooling) scenarios

### 5.2 Export Command Updates - COMPLETE
- [x] Add `--profile` and `--environment` options
- [x] Profile-based auth only (no legacy fallback - pre-v1)
- [x] Use active profile when no `--profile` specified

### 5.3 Import Command Updates - COMPLETE
- [x] Add `--profile` and `--environment` options
- [x] Profile-based auth only (no legacy fallback - pre-v1)
- [x] Use active profile when no `--profile` specified

### 5.4 Copy Command Updates - COMPLETE
- [x] Add `--profile`, `--source-profile`, `--target-profile` options
- [x] Add `--source-env`, `--target-env` options (required)
- [x] Profile-based auth only (no legacy fallback - pre-v1)

### 5.5 Schema & Users Commands - COMPLETE
- [x] Update `SchemaCommand` to use profile-based auth
- [x] Update `UsersCommand` to use profile-based auth
- [x] Add `--profile`, `--environment`, `--source-env`, `--target-env` options

### 5.6 Shared Options - COMPLETE
- [x] Add `ProfileOption` and `EnvironmentOption` to `DataCommandGroup`
- [x] Options shared across all commands

### 5.7 Cleanup - COMPLETE
- [x] Remove global `--url` and `--auth` options from Program.cs
- [x] Delete legacy auth infrastructure (AuthMode, AuthResolver, DeviceCodeTokenProvider)
- [x] Simplify ServiceFactory (keep only CreateProgressReporter, CreateAnalysisProvider)

**Files Created:**
- src/PPDS.Cli/Infrastructure/ProfileConnectionSourceAdapter.cs
- src/PPDS.Cli/Infrastructure/ProfileServiceFactory.cs

**Files Modified:**
- src/PPDS.Cli/Program.cs
- src/PPDS.Cli/Infrastructure/ServiceFactory.cs
- src/PPDS.Cli/Commands/Data/DataCommandGroup.cs
- src/PPDS.Cli/Commands/Data/ExportCommand.cs
- src/PPDS.Cli/Commands/Data/ImportCommand.cs
- src/PPDS.Cli/Commands/Data/CopyCommand.cs
- src/PPDS.Cli/Commands/SchemaCommand.cs
- src/PPDS.Cli/Commands/UsersCommand.cs

**Files Deleted:**
- src/PPDS.Cli/Infrastructure/AuthMode.cs
- src/PPDS.Cli/Infrastructure/AuthResolver.cs
- src/PPDS.Cli/Infrastructure/DeviceCodeTokenProvider.cs

### 5.8 Interactive Browser Auth - COMPLETE
- [x] Create `InteractiveBrowserCredentialProvider` using MSAL's `AcquireTokenInteractive`
- [x] Auto-detect headless environments (SSH, CI, containers, no display)
- [x] Browser auth by default, device code fallback for headless
- [x] Update `CredentialProviderFactory` to select appropriate provider

**Files Created:**
- src/PPDS.Auth/Credentials/InteractiveBrowserCredentialProvider.cs

**Files Modified:**
- src/PPDS.Auth/Credentials/CredentialProviderFactory.cs

---

## Phase 6: Pooling Support - COMPLETE

**Goal:** Enable multiple profiles for high-throughput operations.

### 6.1 Multi-Profile Infrastructure - COMPLETE
- [x] `ConnectionResolver.ParseProfileString()` - parses comma-separated profiles
- [x] `ConnectionResolver.ResolveMultipleAsync()` - resolves multiple profiles with validation
- [x] `ProfileServiceFactory.CreateFromProfilesAsync()` - creates pool with multiple sources
- [x] Environment validation ensures all profiles target same environment

### 6.2 Command Integration - COMPLETE
- [x] `--profile` option accepts comma-separated names (e.g., `--profile app1,app2,app3`)
- [x] Works with export, import, schema commands
- [x] Updated help text to document pooling usage

### Usage Example
```bash
# Create Application User profiles
ppds auth create --name app1 --client-id <id1> --client-secret <secret1>
ppds auth create --name app2 --client-id <id2> --client-secret <secret2>
ppds auth create --name app3 --client-id <id3> --client-secret <secret3>

# Select same environment for all
ppds env select --profile app1 --environment "Prod"
ppds env select --profile app2 --environment "Prod"
ppds env select --profile app3 --environment "Prod"

# Use all three for 3x API quota
ppds data import --data large-data.zip --profile app1,app2,app3
```

**Files Modified:**
- src/PPDS.Cli/Commands/Data/DataCommandGroup.cs (updated help text)

---

## Phase 7: Polish & Documentation - COMPLETE

**Goal:** Production-ready quality and documentation.

### 7.1 Core Functionality - COMPLETE
- [x] All auth methods working (browser, device code, client secret, certificate, managed identity)
- [x] Environment discovery and selection
- [x] Data commands with profile-based auth
- [x] Multi-profile pooling support
- [x] Help text documents all options

### 7.2 Deferred to Future Releases
- [ ] Additional unit test coverage for PPDS.Auth
- [ ] CHANGELOG updates (will be done at release time)
- [ ] README documentation (will be done at release time)

**The unified CLI is feature-complete for v1.**

---

## Notes & Decisions Log

| Date | Decision | Rationale |
|------|----------|-----------|
| 2025-01-27 | Profile flag accepts names only, not indexes | Stability for pooling; indexes can shift |
| 2025-01-27 | Rename PPDS.Migration.Cli in place | Preserve git history |
| 2025-01-27 | Use `ppds env` not `ppds environment` | Shorter, cleaner command |
| 2025-01-27 | `data copy` instead of `data migrate` | Avoid `ppds migrate migrate` confusion |
| 2025-01-27 | No confirmation for `auth clear` | CI-friendly, scriptable |
| 2025-01-27 | Environment not auto-selected | Explicit is safer for production |
| 2025-12-27 | Combined CLI restructure and auth commands in Phase 2 | Reduced context switches |
| 2025-12-27 | Use ServiceClient.DiscoverOnlineOrganizationsAsync | Built-in GDS support, no custom HTTP |
| 2025-12-27 | Adapter pattern for ProfileConnectionSource | Avoids circular dep between Auth and Dataverse |
| 2025-12-27 | No legacy --url/--auth options | Pre-v1, no backwards compat needed |
| 2025-12-27 | Active profile used when no --profile specified | Seamless UX after `ppds auth create` |
| 2025-12-27 | Browser auth default, device code fallback | Better UX for desktop; headless fallback for CI/SSH |

---

## Blockers & Issues

| Issue | Status | Resolution |
|-------|--------|------------|
| None | - | - |

---

## Progress Summary

| Phase | Description | Status |
|-------|-------------|--------|
| 1 | PPDS.Auth Foundation | **Complete** |
| 2 | CLI Restructure + Auth Commands | **Complete** |
| 3 | Environment Discovery | **Complete** |
| 4 | Additional Auth Methods | **Complete** |
| 5 | Data Command Integration | **Complete** |
| 6 | Pooling Support | **Complete** |
| 7 | Polish & Documentation | **Complete** |

**Overall Progress:** All 7 phases complete (100%) - Feature complete for v1!

---

## Quick Reference

```bash
# Build
dotnet build

# Test
dotnet test

# Pack and test locally
dotnet pack -c Release -o ./nupkgs
dotnet tool install --global --add-source ./nupkgs PPDS.Cli

# Uninstall for re-testing
dotnet tool uninstall --global PPDS.Cli

# Auth workflow
ppds auth create --name dev
ppds auth list
ppds auth who

# Environment workflow
ppds env list
ppds env select "My Environment"
ppds env who
```
