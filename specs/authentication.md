# Authentication

**Status:** Implemented (BAP discovery: draft, env var auth: draft)
**Last Updated:** 2026-04-25
**Code:** [src/PPDS.Auth/](../src/PPDS.Auth/)
**Surfaces:** All

---

## Overview

The authentication system provides secure credential management, multi-method authentication, and environment discovery for Power Platform development. It supports nine authentication methods spanning interactive user flows, service principals, managed identities, and federated workload identities, with platform-native secure storage for secrets.

### Goals

- **Multi-Method Support**: Interactive browser, device code, service principal, certificates, managed identity, and federated credentials
- **Secure Storage**: Platform-native credential storage (Windows DPAPI, macOS Keychain, Linux libsecret)
- **Profile Management**: Named profiles with environment binding for easy switching
- **Token Caching**: Silent authentication via MSAL token cache persistence

### Non-Goals

- OAuth refresh token management (delegated to MSAL)
- Custom identity providers (Entra ID only)
- Cross-tenant authentication (single tenant per profile)

---

## Architecture

```
┌──────────────────────────────────────────────────────────────────────────┐
│                         Application Layer                                 │
│                  (CLI, TUI, RPC, MCP, Migration)                         │
└────────────────────────────────┬─────────────────────────────────────────┘
                                 │
                                 ▼
┌──────────────────────────────────────────────────────────────────────────┐
│                    CredentialProviderFactory                              │
│  Creates ICredentialProvider based on AuthProfile.AuthMethod              │
│  Retrieves secrets from ISecureCredentialStore                           │
└────────────────────────────────┬─────────────────────────────────────────┘
                                 │
          ┌──────────────────────┼──────────────────────┐
          │                      │                      │
          ▼                      ▼                      ▼
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│   Interactive   │    │  Service Princ  │    │   Federated     │
│    Providers    │    │    Providers    │    │   Providers     │
├─────────────────┤    ├─────────────────┤    ├─────────────────┤
│ Browser         │    │ ClientSecret    │    │ GitHub Actions  │
│ DeviceCode      │    │ CertificateFile │    │ Azure DevOps    │
│ UsernamePassword│    │ CertificateStore│    │                 │
│                 │    │ ManagedIdentity │    │                 │
└────────┬────────┘    └────────┬────────┘    └────────┬────────┘
         │                      │                      │
         └──────────────────────┼──────────────────────┘
                                │
                                ▼
┌──────────────────────────────────────────────────────────────────────────┐
│                        ServiceClient                                      │
│  Authenticated Dataverse connection for use with connection pool          │
└──────────────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────────────┐
│                    Profile & Storage Layer                                │
├────────────────────────────────────────────────────────────────────────┬─┤
│  ProfileStore                │   NativeCredentialStore                 │ │
│  ├─ profiles.json (v2)       │   ├─ Windows: Credential Manager (DPAPI)│ │
│  ├─ ProfileCollection        │   ├─ macOS: Keychain Services           │ │
│  └─ ProfileResolver          │   └─ Linux: libsecret (+ plaintext CI)  │ │
├──────────────────────────────┴─────────────────────────────────────────┴─┤
│  TokenCacheManager           │   ProfileEncryption                       │
│  └─ msal_token_cache.bin     │   └─ DPAPI (Win) / XOR (Unix)            │
└──────────────────────────────────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `CredentialProviderFactory` | Creates appropriate credential provider from profile + secrets |
| `ICredentialProvider` | Authenticates and creates `ServiceClient` instances |
| `ISecureCredentialStore` | Platform-native secret storage (DPAPI, Keychain, libsecret) |
| `ProfileStore` | Persists profile collection to JSON file |
| `ProfileResolver` | Resolves profile by name, index, or environment variable |
| `GlobalDiscoveryService` | Discovers accessible Dataverse environments (interactive users only) |
| `BapEnvironmentService` | Discovers environments via BAP API (service principals) |
| `ProfileConnectionSource` | Bridges profiles to connection pool (IConnectionSource) |
| `CloudEndpoints` | Cloud-specific URLs (Public, GCC, GCCHigh, DoD, China) |

### Dependencies

- Depends on: [architecture.md](./architecture.md) (error handling, DI patterns)
- Consumed by: [connection-pooling.md](./connection-pooling.md) (ProfileConnectionSource implements IConnectionSource)

---

## Specification

### Core Requirements

1. **Secrets never stored in profiles**: ClientSecret, Password, CertificatePassword stored in OS credential manager, keyed by ApplicationId
2. **Silent authentication preferred**: MSAL cache enables token reuse without user interaction
3. **Environment binding optional**: Profiles work across environments; explicit binding enables per-environment switching
4. **Multi-cloud support**: Single codebase supports Public, GCC, GCCHigh, DoD, and China clouds
5. **Environment variable authentication**: Fully stateless auth via `PPDS_CLIENT_ID`, `PPDS_CLIENT_SECRET`, `PPDS_TENANT_ID`, `PPDS_ENVIRONMENT_URL` — no profile on disk required

### Primary Flows

**Interactive Authentication (Browser/DeviceCode):**

1. **Load profile**: ProfileStore retrieves profile by name or index
2. **Create MSAL client**: MsalClientBuilder configures authority, cache, and redirect URI
3. **Try silent auth**: AcquireTokenSilent with cached HomeAccountId
4. **If silent fails**: Launch browser or display device code
5. **Capture HomeAccountId**: Store for future silent auth
6. **Create ServiceClient**: Wrap token in ConnectionOptions.AccessTokenProviderFunctionAsync

**Service Principal Authentication (ClientSecret/Certificate):**

1. **Load profile**: ProfileStore retrieves profile with ApplicationId, TenantId
2. **Load secret**: NativeCredentialStore retrieves ClientSecret or CertificatePassword
3. **Build connection string**: ConnectionStringBuilder with credentials
4. **Create ServiceClient**: Direct instantiation (SDK handles token internally)

**Environment Discovery (Interactive — Global Discovery):**

1. **Create GlobalDiscoveryService**: From profile with user-delegated auth method
2. **Authenticate**: Interactive or silent via MSAL public client
3. **Call Discovery API**: ServiceClient.DiscoverOnlineOrganizationsAsync
4. **Map results**: DiscoveredEnvironment with Id, Name, Url, Region, Type

**Environment Discovery (SPN — BAP API):**

1. **Create BapEnvironmentService**: From profile with confidential-client auth (ClientSecret, CertificateFile, CertificateStore). MSAL `IConfidentialClientApplication` is required; federated, managed-identity, and ROPC flows are not supported by this service today.
2. **Acquire token**: MSAL confidential client with scope `https://api.bap.microsoft.com/.default`
3. **Call BAP API**: `GET {CloudEndpoints.GetBapApiUrl(cloud)}/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments?api-version=2020-10-01`
4. **Map results**: Parse JSON response to `DiscoveredEnvironment`:
   - `name` → `EnvironmentId`
   - `properties.linkedEnvironmentMetadata.friendlyName` → `FriendlyName` (fallback: `properties.displayName` if missing)
   - `properties.linkedEnvironmentMetadata.instanceUrl` → `ApiUrl`
   - `properties.linkedEnvironmentMetadata.uniqueName` → `UniqueName`
   - `properties.linkedEnvironmentMetadata.domainName` → `UrlName`
   - `properties.linkedEnvironmentMetadata.resourceId` → `Id` (Guid)
   - `properties.linkedEnvironmentMetadata.instanceState` → `State` (map "Ready" → 0, other → 1)
   - `properties.azureRegion` → `Region`
   - `properties.environmentSku` → `OrganizationType` (map "Production" → 0, "Sandbox" → 5, "Developer" → 6, "Trial" → 11, "Default" → -1)
   - `properties.tenantId` → `TenantId`
5. **Return**: Same `DiscoveredEnvironment` type as Global Discovery

**Environment Resolution Strategy:**

1. **URL provided** → direct connection (no discovery needed)
2. **Name/ID + interactive auth** (InteractiveBrowser, DeviceCode) → Global Discovery Service
3. **Name/ID + confidential-client auth** (ClientSecret, CertificateFile, CertificateStore) → BAP Environment Service
4. **Name/ID + any other auth method** (UsernamePassword, ManagedIdentity, GitHubFederated, AzureDevOpsFederated) → returns a helpful error directing the caller to provide a full environment URL or switch to a supported auth method (no name-based discovery available today)
5. **Name matching**: case-insensitive match on `FriendlyName` or `UniqueName`; exact match preferred, partial match throws `AmbiguousMatchException` if multiple candidates

**Environment Variable Authentication (Stateless):**

1. **Check env vars**: `EnvironmentVariableAuth.TryCreateProfile()` checks for `PPDS_CLIENT_ID`, `PPDS_CLIENT_SECRET`, `PPDS_TENANT_ID`, `PPDS_ENVIRONMENT_URL`
2. **Partial detection**: If any of the four are set but not all, throw `PpdsException` with `Auth.IncompleteEnvironmentConfig` listing which vars are missing
3. **Synthesize profile**: Construct an in-memory `AuthProfile` with `AuthMethod.ClientSecret`, bound environment — no disk I/O, no ProfileStore. Cloud defaults to `Public`; override via optional `PPDS_CLOUD` env var (values: `Public`, `UsGov`, `UsGovHigh`, `UsGovDod`, `China`)
4. **Priority**: `ConnectionResolver` calls `EnvironmentVariableAuth.TryCreateProfile()` before `ProfileResolver`. Env vars take precedence over all profile-based auth
5. **No side effects**: No profile written to disk, no MSAL cache interaction, no credential store interaction. The synthetic profile flows through `CredentialProviderFactory` → `ClientSecretCredentialProvider`. The env var secret bypasses the credential store via the same mechanism as `PPDS_SPN_SECRET`: `CredentialProviderFactory.GetSpnSecretFromEnvironment()` already returns env var secrets and `ShouldBypassCredentialStore()` gates store access. The synthetic profile sets `PPDS_SPN_SECRET` in-process (or the factory is extended to accept a direct secret parameter) so no credential store lookup occurs

### Constraints

- Global Discovery only supports interactive methods (no service principals)
- BAP API requires SPN to be registered as a Power Platform management application via `New-PowerAppManagementApp` (one-time admin setup)
- BAP API only returns environments with linked Dataverse instances (`linkedEnvironmentMetadata` present); default/non-Dataverse environments are excluded from name resolution
- MSAL cache persistence requires write access to data directory
- Linux CI environments may require plaintext fallback (`--allow-cleartext-cache`)

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| ClientSecret profile | Requires ApplicationId, TenantId | `Auth.InvalidCredentials` |
| CertificateFile profile | Requires ApplicationId, CertificatePath, TenantId | `Auth.InvalidCredentials` |
| CertificateStore profile | Requires ApplicationId, CertificateThumbprint, TenantId | `Auth.InvalidCredentials` |
| UsernamePassword profile | Requires Username | `Auth.InvalidCredentials` |
| Federated profile | Requires ApplicationId, TenantId | `Auth.InvalidCredentials` |
| ManagedIdentity profile | No required fields (ApplicationId optional for user-assigned) | - |
| BAP API 403 | SPN not registered as management app | `Auth.BapApiForbidden` |
| BAP API 401 | Invalid or expired SPN token | `Auth.BapApiUnauthorized` |
| BAP API timeout | Network timeout calling BAP endpoint | `Auth.BapApiTimeout` |
| BAP API other error | 429, 5xx, or unexpected status | `Auth.BapApiError` |
| BAP name not found | No environment matches provided name | `Auth.EnvironmentNotFound` |

---

## Core Types

### ICredentialProvider

Core interface for authentication ([`Credentials/ICredentialProvider.cs`](../src/PPDS.Auth/Credentials/ICredentialProvider.cs)).

```csharp
public interface ICredentialProvider : IDisposable
{
    AuthMethod AuthMethod { get; }
    string? Identity { get; }
    string? TenantId { get; }
    DateTimeOffset? TokenExpiresAt { get; }
    string? ObjectId { get; }
    string? HomeAccountId { get; }
    string? AccessToken { get; }
    ClaimsPrincipal? IdTokenClaims { get; }

    Task<ServiceClient> CreateServiceClientAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default,
        bool forceInteractive = false);

    Task<CachedTokenInfo?> GetCachedTokenInfoAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default);
}
```

Supporting types:
- `CachedTokenInfo(DateTimeOffset ExpiresOn, string? Username, bool IsExpired)` — cached token state without triggering auth
- `CredentialResult` — factory class with `Succeeded()` and `Failed()` static methods

The implementation ([`InteractiveBrowserCredentialProvider.cs:89-156`](../src/PPDS.Auth/Credentials/InteractiveBrowserCredentialProvider.cs#L89-L156)) manages MSAL token acquisition with silent-first strategy.

### ISecureCredentialStore

Platform-native secret storage ([`Credentials/ISecureCredentialStore.cs`](../src/PPDS.Auth/Credentials/ISecureCredentialStore.cs)).

```csharp
public interface ISecureCredentialStore
{
    bool IsCleartextCachingEnabled { get; }
    Task StoreAsync(StoredCredential credential, CancellationToken ct = default);
    Task<StoredCredential?> GetAsync(string applicationId, CancellationToken ct = default);
    Task<bool> RemoveAsync(string applicationId, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
    Task<bool> ExistsAsync(string applicationId, CancellationToken ct = default);
}
```

The implementation ([`NativeCredentialStore.cs`](../src/PPDS.Auth/Credentials/NativeCredentialStore.cs)) delegates to a vendored subset of Microsoft's [git-credential-manager](https://github.com/git-ecosystem/git-credential-manager) source (MIT) under [`src/PPDS.Auth/Internal/CredentialStore/`](../src/PPDS.Auth/Internal/CredentialStore/), which owns the platform-native backends (Windows Credential Manager, macOS Keychain, libsecret). See "Why Vendored git-credential-manager?" below.

### IGlobalDiscoveryService

Environment discovery for interactive users ([`Discovery/IGlobalDiscoveryService.cs`](../src/PPDS.Auth/Discovery/IGlobalDiscoveryService.cs)).

```csharp
public interface IGlobalDiscoveryService
{
    Task<IReadOnlyList<DiscoveredEnvironment>> DiscoverEnvironmentsAsync(
        CancellationToken cancellationToken = default);
}
```

### IEnvironmentDiscoveryService

Shared interface for environment discovery, implemented by both `GlobalDiscoveryService` (interactive) and `BapEnvironmentService` (non-interactive). Both `IGlobalDiscoveryService` and the new `BapEnvironmentService` implement this interface, so consumers that don't care about the discovery method can depend on the shared contract.

```csharp
public interface IEnvironmentDiscoveryService
{
    Task<IReadOnlyList<DiscoveredEnvironment>> DiscoverEnvironmentsAsync(
        CancellationToken cancellationToken = default);
}
```

`BapEnvironmentService` ([`Discovery/BapEnvironmentService.cs`](../src/PPDS.Auth/Discovery/BapEnvironmentService.cs)) implements `IEnvironmentDiscoveryService` using `HttpClient` with a bearer token acquired via MSAL `IConfidentialClientApplication` (scope: `https://api.bap.microsoft.com/.default`). Parses the BAP JSON response and maps to the same `DiscoveredEnvironment` type used by `GlobalDiscoveryService`.

### AuthProfile

Profile model with auth configuration ([`Profiles/AuthProfile.cs`](../src/PPDS.Auth/Profiles/AuthProfile.cs)).

```csharp
public sealed class AuthProfile
{
    // Identity
    public int Index { get; set; }              // 1-based identifier
    public string? Name { get; set; }           // Optional display name
    public AuthMethod AuthMethod { get; set; }  // One of 9 methods
    public CloudEnvironment Cloud { get; set; } // Public, GCC, etc.
    public string? TenantId { get; set; }
    public string? Username { get; set; }       // For device code / password auth
    public string? ObjectId { get; set; }       // Entra ID Object ID

    // Application auth
    public string? ApplicationId { get; set; }

    // Certificate auth
    public string? CertificatePath { get; set; }
    public string? CertificateThumbprint { get; set; }
    public string? CertificateStoreName { get; set; }
    public string? CertificateStoreLocation { get; set; }

    // Environment
    public EnvironmentInfo? Environment { get; set; }

    // Metadata
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }

    // Token claims
    public string? Puid { get; set; }
    public string? HomeAccountId { get; set; }
    public string? Authority { get; set; }

    // Computed
    public bool HasEnvironment { get; }
    public bool HasName { get; }
    public string DisplayIdentifier { get; }
    public string IdentityDisplay { get; }

    // Methods
    public void Validate();
    public AuthProfile Clone();
}
```

### AuthMethod Enumeration

Nine supported authentication methods ([`Profiles/AuthMethod.cs`](../src/PPDS.Auth/Profiles/AuthMethod.cs)):

| Method | Use Case | Requirements |
|--------|----------|--------------|
| `InteractiveBrowser` | Desktop users | None (opens browser) |
| `DeviceCode` | SSH, headless | None (displays code) |
| `UsernamePassword` | Legacy/automation | Username + password in store |
| `ClientSecret` | CI/CD, services | ApplicationId + secret in store |
| `CertificateFile` | Secure deployments | ApplicationId + cert path |
| `CertificateStore` | Windows enterprise | ApplicationId + thumbprint |
| `ManagedIdentity` | Azure workloads | Azure environment |
| `GitHubFederated` | GitHub Actions | ApplicationId + env vars |
| `AzureDevOpsFederated` | Azure DevOps | ApplicationId + env vars |

### Usage Pattern

```csharp
// Load profile and create provider
var store = new ProfileStore();
var profile = (await store.LoadAsync()).GetByName("my-profile");
var credStore = new NativeCredentialStore();
var provider = await CredentialProviderFactory.CreateAsync(profile, credStore);

// Create authenticated client
var client = await provider.CreateServiceClientAsync(environmentUrl);

// Or use ProfileConnectionSource for pooling
var source = await ProfileConnectionSource.FromProfile(profile);
var pool = new DataverseConnectionPool([source]);
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| `AuthenticationException` | MSAL auth failure, invalid credentials | Check credentials, re-authenticate |
| `CredentialUnavailableException` | Managed identity not in Azure | Use different auth method |
| `MsalUiRequiredException` | Token expired, cache invalid | Force interactive auth |
| `TimeoutException` | Network timeout (60s for SPN) | Retry, check connectivity |

### Recovery Strategies

- **Token expired**: Provider attempts silent refresh via MSAL; on failure, triggers interactive auth
- **Invalid credentials**: Throws `AuthenticationException` with descriptive message
- **Managed identity unavailable**: Clear error message guiding to alternative auth methods
- **Federated token missing**: Validates environment variables, provides setup guidance

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| Empty credential store | Interactive methods work; SPN fails with clear error |
| Profile v1 detected | File deleted, warning logged, requires re-authentication |
| No DISPLAY on Linux | DeviceCode used instead of browser |
| Certificate without private key | Clear error with certificate store guidance |

---

## Design Decisions

### Why Secrets Separate from Profiles?

**Context:** Storing secrets in profiles.json would leave them in plaintext or require custom encryption.

**Decision:** Store secrets in platform-native credential managers (DPAPI, Keychain, libsecret), keyed by ApplicationId.

**Consequences:**
- Positive: OS-level encryption, no custom crypto
- Positive: Secrets survive profile file deletion
- Negative: Cannot export/import profiles with secrets
- Negative: Linux CI requires plaintext fallback flag

### Why Vendored git-credential-manager?

**Context:** The original implementation depended on the `Devlooped.CredentialManager` NuGet package for platform-native credential storage. Devlooped's source is MIT, but its binary distribution carries the **Open Source Maintenance Fee Agreement (OSMFEULA)** — an optional fee clause targeting revenue-generating users. For an MIT-licensed project adopted in enterprise contexts (Microsoft-ecosystem Dataverse work in particular), this atypical fee clause creates unnecessary friction in legal and OSS-compliance reviews.

**Decision:** Vendor the minimal subset of Microsoft's upstream [git-credential-manager](https://github.com/git-ecosystem/git-credential-manager) source directly into `src/PPDS.Auth/Internal/CredentialStore/`, preserving original Microsoft copyright and MIT headers. Drop the `Devlooped.CredentialManager` PackageReference. `NativeCredentialStore` continues to target the same OS storage APIs (DPAPI, Keychain, libsecret) with the same service name and key format.

**Alternatives considered:**
- Keep `Devlooped.CredentialManager`: Fails the enterprise legal-review bar — OSMFEULA is atypical for OSS and gets flagged in rigorous reviews.
- Write a fully custom P/Invoke implementation from scratch: Removes the vendored-file attribution surface but introduces ~500 lines of novel hand-rolled OS-boundary code. Enterprise reviewers must reason about correctness, Unicode edges, secret length limits, and libsecret error paths from first principles, rather than recognizing well-known Microsoft-MIT code. Net legal-review friction is not actually lower; implementation risk is strictly higher.

**Consequences:**
- Positive: Clean MIT licensing — the vendored files carry recognizable Microsoft copyright headers with preserved attribution in `THIRD_PARTY_NOTICES.md`.
- Positive: No runtime behavior change intended — same OS backends, same service name (`https://ppds.credentials`), same key format. Because storage targets identical OS entries, secrets written by the prior `Devlooped` implementation should read back under the vendored implementation; this is dev-verified during implementation rather than guaranteed by an automated AC.
- Positive: PPDS owns the vendored copy — free to evolve or trim independently of upstream.
- Negative: Upstream patches (bug fixes, new platforms) do not propagate automatically; periodic manual review of the source is required.
- Negative: ~5–10 vendored files carry Microsoft copyright headers in-tree; reviewers unfamiliar with vendoring may flag these (resolvable via pointer to NOTICES).

### Why HomeAccountId Persistence?

**Context:** MSAL silent auth requires HomeAccountId to find cached tokens. Without it, users authenticate interactively every time.

**Decision:** Capture HomeAccountId after successful auth and persist to profile.

**Implementation:**
```csharp
// After successful auth
if (provider.HomeAccountId != null && profile.HomeAccountId != provider.HomeAccountId)
{
    profile.HomeAccountId = provider.HomeAccountId;
    await profileStore.SaveAsync(collection);
}
```

**Consequences:**
- Positive: Silent auth works across sessions
- Positive: Token refresh happens transparently
- Negative: Profile file updated on every first login

### Why Multi-Tenant Authority for Discovery?

**Context:** Users may have profiles in multiple tenants. MSAL caches tokens per authority.

**Decision:** Use `organizations` authority for GlobalDiscoveryService, enabling cross-tenant token reuse.

**Consequences:**
- Positive: Single cache serves multiple tenant profiles
- Positive: Faster discovery after first auth
- Negative: Cache grows larger with multi-tenant usage

### Why v1 Profile Migration Deletes File?

**Context:** Profile format changed significantly (dict → array, new fields). Migration would require mapping obsolete fields.

**Decision:** Detect v1 profiles, delete file, require re-authentication.

**Consequences:**
- Positive: Clean start, no migration bugs
- Positive: Simpler code, no legacy handling
- Negative: Users lose profiles on upgrade (one-time)

### Why ProfileConnectionSource as Bridge?

**Context:** PPDS.Auth and PPDS.Dataverse cannot have circular dependencies, but authentication must provide connection sources.

**Decision:** `ProfileConnectionSource` in PPDS.Auth implements the `IConnectionSource` contract expected by the pool.

**Consequences:**
- Positive: Clean dependency direction (Auth → Dataverse)
- Positive: Profile-aware connection creation
- Negative: Adapter layer adds indirection

### Why EnvironmentVariableAuth as a Separate Class?

**Context:** Env var auth needs a home. Options: extend `ProfileResolver`, add to `CredentialProviderFactory`, or create a new class.

**Decision:** New `EnvironmentVariableAuth` class with a single `TryCreateProfile()` method. Called from `ConnectionResolver` before `ProfileResolver`.

**Alternatives considered:**
- Extend `ProfileResolver` with a 4th tier: Works but changes ProfileResolver's responsibility from "resolve persisted profiles" to "resolve auth config from any source." Becomes a dumping ground if more auth sources appear.
- Add to `CredentialProviderFactory`: Wrong layer — the factory creates credential providers from profiles, not profiles from env vars.
- Full `IAuthSource` chain-of-responsibility: YAGNI — two sources don't justify an abstraction. If a third source appears, extract the interface then.

**Consequences:**
- Positive: Single responsibility — ProfileResolver resolves profiles, EnvironmentVariableAuth synthesizes from env vars
- Positive: Testable in isolation — mock env vars, assert AuthProfile fields
- Positive: Trivial to refactor into an interface if a third auth source appears
- Negative: One more class in PPDS.Auth (minimal — it's small and focused)

### Why Timeout on Seed Client Creation?

**Context:** Credential provider creation and Dataverse connection can hang on network issues, blocking callers indefinitely.

**Decision:** 20-second timeout for credential provider, 60-second timeout for ServiceClient creation.

**Implementation ([`ProfileConnectionSource.cs:112-145`](../src/PPDS.Auth/Pooling/ProfileConnectionSource.cs#L112-L145)):**
```csharp
using var providerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
providerCts.CancelAfter(TimeSpan.FromSeconds(20));

using var clientCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
clientCts.CancelAfter(TimeSpan.FromSeconds(60));
```

**Consequences:**
- Positive: Predictable failure behavior
- Positive: Clear error messages on timeout
- Negative: May fail on slow networks (rare)

### Why BAP API Alongside Global Discovery?

**Context:** Service principals cannot resolve environment names to URLs because Global Discovery Service (`globaldisco.crm.dynamics.com`) requires delegated (user-present) permissions. SPNs must provide full environment URLs, making CI/CD configuration harder — operators need to know `https://orgcabef92d.crm.dynamics.com` instead of just `PPDS Demo - Dev`.

**Decision:** Add `BapEnvironmentService` as a separate discovery service using the BAP API (`api.bap.microsoft.com`), which supports application (client_credentials) authentication. Keep `GlobalDiscoveryService` unchanged for interactive users.

**Alternatives considered:**
- Extend `GlobalDiscoveryService` to handle both flows: Violates SRP — the service currently owns MSAL public client auth and the SDK's `DiscoverOnlineOrganizationsAsync`. BAP uses HttpClient with a confidential client. Mixing both in one class creates two unrelated auth paths.
- Common `IEnvironmentDiscoveryService` with factory: Both services share `IEnvironmentDiscoveryService` for consumers that don't care about the method. No factory needed — the caller (resolution strategy) picks the right implementation based on auth method. The shared interface avoids two redundant interface definitions with identical signatures.
- Use Power Platform API (`api.powerplatform.com`) instead of BAP: The new API uses RBAC and is more granular, but `api.bap.microsoft.com` is the GA path with simpler setup (`New-PowerAppManagementApp`). Can migrate later if needed.

**Prerequisites:** SPN must be registered as a Power Platform management application. This is a one-time tenant-admin action via `New-PowerAppManagementApp -ApplicationId {appId}` (PowerShell) or the BAP `adminApplications` REST endpoint. Without registration, the BAP API returns 403.

**Consequences:**
- Positive: SPNs can use `--environment "QA"` instead of full URLs
- Positive: No changes to existing Global Discovery flow
- Positive: Same `DiscoveredEnvironment` return type — consumers don't care which discovery method was used
- Negative: One-time admin registration step required per SPN
- Negative: BAP API only returns environments with linked Dataverse instances

---

## Extension Points

### Adding a New Credential Provider

1. **Create class** implementing `ICredentialProvider` in `src/PPDS.Auth/Credentials/`
2. **Add enum value** to `AuthMethod` in `src/PPDS.Auth/Profiles/AuthMethod.cs`
3. **Add case** to `CredentialProviderFactory.CreateAsync()` factory method
4. **Add validation** in `AuthProfile.Validate()` for required fields

**Example skeleton:**

```csharp
public class MyCredentialProvider : ICredentialProvider
{
    public AuthMethod AuthMethod => AuthMethod.MyMethod;
    public string? Identity { get; private set; }
    public string? TenantId { get; }
    public DateTimeOffset? TokenExpiresAt { get; private set; }

    public async Task<ServiceClient> CreateServiceClientAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default,
        bool forceInteractive = false)
    {
        // Implement authentication flow
        // Return authenticated ServiceClient
    }

    public void Dispose() { /* cleanup */ }
}
```

### Adding a New Cloud Environment

1. **Add enum value** to `CloudEnvironment` in `src/PPDS.Auth/Cloud/CloudEnvironment.cs`
2. **Add endpoints** to each method in `CloudEndpoints`:
   - `GetAuthorityBaseUrl()`
   - `GetGlobalDiscoveryUrl()`
   - `GetBapApiUrl()`
   - `GetPowerAppsApiUrl()`
   - etc.

---

## Configuration

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| `PPDS_CLIENT_ID` | env var | No* | - | Stateless auth: application (client) ID |
| `PPDS_CLIENT_SECRET` | env var | No* | - | Stateless auth: client secret |
| `PPDS_TENANT_ID` | env var | No* | - | Stateless auth: Entra tenant ID |
| `PPDS_ENVIRONMENT_URL` | env var | No* | - | Stateless auth: Dataverse environment URL |
| `PPDS_CLOUD` | env var | No | `Public` | Stateless auth: cloud environment (`Public`, `UsGov`, `UsGovHigh`, `UsGovDod`, `China`) |
| `PPDS_PROFILE` | env var | No | - | Override active profile (profile-based auth) |
| `PPDS_CONFIG_DIR` | env var | No | Platform default | Override data directory |
| `PPDS_SPN_SECRET` | env var | No | - | ClientSecret override for existing profile (CI/CD) |
| Profile.Cloud | enum | No | Public | Cloud environment |
| Profile.TenantId | string | Varies | - | Entra tenant ID |
| Profile.ApplicationId | string | Varies | - | App registration client ID |

\* All four `PPDS_CLIENT_ID`, `PPDS_CLIENT_SECRET`, `PPDS_TENANT_ID`, `PPDS_ENVIRONMENT_URL` must be set together. Setting 1–3 of 4 is a hard error (`Auth.IncompleteEnvironmentConfig`). `PPDS_CLOUD` is optional and defaults to `Public`.

### Storage Locations

| Platform | Data Directory | Example |
|----------|----------------|---------|
| Windows | `%LOCALAPPDATA%\PPDS\` | `C:\Users\me\AppData\Local\PPDS\` |
| macOS/Linux | `~/.ppds/` | `/home/me/.ppds/` |

### Stored Files

| File | Purpose |
|------|---------|
| `profiles.json` | Profile collection (v2 format) |
| `msal_token_cache.bin` | MSAL token cache (encrypted) |

---

## Testing

### Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-01 | All 9 auth methods create valid ServiceClient | Integration tests | ✅ |
| AC-02 | Silent auth succeeds with cached HomeAccountId | Integration tests | ✅ |
| AC-03 | Secrets retrieved from native credential store | `NativeCredentialStoreTests` | ✅ |
| AC-04 | Profile CRUD operations persist correctly | `ProfileStoreTests` | ✅ |
| AC-05 | Cloud endpoints return correct URLs per environment | `CloudEndpointsTests` | ✅ |
| AC-06 | Global discovery returns accessible environments | Integration tests | ✅ |
| AC-07 | All four env vars set → `TryCreateProfile` returns synthetic `AuthProfile` with `AuthMethod.ClientSecret` and bound environment | `EnvironmentVariableAuthTests.AllVarsSet_ReturnsSyntheticProfile` | 🔲 |
| AC-08 | No env vars set → `TryCreateProfile` returns null, profile resolution proceeds normally | `EnvironmentVariableAuthTests.NoVarsSet_ReturnsNull` | 🔲 |
| AC-09 | Partial env vars (1-3 of 4) → throws `PpdsException` with `Auth.IncompleteEnvironmentConfig` listing missing vars | `EnvironmentVariableAuthTests.PartialVars_ThrowsWithMissingList` | 🔲 |
| AC-10 | Env var auth takes precedence over `PPDS_PROFILE` and active profile | `EnvironmentVariableAuthTests.EnvVarAuth_TakesPrecedence` | 🔲 |
| AC-11 | Synthetic profile produces working `ServiceClient` via `CredentialProviderFactory` | `EnvironmentVariableAuthTests.SyntheticProfile_CreatesProvider` | 🔲 |
| AC-12 | No disk I/O: no profile written, no MSAL cache, no credential store access | `EnvironmentVariableAuthTests.NoSideEffects` | 🔲 |
| AC-13 | `PPDS.Auth` assembly references no `Devlooped.*` assembly at runtime | `DependencyAuditTests.PpdsAuthAssembly_DoesNotReferenceDevlooped` | ✅ |
| AC-14a | Vendored credential store round-trips a secret via Windows Credential Manager (DPAPI) | `NativeCredentialStoreInteropTests.Windows_RoundTripsSecret` (CI: `windows-latest`) | 🔲 (flips ✅ on green CI matrix) |
| AC-14b | Vendored credential store round-trips a secret via macOS Keychain | `NativeCredentialStoreInteropTests.MacOS_RoundTripsSecret` (CI: `macos-latest`) | 🔲 (flips ✅ on green CI matrix) |
| AC-14c | Vendored credential store round-trips a secret via Linux libsecret | `NativeCredentialStoreInteropTests.Linux_RoundTripsSecret` (CI: `ubuntu-latest` with libsecret installed) | 🔲 (flips ✅ on green CI matrix) |
| AC-15 | `BapEnvironmentService.DiscoverEnvironmentsAsync` returns environments with correct FriendlyName, ApiUrl, UniqueName, EnvironmentId, Region, State, and OrganizationType from BAP API JSON | `BapEnvironmentServiceTests.DiscoverEnvironments_MapsJsonToDiscoveredEnvironments` | ✅ |
| AC-16 | `BapEnvironmentService` skips environments without `linkedEnvironmentMetadata` (no Dataverse instance) | `BapEnvironmentServiceTests.DiscoverEnvironments_SkipsNonDataverseEnvironments` | ✅ |
| AC-17 | `BapEnvironmentService` throws `AuthenticationException` with `ErrorCode = Auth.BapApiForbidden` on 403 response (SPN not registered as management app) | `BapEnvironmentServiceTests.DiscoverEnvironments_Throws_OnForbidden` | ✅ |
| AC-18 | `CloudEndpoints.GetBapApiUrl` returns correct URL for each cloud (Public, GCC, GCCHigh, DoD, China) | `CloudEndpointsTests.GetBapApiUrl_ReturnsCorrectUrl_ForEachCloud` | ✅ |
| AC-19 | BAP discovery returns environments for SPN with management app registration | `BapDiscoveryIntegrationTests.BapEnvironmentService_DiscoversEnvironments` (Category=Integration) | ✅ |
| AC-20 | `ClientSecretCredentialProvider` validates required fields (ApplicationId, ClientSecret, TenantId) and rejects null/empty inputs | `ClientSecretCredentialProviderTests` | ✅ |
| AC-21 | `CertificateFileCredentialProvider` validates required fields (ApplicationId, CertificatePath, TenantId) and rejects invalid cert paths | `CertificateFileCredentialProviderTests` | ✅ |
| AC-22 | `CertificateStoreCredentialProvider` validates required fields (ApplicationId, Thumbprint, TenantId) and rejects invalid inputs | `CertificateStoreCredentialProviderTests` | ✅ |
| AC-23 | `ManagedIdentityCredentialProvider` constructs with optional ClientId and sets correct AuthMethod | `ManagedIdentityCredentialProviderTests` | ✅ |
| AC-24 | `GitHubFederatedCredentialProvider` validates required fields (ApplicationId, TenantId) and sets correct AuthMethod | `GitHubFederatedCredentialProviderTests` | ✅ |
| AC-25 | `AzureDevOpsFederatedCredentialProvider` validates required fields (ApplicationId, TenantId) and sets correct AuthMethod | `AzureDevOpsFederatedCredentialProviderTests` | ✅ |
| AC-26 | Non-interactive auth method + environment name routes to `BapEnvironmentService` for resolution | `EnvironmentResolutionTests.SpnAuth_NameIdentifier_RoutesBapDiscovery` | ✅ |
| AC-27 | Interactive auth method + environment name routes to `GlobalDiscoveryService` for resolution | `EnvironmentResolutionTests.Interactive_SupportsGlobalDiscovery` | ✅ |
| AC-28 | Environment URL provided directly skips discovery entirely regardless of auth method | `EnvironmentResolutionTests.UrlIdentifier_AttemptsDirectConnection` | ✅ |
| AC-29 | BAP-discovered environments resolve by name case-insensitively matching FriendlyName or UniqueName | `EnvironmentResolverTests.Resolve_ByFriendlyNameCaseInsensitive_ReturnsEnvironment`, `Resolve_ByUniqueNameCaseInsensitive_ReturnsEnvironment` | ✅ |
| AC-30 | BAP API 401 response throws `AuthenticationException` with `ErrorCode = Auth.BapApiUnauthorized` | `BapEnvironmentServiceTests.DiscoverEnvironments_Throws_OnUnauthorized` | ✅ |
| AC-31 | BAP API 5xx / unexpected status throws `AuthenticationException` with `ErrorCode = Auth.BapApiError` (status code included in the message) | `BapEnvironmentServiceTests.DiscoverEnvironments_Throws_OnServerError` | ✅ |
| AC-32 | BAP API request that times out (non-cancelled `TaskCanceledException`) throws `AuthenticationException` with `ErrorCode = Auth.BapApiTimeout` wrapping the inner timeout | `BapEnvironmentServiceTests.DiscoverEnvironments_Throws_OnTimeout` | ✅ |
| AC-33 | BAP API request whose `CancellationToken` is already cancelled propagates `OperationCanceledException` (no swallow into `Auth.BapApiTimeout`) | `BapEnvironmentServiceTests.DiscoverEnvironments_PropagatesCancellation_WhenTokenCancelled` | ✅ |
| AC-34 | `EnvironmentResolutionResult.Failed` carries a structured `ErrorCode`; "name not found" branches set `Auth.EnvironmentNotFound` and the message lists the available names | `EnvironmentResolutionTests.EnvironmentNotFoundMessage_ListsAvailableNames`, `EnvironmentResolutionResultTests.Failed_PreservesErrorCode` | ✅ |

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Profile not found | `GetByName("nonexistent")` | Returns null |
| Secret not stored | ClientSecret profile, empty store | Throws `AuthenticationException` |
| v1 profile file | Old format JSON | File deleted, empty collection returned |
| Managed identity outside Azure | ManagedIdentity auth | `CredentialUnavailableException` |
| Certificate thumbprint invalid | CertificateStore auth | Clear error with thumbprint |
| All four env vars set | `PPDS_CLIENT_ID`, `PPDS_CLIENT_SECRET`, `PPDS_TENANT_ID`, `PPDS_ENVIRONMENT_URL` | Synthetic profile, no disk I/O |
| Only `PPDS_CLIENT_ID` set | One of four vars | `PpdsException` listing 3 missing vars |
| Env vars set + `--profile` flag | Both present | Env vars win (highest priority) |
| `PPDS_ENVIRONMENT_URL` missing scheme | `myorg.crm.dynamics.com` | `PpdsException` with `Auth.InvalidEnvironmentUrl` — must be full URL with `https://` |
| Env var values are whitespace | `PPDS_CLIENT_ID=" "` | Treated as not set (trimmed) |
| BAP API 403 (SPN not registered) | SPN without management app registration | `AuthenticationException` with `ErrorCode = Auth.BapApiForbidden` and guidance to run `New-PowerAppManagementApp` |
| BAP API returns no Dataverse environments | All environments are default/non-Dataverse | Empty list returned (no match possible) |
| BAP API exact name matches multiple | Two environments with identical FriendlyName "QA" | First exact match returned (rare — BAP returns deterministic order) |
| BAP API partial name matches multiple | Input "QA" matches "QA Dev" and "QA Test" | `AmbiguousMatchException` listing matching environment names |
| BAP API network timeout | Endpoint unreachable | `AuthenticationException` with `ErrorCode = Auth.BapApiTimeout` wrapping inner `TaskCanceledException` |
| BAP API 401 (invalid token) | Expired or malformed SPN token | `AuthenticationException` with `ErrorCode = Auth.BapApiUnauthorized` |
| BAP API 429/5xx (transient) | Rate limited or server error | `AuthenticationException` with `ErrorCode = Auth.BapApiError` including status code |
| BAP name not found | `--environment "Staging"` but no match | `EnvironmentResolutionResult.Failed` with `ErrorCode = Auth.EnvironmentNotFound`; the message lists the available environment names |

### Test Examples

```csharp
[Fact]
public async Task ProfileStore_RoundTrips_ProfileCollection()
{
    var store = new ProfileStore(tempPath);
    var profile = new AuthProfile
    {
        Index = 1,
        Name = "test",
        AuthMethod = AuthMethod.InteractiveBrowser,
        Cloud = CloudEnvironment.Public
    };

    var collection = new ProfileCollection();
    collection.Add(profile, setAsActive: true);
    await store.SaveAsync(collection);

    var loaded = await store.LoadAsync();
    Assert.Single(loaded.Profiles);
    Assert.Equal("test", loaded.ActiveProfile?.Name);
}

[Fact]
public async Task NativeCredentialStore_StoresAndRetrieves_Secret()
{
    var store = new NativeCredentialStore();
    var credential = new StoredCredential
    {
        ApplicationId = "test-app-id",
        ClientSecret = "test-secret"
    };

    await store.StoreAsync(credential);
    var retrieved = await store.GetAsync("test-app-id");

    Assert.Equal("test-secret", retrieved?.ClientSecret);
}

[Fact]
public void CloudEndpoints_ReturnsCorrectAuthority_ForEachCloud()
{
    Assert.Equal("https://login.microsoftonline.com",
        CloudEndpoints.GetAuthorityBaseUrl(CloudEnvironment.Public));
    Assert.Equal("https://login.microsoftonline.us",
        CloudEndpoints.GetAuthorityBaseUrl(CloudEnvironment.UsGov));
}
```

---

## Related Specs

- [architecture.md](./architecture.md) - Application Services pattern, error handling
- [connection-pooling.md](./connection-pooling.md) - ProfileConnectionSource integrates as IConnectionSource
- [mcp.md](./mcp.md) - MCP server uses profiles for authentication

---

## Changelog

| Date | Change |
|------|--------|
| 2026-04-25 | Added BAP Environment Service for SPN name-based environment resolution (#99); added credential provider unit test ACs (#133) |
| 2026-04-18 | Vendored `microsoft/git-credential-manager` storage code into `src/PPDS.Auth/Internal/CredentialStore/`; removed `Devlooped.CredentialManager` dependency and its OSMFEULA-licensed binary distribution |
| 2026-03-26 | Added environment variable authentication (stateless auth via PPDS_CLIENT_ID/SECRET/TENANT_ID/ENVIRONMENT_URL) |
| 2026-03-18 | Added Surfaces frontmatter, Changelog per spec governance |

## Roadmap

- Certificate auto-renewal detection and prompting
- Support for external identity providers via SAML/WS-Fed
- Profile import/export with encrypted secrets
- Multi-tenant profile support (single profile, multiple tenants)
