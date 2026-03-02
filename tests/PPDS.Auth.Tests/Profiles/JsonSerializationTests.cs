using System.Text.Json;
using FluentAssertions;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Profiles;

public class JsonSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    [Fact]
    public void QuerySafetySettings_RoundTrip_PreservesAllProperties()
    {
        var original = new QuerySafetySettings
        {
            WarnInsertThreshold = 50,
            WarnUpdateThreshold = 25,
            WarnDeleteThreshold = 10,
            PreventUpdateWithoutWhere = false,
            PreventDeleteWithoutWhere = false,
            DmlBatchSize = 200,
            MaxResultRows = 5000,
            QueryTimeoutSeconds = 600,
            UseTdsEndpoint = true,
            BypassCustomPlugins = BypassPluginMode.Synchronous,
            BypassPowerAutomateFlows = true,
            MaxParallelism = 8,
            UseBulkDelete = true,
            DateTimeMode = DateTimeMode.Local,
            ShowFetchXmlInExplain = false,
            MaxPageRetrievals = 500
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<QuerySafetySettings>(json, JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.WarnInsertThreshold.Should().Be(50);
        deserialized.WarnUpdateThreshold.Should().Be(25);
        deserialized.WarnDeleteThreshold.Should().Be(10);
        deserialized.PreventUpdateWithoutWhere.Should().BeFalse();
        deserialized.PreventDeleteWithoutWhere.Should().BeFalse();
        deserialized.DmlBatchSize.Should().Be(200);
        deserialized.MaxResultRows.Should().Be(5000);
        deserialized.QueryTimeoutSeconds.Should().Be(600);
        deserialized.UseTdsEndpoint.Should().BeTrue();
        deserialized.BypassCustomPlugins.Should().Be(BypassPluginMode.Synchronous);
        deserialized.BypassPowerAutomateFlows.Should().BeTrue();
        deserialized.MaxParallelism.Should().Be(8);
        deserialized.UseBulkDelete.Should().BeTrue();
        deserialized.DateTimeMode.Should().Be(DateTimeMode.Local);
        deserialized.ShowFetchXmlInExplain.Should().BeFalse();
        deserialized.MaxPageRetrievals.Should().Be(500);
    }

    [Fact]
    public void QuerySafetySettings_RoundTrip_WithNullableProperties()
    {
        var original = new QuerySafetySettings
        {
            MaxParallelism = null,
            MaxPageRetrievals = null,
            WarnInsertThreshold = null,
            DateTimeMode = DateTimeMode.EnvironmentTimezone
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<QuerySafetySettings>(json, JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.MaxParallelism.Should().BeNull();
        deserialized.MaxPageRetrievals.Should().BeNull();
        deserialized.WarnInsertThreshold.Should().BeNull();
        deserialized.DateTimeMode.Should().Be(DateTimeMode.EnvironmentTimezone);
    }

    [Fact]
    public void QuerySafetySettings_DateTimeMode_SerializesAsString()
    {
        var settings = new QuerySafetySettings
        {
            DateTimeMode = DateTimeMode.EnvironmentTimezone
        };

        var json = JsonSerializer.Serialize(settings, JsonOptions);

        json.Should().Contain("\"datetime_mode\": \"EnvironmentTimezone\"");
    }

    [Fact]
    public void QuerySafetySettings_DateTimeMode_DeserializesFromString()
    {
        var json = """
        {
            "datetime_mode": "Local"
        }
        """;

        var settings = JsonSerializer.Deserialize<QuerySafetySettings>(json, JsonOptions);

        settings.Should().NotBeNull();
        settings!.DateTimeMode.Should().Be(DateTimeMode.Local);
    }

    [Fact]
    public void QuerySafetySettings_BypassPluginMode_SerializesAsString()
    {
        var settings = new QuerySafetySettings
        {
            BypassCustomPlugins = BypassPluginMode.All
        };

        var json = JsonSerializer.Serialize(settings, JsonOptions);

        json.Should().Contain("\"bypass_custom_plugins\": \"All\"");
    }

    [Fact]
    public void EnvironmentConfig_RoundTrip_WithProtectionLevel()
    {
        var original = new EnvironmentConfig
        {
            Url = "https://test.crm.dynamics.com/",
            Label = "Test Environment",
            Type = EnvironmentType.Production,
            Color = EnvironmentColor.Red,
            Protection = ProtectionLevel.Production,
            SafetySettings = new QuerySafetySettings
            {
                MaxParallelism = 4,
                UseBulkDelete = true,
                DateTimeMode = DateTimeMode.Utc,
                ShowFetchXmlInExplain = true,
                MaxPageRetrievals = 100
            }
        };

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<EnvironmentConfig>(json, JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.Url.Should().Be("https://test.crm.dynamics.com/");
        deserialized.Label.Should().Be("Test Environment");
        deserialized.Type.Should().Be(EnvironmentType.Production);
        deserialized.Color.Should().Be(EnvironmentColor.Red);
        deserialized.Protection.Should().Be(ProtectionLevel.Production);
        deserialized.SafetySettings.Should().NotBeNull();
        deserialized.SafetySettings!.MaxParallelism.Should().Be(4);
        deserialized.SafetySettings.UseBulkDelete.Should().BeTrue();
        deserialized.SafetySettings.DateTimeMode.Should().Be(DateTimeMode.Utc);
        deserialized.SafetySettings.ShowFetchXmlInExplain.Should().BeTrue();
        deserialized.SafetySettings.MaxPageRetrievals.Should().Be(100);
    }

    [Fact]
    public void EnvironmentConfig_Protection_SerializesAsString()
    {
        var config = new EnvironmentConfig
        {
            Url = "https://test.crm.dynamics.com/",
            Protection = ProtectionLevel.Test
        };

        var json = JsonSerializer.Serialize(config, JsonOptions);

        json.Should().Contain("\"protection\": \"Test\"");
    }

    [Fact]
    public void EnvironmentConfig_Protection_DeserializesFromString()
    {
        var json = """
        {
            "url": "https://test.crm.dynamics.com/",
            "protection": "Development"
        }
        """;

        var config = JsonSerializer.Deserialize<EnvironmentConfig>(json, JsonOptions);

        config.Should().NotBeNull();
        config!.Protection.Should().Be(ProtectionLevel.Development);
    }

    [Fact]
    public void EnvironmentConfig_Protection_NullByDefault()
    {
        var config = new EnvironmentConfig
        {
            Url = "https://test.crm.dynamics.com/"
        };

        var json = JsonSerializer.Serialize(config, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<EnvironmentConfig>(json, JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.Protection.Should().BeNull();
    }

    [Fact]
    public void QuerySafetySettings_DefaultValues_RoundTrip()
    {
        var original = new QuerySafetySettings();

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<QuerySafetySettings>(json, JsonOptions);

        deserialized.Should().NotBeNull();
        deserialized!.PreventUpdateWithoutWhere.Should().BeTrue();
        deserialized.PreventDeleteWithoutWhere.Should().BeTrue();
        deserialized.UseTdsEndpoint.Should().BeFalse();
        deserialized.BypassCustomPlugins.Should().Be(BypassPluginMode.None);
        deserialized.BypassPowerAutomateFlows.Should().BeFalse();
        deserialized.UseBulkDelete.Should().BeFalse();
        deserialized.DateTimeMode.Should().Be(DateTimeMode.Utc);
        deserialized.ShowFetchXmlInExplain.Should().BeTrue();
    }
}
