# PPDS.Auth: Credential Providers

## Overview

The Credential Providers subsystem provides authenticated `ServiceClient` instances for various authentication methods. It includes a factory for creating providers from auth profiles, platform-native secure storage for secrets, and implementations for interactive, service principal, managed identity, and federated authentication flows.

## Public API

### Interfaces

| Interface | Purpose |
|-----------|---------|
| `ICredentialProvider` | Creates authenticated ServiceClient for a specific auth method |
| `ISecureCredentialStore` | Platform-native secure storage for secrets |

### Classes

| Class | Purpose |
|-------|---------|
| `CredentialProviderFactory` | Creates providers from auth profiles |
| `NativeCredentialStore` | OS-native credential storage implementation |

### Credential Provider Implementations

| Provider | Auth Method | Description |
|----------|-------------|-------------|
| `InteractiveBrowserCredentialProvider` | InteractiveBrowser | Browser popup flow |
| `DeviceCodeCredentialProvider` | DeviceCode | URL + code for headless |
| `ClientSecretCredentialProvider` | ClientSecret | App + secret |
| `CertificateFileCredentialProvider` | CertificateFile | App + PFX file |
| `CertificateStoreCredentialProvider` | CertificateStore | App + Windows cert store |
| `ManagedIdentityCredentialProvider` | ManagedIdentity | Azure managed identity |
| `GitHubFederatedCredentialProvider` | GitHubFederated | GitHub Actions OIDC |
| `AzureDevOpsFederatedCredentialProvider` | AzureDevOpsFederated | Azure DevOps OIDC |
| `UsernamePasswordCredentialProvider` | UsernamePassword | ROPC flow |

### DTOs/Models

| Type | Purpose |
|------|---------|
| `StoredCredential` | Credential data model (secrets, passwords) |
| `CachedTokenInfo` | Token cache status without triggering auth |
| `CredentialResult` | Success/failure result of auth attempt |
| `DeviceCodeInfo` | Device code display info (code, URL, expiry) |
| `PreAuthDialogResult` | User choice before interactive auth |

## Behaviors

### Factory Operations

| Method | Description |
|--------|-------------|
| `CreateAsync` | Create provider from profile (async, supports secure store) |
| `Create` | Create provider from profile (sync, limited functionality) |
| `IsSupported` | Check if auth method is supported |
| `RequiresCredentialStore` | Check if auth method needs secure storage |

### Provider Interface

| Method/Property | Description |
|-----------------|-------------|
| `CreateServiceClientAsync` | Authenticate and create ServiceClient |
| `GetCachedTokenInfoAsync` | Query token cache without triggering auth |
| `Identity` | Username or app ID (after auth) |
| `TokenExpiresAt` | Token expiration time |
| `TenantId`, `ObjectId` | Identity claims from token |
| `HomeAccountId` | MSAL account identifier |
| `AccessToken` | Raw token for claim extraction |

### Secure Credential Store

| Method | Description |
|--------|-------------|
| `StoreAsync` | Save credential keyed by applicationId |
| `GetAsync` | Retrieve credential by applicationId |
| `RemoveAsync` | Delete credential |
| `ClearAsync` | Delete all stored credentials |
| `ExistsAsync` | Check if credential exists |

## Platform Storage

| Platform | Mechanism | Notes |
|----------|-----------|-------|
| Windows | DPAPI (CurrentUser scope) | Windows Credential Manager |
| macOS | Keychain Services | System keychain |
| Linux | libsecret | GNOME Keyring or KWallet |
| Linux (CI) | Plaintext file | When libsecret unavailable |

## Environment Variable Overrides

| Variable | Purpose |
|----------|---------|
| `PPDS_SPN_SECRET` | Client secret bypass for CI/CD |
| `PPDS_TEST_CLIENT_SECRET` | Fallback for test scenarios |
| `GCM_CREDENTIAL_STORE` | Git Credential Manager backend |

## Edge Cases

| Scenario | Behavior | Notes |
|----------|----------|-------|
| No secret in store | Throws `InvalidOperationException` | Requires profile creation |
| Environment var set | Uses env var over store | CI/CD priority |
| Headless + InteractiveBrowser | Falls back to DeviceCode | Checks `IsAvailable()` |
| PFX password missing | Prompts or uses empty | Optional for unprotected certs |
| Managed identity on non-Azure | Auth fails | Only works on Azure resources |
| Token expired | Re-authenticates | MSAL handles refresh |
| User cancels browser | Throws `OperationCanceledException` | Graceful cancel |

## Error Handling

| Exception | Condition | Recovery |
|-----------|-----------|----------|
| `AuthenticationException` | Auth failed | Check credentials, retry |
| `InvalidOperationException` | Missing required secret | Create profile or set env var |
| `NotSupportedException` | Unknown auth method | Use supported method |
| `OperationCanceledException` | User cancelled | Handle cancellation |

## Authentication Flow Details

### Interactive Browser Flow

1. Invoke `beforeInteractiveAuth` callback for user choice
2. User selects: OpenBrowser, UseDeviceCode, or Cancel
3. If browser: open system browser for login
4. Receive callback with authorization code
5. Exchange code for tokens
6. Cache tokens via MSAL

### Device Code Flow

1. Request device code from Azure AD
2. Display code and URL via callback
3. User visits URL, enters code
4. Poll for completion
5. Receive tokens on success

### Service Principal Flows

1. Load secret/certificate from secure store or env var
2. Create `ConfidentialClientApplication`
3. Acquire token with client credentials
4. Create ServiceClient with token provider

### Federated Identity Flows

1. Read OIDC token from environment (GitHub/Azure DevOps)
2. Exchange OIDC token for Azure AD token
3. Create ServiceClient with federated token

## Stored Credential Format

```json
{
  "s": "client-secret-value",
  "c": "cert/path.pfx||||password",
  "p": "user-password"
}
```

- `s`: Client secret for ClientSecret auth
- `c`: Certificate path + optional password (separated by `||||`)
- `p`: Password for UsernamePassword auth

## Thread Safety

- **CredentialProviderFactory**: Thread-safe (static methods)
- **NativeCredentialStore**: Thread-safe (uses Git Credential Manager)
- **Provider implementations**: Generally stateless; thread-safe for `CreateServiceClientAsync`

## Dependencies

- **Internal**:
  - `PPDS.Auth.Profiles` - Auth profile configuration
  - `PPDS.Auth.Cloud` - Cloud endpoint resolution
- **External**:
  - `Microsoft.PowerPlatform.Dataverse.Client` (ServiceClient)
  - `Microsoft.Identity.Client` (MSAL)
  - `GitCredentialManager` (OS credential stores)
  - `Azure.Identity` (ManagedIdentity, WorkloadIdentity)

## Related

- [Profile Storage spec](./01-profile-storage.md) - Provides auth configuration
- [Token Management spec](./03-token-management.md) - MSAL token caching
- [Cloud Support spec](./05-cloud-support.md) - Cloud-specific endpoints

## Source Files

| File | Purpose |
|------|---------|
| `src/PPDS.Auth/Credentials/ICredentialProvider.cs` | Provider interface |
| `src/PPDS.Auth/Credentials/CredentialProviderFactory.cs` | Factory |
| `src/PPDS.Auth/Credentials/ISecureCredentialStore.cs` | Store interface |
| `src/PPDS.Auth/Credentials/NativeCredentialStore.cs` | Platform-native store |
| `src/PPDS.Auth/Credentials/InteractiveBrowserCredentialProvider.cs` | Browser flow |
| `src/PPDS.Auth/Credentials/DeviceCodeCredentialProvider.cs` | Device code flow |
| `src/PPDS.Auth/Credentials/ClientSecretCredentialProvider.cs` | Client secret flow |
| `src/PPDS.Auth/Credentials/CertificateFileCredentialProvider.cs` | Certificate file flow |
| `src/PPDS.Auth/Credentials/CertificateStoreCredentialProvider.cs` | Certificate store flow |
| `src/PPDS.Auth/Credentials/ManagedIdentityCredentialProvider.cs` | Managed identity |
| `src/PPDS.Auth/Credentials/GitHubFederatedCredentialProvider.cs` | GitHub OIDC |
| `src/PPDS.Auth/Credentials/AzureDevOpsFederatedCredentialProvider.cs` | Azure DevOps OIDC |
| `src/PPDS.Auth/Credentials/UsernamePasswordCredentialProvider.cs` | ROPC flow |
| `src/PPDS.Auth/Credentials/AuthenticationException.cs` | Auth exception |
| `src/PPDS.Auth/Credentials/PreAuthDialogResult.cs` | Pre-auth user choice |
