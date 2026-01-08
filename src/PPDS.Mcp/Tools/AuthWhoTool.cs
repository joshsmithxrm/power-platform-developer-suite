using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that returns the current authentication profile context.
/// </summary>
[McpServerToolType]
public sealed class AuthWhoTool
{
    private readonly McpToolContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuthWhoTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public AuthWhoTool(McpToolContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Gets the current active authentication profile.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current profile information including identity, environment, and token status.</returns>
    [McpServerTool(Name = "ppds_auth_who")]
    [Description("Get the current authentication profile context including identity, connected environment, and token status. Use this to understand which Dataverse environment queries will run against.")]
    public async Task<AuthWhoResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var profile = await _context.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);

        return new AuthWhoResult
        {
            Index = profile.Index,
            Name = profile.Name,
            AuthMethod = profile.AuthMethod.ToString(),
            Cloud = profile.Cloud.ToString(),
            TenantId = profile.TenantId,
            Username = profile.Username,
            ObjectId = profile.ObjectId,
            ApplicationId = profile.ApplicationId,
            TokenExpiresOn = profile.TokenExpiresOn,
            TokenStatus = profile.TokenExpiresOn.HasValue
                ? (profile.TokenExpiresOn.Value < DateTimeOffset.UtcNow ? "expired" : "valid")
                : null,
            Environment = profile.Environment != null ? new EnvironmentDetails
            {
                Url = profile.Environment.Url,
                DisplayName = profile.Environment.DisplayName,
                UniqueName = profile.Environment.UniqueName,
                EnvironmentId = profile.Environment.EnvironmentId,
                OrganizationId = profile.Environment.OrganizationId,
                Type = profile.Environment.Type,
                Region = profile.Environment.Region
            } : null,
            CreatedAt = profile.CreatedAt,
            LastUsedAt = profile.LastUsedAt
        };
    }
}

/// <summary>
/// Result of the auth_who tool.
/// </summary>
public sealed class AuthWhoResult
{
    /// <summary>
    /// Profile index (1-based).
    /// </summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>
    /// Profile name (optional, user-assigned).
    /// </summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>
    /// Authentication method (DeviceCode, ClientSecret, Certificate, etc.).
    /// </summary>
    [JsonPropertyName("authMethod")]
    public string AuthMethod { get; set; } = "";

    /// <summary>
    /// Cloud environment (Public, UsGov, China, etc.).
    /// </summary>
    [JsonPropertyName("cloud")]
    public string Cloud { get; set; } = "";

    /// <summary>
    /// Azure AD tenant ID.
    /// </summary>
    [JsonPropertyName("tenantId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TenantId { get; set; }

    /// <summary>
    /// Username (for user-based authentication).
    /// </summary>
    [JsonPropertyName("username")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Username { get; set; }

    /// <summary>
    /// Azure AD object ID.
    /// </summary>
    [JsonPropertyName("objectId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObjectId { get; set; }

    /// <summary>
    /// Application ID (for service principal authentication).
    /// </summary>
    [JsonPropertyName("applicationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApplicationId { get; set; }

    /// <summary>
    /// Token expiration time.
    /// </summary>
    [JsonPropertyName("tokenExpiresOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? TokenExpiresOn { get; set; }

    /// <summary>
    /// Token status: "valid" or "expired".
    /// </summary>
    [JsonPropertyName("tokenStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TokenStatus { get; set; }

    /// <summary>
    /// Currently selected environment details.
    /// </summary>
    [JsonPropertyName("environment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EnvironmentDetails? Environment { get; set; }

    /// <summary>
    /// Profile creation timestamp.
    /// </summary>
    [JsonPropertyName("createdAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>
    /// Last time the profile was used.
    /// </summary>
    [JsonPropertyName("lastUsedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>
/// Detailed environment information.
/// </summary>
public sealed class EnvironmentDetails
{
    /// <summary>
    /// Environment API URL.
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Unique environment name.
    /// </summary>
    [JsonPropertyName("uniqueName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UniqueName { get; set; }

    /// <summary>
    /// Power Platform environment ID.
    /// </summary>
    [JsonPropertyName("environmentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnvironmentId { get; set; }

    /// <summary>
    /// Dataverse organization ID.
    /// </summary>
    [JsonPropertyName("organizationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OrganizationId { get; set; }

    /// <summary>
    /// Environment type (Production, Sandbox, Developer, Trial).
    /// </summary>
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    /// <summary>
    /// Geographic region.
    /// </summary>
    [JsonPropertyName("region")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Region { get; set; }
}
