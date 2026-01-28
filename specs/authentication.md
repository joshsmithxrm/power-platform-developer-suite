# Authentication

**Status:** Implemented
**Version:** 1.0
**Last Updated:** 2026-01-23
**Code:** [src/PPDS.Auth/](../src/PPDS.Auth/)

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
| `GlobalDiscoveryService` | Discovers accessible Dataverse environments |
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

**Environment Discovery:**

1. **Create GlobalDiscoveryService**: From profile with user-delegated auth method
2. **Authenticate**: Interactive or silent via MSAL public client
3. **Call Discovery API**: ServiceClient.DiscoverOnlineOrganizationsAsync
4. **Map results**: DiscoveredEnvironment with Id, Name, Url, Region, Type

### Constraints

- Global Discovery only supports interactive methods (no service principals)
- Service principals must use full environment URLs (cannot discover by name)
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

The implementation ([`NativeCredentialStore.cs:45-180`](../src/PPDS.Auth/Credentials/NativeCredentialStore.cs#L45-L180)) uses Git Credential Manager for cross-platform support.

### IGlobalDiscoveryService

Environment discovery for interactive users ([`Discovery/IGlobalDiscoveryService.cs`](../src/PPDS.Auth/Discovery/IGlobalDiscoveryService.cs)).

```csharp
public interface IGlobalDiscoveryService
{
    Task<IReadOnlyList<DiscoveredEnvironment>> DiscoverEnvironmentsAsync(
        CancellationToken cancellationToken = default);
}
```

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
   - `GetPowerAppsApiUrl()`
   - etc.

---

## Configuration

| Setting | Type | Required | Default | Description |
|---------|------|----------|---------|-------------|
| `PPDS_PROFILE` | env var | No | - | Override active profile |
| `PPDS_CONFIG_DIR` | env var | No | Platform default | Override data directory |
| `PPDS_SPN_SECRET` | env var | No | - | ClientSecret override (CI/CD) |
| Profile.Cloud | enum | No | Public | Cloud environment |
| Profile.TenantId | string | Varies | - | Entra tenant ID |
| Profile.ApplicationId | string | Varies | - | App registration client ID |

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

- [ ] All 9 auth methods create valid ServiceClient
- [ ] Silent auth succeeds with cached HomeAccountId
- [ ] Secrets retrieved from native credential store
- [ ] Profile CRUD operations persist correctly
- [ ] Cloud endpoints return correct URLs per environment
- [ ] Global discovery returns accessible environments

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Profile not found | `GetByName("nonexistent")` | Returns null |
| Secret not stored | ClientSecret profile, empty store | Throws `AuthenticationException` |
| v1 profile file | Old format JSON | File deleted, empty collection returned |
| Managed identity outside Azure | ManagedIdentity auth | `CredentialUnavailableException` |
| Certificate thumbprint invalid | CertificateStore auth | Clear error with thumbprint |

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

## Roadmap

- Certificate auto-renewal detection and prompting
- Support for external identity providers via SAML/WS-Fed
- Profile import/export with encrypted secrets
- Multi-tenant profile support (single profile, multiple tenants)
