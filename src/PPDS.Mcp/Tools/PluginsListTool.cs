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
public sealed class PluginsListTool
{
    private readonly McpToolContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginsListTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public PluginsListTool(McpToolContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Lists registered plugin assemblies in the environment.
    /// </summary>
    /// <param name="nameFilter">Filter by assembly name (contains).</param>
    /// <param name="maxRows">Maximum rows to return (default 50).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of plugin assemblies with types and steps.</returns>
    [McpServerTool(Name = "ppds_plugins_list")]
    [Description("List registered plugin assemblies in the Dataverse environment. Shows plugin types and their registered steps (message/entity combinations).")]
    public async Task<PluginsListResult> ExecuteAsync(
        [Description("Filter by assembly name (partial match)")]
        string? nameFilter = null,
        [Description("Maximum rows to return (default 50, max 200)")]
        int maxRows = 50,
        CancellationToken cancellationToken = default)
    {
        maxRows = Math.Clamp(maxRows, 1, 200);

        await using var serviceProvider = await _context.CreateServiceProviderAsync(cancellationToken).ConfigureAwait(false);
        var queryExecutor = serviceProvider.GetRequiredService<IQueryExecutor>();

        // Query plugin assemblies.
        var assemblyQuery = BuildAssemblyQuery(nameFilter, maxRows);
        var assemblyResult = await queryExecutor.ExecuteFetchXmlAsync(
            assemblyQuery, null, null, false, cancellationToken).ConfigureAwait(false);

        var assemblies = new List<PluginAssemblyResult>();

        foreach (var record in assemblyResult.Records)
        {
            var assemblyId = GetGuidValue(record, "pluginassemblyid");
            if (assemblyId == Guid.Empty) continue;

            var assembly = new PluginAssemblyResult
            {
                Id = assemblyId,
                Name = GetStringValue(record, "name"),
                Version = GetStringValue(record, "version"),
                PublicKeyToken = GetStringValue(record, "publickeytoken"),
                IsolationMode = GetFormattedValue(record, "isolationmode"),
                SourceType = GetFormattedValue(record, "sourcetype"),
                Types = []
            };

            // Query types for this assembly.
            var typesQuery = BuildTypesQuery(assemblyId);
            var typesResult = await queryExecutor.ExecuteFetchXmlAsync(
                typesQuery, null, null, false, cancellationToken).ConfigureAwait(false);

            foreach (var typeRecord in typesResult.Records)
            {
                var typeId = GetGuidValue(typeRecord, "plugintypeid");

                var pluginType = new PluginTypeResult
                {
                    Id = typeId,
                    TypeName = GetStringValue(typeRecord, "typename"),
                    FriendlyName = GetStringValue(typeRecord, "friendlyname"),
                    Steps = []
                };

                assembly.Types.Add(pluginType);
            }

            assemblies.Add(assembly);
        }

        return new PluginsListResult
        {
            Assemblies = assemblies,
            Count = assemblies.Count
        };
    }

    private static string BuildAssemblyQuery(string? nameFilter, int maxRows)
    {
        var filter = "";
        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            filter = $@"
                <filter type=""and"">
                    <condition attribute=""name"" operator=""like"" value=""%{EscapeXmlValue(nameFilter)}%"" />
                </filter>";
        }

        return $@"
            <fetch top=""{maxRows}"">
                <entity name=""pluginassembly"">
                    <attribute name=""pluginassemblyid"" />
                    <attribute name=""name"" />
                    <attribute name=""version"" />
                    <attribute name=""publickeytoken"" />
                    <attribute name=""isolationmode"" />
                    <attribute name=""sourcetype"" />
                    {filter}
                    <order attribute=""name"" />
                </entity>
            </fetch>";
    }

    private static string BuildTypesQuery(Guid assemblyId)
    {
        return $@"
            <fetch top=""100"">
                <entity name=""plugintype"">
                    <attribute name=""plugintypeid"" />
                    <attribute name=""typename"" />
                    <attribute name=""friendlyname"" />
                    <filter type=""and"">
                        <condition attribute=""pluginassemblyid"" operator=""eq"" value=""{assemblyId}"" />
                    </filter>
                    <order attribute=""typename"" />
                </entity>
            </fetch>";
    }

    private static Guid GetGuidValue(IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv) && qv.Value != null)
        {
            if (qv.Value is Guid g) return g;
            if (Guid.TryParse(qv.Value.ToString(), out var parsed)) return parsed;
        }
        return Guid.Empty;
    }

    private static string? GetStringValue(IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv))
        {
            return qv.Value?.ToString();
        }
        return null;
    }

    private static string? GetFormattedValue(IReadOnlyDictionary<string, QueryValue> record, string key)
    {
        if (record.TryGetValue(key, out var qv))
        {
            return qv.FormattedValue ?? qv.Value?.ToString();
        }
        return null;
    }

    private static string EscapeXmlValue(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
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
