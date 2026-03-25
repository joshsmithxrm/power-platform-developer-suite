using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Query;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that lists Dataverse service endpoints and webhooks.
/// </summary>
[McpServerToolType]
public sealed class ServiceEndpointsListTool : McpToolBase
{
    // Contract option set values
    private const int ContractOneWay = 1;
    private const int ContractQueue = 2;
    private const int ContractRest = 3;
    private const int ContractTwoWay = 4;
    private const int ContractTopic = 5;
    private const int ContractEventHub = 7;
    private const int ContractWebhook = 8;

    // Auth type option set values
    private const int AuthSASKey = 2;
    private const int AuthSASToken = 3;
    private const int AuthWebhookKey = 4;
    private const int AuthHttpHeader = 5;
    private const int AuthHttpQueryString = 6;

    // Message format option set values
    private const int MessageFormatBinaryXml = 1;
    private const int MessageFormatJson = 2;
    private const int MessageFormatTextXml = 3;

    // User claim option set values
    private const int UserClaimNone = 1;
    private const int UserClaimUserId = 2;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServiceEndpointsListTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public ServiceEndpointsListTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Lists all service endpoints and webhooks in the environment.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of service endpoints and webhooks.</returns>
    [McpServerTool(Name = "ppds_service_endpoints_list")]
    [Description("List all service endpoints and webhooks registered in the Dataverse environment. Includes Azure Service Bus (Queue, Topic, EventHub), REST endpoints, and HTTP webhooks. Used for event-driven integrations that receive Dataverse change notifications.")]
    public async Task<ServiceEndpointsListResult> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        var fetchXml = @"
            <fetch>
                <entity name=""serviceendpoint"">
                    <attribute name=""serviceendpointid"" />
                    <attribute name=""name"" />
                    <attribute name=""description"" />
                    <attribute name=""contract"" />
                    <attribute name=""authtype"" />
                    <attribute name=""url"" />
                    <attribute name=""namespaceaddress"" />
                    <attribute name=""path"" />
                    <attribute name=""messageformat"" />
                    <attribute name=""userclaim"" />
                    <attribute name=""ismanaged"" />
                    <attribute name=""createdon"" />
                    <attribute name=""modifiedon"" />
                    <order attribute=""name"" />
                </entity>
            </fetch>";

        await using var serviceProvider = await CreateScopeAsync(cancellationToken).ConfigureAwait(false);
        var queryExecutor = serviceProvider.GetRequiredService<IQueryExecutor>();

        var result = await queryExecutor.ExecuteFetchXmlAsync(fetchXml, null, null, false, cancellationToken).ConfigureAwait(false);

        var endpoints = result.Records.Select(record =>
        {
            var contractRaw = record.GetInt("contract");
            var authTypeRaw = record.GetInt("authtype");
            var messageFormatRaw = record.GetIntNullable("messageformat");
            var userClaimRaw = record.GetIntNullable("userclaim");
            var contractType = MapContract(contractRaw);
            var isWebhook = contractRaw == ContractWebhook;

            return new ServiceEndpointSummary
            {
                Id = record.GetGuid("serviceendpointid"),
                Name = record.GetString("name") ?? "",
                Description = record.GetString("description"),
                ContractType = contractType,
                IsWebhook = isWebhook,
                Url = record.GetString("url"),
                NamespaceAddress = record.GetString("namespaceaddress"),
                Path = record.GetString("path"),
                AuthType = MapAuthType(authTypeRaw),
                MessageFormat = messageFormatRaw.HasValue ? MapMessageFormat(messageFormatRaw.Value) : null,
                UserClaim = userClaimRaw.HasValue ? MapUserClaim(userClaimRaw.Value) : null,
                IsManaged = record.GetBool("ismanaged"),
                CreatedOn = record.GetDateTime("createdon"),
                ModifiedOn = record.GetDateTime("modifiedon")
            };
        }).ToList();

        return new ServiceEndpointsListResult
        {
            Endpoints = endpoints,
            Count = endpoints.Count
        };
    }

    private static string MapContract(int value) => value switch
    {
        ContractOneWay => "OneWay",
        ContractQueue => "Queue",
        ContractRest => "Rest",
        ContractTwoWay => "TwoWay",
        ContractTopic => "Topic",
        ContractEventHub => "EventHub",
        ContractWebhook => "Webhook",
        _ => value.ToString()
    };

    private static string MapAuthType(int value) => value switch
    {
        AuthSASKey => "SASKey",
        AuthSASToken => "SASToken",
        AuthWebhookKey => "WebhookKey",
        AuthHttpHeader => "HttpHeader",
        AuthHttpQueryString => "HttpQueryString",
        _ => value.ToString()
    };

    private static string MapMessageFormat(int value) => value switch
    {
        MessageFormatBinaryXml => "BinaryXML",
        MessageFormatJson => "Json",
        MessageFormatTextXml => "TextXML",
        _ => value.ToString()
    };

    private static string MapUserClaim(int value) => value switch
    {
        UserClaimNone => "None",
        UserClaimUserId => "UserId",
        _ => value.ToString()
    };

}

/// <summary>
/// Result of the ppds_service_endpoints_list tool.
/// </summary>
public sealed class ServiceEndpointsListResult
{
    /// <summary>
    /// List of service endpoints and webhooks.
    /// </summary>
    [JsonPropertyName("endpoints")]
    public List<ServiceEndpointSummary> Endpoints { get; set; } = [];

    /// <summary>
    /// Number of endpoints returned.
    /// </summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }
}

/// <summary>
/// Summary information about a service endpoint or webhook.
/// </summary>
public sealed class ServiceEndpointSummary
{
    /// <summary>
    /// Endpoint ID.
    /// </summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Display name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Optional description.
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>
    /// Contract type: Queue, Topic, EventHub, Webhook, Rest, OneWay, TwoWay.
    /// </summary>
    [JsonPropertyName("contractType")]
    public string ContractType { get; set; } = "";

    /// <summary>
    /// True when this is an HTTP webhook.
    /// </summary>
    [JsonPropertyName("isWebhook")]
    public bool IsWebhook { get; set; }

    /// <summary>
    /// Webhook URL (webhooks only).
    /// </summary>
    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    /// <summary>
    /// Service Bus namespace address (service bus endpoints only).
    /// </summary>
    [JsonPropertyName("namespaceAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NamespaceAddress { get; set; }

    /// <summary>
    /// Queue/topic/eventhub name (service bus endpoints only).
    /// </summary>
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; set; }

    /// <summary>
    /// Authentication type: SASKey, SASToken, HttpHeader, WebhookKey, HttpQueryString.
    /// </summary>
    [JsonPropertyName("authType")]
    public string AuthType { get; set; } = "";

    /// <summary>
    /// Message format: BinaryXML, Json, TextXML (service bus endpoints only).
    /// </summary>
    [JsonPropertyName("messageFormat")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageFormat { get; set; }

    /// <summary>
    /// User claim: None, UserId (service bus endpoints only).
    /// </summary>
    [JsonPropertyName("userClaim")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserClaim { get; set; }

    /// <summary>
    /// True when part of a managed solution.
    /// </summary>
    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    /// <summary>
    /// UTC creation timestamp.
    /// </summary>
    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CreatedOn { get; set; }

    /// <summary>
    /// UTC last-modified timestamp.
    /// </summary>
    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ModifiedOn { get; set; }
}
