using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Query;
using PPDS.Mcp.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that lists Dataverse Custom APIs with their parameters.
/// </summary>
[McpServerToolType]
public sealed class CustomApisListTool : McpToolBase
{
    // BindingType option set values
    private const int BindingTypeGlobal = 0;
    private const int BindingTypeEntity = 1;
    private const int BindingTypeEntityCollection = 2;

    // AllowedCustomProcessingStepType option set values
    private const int ProcessingStepNone = 0;
    private const int ProcessingStepAsyncOnly = 1;
    private const int ProcessingStepSyncAndAsync = 2;

    // Parameter type option set values
    private const int TypeBoolean = 0;
    private const int TypeDateTime = 1;
    private const int TypeDecimal = 2;
    private const int TypeEntity = 3;
    private const int TypeEntityCollection = 4;
    private const int TypeEntityReference = 5;
    private const int TypeFloat = 6;
    private const int TypeInteger = 7;
    private const int TypeMoney = 8;
    private const int TypePicklist = 9;
    private const int TypeString = 10;
    private const int TypeStringArray = 11;
    private const int TypeGuid = 12;

    /// <summary>
    /// Initializes a new instance of the <see cref="CustomApisListTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public CustomApisListTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Lists all Custom APIs in the environment with their parameters.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of custom APIs with request parameters and response properties.</returns>
    [McpServerTool(Name = "ppds_custom_apis_list")]
    [Description("List all Custom APIs registered in the Dataverse environment. Custom APIs are developer-defined messages that extend the Dataverse API surface with custom business logic. Returns each API with its request parameters and response properties.")]
    public async Task<CustomApisListResult> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var apiFetchXml = @"
                <fetch>
                    <entity name=""customapi"">
                        <attribute name=""customapiid"" />
                        <attribute name=""uniquename"" />
                        <attribute name=""displayname"" />
                        <attribute name=""name"" />
                        <attribute name=""description"" />
                        <attribute name=""plugintypeid"" />
                        <attribute name=""bindingtype"" />
                        <attribute name=""boundentitylogicalname"" />
                        <attribute name=""allowedcustomprocessingsteptype"" />
                        <attribute name=""isfunction"" />
                        <attribute name=""isprivate"" />
                        <attribute name=""executeprivilegename"" />
                        <attribute name=""ismanaged"" />
                        <attribute name=""createdon"" />
                        <attribute name=""modifiedon"" />
                        <order attribute=""uniquename"" />
                    </entity>
                </fetch>";

            var reqParamFetchXml = @"
                <fetch>
                    <entity name=""customapirequestparameter"">
                        <attribute name=""customapirequestparameterid"" />
                        <attribute name=""customapiid"" />
                        <attribute name=""uniquename"" />
                        <attribute name=""displayname"" />
                        <attribute name=""name"" />
                        <attribute name=""description"" />
                        <attribute name=""type"" />
                        <attribute name=""logicalentityname"" />
                        <attribute name=""isoptional"" />
                        <attribute name=""ismanaged"" />
                        <order attribute=""uniquename"" />
                    </entity>
                </fetch>";

            var respPropFetchXml = @"
                <fetch>
                    <entity name=""customapiresponseproperty"">
                        <attribute name=""customapiresponsepropertyid"" />
                        <attribute name=""customapiid"" />
                        <attribute name=""uniquename"" />
                        <attribute name=""displayname"" />
                        <attribute name=""name"" />
                        <attribute name=""description"" />
                        <attribute name=""type"" />
                        <attribute name=""logicalentityname"" />
                        <attribute name=""ismanaged"" />
                        <order attribute=""uniquename"" />
                    </entity>
                </fetch>";

            await using var serviceProvider = await CreateScopeAsync(cancellationToken).ConfigureAwait(false);
            var queryExecutor = serviceProvider.GetRequiredService<IQueryExecutor>();

            // Execute all three queries sequentially (parallel would require .Result which is blocked by PPDS012)
            var apiResult = await queryExecutor.ExecuteFetchXmlAsync(apiFetchXml, null, null, false, cancellationToken).ConfigureAwait(false);
            var reqResult = await queryExecutor.ExecuteFetchXmlAsync(reqParamFetchXml, null, null, false, cancellationToken).ConfigureAwait(false);
            var respResult = await queryExecutor.ExecuteFetchXmlAsync(respPropFetchXml, null, null, false, cancellationToken).ConfigureAwait(false);

            // Group parameters and properties by API ID
            var requestParamsByApi = reqResult.Records
                .GroupBy(r => r.GetGuid("customapiid"))
                .ToDictionary(g => g.Key, g => g.ToList());

            var responsePropertiesByApi = respResult.Records
                .GroupBy(r => r.GetGuid("customapiid"))
                .ToDictionary(g => g.Key, g => g.ToList());

            var apis = apiResult.Records.Select(record =>
            {
                var apiId = record.GetGuid("customapiid");
                var bindingRaw = record.GetInt("bindingtype");
                var stepTypeRaw = record.GetInt("allowedcustomprocessingsteptype");

                var requestParams = requestParamsByApi.TryGetValue(apiId, out var reqList)
                    ? reqList.Select(r => MapParameter(r, "customapirequestparameterid", isOptionalAvailable: true)).ToList()
                    : [];

                var responseProps = responsePropertiesByApi.TryGetValue(apiId, out var respList)
                    ? respList.Select(r => MapParameter(r, "customapiresponsepropertyid", isOptionalAvailable: false)).ToList()
                    : [];

                return new CustomApiSummary
                {
                    Id = apiId,
                    UniqueName = record.GetString("uniquename") ?? "",
                    DisplayName = record.GetString("displayname") ?? "",
                    Name = record.GetString("name"),
                    Description = record.GetString("description"),
                    PluginTypeId = record.GetGuidNullable("plugintypeid"),
                    BindingType = MapBindingType(bindingRaw),
                    BoundEntity = record.GetString("boundentitylogicalname"),
                    AllowedProcessingStepType = MapProcessingStepType(stepTypeRaw),
                    IsFunction = record.GetBool("isfunction"),
                    IsPrivate = record.GetBool("isprivate"),
                    ExecutePrivilegeName = record.GetString("executeprivilegename"),
                    IsManaged = record.GetBool("ismanaged"),
                    CreatedOn = record.GetDateTime("createdon"),
                    ModifiedOn = record.GetDateTime("modifiedon"),
                    RequestParameters = requestParams,
                    ResponseProperties = responseProps
                };
            }).ToList();

            return new CustomApisListResult
            {
                Apis = apis,
                Count = apis.Count
            };
        }
        catch (PpdsException ex)
        {
            McpToolErrorHelper.ThrowStructuredError(ex);
            throw; // unreachable — ThrowStructuredError always throws
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not ArgumentException)
        {
            McpToolErrorHelper.ThrowStructuredError(ex);
            throw; // unreachable — ThrowStructuredError always throws
        }
    }

    private static CustomApiParameterSummary MapParameter(
        IReadOnlyDictionary<string, QueryValue> record,
        string idField,
        bool isOptionalAvailable)
    {
        var typeRaw = record.GetInt("type");
        return new CustomApiParameterSummary
        {
            Id = record.GetGuid(idField),
            UniqueName = record.GetString("uniquename") ?? "",
            DisplayName = record.GetString("displayname") ?? "",
            Name = record.GetString("name"),
            Description = record.GetString("description"),
            Type = MapParameterType(typeRaw),
            LogicalEntityName = record.GetString("logicalentityname"),
            IsOptional = isOptionalAvailable && record.GetBool("isoptional"),
            IsManaged = record.GetBool("ismanaged")
        };
    }

    private static string MapBindingType(int value) => value switch
    {
        BindingTypeGlobal => "Global",
        BindingTypeEntity => "Entity",
        BindingTypeEntityCollection => "EntityCollection",
        _ => value.ToString()
    };

    private static string MapProcessingStepType(int value) => value switch
    {
        ProcessingStepNone => "None",
        ProcessingStepAsyncOnly => "AsyncOnly",
        ProcessingStepSyncAndAsync => "SyncAndAsync",
        _ => value.ToString()
    };

    private static string MapParameterType(int value) => value switch
    {
        TypeBoolean => "Boolean",
        TypeDateTime => "DateTime",
        TypeDecimal => "Decimal",
        TypeEntity => "Entity",
        TypeEntityCollection => "EntityCollection",
        TypeEntityReference => "EntityReference",
        TypeFloat => "Float",
        TypeInteger => "Integer",
        TypeMoney => "Money",
        TypePicklist => "Picklist",
        TypeString => "String",
        TypeStringArray => "StringArray",
        TypeGuid => "Guid",
        _ => value.ToString()
    };

}

/// <summary>
/// Result of the ppds_custom_apis_list tool.
/// </summary>
public sealed class CustomApisListResult
{
    /// <summary>
    /// List of Custom APIs.
    /// </summary>
    [JsonPropertyName("apis")]
    public List<CustomApiSummary> Apis { get; set; } = [];

    /// <summary>
    /// Number of APIs returned.
    /// </summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }
}

/// <summary>
/// Summary information about a Custom API.
/// </summary>
public sealed class CustomApiSummary
{
    /// <summary>
    /// Custom API ID.
    /// </summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Unique API name (used as the message name in Dataverse).
    /// </summary>
    [JsonPropertyName("uniqueName")]
    public string UniqueName { get; set; } = "";

    /// <summary>
    /// Display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Locale-independent name.
    /// </summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>
    /// Optional description.
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>
    /// Plugin type ID backing this API (if any).
    /// </summary>
    [JsonPropertyName("pluginTypeId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? PluginTypeId { get; set; }

    /// <summary>
    /// Binding type: Global, Entity, or EntityCollection.
    /// </summary>
    [JsonPropertyName("bindingType")]
    public string BindingType { get; set; } = "Global";

    /// <summary>
    /// Logical name of the bound entity (Entity/EntityCollection bindings only).
    /// </summary>
    [JsonPropertyName("boundEntity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BoundEntity { get; set; }

    /// <summary>
    /// Allowed processing step type: None, AsyncOnly, or SyncAndAsync.
    /// </summary>
    [JsonPropertyName("allowedProcessingStepType")]
    public string AllowedProcessingStepType { get; set; } = "None";

    /// <summary>
    /// True when this API is a function (returns a value via HTTP GET).
    /// </summary>
    [JsonPropertyName("isFunction")]
    public bool IsFunction { get; set; }

    /// <summary>
    /// True when this API is private (hidden from metadata).
    /// </summary>
    [JsonPropertyName("isPrivate")]
    public bool IsPrivate { get; set; }

    /// <summary>
    /// Optional privilege name required to execute the API.
    /// </summary>
    [JsonPropertyName("executePrivilegeName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutePrivilegeName { get; set; }

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

    /// <summary>
    /// Request parameters.
    /// </summary>
    [JsonPropertyName("requestParameters")]
    public List<CustomApiParameterSummary> RequestParameters { get; set; } = [];

    /// <summary>
    /// Response properties.
    /// </summary>
    [JsonPropertyName("responseProperties")]
    public List<CustomApiParameterSummary> ResponseProperties { get; set; } = [];
}

/// <summary>
/// Summary information about a Custom API request parameter or response property.
/// </summary>
public sealed class CustomApiParameterSummary
{
    /// <summary>
    /// Parameter ID.
    /// </summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Unique parameter name.
    /// </summary>
    [JsonPropertyName("uniqueName")]
    public string UniqueName { get; set; } = "";

    /// <summary>
    /// Display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Locale-independent name.
    /// </summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>
    /// Optional description.
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>
    /// Data type: Boolean, DateTime, Decimal, Entity, EntityCollection, EntityReference,
    /// Float, Integer, Money, Picklist, String, StringArray, or Guid.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>
    /// Logical entity name (Entity/EntityCollection/EntityReference types only).
    /// </summary>
    [JsonPropertyName("logicalEntityName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogicalEntityName { get; set; }

    /// <summary>
    /// True when the parameter is optional (request parameters only).
    /// </summary>
    [JsonPropertyName("isOptional")]
    public bool IsOptional { get; set; }

    /// <summary>
    /// True when part of a managed solution.
    /// </summary>
    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }
}
