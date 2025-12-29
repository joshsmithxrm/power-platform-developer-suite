# PPDS.Auth

Authentication profile management for Power Platform with encrypted credential storage and multi-cloud support.

## Installation

```bash
dotnet add package PPDS.Auth
```

## Quick Start

```csharp
// 1. Create and store a profile
var store = new ProfileStore();
var profile = new AuthProfile("dev")
{
    AuthMethod = AuthMethod.InteractiveBrowser,
    ClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d" // Default Power Platform client
};
await store.SaveAsync(profile);

// 2. Authenticate and get a ServiceClient
var provider = CredentialProviderFactory.Create(profile);
var client = await ServiceClientFactory.CreateAsync(provider, profile);
```

## Features

### Profile Storage

Profiles are stored with encrypted secrets (DPAPI on Windows):

```csharp
var store = new ProfileStore();

// Save a profile
await store.SaveAsync(profile);

// Load all profiles
var profiles = await store.LoadAsync();

// Get active profile
var active = profiles.GetActiveProfile();

// Set active profile
profiles.SetActive("dev");
await store.SaveAsync(profiles);
```

Default storage location: `~/.ppds/profiles.json`

### Authentication Methods

```csharp
// Interactive Browser (opens browser for OAuth)
var profile = new AuthProfile("dev")
{
    AuthMethod = AuthMethod.InteractiveBrowser,
    ClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d"
};

// Device Code (for headless/SSH environments)
var profile = new AuthProfile("dev")
{
    AuthMethod = AuthMethod.DeviceCode,
    ClientId = "51f81489-12ee-4a9e-aaae-a2591f45987d"
};

// Client Secret (service principal)
var profile = new AuthProfile("ci")
{
    AuthMethod = AuthMethod.ClientSecret,
    ApplicationId = "your-app-id",
    TenantId = "your-tenant-id",
    ClientSecret = "your-secret"  // Stored encrypted
};

// Certificate File
var profile = new AuthProfile("prod")
{
    AuthMethod = AuthMethod.CertificateFile,
    ApplicationId = "your-app-id",
    TenantId = "your-tenant-id",
    CertificatePath = "/path/to/cert.pfx",
    CertificatePassword = "password"  // Stored encrypted
};

// Certificate Store (Windows)
var profile = new AuthProfile("prod")
{
    AuthMethod = AuthMethod.CertificateStore,
    ApplicationId = "your-app-id",
    TenantId = "your-tenant-id",
    CertificateThumbprint = "ABC123...",
    CertificateStoreLocation = StoreLocation.CurrentUser
};

// Managed Identity (Azure-hosted)
var profile = new AuthProfile("azure")
{
    AuthMethod = AuthMethod.ManagedIdentity,
    ManagedIdentityClientId = "user-assigned-id"  // Optional for user-assigned
};

// GitHub Actions OIDC
var profile = new AuthProfile("github")
{
    AuthMethod = AuthMethod.GitHubFederated,
    ApplicationId = "your-app-id",
    TenantId = "your-tenant-id"
};

// Azure DevOps OIDC
var profile = new AuthProfile("azdo")
{
    AuthMethod = AuthMethod.AzureDevOpsFederated,
    ApplicationId = "your-app-id",
    TenantId = "your-tenant-id",
    AzureDevOpsServiceConnectionId = "service-connection-id"
};
```

### Environment Discovery

Discover environments accessible to the authenticated user:

```csharp
var provider = CredentialProviderFactory.Create(profile);
var discovery = new GlobalDiscoveryService();

var environments = await discovery.GetEnvironmentsAsync(provider.GetTokenAsync);

foreach (var env in environments)
{
    Console.WriteLine($"{env.FriendlyName} - {env.Url}");
}
```

### Environment Resolution

Resolve environments by various identifiers:

```csharp
var resolver = new EnvironmentResolver(discovery, provider.GetTokenAsync);

// By URL
var env = await resolver.ResolveAsync("https://org.crm.dynamics.com");

// By unique name
var env = await resolver.ResolveAsync("org");

// By environment ID
var env = await resolver.ResolveAsync("00000000-0000-0000-0000-000000000000");

// By partial friendly name (fuzzy match)
var env = await resolver.ResolveAsync("Production");
```

### Multi-Cloud Support

```csharp
var profile = new AuthProfile("gcc")
{
    Cloud = CloudEnvironment.UsGov,  // GCC
    // ... other settings
};

// Supported clouds:
// - CloudEnvironment.Public (default)
// - CloudEnvironment.UsGov (GCC)
// - CloudEnvironment.UsGovHigh (GCC High)
// - CloudEnvironment.UsGovDoD (DoD)
// - CloudEnvironment.China
// - CloudEnvironment.UsNat
// - CloudEnvironment.UsSec
```

### Integration with PPDS.Dataverse

Use profiles as a connection source for the connection pool:

```csharp
var store = new ProfileStore();
var profiles = await store.LoadAsync();
var profile = profiles.GetActiveProfile();

var provider = CredentialProviderFactory.Create(profile);
var source = new ProfileConnectionSource(profile, provider);

services.AddDataverseConnectionPool(options =>
{
    options.ConnectionSources.Add(source);
});
```

## Custom Credential Providers

Implement `ICredentialProvider` for custom authentication:

```csharp
public class CustomCredentialProvider : ICredentialProvider
{
    public string Name => "Custom";
    public bool RequiresInteraction => false;

    public Task<string> GetTokenAsync(string resource, CancellationToken ct)
    {
        // Your custom token acquisition logic
        return Task.FromResult("your-token");
    }

    public Task<TokenInfo> GetTokenInfoAsync(CancellationToken ct)
    {
        return Task.FromResult(new TokenInfo
        {
            UserPrincipalName = "user@example.com",
            TenantId = "..."
        });
    }
}
```

## Security

### Credential Encryption

Secrets (client secrets, passwords, etc.) are encrypted at rest:
- **Windows**: DPAPI (user-scoped encryption)
- **Linux/macOS**: Base64 encoding (use OS-level file permissions)

### Token Caching

MSAL token caching is used for interactive and device code flows:

```csharp
var profile = new AuthProfile("dev")
{
    TokenCacheType = TokenCacheType.Persistent  // Default
    // TokenCacheType = TokenCacheType.InMemory  // Per-process only
};
```

## Target Frameworks

- `net8.0`
- `net9.0`
- `net10.0`

## License

MIT License
