using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Query;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that retrieves detailed information for a single plugin registration entity.
/// </summary>
[McpServerToolType]
public sealed class PluginsGetTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginsGetTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public PluginsGetTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Gets detailed information for a specific plugin registration entity.
    /// </summary>
    /// <param name="type">Entity type: assembly, package, type, step, or image.</param>
    /// <param name="nameOrId">Entity name or GUID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Detailed entity information.</returns>
    [McpServerTool(Name = "ppds_plugins_get")]
    [Description("Get detailed information for a specific plugin registration entity (assembly, package, type, step, or image). Use ppds_plugins_list first to discover entity names/IDs.")]
    public async Task<PluginsGetResult> ExecuteAsync(
        [Description("Entity type: assembly, package, type, step, or image")]
        string type,
        [Description("Entity name or GUID")]
        string nameOrId,
        CancellationToken cancellationToken = default)
    {
        string[] validTypes = ["assembly", "package", "type", "step", "image"];
        var typeLower = (type ?? throw new ArgumentNullException(nameof(type))).ToLowerInvariant();
        if (!validTypes.Contains(typeLower))
            throw new ArgumentException($"Invalid type '{type}'. Must be one of: {string.Join(", ", validTypes)}");

        await using var serviceProvider = await CreateScopeAsync(cancellationToken, (nameof(nameOrId), nameOrId)).ConfigureAwait(false);
        var queryExecutor = serviceProvider.GetRequiredService<IQueryExecutor>();

        return typeLower switch
        {
            "assembly" => await GetAssemblyAsync(nameOrId, queryExecutor, cancellationToken).ConfigureAwait(false),
            "package" => await GetPackageAsync(nameOrId, queryExecutor, cancellationToken).ConfigureAwait(false),
            "type" => await GetPluginTypeAsync(nameOrId, queryExecutor, cancellationToken).ConfigureAwait(false),
            "step" => await GetStepAsync(nameOrId, queryExecutor, cancellationToken).ConfigureAwait(false),
            "image" => await GetImageAsync(nameOrId, queryExecutor, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"Invalid type '{type}'. Must be one of: {string.Join(", ", validTypes)}")
        };
    }

    private static async Task<PluginsGetResult> GetAssemblyAsync(
        string nameOrId,
        IQueryExecutor queryExecutor,
        CancellationToken cancellationToken)
    {
        var isGuid = Guid.TryParse(nameOrId, out var id);
        var condition = isGuid
            ? $@"<condition attribute=""pluginassemblyid"" operator=""eq"" value=""{id}"" />"
            : $@"<condition attribute=""name"" operator=""eq"" value=""{EscapeXml(nameOrId)}"" />";

        var fetchXml = $@"
            <fetch top=""1"">
                <entity name=""pluginassembly"">
                    <attribute name=""pluginassemblyid"" />
                    <attribute name=""name"" />
                    <attribute name=""version"" />
                    <attribute name=""publickeytoken"" />
                    <attribute name=""culture"" />
                    <attribute name=""isolationmode"" />
                    <attribute name=""sourcetype"" />
                    <attribute name=""ismanaged"" />
                    <attribute name=""createdon"" />
                    <attribute name=""modifiedon"" />
                    <filter type=""and"">
                        {condition}
                    </filter>
                </entity>
            </fetch>";

        var result = await queryExecutor.ExecuteFetchXmlAsync(fetchXml, null, null, false, cancellationToken).ConfigureAwait(false);
        var record = result.Records.FirstOrDefault();

        if (record == null)
            return new PluginsGetResult { Found = false };

        return new PluginsGetResult
        {
            Found = true,
            EntityType = "assembly",
            Assembly = new PluginAssemblyDetail
            {
                Id = GetGuid(record, "pluginassemblyid"),
                Name = GetString(record, "name"),
                Version = GetString(record, "version"),
                PublicKeyToken = GetString(record, "publickeytoken"),
                Culture = GetString(record, "culture"),
                IsolationMode = GetFormatted(record, "isolationmode"),
                SourceType = GetFormatted(record, "sourcetype"),
                IsManaged = GetBool(record, "ismanaged"),
                CreatedOn = GetDateTime(record, "createdon"),
                ModifiedOn = GetDateTime(record, "modifiedon")
            }
        };
    }

    private static async Task<PluginsGetResult> GetPackageAsync(
        string nameOrId,
        IQueryExecutor queryExecutor,
        CancellationToken cancellationToken)
    {
        var isGuid = Guid.TryParse(nameOrId, out var id);
        var condition = isGuid
            ? $@"<condition attribute=""pluginpackageid"" operator=""eq"" value=""{id}"" />"
            : $@"<filter type=""or"">
                    <condition attribute=""name"" operator=""eq"" value=""{EscapeXml(nameOrId)}"" />
                    <condition attribute=""uniquename"" operator=""eq"" value=""{EscapeXml(nameOrId)}"" />
                </filter>";

        var fetchXml = $@"
            <fetch top=""1"">
                <entity name=""pluginpackage"">
                    <attribute name=""pluginpackageid"" />
                    <attribute name=""name"" />
                    <attribute name=""uniquename"" />
                    <attribute name=""version"" />
                    <attribute name=""ismanaged"" />
                    <attribute name=""createdon"" />
                    <attribute name=""modifiedon"" />
                    <filter type=""and"">
                        {condition}
                    </filter>
                </entity>
            </fetch>";

        var result = await queryExecutor.ExecuteFetchXmlAsync(fetchXml, null, null, false, cancellationToken).ConfigureAwait(false);
        var record = result.Records.FirstOrDefault();

        if (record == null)
            return new PluginsGetResult { Found = false };

        return new PluginsGetResult
        {
            Found = true,
            EntityType = "package",
            Package = new PluginPackageDetail
            {
                Id = GetGuid(record, "pluginpackageid"),
                Name = GetString(record, "name"),
                UniqueName = GetString(record, "uniquename"),
                Version = GetString(record, "version"),
                IsManaged = GetBool(record, "ismanaged"),
                CreatedOn = GetDateTime(record, "createdon"),
                ModifiedOn = GetDateTime(record, "modifiedon")
            }
        };
    }

    private static async Task<PluginsGetResult> GetPluginTypeAsync(
        string nameOrId,
        IQueryExecutor queryExecutor,
        CancellationToken cancellationToken)
    {
        var isGuid = Guid.TryParse(nameOrId, out var id);
        var condition = isGuid
            ? $@"<condition attribute=""plugintypeid"" operator=""eq"" value=""{id}"" />"
            : $@"<condition attribute=""typename"" operator=""eq"" value=""{EscapeXml(nameOrId)}"" />";

        var fetchXml = $@"
            <fetch top=""1"">
                <entity name=""plugintype"">
                    <attribute name=""plugintypeid"" />
                    <attribute name=""typename"" />
                    <attribute name=""friendlyname"" />
                    <attribute name=""name"" />
                    <attribute name=""pluginassemblyid"" />
                    <attribute name=""ismanaged"" />
                    <attribute name=""createdon"" />
                    <attribute name=""modifiedon"" />
                    <filter type=""and"">
                        {condition}
                    </filter>
                    <link-entity name=""pluginassembly"" from=""pluginassemblyid"" to=""pluginassemblyid"" link-type=""outer"" alias=""asm"">
                        <attribute name=""name"" alias=""assemblyname"" />
                    </link-entity>
                </entity>
            </fetch>";

        var result = await queryExecutor.ExecuteFetchXmlAsync(fetchXml, null, null, false, cancellationToken).ConfigureAwait(false);
        var record = result.Records.FirstOrDefault();

        if (record == null)
            return new PluginsGetResult { Found = false };

        return new PluginsGetResult
        {
            Found = true,
            EntityType = "type",
            PluginType = new PluginTypeDetail
            {
                Id = GetGuid(record, "plugintypeid"),
                TypeName = GetString(record, "typename"),
                FriendlyName = GetString(record, "friendlyname"),
                Name = GetString(record, "name"),
                AssemblyId = GetGuidNullable(record, "pluginassemblyid"),
                AssemblyName = GetString(record, "assemblyname"),
                IsManaged = GetBool(record, "ismanaged"),
                CreatedOn = GetDateTime(record, "createdon"),
                ModifiedOn = GetDateTime(record, "modifiedon")
            }
        };
    }

    private static async Task<PluginsGetResult> GetStepAsync(
        string nameOrId,
        IQueryExecutor queryExecutor,
        CancellationToken cancellationToken)
    {
        var isGuid = Guid.TryParse(nameOrId, out var id);
        var condition = isGuid
            ? $@"<condition attribute=""sdkmessageprocessingstepid"" operator=""eq"" value=""{id}"" />"
            : $@"<condition attribute=""name"" operator=""eq"" value=""{EscapeXml(nameOrId)}"" />";

        var fetchXml = $@"
            <fetch top=""1"">
                <entity name=""sdkmessageprocessingstep"">
                    <attribute name=""sdkmessageprocessingstepid"" />
                    <attribute name=""name"" />
                    <attribute name=""description"" />
                    <attribute name=""stage"" />
                    <attribute name=""mode"" />
                    <attribute name=""rank"" />
                    <attribute name=""filteringattributes"" />
                    <attribute name=""configuration"" />
                    <attribute name=""statecode"" />
                    <attribute name=""plugintypeid"" />
                    <attribute name=""ismanaged"" />
                    <attribute name=""iscustomizable"" />
                    <attribute name=""asyncautodelete"" />
                    <attribute name=""createdon"" />
                    <attribute name=""modifiedon"" />
                    <filter type=""and"">
                        {condition}
                    </filter>
                    <link-entity name=""sdkmessage"" from=""sdkmessageid"" to=""sdkmessageid"" link-type=""outer"" alias=""msg"">
                        <attribute name=""name"" alias=""messagename"" />
                    </link-entity>
                    <link-entity name=""sdkmessagefilter"" from=""sdkmessagefilterid"" to=""sdkmessagefilterid"" link-type=""outer"" alias=""flt"">
                        <attribute name=""primaryobjecttypecode"" alias=""primaryentity"" />
                        <attribute name=""secondaryobjecttypecode"" alias=""secondaryentity"" />
                    </link-entity>
                    <link-entity name=""plugintype"" from=""plugintypeid"" to=""plugintypeid"" link-type=""outer"" alias=""pt"">
                        <attribute name=""typename"" alias=""plugintypename"" />
                    </link-entity>
                </entity>
            </fetch>";

        var result = await queryExecutor.ExecuteFetchXmlAsync(fetchXml, null, null, false, cancellationToken).ConfigureAwait(false);
        var record = result.Records.FirstOrDefault();

        if (record == null)
            return new PluginsGetResult { Found = false };

        var stateCode = GetInt(record, "statecode");

        return new PluginsGetResult
        {
            Found = true,
            EntityType = "step",
            Step = new PluginStepDetail
            {
                Id = GetGuid(record, "sdkmessageprocessingstepid"),
                Name = GetString(record, "name"),
                Description = GetString(record, "description"),
                Message = GetString(record, "messagename"),
                PrimaryEntity = GetString(record, "primaryentity"),
                SecondaryEntity = GetString(record, "secondaryentity"),
                Stage = GetFormatted(record, "stage"),
                Mode = GetFormatted(record, "mode"),
                ExecutionOrder = GetInt(record, "rank"),
                FilteringAttributes = GetString(record, "filteringattributes"),
                Configuration = GetString(record, "configuration"),
                IsEnabled = stateCode == 0,
                PluginTypeId = GetGuidNullable(record, "plugintypeid"),
                PluginTypeName = GetString(record, "plugintypename"),
                IsManaged = GetBool(record, "ismanaged"),
                AsyncAutoDelete = GetBool(record, "asyncautodelete"),
                CreatedOn = GetDateTime(record, "createdon"),
                ModifiedOn = GetDateTime(record, "modifiedon")
            }
        };
    }

    private static async Task<PluginsGetResult> GetImageAsync(
        string nameOrId,
        IQueryExecutor queryExecutor,
        CancellationToken cancellationToken)
    {
        var isGuid = Guid.TryParse(nameOrId, out var id);
        var condition = isGuid
            ? $@"<condition attribute=""sdkmessageprocessingstepimageid"" operator=""eq"" value=""{id}"" />"
            : $@"<condition attribute=""name"" operator=""eq"" value=""{EscapeXml(nameOrId)}"" />";

        var fetchXml = $@"
            <fetch top=""1"">
                <entity name=""sdkmessageprocessingstepimage"">
                    <attribute name=""sdkmessageprocessingstepimageid"" />
                    <attribute name=""name"" />
                    <attribute name=""entityalias"" />
                    <attribute name=""imagetype"" />
                    <attribute name=""attributes"" />
                    <attribute name=""messagepropertyname"" />
                    <attribute name=""sdkmessageprocessingstepid"" />
                    <attribute name=""ismanaged"" />
                    <attribute name=""iscustomizable"" />
                    <attribute name=""createdon"" />
                    <attribute name=""modifiedon"" />
                    <filter type=""and"">
                        {condition}
                    </filter>
                    <link-entity name=""sdkmessageprocessingstep"" from=""sdkmessageprocessingstepid"" to=""sdkmessageprocessingstepid"" link-type=""outer"" alias=""stp"">
                        <attribute name=""name"" alias=""stepname"" />
                    </link-entity>
                </entity>
            </fetch>";

        var result = await queryExecutor.ExecuteFetchXmlAsync(fetchXml, null, null, false, cancellationToken).ConfigureAwait(false);
        var record = result.Records.FirstOrDefault();

        if (record == null)
            return new PluginsGetResult { Found = false };

        return new PluginsGetResult
        {
            Found = true,
            EntityType = "image",
            Image = new PluginImageDetail
            {
                Id = GetGuid(record, "sdkmessageprocessingstepimageid"),
                Name = GetString(record, "name"),
                EntityAlias = GetString(record, "entityalias"),
                ImageType = GetFormatted(record, "imagetype"),
                Attributes = GetString(record, "attributes"),
                MessagePropertyName = GetString(record, "messagepropertyname"),
                StepId = GetGuidNullable(record, "sdkmessageprocessingstepid"),
                StepName = GetString(record, "stepname"),
                IsManaged = GetBool(record, "ismanaged"),
                CreatedOn = GetDateTime(record, "createdon"),
                ModifiedOn = GetDateTime(record, "modifiedon")
            }
        };
    }

    // Value extraction helpers

    private static Guid GetGuid(IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv) && qv.Value != null)
        {
            if (qv.Value is Guid g) return g;
            if (Guid.TryParse(qv.Value.ToString(), out var parsed)) return parsed;
        }
        return Guid.Empty;
    }

    private static Guid? GetGuidNullable(IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv) && qv.Value != null)
        {
            if (qv.Value is Guid g) return g;
            if (Guid.TryParse(qv.Value.ToString(), out var parsed)) return parsed;
        }
        return null;
    }

    private static string? GetString(IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv))
            return qv.Value?.ToString();
        return null;
    }

    private static string? GetFormatted(IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv))
            return qv.FormattedValue ?? qv.Value?.ToString();
        return null;
    }

    private static bool GetBool(IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv) && qv.Value != null)
        {
            if (qv.Value is bool b) return b;
            if (bool.TryParse(qv.Value.ToString(), out var parsed)) return parsed;
        }
        return false;
    }

    private static int GetInt(IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv) && qv.Value != null)
        {
            if (qv.Value is int i) return i;
            if (int.TryParse(qv.Value.ToString(), out var parsed)) return parsed;
        }
        return 0;
    }

    private static DateTime? GetDateTime(IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv) && qv.Value != null)
        {
            if (qv.Value is DateTime dt) return dt;
            if (DateTime.TryParse(qv.Value.ToString(), out var parsed)) return parsed;
        }
        return null;
    }

    private static string EscapeXml(string value) =>
        value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
}

/// <summary>
/// Result of the ppds_plugins_get tool.
/// </summary>
public sealed class PluginsGetResult
{
    /// <summary>
    /// Whether the entity was found.
    /// </summary>
    [JsonPropertyName("found")]
    public bool Found { get; set; }

    /// <summary>
    /// Entity type (assembly, package, type, step, image).
    /// </summary>
    [JsonPropertyName("entityType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntityType { get; set; }

    /// <summary>
    /// Assembly details (when entityType = "assembly").
    /// </summary>
    [JsonPropertyName("assembly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PluginAssemblyDetail? Assembly { get; set; }

    /// <summary>
    /// Package details (when entityType = "package").
    /// </summary>
    [JsonPropertyName("package")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PluginPackageDetail? Package { get; set; }

    /// <summary>
    /// Plugin type details (when entityType = "type").
    /// </summary>
    [JsonPropertyName("pluginType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PluginTypeDetail? PluginType { get; set; }

    /// <summary>
    /// Processing step details (when entityType = "step").
    /// </summary>
    [JsonPropertyName("step")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PluginStepDetail? Step { get; set; }

    /// <summary>
    /// Step image details (when entityType = "image").
    /// </summary>
    [JsonPropertyName("image")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PluginImageDetail? Image { get; set; }
}

/// <summary>
/// Detailed information about a plugin assembly.
/// </summary>
public sealed class PluginAssemblyDetail
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonPropertyName("publicKeyToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublicKeyToken { get; set; }

    [JsonPropertyName("culture")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Culture { get; set; }

    [JsonPropertyName("isolationMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IsolationMode { get; set; }

    [JsonPropertyName("sourceType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceType { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CreatedOn { get; set; }

    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ModifiedOn { get; set; }
}

/// <summary>
/// Detailed information about a plugin package.
/// </summary>
public sealed class PluginPackageDetail
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("uniqueName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UniqueName { get; set; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CreatedOn { get; set; }

    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ModifiedOn { get; set; }
}

/// <summary>
/// Detailed information about a plugin type.
/// </summary>
public sealed class PluginTypeDetail
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("typeName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TypeName { get; set; }

    [JsonPropertyName("friendlyName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FriendlyName { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("assemblyId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? AssemblyId { get; set; }

    [JsonPropertyName("assemblyName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AssemblyName { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CreatedOn { get; set; }

    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ModifiedOn { get; set; }
}

/// <summary>
/// Detailed information about a processing step.
/// </summary>
public sealed class PluginStepDetail
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    [JsonPropertyName("primaryEntity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryEntity { get; set; }

    [JsonPropertyName("secondaryEntity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SecondaryEntity { get; set; }

    [JsonPropertyName("stage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stage { get; set; }

    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }

    [JsonPropertyName("executionOrder")]
    public int ExecutionOrder { get; set; }

    [JsonPropertyName("filteringAttributes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FilteringAttributes { get; set; }

    [JsonPropertyName("configuration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Configuration { get; set; }

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; }

    [JsonPropertyName("pluginTypeId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? PluginTypeId { get; set; }

    [JsonPropertyName("pluginTypeName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PluginTypeName { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("asyncAutoDelete")]
    public bool AsyncAutoDelete { get; set; }

    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CreatedOn { get; set; }

    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ModifiedOn { get; set; }
}

/// <summary>
/// Detailed information about a step image.
/// </summary>
public sealed class PluginImageDetail
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("entityAlias")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntityAlias { get; set; }

    [JsonPropertyName("imageType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageType { get; set; }

    [JsonPropertyName("attributes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Attributes { get; set; }

    [JsonPropertyName("messagePropertyName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessagePropertyName { get; set; }

    [JsonPropertyName("stepId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? StepId { get; set; }

    [JsonPropertyName("stepName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StepName { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CreatedOn { get; set; }

    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ModifiedOn { get; set; }
}
