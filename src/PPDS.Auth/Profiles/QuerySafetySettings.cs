using System.Text.Json.Serialization;

namespace PPDS.Auth.Profiles;

/// <summary>
/// Per-environment query safety settings. Stored in environment config JSON.
/// All settings have sensible defaults — null means "use default".
/// </summary>
public sealed class QuerySafetySettings
{
    // ── DML Safety Thresholds ──

    /// <summary>Prompt when inserting more than N records (0 = always prompt). Default: 1.</summary>
    [JsonPropertyName("warn_insert_threshold")]
    public int? WarnInsertThreshold { get; set; }

    /// <summary>Prompt when updating more than N records (0 = always prompt). Default: 0.</summary>
    [JsonPropertyName("warn_update_threshold")]
    public int? WarnUpdateThreshold { get; set; }

    /// <summary>Prompt when deleting more than N records (0 = always prompt). Default: 0.</summary>
    [JsonPropertyName("warn_delete_threshold")]
    public int? WarnDeleteThreshold { get; set; }

    /// <summary>Block UPDATE without WHERE clause. Default: true.</summary>
    [JsonPropertyName("prevent_update_without_where")]
    public bool PreventUpdateWithoutWhere { get; set; } = true;

    /// <summary>Block DELETE without WHERE clause. Default: true.</summary>
    [JsonPropertyName("prevent_delete_without_where")]
    public bool PreventDeleteWithoutWhere { get; set; } = true;

    // ── Execution Settings ──

    /// <summary>Records per DML batch (1-1000). Default: 100.</summary>
    [JsonPropertyName("dml_batch_size")]
    public int? DmlBatchSize { get; set; }

    /// <summary>Maximum rows returned (0 = unlimited). Default: 0.</summary>
    [JsonPropertyName("max_result_rows")]
    public int? MaxResultRows { get; set; }

    /// <summary>Cancel query after N seconds (0 = no timeout). Default: 300.</summary>
    [JsonPropertyName("query_timeout_seconds")]
    public int? QueryTimeoutSeconds { get; set; }

    /// <summary>Route SELECT queries to TDS read replica. Default: false.</summary>
    [JsonPropertyName("use_tds_endpoint")]
    public bool UseTdsEndpoint { get; set; }

    /// <summary>Bypass custom plugin execution. Default: None.</summary>
    [JsonPropertyName("bypass_custom_plugins")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BypassPluginMode BypassCustomPlugins { get; set; } = BypassPluginMode.None;

    /// <summary>Suppress Power Automate flow triggers on DML. Default: false.</summary>
    [JsonPropertyName("bypass_power_automate_flows")]
    public bool BypassPowerAutomateFlows { get; set; }
}

/// <summary>Which plugin types to bypass during DML operations.</summary>
public enum BypassPluginMode
{
    /// <summary>Execute all plugins normally.</summary>
    None,
    /// <summary>Bypass synchronous plugins only.</summary>
    Synchronous,
    /// <summary>Bypass asynchronous plugins only.</summary>
    Asynchronous,
    /// <summary>Bypass all custom plugins.</summary>
    All
}

/// <summary>Environment protection level determining DML behavior.</summary>
public enum ProtectionLevel
{
    /// <summary>Unrestricted DML. Sandbox and Developer environments.</summary>
    Development,
    /// <summary>Warn per thresholds. Trial environments.</summary>
    Test,
    /// <summary>Block by default, require explicit confirmation with preview. Production and unknown environments.</summary>
    Production
}
