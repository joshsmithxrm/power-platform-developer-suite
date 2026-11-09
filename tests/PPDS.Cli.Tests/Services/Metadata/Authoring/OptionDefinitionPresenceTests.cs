using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using PPDS.Cli.Commands.Metadata;
using PPDS.Cli.Commands.Metadata.Attribute;
using PPDS.Dataverse.Metadata.Authoring;
using Xunit;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

[Trait("Category", "Unit")]
public sealed class OptionDefinitionPresenceTests
{
    [Fact]
    [Trait("AC", "AC-111")]
    public void ExistingIntAndConstructorContract_RemainsWithAdditiveReadOnlyPresence()
    {
        var type = typeof(OptionDefinition);
        var value = type.GetProperty("Value")!;
        Assert.Equal(typeof(int), value.PropertyType);
        Assert.True(value.GetMethod!.IsPublic);
        Assert.True(value.SetMethod!.IsPublic);
        Assert.NotNull(type.GetConstructor(Type.EmptyTypes));
        // Reflection keeps this exact intended test compilable against the pre-fix binary surface.
        var presence = type.GetProperty("HasExplicitValue", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(presence);
        Assert.Equal(typeof(bool), presence.PropertyType);
        Assert.Null(presence.SetMethod);
        var legacy = new OptionDefinition { Label = "Existing caller", Value = 0 };
        Assert.Equal(0, legacy.Value);
        Assert.True((bool)presence.GetValue(legacy)!);
        Assert.False((bool)presence.GetValue(new OptionDefinition())!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("AC", "AC-112")]
    public void RoundTrip_OmissionAndExplicitZeroRemainDifferent(bool camelCase)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = camelCase ? JsonNamingPolicy.CamelCase : null };
        var name = camelCase ? "value" : "Value";
        var omitted = new OptionDefinition { Label = "Automatic", Description = "Description", Color = "#123456" };
        var explicitZero = new OptionDefinition { Label = "Zero", Value = 0, Description = "Description", Color = "#123456" };
        var absentJson = JsonSerializer.Serialize(omitted, options);
        var zeroJson = JsonSerializer.Serialize(explicitZero, options);
        using var absent = JsonDocument.Parse(absentJson);
        using var zero = JsonDocument.Parse(zeroJson);
        Assert.False(absent.RootElement.TryGetProperty(name, out _));
        Assert.Equal(0, zero.RootElement.GetProperty(name).GetInt32());
        Assert.Equal(absentJson, JsonSerializer.Serialize(JsonSerializer.Deserialize<OptionDefinition>(absentJson, options), options));
        Assert.Equal(zeroJson, JsonSerializer.Serialize(JsonSerializer.Deserialize<OptionDefinition>(zeroJson, options), options));
        Assert.DoesNotContain("HasExplicitValue", zeroJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Description", JsonSerializer.Deserialize<OptionDefinition>(absentJson, options)!.Description);
        Assert.Equal("#123456", JsonSerializer.Deserialize<OptionDefinition>(absentJson, options)!.Color);
    }

    [Fact]
    [Trait("AC", "AC-112")]
    public void ExplicitZero_SurvivesDefaultValueIgnorePolicy()
    {
        var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new OptionDefinition { Label = "Zero", Value = 0 }, options));
        Assert.Equal(0, json.RootElement.GetProperty("Value").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("AC", "AC-112")]
    public void Read_PreservesCaseSensitivityAndAllExistingFields(bool caseInsensitive)
    {
        const string payload = "{\"lAbEl\":\"Mixed\",\"vAlUe\":7,\"dEsCrIpTiOn\":\"Details\",\"cOlOr\":\"#123456\",\"unknown\":true}";
        var option = JsonSerializer.Deserialize<OptionDefinition>(payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = caseInsensitive })!;
        Assert.Equal(caseInsensitive ? "Mixed" : "", option.Label);
        Assert.Equal(caseInsensitive ? 7 : 0, option.Value);
        Assert.Equal(caseInsensitive ? "Details" : null, option.Description);
        Assert.Equal(caseInsensitive ? "#123456" : null, option.Color);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(option));
        Assert.Equal(caseInsensitive, json.RootElement.TryGetProperty("Value", out _));
        Assert.False(json.RootElement.TryGetProperty("unknown", out _));
    }

    [Theory]
    [InlineData("{\"Value\":null}")]
    [InlineData("{\"Value\":\"7\"}")]
    [InlineData("{\"Value\":2147483648}")]
    [Trait("AC", "AC-112")]
    public void DirectModel_InvalidIntJsonRetainsRejection(string payload) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OptionDefinition>(payload));

    [Fact]
    [Trait("AC", "AC-112")]
    public void Read_RespectsExplicitNumberHandling()
    {
        var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowReadingFromString };
        Assert.Equal(7, JsonSerializer.Deserialize<OptionDefinition>("{\"Value\":\"7\"}", options)!.Value);
    }

    [Fact]
    [Trait("AC", "AC-112")]
    public void UnknownMemberDisallow_RemainsEnforced()
    {
        var options = new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OptionDefinition>("{\"Label\":\"A\",\"Surprise\":1}", options));
    }

    [Fact]
    [Trait("AC", "AC-110")]
    public void GlobalGrammar_PreservesValidLegacyAndAddsBareLabels()
    {
        var options = OptionSetCommand.ParseOptionDefinitions("Automatic,Zero=0,X=Y=718980007,,");
        Assert.Equal(new[] { "Automatic", "Zero", "X=Y" }, options.Select(o => o.Label));
        using var automatic = JsonDocument.Parse(JsonSerializer.Serialize(options[0]));
        Assert.False(automatic.RootElement.TryGetProperty("Value", out _));
        Assert.Equal(0, options[1].Value);
        Assert.Equal(718980007, options[2].Value);
        Assert.Empty(OptionSetCommand.ParseOptionDefinitions(", ,")); // Existing empty/trailing delimiter behavior remains.
    }

    [Theory]
    [InlineData("Broken=abc")]
    [InlineData("=1")]
    [InlineData("Broken=")]
    [Trait("AC", "AC-110")]
    public void GlobalGrammar_MalformedNonemptyDefinitionsRejectInsteadOfDisappearing(string raw) =>
        Assert.Throws<FormatException>(() => OptionSetCommand.ParseOptionDefinitions(raw));

    [Fact]
    [Trait("AC", "AC-110")]
    public void OptionsFile_OmittedAndExistingNullCompatibilityAreDistinctFromExplicitZero()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ppds-option-presence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "options.json");
        try
        {
            File.WriteAllText(path, "[{\"Label\":\"Missing\",\"Color\":\"#123456\"},{\"Label\":\"Existing null\",\"Value\":null},{\"lAbEl\":\"Zero\",\"vAlUe\":0}]");
            var options = AttributeCommandGroup.ParseOptionSpecs(null, path, null)!;
            Assert.Equal(3, options.Length);
            Assert.Equal("#123456", options[0].Color);
            Assert.Equal("Zero", options[2].Label);
            using var missing = JsonDocument.Parse(JsonSerializer.Serialize(options[0]));
            using var existingNull = JsonDocument.Parse(JsonSerializer.Serialize(options[1]));
            using var zero = JsonDocument.Parse(JsonSerializer.Serialize(options[2]));
            Assert.False(missing.RootElement.TryGetProperty("Value", out _));
            Assert.False(existingNull.RootElement.TryGetProperty("Value", out _));
            Assert.Equal(0, zero.RootElement.GetProperty("Value").GetInt32());
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }
}
