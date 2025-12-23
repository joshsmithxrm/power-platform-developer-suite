# Structured Configuration - Design Specification

**Status:** Approved for Implementation
**Target:** Future release
**Author:** Claude Code
**Date:** 2025-12-23

---

## Problem Statement

Current configuration requires raw connection strings:

```json
{
  "Dataverse": {
    "Connections": [
      {
        "Name": "Primary",
        "ConnectionString": "AuthType=ClientSecret;Url=https://org.crm.dynamics.com;ClientId=xxx;ClientSecret=yyy;TenantId=zzz"
      }
    ]
  }
}
```

### Issues

| Problem | Impact |
|---------|--------|
| **Secrets in config** | Easy to commit to source control, visible in logs |
| **Error prone** | Syntax errors, typos, no validation until runtime |
| **Duplication** | Same Url/TenantId repeated across connections |
| **Hard to compose** | Can't override just URL for different environments |
| **No IntelliSense** | No IDE help, just a string |
| **No Key Vault support** | Must inline secrets or build custom resolution |

The existence of `ConnectionStringRedactor` proves we're already fighting secret leakage.

---

## Solution: Typed Configuration with Secret Resolution

Replace connection strings with structured, typed configuration that:
- Separates secrets from config files
- Supports multiple authentication types
- Enables Key Vault and environment variable resolution
- Provides validation at startup
- Maintains backwards compatibility

---

## Configuration Model

### Root Options

```csharp
public class DataverseOptions
{
    /// <summary>
    /// Default Dataverse environment URL. Inherited by connections if not specified.
    /// Example: https://org.crm.dynamics.com
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Default Azure AD tenant ID. Inherited by connections if not specified.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Connection configurations for Application Users.
    /// </summary>
    public List<DataverseConnectionOptions> Connections { get; set; } = new();

    /// <summary>
    /// Connection pool settings.
    /// </summary>
    public PoolOptions Pool { get; set; } = new();

    /// <summary>
    /// Adaptive rate control settings.
    /// </summary>
    public AdaptiveRateOptions AdaptiveRate { get; set; } = new();
}
```

### Connection Options

```csharp
public class DataverseConnectionOptions
{
    /// <summary>
    /// Connection name for identification and logging.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Dataverse environment URL. Overrides root Url if specified.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Azure AD tenant ID. Overrides root TenantId if specified.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Authentication type. Default: ClientSecret.
    /// </summary>
    public DataverseAuthType AuthType { get; set; } = DataverseAuthType.ClientSecret;

    /// <summary>
    /// Azure AD application (client) ID. Required for ClientSecret and Certificate auth.
    /// </summary>
    public string? ClientId { get; set; }

    #region Secret Resolution (Priority Order)

    /// <summary>
    /// Azure Key Vault URI for client secret. Highest priority.
    /// Example: https://myvault.vault.azure.net/secrets/dataverse-secret
    /// </summary>
    public string? ClientSecretKeyVaultUri { get; set; }

    /// <summary>
    /// Environment variable name containing client secret. Second priority.
    /// Example: DATAVERSE_CLIENT_SECRET
    /// </summary>
    public string? ClientSecretVariable { get; set; }

    /// <summary>
    /// Direct client secret value. Lowest priority.
    /// NOT RECOMMENDED for production - use KeyVault or environment variable.
    /// </summary>
    [Obsolete("Use ClientSecretKeyVaultUri or ClientSecretVariable for production")]
    public string? ClientSecret { get; set; }

    #endregion

    #region Certificate Auth

    /// <summary>
    /// Certificate thumbprint. Required for Certificate auth.
    /// </summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>
    /// Certificate store name. Default: My.
    /// </summary>
    public StoreName CertificateStoreName { get; set; } = StoreName.My;

    /// <summary>
    /// Certificate store location. Default: CurrentUser.
    /// </summary>
    public StoreLocation CertificateStoreLocation { get; set; } = StoreLocation.CurrentUser;

    /// <summary>
    /// Path to PFX certificate file. Alternative to store-based certificate.
    /// </summary>
    public string? CertificatePath { get; set; }

    /// <summary>
    /// Environment variable containing PFX password.
    /// </summary>
    public string? CertificatePasswordVariable { get; set; }

    #endregion

    #region OAuth (Interactive)

    /// <summary>
    /// OAuth redirect URI. Required for OAuth auth.
    /// </summary>
    public string? RedirectUri { get; set; }

    /// <summary>
    /// OAuth login prompt behavior. Default: Auto.
    /// </summary>
    public OAuthLoginPrompt LoginPrompt { get; set; } = OAuthLoginPrompt.Auto;

    #endregion

    /// <summary>
    /// Raw connection string. Escape hatch for unsupported scenarios.
    /// If set, overrides all other properties.
    /// </summary>
    public string? ConnectionString { get; set; }
}
```

### Authentication Types

```csharp
public enum DataverseAuthType
{
    /// <summary>
    /// App registration with client secret. Most common for server-to-server.
    /// </summary>
    ClientSecret,

    /// <summary>
    /// App registration with certificate. More secure than client secret.
    /// </summary>
    Certificate,

    /// <summary>
    /// Azure Managed Identity. Best for Azure-hosted services. No secrets needed.
    /// </summary>
    ManagedIdentity,

    /// <summary>
    /// Interactive OAuth. For desktop apps, not recommended for servers.
    /// </summary>
    OAuth,

    /// <summary>
    /// Raw connection string. Escape hatch for unsupported auth types.
    /// </summary>
    ConnectionString
}

public enum OAuthLoginPrompt
{
    Auto,
    Always,
    Never,
    SelectAccount
}
```

---

## Configuration Examples

### Development (Environment Variable)

```json
{
  "Dataverse": {
    "Url": "https://dev-org.crm.dynamics.com",
    "TenantId": "00000000-0000-0000-0000-000000000000",
    "Connections": [
      {
        "Name": "Primary",
        "ClientId": "11111111-1111-1111-1111-111111111111",
        "ClientSecretVariable": "DATAVERSE_SECRET"
      }
    ]
  }
}
```

Run with: `DATAVERSE_SECRET=my-secret dotnet run`

### Production (Azure Key Vault)

```json
{
  "Dataverse": {
    "Url": "https://prod-org.crm.dynamics.com",
    "TenantId": "00000000-0000-0000-0000-000000000000",
    "Connections": [
      {
        "Name": "Primary",
        "ClientId": "11111111-1111-1111-1111-111111111111",
        "ClientSecretKeyVaultUri": "https://myvault.vault.azure.net/secrets/dataverse-primary"
      },
      {
        "Name": "Secondary",
        "ClientId": "22222222-2222-2222-2222-222222222222",
        "ClientSecretKeyVaultUri": "https://myvault.vault.azure.net/secrets/dataverse-secondary"
      }
    ]
  }
}
```

### Azure Functions (Managed Identity)

```json
{
  "Dataverse": {
    "Url": "https://org.crm.dynamics.com",
    "Connections": [
      {
        "Name": "Primary",
        "AuthType": "ManagedIdentity"
      }
    ]
  }
}
```

No secrets needed! Azure handles authentication.

### Certificate Authentication

```json
{
  "Dataverse": {
    "Url": "https://org.crm.dynamics.com",
    "TenantId": "00000000-0000-0000-0000-000000000000",
    "Connections": [
      {
        "Name": "Primary",
        "AuthType": "Certificate",
        "ClientId": "11111111-1111-1111-1111-111111111111",
        "CertificateThumbprint": "ABC123DEF456...",
        "CertificateStoreLocation": "LocalMachine"
      }
    ]
  }
}
```

### Legacy (Raw Connection String)

```json
{
  "Dataverse": {
    "Connections": [
      {
        "Name": "Primary",
        "ConnectionString": "AuthType=ClientSecret;Url=https://org.crm.dynamics.com;ClientId=...;ClientSecret=...;TenantId=..."
      }
    ]
  }
}
```

Still supported for backwards compatibility and edge cases.

---

## Connection String Builder

```csharp
internal static class ConnectionStringBuilder
{
    public static string Build(DataverseConnectionOptions connection, DataverseOptions root)
    {
        // Escape hatch: raw connection string takes precedence
        if (!string.IsNullOrEmpty(connection.ConnectionString))
        {
            return connection.ConnectionString;
        }

        // Inherit from root
        var url = connection.Url ?? root.Url
            ?? throw new ConfigurationException($"Url required for connection '{connection.Name}'");
        var tenantId = connection.TenantId ?? root.TenantId;

        return connection.AuthType switch
        {
            DataverseAuthType.ClientSecret => BuildClientSecret(url, tenantId, connection),
            DataverseAuthType.Certificate => BuildCertificate(url, tenantId, connection),
            DataverseAuthType.ManagedIdentity => BuildManagedIdentity(url),
            DataverseAuthType.OAuth => BuildOAuth(url, connection),
            DataverseAuthType.ConnectionString => connection.ConnectionString
                ?? throw new ConfigurationException($"ConnectionString required for connection '{connection.Name}'"),
            _ => throw new ConfigurationException($"Unsupported AuthType '{connection.AuthType}'")
        };
    }

    private static string BuildClientSecret(string url, string? tenantId, DataverseConnectionOptions connection)
    {
        var clientId = connection.ClientId
            ?? throw new ConfigurationException($"ClientId required for connection '{connection.Name}'");
        var secret = ResolveSecret(connection)
            ?? throw new ConfigurationException($"Client secret required for connection '{connection.Name}'");

        var sb = new StringBuilder();
        sb.Append($"AuthType=ClientSecret;Url={url};ClientId={clientId};ClientSecret={secret}");

        if (!string.IsNullOrEmpty(tenantId))
        {
            sb.Append($";TenantId={tenantId}");
        }

        return sb.ToString();
    }

    private static string BuildManagedIdentity(string url)
    {
        return $"AuthType=ManagedIdentity;Url={url}";
    }

    private static string BuildCertificate(string url, string? tenantId, DataverseConnectionOptions connection)
    {
        var clientId = connection.ClientId
            ?? throw new ConfigurationException($"ClientId required for connection '{connection.Name}'");
        var thumbprint = connection.CertificateThumbprint
            ?? throw new ConfigurationException($"CertificateThumbprint required for connection '{connection.Name}'");

        var sb = new StringBuilder();
        sb.Append($"AuthType=Certificate;Url={url};ClientId={clientId};Thumbprint={thumbprint}");

        if (!string.IsNullOrEmpty(tenantId))
        {
            sb.Append($";TenantId={tenantId}");
        }

        sb.Append($";StoreLocation={connection.CertificateStoreLocation}");

        return sb.ToString();
    }

    private static string BuildOAuth(string url, DataverseConnectionOptions connection)
    {
        var clientId = connection.ClientId
            ?? throw new ConfigurationException($"ClientId required for connection '{connection.Name}'");
        var redirectUri = connection.RedirectUri
            ?? throw new ConfigurationException($"RedirectUri required for connection '{connection.Name}'");

        return $"AuthType=OAuth;Url={url};ClientId={clientId};RedirectUri={redirectUri};LoginPrompt={connection.LoginPrompt}";
    }
}
```

---

## Secret Resolution

```csharp
internal static class SecretResolver
{
    public static string? ResolveSecret(DataverseConnectionOptions connection)
    {
        // Priority 1: Azure Key Vault
        if (!string.IsNullOrEmpty(connection.ClientSecretKeyVaultUri))
        {
            return ResolveFromKeyVault(connection.ClientSecretKeyVaultUri);
        }

        // Priority 2: Environment Variable
        if (!string.IsNullOrEmpty(connection.ClientSecretVariable))
        {
            var value = Environment.GetEnvironmentVariable(connection.ClientSecretVariable);
            if (string.IsNullOrEmpty(value))
            {
                throw new ConfigurationException(
                    $"Environment variable '{connection.ClientSecretVariable}' not found or empty " +
                    $"for connection '{connection.Name}'");
            }
            return value;
        }

        // Priority 3: Direct value (not recommended)
        #pragma warning disable CS0618
        return connection.ClientSecret;
        #pragma warning restore CS0618
    }

    private static string ResolveFromKeyVault(string secretUri)
    {
        // Use Azure.Identity DefaultAzureCredential for Key Vault access
        var credential = new DefaultAzureCredential();
        var client = new SecretClient(new Uri(GetVaultUri(secretUri)), credential);

        var secretName = GetSecretName(secretUri);
        var secret = client.GetSecret(secretName);

        return secret.Value.Value;
    }

    private static string GetVaultUri(string secretUri)
    {
        // https://myvault.vault.azure.net/secrets/mysecret -> https://myvault.vault.azure.net
        var uri = new Uri(secretUri);
        return $"{uri.Scheme}://{uri.Host}";
    }

    private static string GetSecretName(string secretUri)
    {
        // https://myvault.vault.azure.net/secrets/mysecret -> mysecret
        var uri = new Uri(secretUri);
        var segments = uri.AbsolutePath.Split('/');
        return segments[^1];
    }
}
```

---

## Validation

```csharp
internal static class ConfigurationValidator
{
    public static void Validate(DataverseOptions options)
    {
        if (options.Connections.Count == 0)
        {
            throw new ConfigurationException("At least one connection must be configured");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var connection in options.Connections)
        {
            ValidateConnection(connection, options, names);
        }
    }

    private static void ValidateConnection(
        DataverseConnectionOptions connection,
        DataverseOptions root,
        HashSet<string> names)
    {
        // Name required and unique
        if (string.IsNullOrWhiteSpace(connection.Name))
        {
            throw new ConfigurationException("Connection Name is required");
        }

        if (!names.Add(connection.Name))
        {
            throw new ConfigurationException($"Duplicate connection name: '{connection.Name}'");
        }

        // Raw connection string bypasses other validation
        if (!string.IsNullOrEmpty(connection.ConnectionString))
        {
            return;
        }

        // URL required (from connection or root)
        if (string.IsNullOrEmpty(connection.Url) && string.IsNullOrEmpty(root.Url))
        {
            throw new ConfigurationException($"Url required for connection '{connection.Name}'");
        }

        // Auth-type specific validation
        switch (connection.AuthType)
        {
            case DataverseAuthType.ClientSecret:
                ValidateClientSecret(connection);
                break;
            case DataverseAuthType.Certificate:
                ValidateCertificate(connection);
                break;
            case DataverseAuthType.ManagedIdentity:
                // No additional requirements
                break;
            case DataverseAuthType.OAuth:
                ValidateOAuth(connection);
                break;
        }
    }

    private static void ValidateClientSecret(DataverseConnectionOptions connection)
    {
        if (string.IsNullOrEmpty(connection.ClientId))
        {
            throw new ConfigurationException($"ClientId required for connection '{connection.Name}'");
        }

        #pragma warning disable CS0618
        var hasSecret = !string.IsNullOrEmpty(connection.ClientSecretKeyVaultUri)
                     || !string.IsNullOrEmpty(connection.ClientSecretVariable)
                     || !string.IsNullOrEmpty(connection.ClientSecret);
        #pragma warning restore CS0618

        if (!hasSecret)
        {
            throw new ConfigurationException(
                $"Client secret required for connection '{connection.Name}'. " +
                "Use ClientSecretKeyVaultUri, ClientSecretVariable, or ClientSecret.");
        }
    }

    private static void ValidateCertificate(DataverseConnectionOptions connection)
    {
        if (string.IsNullOrEmpty(connection.ClientId))
        {
            throw new ConfigurationException($"ClientId required for connection '{connection.Name}'");
        }

        var hasThumbprint = !string.IsNullOrEmpty(connection.CertificateThumbprint);
        var hasPath = !string.IsNullOrEmpty(connection.CertificatePath);

        if (!hasThumbprint && !hasPath)
        {
            throw new ConfigurationException(
                $"CertificateThumbprint or CertificatePath required for connection '{connection.Name}'");
        }
    }

    private static void ValidateOAuth(DataverseConnectionOptions connection)
    {
        if (string.IsNullOrEmpty(connection.ClientId))
        {
            throw new ConfigurationException($"ClientId required for connection '{connection.Name}'");
        }

        if (string.IsNullOrEmpty(connection.RedirectUri))
        {
            throw new ConfigurationException($"RedirectUri required for connection '{connection.Name}'");
        }
    }
}
```

---

## File Changes

| File | Change |
|------|--------|
| `src/PPDS.Dataverse/DependencyInjection/DataverseConnectionOptions.cs` | New structured options |
| `src/PPDS.Dataverse/DependencyInjection/DataverseAuthType.cs` | New auth type enum |
| `src/PPDS.Dataverse/Configuration/ConnectionStringBuilder.cs` | New builder |
| `src/PPDS.Dataverse/Configuration/SecretResolver.cs` | New secret resolution |
| `src/PPDS.Dataverse/Configuration/ConfigurationValidator.cs` | New validation |
| `src/PPDS.Dataverse/Configuration/ConfigurationException.cs` | New exception type |
| `src/PPDS.Dataverse/Pooling/DataverseConnectionPool.cs` | Use builder instead of raw string |
| `tests/PPDS.Dataverse.Tests/Configuration/` | New test files |

---

## Dependencies

```xml
<!-- For Key Vault integration -->
<PackageReference Include="Azure.Identity" Version="1.10.0" />
<PackageReference Include="Azure.Security.KeyVault.Secrets" Version="4.5.0" />
```

Note: These are optional - Key Vault resolution only attempted if `ClientSecretKeyVaultUri` is used.

---

## Migration Guide

### From Raw Connection String

**Before:**
```json
{
  "Dataverse": {
    "Connections": [
      {
        "Name": "Primary",
        "ConnectionString": "AuthType=ClientSecret;Url=https://org.crm.dynamics.com;ClientId=xxx;ClientSecret=yyy;TenantId=zzz"
      }
    ]
  }
}
```

**After:**
```json
{
  "Dataverse": {
    "Url": "https://org.crm.dynamics.com",
    "TenantId": "zzz",
    "Connections": [
      {
        "Name": "Primary",
        "ClientId": "xxx",
        "ClientSecretVariable": "DATAVERSE_SECRET"
      }
    ]
  }
}
```

Then set environment variable: `DATAVERSE_SECRET=yyy`

### Backwards Compatibility

Raw `ConnectionString` property is still supported as escape hatch. Existing configs continue to work unchanged.

---

## References

- [Azure.Identity Documentation](https://learn.microsoft.com/en-us/dotnet/api/azure.identity)
- [Key Vault Secrets Client](https://learn.microsoft.com/en-us/dotnet/api/azure.security.keyvault.secrets)
- [Dataverse Connection Strings](https://learn.microsoft.com/en-us/power-apps/developer/data-platform/xrm-tooling/use-connection-strings-xrm-tooling-connect)
