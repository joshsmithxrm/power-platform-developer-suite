using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PPDS.Auth.Cloud;
using PPDS.Auth.Credentials;

namespace PPDS.Cli.Services;

/// <summary>
/// Service for Power Platform connection operations via the Power Apps Admin API.
/// </summary>
public class ConnectionService : IConnectionService
{
    private readonly IPowerPlatformTokenProvider _tokenProvider;
    private readonly CloudEnvironment _cloud;
    private readonly string _environmentId;
    private readonly ILogger<ConnectionService> _logger;
    private readonly HttpClient _httpClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionService"/> class.
    /// </summary>
    /// <param name="tokenProvider">The Power Platform token provider.</param>
    /// <param name="cloud">The cloud environment.</param>
    /// <param name="environmentId">The environment ID.</param>
    /// <param name="logger">The logger.</param>
    public ConnectionService(
        IPowerPlatformTokenProvider tokenProvider,
        CloudEnvironment cloud,
        string environmentId,
        ILogger<ConnectionService> logger)
    {
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _cloud = cloud;
        _environmentId = environmentId ?? throw new ArgumentNullException(nameof(environmentId));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = new HttpClient();
    }

    /// <inheritdoc />
    public async Task<List<ConnectionInfo>> ListAsync(
        string? connectorFilter = null,
        CancellationToken cancellationToken = default)
    {
        // Use service.powerapps.com scope for Power Apps Admin API
        var token = await _tokenProvider.GetFlowApiTokenAsync(cancellationToken);
        var baseUrl = CloudEndpoints.GetPowerAppsApiUrl(_cloud);

        // Power Apps Admin API connections endpoint
        // Path: /providers/Microsoft.PowerApps/scopes/admin/environments/{environmentId}/connections
        var url = $"{baseUrl}/providers/Microsoft.PowerApps/scopes/admin/environments/{_environmentId}/connections?api-version=2016-11-01";

        if (!string.IsNullOrEmpty(connectorFilter))
        {
            // Filter by connector ID
            url += $"&$filter=properties/apiId eq '{connectorFilter}'";
        }

        _logger.LogDebug("Querying connections from Power Apps Admin API: {Url}", url);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Power Apps Admin API error: {StatusCode} - {Content}", response.StatusCode, errorContent);

            // Check for SPN limitation
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException(
                    $"Cannot access connections API. Status: {response.StatusCode}. " +
                    "Service principals have limited access to the Connections API. " +
                    "Use interactive or device code authentication for full functionality.");
            }

            throw new HttpRequestException($"Power Apps Admin API error: {response.StatusCode} - {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var apiResponse = JsonSerializer.Deserialize<PowerAppsConnectionsResponse>(content, JsonOptions);

        if (apiResponse?.Value == null)
        {
            return new List<ConnectionInfo>();
        }

        var connections = apiResponse.Value.Select(MapToConnectionInfo).ToList();
        _logger.LogDebug("Found {Count} connections", connections.Count);

        return connections;
    }

    /// <inheritdoc />
    public async Task<ConnectionInfo?> GetAsync(
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        // Use service.powerapps.com scope for Power Apps Admin API
        var token = await _tokenProvider.GetFlowApiTokenAsync(cancellationToken);
        var baseUrl = CloudEndpoints.GetPowerAppsApiUrl(_cloud);

        // Direct connection lookup via Power Apps Admin API
        var url = $"{baseUrl}/providers/Microsoft.PowerApps/scopes/admin/environments/{_environmentId}/connections/{connectionId}?api-version=2016-11-01";

        _logger.LogDebug("Getting connection from Power Apps Admin API: {Url}", url);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Power Apps Admin API error: {StatusCode} - {Content}", response.StatusCode, errorContent);
            throw new HttpRequestException($"Power Apps Admin API error: {response.StatusCode}");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var connectionData = JsonSerializer.Deserialize<PowerAppsConnectionData>(content, JsonOptions);

        return connectionData != null ? MapToConnectionInfo(connectionData) : null;
    }

    private static ConnectionInfo MapToConnectionInfo(PowerAppsConnectionData data)
    {
        var status = ConnectionStatus.Unknown;
        if (data.Properties?.Statuses != null)
        {
            var hasError = data.Properties.Statuses.Any(s =>
                string.Equals(s.Status, "Error", StringComparison.OrdinalIgnoreCase));
            status = hasError ? ConnectionStatus.Error : ConnectionStatus.Connected;
        }

        return new ConnectionInfo
        {
            ConnectionId = ExtractConnectionId(data.Name ?? string.Empty),
            DisplayName = data.Properties?.DisplayName,
            ConnectorId = data.Properties?.ApiId ?? string.Empty,
            ConnectorDisplayName = data.Properties?.ApiDisplayName,
            EnvironmentId = data.Properties?.Environment?.Name,
            Status = status,
            IsShared = data.Properties?.IsShared ?? false,
            CreatedOn = data.Properties?.CreatedTime,
            ModifiedOn = data.Properties?.LastModifiedTime,
            CreatedBy = data.Properties?.CreatedBy?.Email
        };
    }

    private static string ExtractConnectionId(string name)
    {
        // Name format: /providers/Microsoft.PowerApps/connections/{connectionId}
        var parts = name.Split('/');
        return parts.Length > 0 ? parts[^1] : name;
    }

    #region API Response Models

    private sealed class PowerAppsConnectionsResponse
    {
        public List<PowerAppsConnectionData>? Value { get; set; }
    }

    private sealed class PowerAppsConnectionData
    {
        public string? Name { get; set; }
        public string? Id { get; set; }
        public string? Type { get; set; }
        public ConnectionProperties? Properties { get; set; }
    }

    private sealed class ConnectionProperties
    {
        public string? DisplayName { get; set; }
        public string? ApiId { get; set; }
        public string? ApiDisplayName { get; set; }
        public bool? IsShared { get; set; }
        public DateTime? CreatedTime { get; set; }
        public DateTime? LastModifiedTime { get; set; }
        public EnvironmentRef? Environment { get; set; }
        public List<ConnectionStatusItem>? Statuses { get; set; }
        public UserInfo? CreatedBy { get; set; }
    }

    private sealed class EnvironmentRef
    {
        public string? Name { get; set; }
        public string? Id { get; set; }
    }

    private sealed class ConnectionStatusItem
    {
        public string? Status { get; set; }
        public string? Error { get; set; }
    }

    private sealed class UserInfo
    {
        public string? Id { get; set; }
        public string? DisplayName { get; set; }
        public string? Email { get; set; }
    }

    #endregion
}
