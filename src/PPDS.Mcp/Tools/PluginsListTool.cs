using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Query;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that lists registered plugins in the environment.
/// </summary>
[McpServerToolType]
public sealed class PluginsListTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginsListTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public PluginsListTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Lists registered plugin assemblies in the environment.
    /// </summary>
    /// <param name="nameFilter">Filter by assembly name (contains).</param>
    /// <param name="includeHidden">Include hidden system steps.</param>
    /// <param name="includeMicrosoft">Include Microsoft assemblies.</param>
    /// <param name="maxRows">Maximum rows to return (default 50).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of plugin assemblies with types and steps.</returns>
    [McpServerTool(Name = "ppds_plugins_list")]
    [Description("List registered plugin assemblies in the Dataverse environment. Shows plugin types and their registered steps (message/entity/stage combinations). By default excludes hidden system assemblies and Microsoft.* assemblies.")]
    public async Task<PluginsListResult> ExecuteAsync(
        [Description("Filter by assembly name (partial match)")]
        string? nameFilter = null,
        [Description("Include hidden system steps (default false)")]
        bool includeHidden = false,
        [Description("Include Microsoft assemblies (default false)")]
        bool includeMicrosoft = false,
        [Description("Maximum rows to return (default 50, max 200)")]
        int maxRows = 50,
        CancellationToken cancellationToken = default)
    {
        maxRows = Math.Clamp(maxRows, 1, 200);

        await using var serviceProvider = await CreateScopeAsync(cancellationToken).ConfigureAwait(false);
        var queryExecutor = serviceProvider.GetRequiredService<IQueryExecutor>();

        // Single query with link-entity joins: assembly -> type -> step -> message.
        var query = BuildJoinedQuery(nameFilter, maxRows, includeHidden, includeMicrosoft);
        var result = await queryExecutor.ExecuteFetchXmlAsync(
            query, null, null, false, cancellationToken).ConfigureAwait(false);

        // Flatten the joined rows back into the assembly -> type -> step hierarchy.
        var assemblyMap = new Dictionary<Guid, PluginAssemblyResult>();
        var typeMap = new Dictionary<Guid, PluginTypeResult>();

        foreach (var record in result.Records)
        {
            var assemblyId = record.GetGuid("pluginassemblyid");
            if (assemblyId == Guid.Empty) continue;

            if (!assemblyMap.TryGetValue(assemblyId, out var assembly))
            {
                assembly = new PluginAssemblyResult
                {
                    Id = assemblyId,
                    Name = record.GetString("name"),
                    Version = record.GetString("version"),
                    PublicKeyToken = record.GetString("publickeytoken"),
                    IsolationMode = record.GetFormatted("isolationmode"),
                    SourceType = record.GetFormatted("sourcetype"),
                    Types = []
                };
                assemblyMap[assemblyId] = assembly;
            }

            var typeId = record.GetGuid("pt.plugintypeid");
            if (typeId == Guid.Empty) continue;

            if (!typeMap.TryGetValue(typeId, out var pluginType))
            {
                pluginType = new PluginTypeResult
                {
                    Id = typeId,
                    TypeName = record.GetString("pt.typename"),
                    FriendlyName = record.GetString("pt.friendlyname"),
                    Steps = []
                };
                typeMap[typeId] = pluginType;
                assembly.Types.Add(pluginType);
            }

            var stepName = record.GetString("step.name");
            if (!string.IsNullOrEmpty(stepName))
            {
                pluginType.Steps.Add(new PluginStepResult
                {
                    Name = stepName,
                    Message = record.GetString("msg.name"),
                    Entity = record.GetString("step.primaryobjecttypecode"),
                    Stage = record.GetFormatted("step.stage"),
                    IsEnabled = record.GetString("step.statecode") == "0"
                });
            }
        }

        var assemblies = assemblyMap.Values.ToList();
        return new PluginsListResult
        {
            Assemblies = assemblies,
            Count = assemblies.Count,
            TotalCount = assemblies.Count
        };
    }

    private static string BuildJoinedQuery(string? nameFilter, int maxRows, bool includeHidden, bool includeMicrosoft)
    {
        var conditions = new System.Text.StringBuilder();

        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            conditions.Append($@"<condition attribute=""name"" operator=""like"" value=""%{QueryValueExtensions.EscapeXml(nameFilter)}%"" />");
        }

        if (!includeHidden)
        {
            conditions.Append(@"<condition attribute=""ishidden"" operator=""eq"" value=""0"" />");
        }

        var filterXml = "";
        if (conditions.Length > 0)
        {
            filterXml = $@"<filter type=""and"">{conditions}</filter>";
        }

        var microsoftFilter = "";
        if (!includeMicrosoft)
        {
            microsoftFilter = @"<filter type=""or"">
                    <condition attribute=""name"" operator=""not-like"" value=""Microsoft%"" />
                    <condition attribute=""name"" operator=""eq"" value=""Microsoft.Crm.ServiceBus"" />
                </filter>";
        }

        // Use top on the outer entity to limit assemblies. The link-entity joins
        // bring in types and steps in a single round-trip, avoiding N+1 queries.
        return $@"
            <fetch top=""{maxRows}"">
                <entity name=""pluginassembly"">
                    <attribute name=""pluginassemblyid"" />
                    <attribute name=""name"" />
                    <attribute name=""version"" />
                    <attribute name=""publickeytoken"" />
                    <attribute name=""isolationmode"" />
                    <attribute name=""sourcetype"" />
                    {filterXml}
                    {microsoftFilter}
                    <order attribute=""name"" />
                    <link-entity name=""plugintype"" from=""pluginassemblyid"" to=""pluginassemblyid"" link-type=""outer"" alias=""pt"">
                        <attribute name=""plugintypeid"" />
                        <attribute name=""typename"" />
                        <attribute name=""friendlyname"" />
                        <order attribute=""typename"" />
                        <link-entity name=""sdkmessageprocessingstep"" from=""plugintypeid"" to=""plugintypeid"" link-type=""outer"" alias=""step"">
                            <attribute name=""name"" />
                            <attribute name=""stage"" />
                            <attribute name=""statecode"" />
                            <attribute name=""primaryobjecttypecode"" />
                            <link-entity name=""sdkmessage"" from=""sdkmessageid"" to=""sdkmessageid"" link-type=""outer"" alias=""msg"">
                                <attribute name=""name"" />
                            </link-entity>
                        </link-entity>
                    </link-entity>
                </entity>
            </fetch>";
    }

}

/// <summary>
/// Result of the plugins_list tool.
/// </summary>
public sealed class PluginsListResult
{
    /// <summary>
    /// List of plugin assemblies.
    /// </summary>
    [JsonPropertyName("assemblies")]
    public List<PluginAssemblyResult> Assemblies { get; set; } = [];

    /// <summary>
    /// Number of assemblies returned.
    /// </summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }

    /// <summary>Total count of assemblies matching the filter (before top limit).</summary>
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}

/// <summary>
/// Plugin assembly information.
/// </summary>
public sealed class PluginAssemblyResult
{
    /// <summary>
    /// Assembly ID.
    /// </summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Assembly name.
    /// </summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>
    /// Assembly version.
    /// </summary>
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    /// <summary>
    /// Public key token.
    /// </summary>
    [JsonPropertyName("publicKeyToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublicKeyToken { get; set; }

    /// <summary>
    /// Isolation mode (None, Sandbox).
    /// </summary>
    [JsonPropertyName("isolationMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IsolationMode { get; set; }

    /// <summary>
    /// Source type (Database, Disk, GAC).
    /// </summary>
    [JsonPropertyName("sourceType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceType { get; set; }

    /// <summary>
    /// Plugin types in this assembly.
    /// </summary>
    [JsonPropertyName("types")]
    public List<PluginTypeResult> Types { get; set; } = [];
}

/// <summary>
/// Plugin type information.
/// </summary>
public sealed class PluginTypeResult
{
    /// <summary>
    /// Plugin type ID.
    /// </summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Full type name (namespace.classname).
    /// </summary>
    [JsonPropertyName("typeName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TypeName { get; set; }

    /// <summary>
    /// Friendly name.
    /// </summary>
    [JsonPropertyName("friendlyName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FriendlyName { get; set; }

    /// <summary>
    /// Registered steps.
    /// </summary>
    [JsonPropertyName("steps")]
    public List<PluginStepResult> Steps { get; set; } = [];
}

/// <summary>
/// Plugin step information.
/// </summary>
public sealed class PluginStepResult
{
    /// <summary>
    /// Step name.
    /// </summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>
    /// Message name (Create, Update, etc.).
    /// </summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    /// <summary>
    /// Primary entity.
    /// </summary>
    [JsonPropertyName("entity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Entity { get; set; }

    /// <summary>
    /// Execution stage (PreValidation, PreOperation, PostOperation).
    /// </summary>
    [JsonPropertyName("stage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stage { get; set; }

    /// <summary>
    /// Whether the step is enabled.
    /// </summary>
    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; }
}
