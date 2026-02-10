using System;
using System.Data.Common;

namespace PPDS.Query.Provider;

/// <summary>
/// Builds and parses connection strings for the PPDS ADO.NET provider.
/// Format: Url=https://org.crm.dynamics.com;AuthType=OAuth;ClientId=...;ClientSecret=...;TenantId=...
/// </summary>
public sealed class PpdsConnectionStringBuilder : DbConnectionStringBuilder
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PpdsConnectionStringBuilder"/> class.
    /// </summary>
    public PpdsConnectionStringBuilder()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PpdsConnectionStringBuilder"/> class
    /// with an existing connection string.
    /// </summary>
    /// <param name="connectionString">The connection string to parse.</param>
    public PpdsConnectionStringBuilder(string connectionString)
    {
        ConnectionString = connectionString;
    }

    /// <summary>
    /// The Dataverse environment URL (e.g., https://org.crm.dynamics.com).
    /// </summary>
    public string Url
    {
        get => GetValueOrDefault<string>("Url") ?? string.Empty;
        set => this["Url"] = value;
    }

    /// <summary>
    /// The authentication type (e.g., OAuth, ClientSecret, Interactive).
    /// </summary>
    public string AuthType
    {
        get => GetValueOrDefault<string>("AuthType") ?? string.Empty;
        set => this["AuthType"] = value;
    }

    /// <summary>
    /// The Azure AD application (client) ID.
    /// </summary>
    public string ClientId
    {
        get => GetValueOrDefault<string>("ClientId") ?? string.Empty;
        set => this["ClientId"] = value;
    }

    /// <summary>
    /// The Azure AD application client secret.
    /// </summary>
    public string ClientSecret
    {
        get => GetValueOrDefault<string>("ClientSecret") ?? string.Empty;
        set => this["ClientSecret"] = value;
    }

    /// <summary>
    /// The Azure AD tenant ID.
    /// </summary>
    public string TenantId
    {
        get => GetValueOrDefault<string>("TenantId") ?? string.Empty;
        set => this["TenantId"] = value;
    }

    private T? GetValueOrDefault<T>(string keyword)
    {
        if (TryGetValue(keyword, out var value) && value is T typed)
            return typed;

        // DbConnectionStringBuilder stores values as strings
        if (TryGetValue(keyword, out value) && value != null)
        {
            try
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return default;
            }
        }

        return default;
    }
}
