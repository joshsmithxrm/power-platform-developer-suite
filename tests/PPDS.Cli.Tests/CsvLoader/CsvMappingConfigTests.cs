using System.Text.Json;
using PPDS.Cli.CsvLoader;
using Xunit;

namespace PPDS.Cli.Tests.CsvLoader;

public class CsvMappingConfigTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    #region Serialization Tests

    [Fact]
    public void Serialize_MinimalConfig_ProducesValidJson()
    {
        var config = new CsvMappingConfig
        {
            Version = "1.0",
            Entity = "account"
        };

        var json = JsonSerializer.Serialize(config, JsonOptions);

        Assert.Contains("\"version\": \"1.0\"", json);
        Assert.Contains("\"entity\": \"account\"", json);
    }

    [Fact]
    public void Serialize_WithSchema_IncludesSchemaUrl()
    {
        var config = new CsvMappingConfig
        {
            Schema = CsvMappingConfig.SchemaUrl,
            Version = "1.0"
        };

        var json = JsonSerializer.Serialize(config, JsonOptions);

        Assert.Contains("\"$schema\":", json);
        Assert.Contains("csv-mapping.schema.json", json);
    }

    [Fact]
    public void Serialize_WithColumns_IncludesColumnMappings()
    {
        var config = new CsvMappingConfig
        {
            Version = "1.0",
            Entity = "account",
            Columns = new Dictionary<string, ColumnMappingEntry>
            {
                ["Account Name"] = new ColumnMappingEntry { Field = "name" },
                ["Phone"] = new ColumnMappingEntry { Field = "telephone1" }
            }
        };

        var json = JsonSerializer.Serialize(config, JsonOptions);

        Assert.Contains("\"Account Name\"", json);
        Assert.Contains("\"field\": \"name\"", json);
        Assert.Contains("\"Phone\"", json);
        Assert.Contains("\"field\": \"telephone1\"", json);
    }

    [Fact]
    public void Serialize_WithLookup_IncludesLookupConfig()
    {
        var config = new CsvMappingConfig
        {
            Version = "1.0",
            Columns = new Dictionary<string, ColumnMappingEntry>
            {
                ["Parent Account"] = new ColumnMappingEntry
                {
                    Field = "parentaccountid",
                    Lookup = new LookupConfig
                    {
                        Entity = "account",
                        MatchBy = "field",
                        KeyField = "name"
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(config, JsonOptions);

        Assert.Contains("\"lookup\":", json);
        Assert.Contains("\"entity\": \"account\"", json);
        Assert.Contains("\"matchBy\": \"field\"", json);
        Assert.Contains("\"keyField\": \"name\"", json);
    }

    [Fact]
    public void Serialize_WithSkipColumn_IncludesSkipFlag()
    {
        var config = new CsvMappingConfig
        {
            Version = "1.0",
            Columns = new Dictionary<string, ColumnMappingEntry>
            {
                ["Internal ID"] = new ColumnMappingEntry { Skip = true }
            }
        };

        var json = JsonSerializer.Serialize(config, JsonOptions);

        Assert.Contains("\"skip\": true", json);
    }

    [Fact]
    public void Serialize_WithOptionsetMap_IncludesLabelMap()
    {
        var config = new CsvMappingConfig
        {
            Version = "1.0",
            Columns = new Dictionary<string, ColumnMappingEntry>
            {
                ["Status"] = new ColumnMappingEntry
                {
                    Field = "statuscode",
                    OptionsetMap = new Dictionary<string, int>
                    {
                        ["Active"] = 1,
                        ["Inactive"] = 0
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(config, JsonOptions);

        Assert.Contains("\"optionsetMap\":", json);
        Assert.Contains("\"Active\": 1", json);
        Assert.Contains("\"Inactive\": 0", json);
    }

    #endregion

    #region Deserialization Tests

    [Fact]
    public void Deserialize_MinimalConfig_ReadsVersion()
    {
        var json = """
        {
            "version": "1.0",
            "entity": "contact"
        }
        """;

        var config = JsonSerializer.Deserialize<CsvMappingConfig>(json, JsonOptions);

        Assert.NotNull(config);
        Assert.Equal("1.0", config.Version);
        Assert.Equal("contact", config.Entity);
    }

    [Fact]
    public void Deserialize_WithColumns_ReadsColumnMappings()
    {
        var json = """
        {
            "version": "1.0",
            "columns": {
                "First Name": { "field": "firstname" },
                "Last Name": { "field": "lastname" }
            }
        }
        """;

        var config = JsonSerializer.Deserialize<CsvMappingConfig>(json, JsonOptions);

        Assert.NotNull(config);
        Assert.Equal(2, config.Columns.Count);
        Assert.Equal("firstname", config.Columns["First Name"].Field);
        Assert.Equal("lastname", config.Columns["Last Name"].Field);
    }

    [Fact]
    public void Deserialize_WithLookup_ReadsLookupConfig()
    {
        var json = """
        {
            "version": "1.0",
            "columns": {
                "Account": {
                    "field": "parentcustomerid",
                    "lookup": {
                        "entity": "account",
                        "matchBy": "field",
                        "keyField": "name"
                    }
                }
            }
        }
        """;

        var config = JsonSerializer.Deserialize<CsvMappingConfig>(json, JsonOptions);

        Assert.NotNull(config);
        var accountMapping = config.Columns["Account"];
        Assert.NotNull(accountMapping.Lookup);
        Assert.Equal("account", accountMapping.Lookup.Entity);
        Assert.Equal("field", accountMapping.Lookup.MatchBy);
        Assert.Equal("name", accountMapping.Lookup.KeyField);
    }

    [Fact]
    public void Deserialize_WithMetadata_ReadsGeneratedFields()
    {
        var json = """
        {
            "version": "1.0",
            "columns": {
                "Name": {
                    "field": "name",
                    "_status": "auto-matched",
                    "_note": "Remove if correct"
                }
            }
        }
        """;

        var config = JsonSerializer.Deserialize<CsvMappingConfig>(json, JsonOptions);

        Assert.NotNull(config);
        var nameMapping = config.Columns["Name"];
        Assert.Equal("auto-matched", nameMapping.Status);
        Assert.Equal("Remove if correct", nameMapping.Note);
    }

    [Fact]
    public void Deserialize_WithUnknownFields_PreservesInExtensionData()
    {
        var json = """
        {
            "version": "1.0",
            "customField": "customValue"
        }
        """;

        var config = JsonSerializer.Deserialize<CsvMappingConfig>(json, JsonOptions);

        Assert.NotNull(config);
        Assert.NotNull(config.ExtensionData);
        Assert.True(config.ExtensionData.ContainsKey("customField"));
    }

    #endregion

    #region Round-Trip Tests

    [Fact]
    public void RoundTrip_FullConfig_PreservesAllFields()
    {
        var original = new CsvMappingConfig
        {
            Schema = CsvMappingConfig.SchemaUrl,
            Version = "1.0",
            Entity = "account",
            GeneratedAt = new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.Zero),
            Columns = new Dictionary<string, ColumnMappingEntry>
            {
                ["Name"] = new ColumnMappingEntry
                {
                    Field = "name",
                    Status = "auto-matched"
                },
                ["Parent"] = new ColumnMappingEntry
                {
                    Field = "parentaccountid",
                    Lookup = new LookupConfig
                    {
                        Entity = "account",
                        MatchBy = "field",
                        KeyField = "name"
                    }
                },
                ["Skip Me"] = new ColumnMappingEntry { Skip = true }
            }
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<CsvMappingConfig>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Schema, deserialized.Schema);
        Assert.Equal(original.Version, deserialized.Version);
        Assert.Equal(original.Entity, deserialized.Entity);
        Assert.Equal(original.Columns.Count, deserialized.Columns.Count);
        Assert.Equal("name", deserialized.Columns["Name"].Field);
        Assert.Equal("account", deserialized.Columns["Parent"].Lookup?.Entity);
        Assert.True(deserialized.Columns["Skip Me"].Skip);
    }

    #endregion

    #region LookupConfig Tests

    [Fact]
    public void LookupConfig_DefaultMatchBy_IsGuid()
    {
        var lookup = new LookupConfig { Entity = "account" };
        Assert.Equal("guid", lookup.MatchBy);
    }

    #endregion
}
