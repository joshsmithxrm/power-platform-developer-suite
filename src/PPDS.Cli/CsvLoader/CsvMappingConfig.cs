using System.Text.Json;
using System.Text.Json.Serialization;
using PPDS.Cli.Plugins.Models;

namespace PPDS.Cli.CsvLoader;

/// <summary>
/// Configuration for CSV-to-Dataverse column mapping.
/// </summary>
public sealed class CsvMappingConfig
{
    /// <summary>
    /// JSON schema reference for validation.
    /// </summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    /// <summary>
    /// Schema version for forward compatibility.
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    /// <summary>
    /// Target entity logical name.
    /// </summary>
    [JsonPropertyName("entity")]
    public string? Entity { get; set; }

    /// <summary>
    /// Timestamp when the mapping was generated.
    /// </summary>
    [JsonPropertyName("generatedAt")]
    [JsonConverter(typeof(ZuluTimeConverter))]
    public DateTimeOffset? GeneratedAt { get; set; }

    /// <summary>
    /// Column mappings keyed by CSV header name.
    /// </summary>
    [JsonPropertyName("columns")]
    public Dictionary<string, ColumnMappingEntry> Columns { get; set; } = new();

    /// <summary>
    /// Default values applied to all rows. Keys are attribute logical names.
    /// </summary>
    [JsonPropertyName("defaults")]
    public Dictionary<string, JsonElement>? Defaults { get; set; }

    /// <summary>
    /// Extension data for forward compatibility.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// Schema URL for generated mapping files.
    /// </summary>
    public const string SchemaUrl = "https://raw.githubusercontent.com/joshsmithxrm/power-platform-developer-suite/main/schemas/csv-mapping.schema.json";
}

/// <summary>
/// Mapping configuration for a single CSV column.
/// </summary>
public sealed class ColumnMappingEntry
{
    /// <summary>
    /// Target attribute logical name.
    /// </summary>
    [JsonPropertyName("field")]
    public string? Field { get; set; }

    /// <summary>
    /// Skip this column during import.
    /// </summary>
    [JsonPropertyName("skip")]
    public bool Skip { get; set; }

    /// <summary>
    /// Lookup resolution configuration.
    /// </summary>
    [JsonPropertyName("lookup")]
    public LookupConfig? Lookup { get; set; }

    /// <summary>
    /// Custom date format string (e.g., 'MM/dd/yyyy').
    /// </summary>
    [JsonPropertyName("dateFormat")]
    public string? DateFormat { get; set; }

    /// <summary>
    /// Map CSV text labels to optionset integer values.
    /// </summary>
    [JsonPropertyName("optionsetMap")]
    public Dictionary<string, int>? OptionsetMap { get; set; }

    // Generated metadata (ignored at runtime)

    /// <summary>
    /// Generated status indicator. Ignored at runtime.
    /// </summary>
    [JsonPropertyName("_status")]
    public string? Status { get; set; }

    /// <summary>
    /// Generated note for user guidance. Ignored at runtime.
    /// </summary>
    [JsonPropertyName("_note")]
    public string? Note { get; set; }

    /// <summary>
    /// Sample values from CSV for reference. Ignored at runtime.
    /// </summary>
    [JsonPropertyName("_csvSample")]
    public List<string>? CsvSample { get; set; }

    /// <summary>
    /// Available optionset values for reference. Ignored at runtime.
    /// </summary>
    [JsonPropertyName("_optionsetValues")]
    public Dictionary<string, int>? OptionsetValues { get; set; }

    /// <summary>
    /// Suggested similar attribute names. Ignored at runtime.
    /// </summary>
    [JsonPropertyName("_similarAttributes")]
    public List<string>? SimilarAttributes { get; set; }

    /// <summary>
    /// Extension data for forward compatibility.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Configuration for resolving lookup field values.
/// </summary>
public sealed class LookupConfig
{
    /// <summary>
    /// Target entity logical name for the lookup.
    /// </summary>
    [JsonPropertyName("entity")]
    public required string Entity { get; set; }

    /// <summary>
    /// Resolution strategy: 'guid' expects GUID values, 'field' queries by keyField.
    /// </summary>
    [JsonPropertyName("matchBy")]
    public string MatchBy { get; set; } = "guid";

    /// <summary>
    /// Attribute to match against when matchBy is 'field'.
    /// </summary>
    [JsonPropertyName("keyField")]
    public string? KeyField { get; set; }

    /// <summary>
    /// Available matching options for reference. Ignored at runtime.
    /// </summary>
    [JsonPropertyName("_options")]
    public List<string>? Options { get; set; }

    /// <summary>
    /// Extension data for forward compatibility.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
