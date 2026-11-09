using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PPDS.Dataverse.Metadata.Authoring;

// Use the normal serializer for each existing field so caller naming, casing, number
// handling and unmapped-member settings retain their ordinary POCO behavior.
internal sealed class OptionDefinitionJsonConverter : JsonConverter<OptionDefinition>
{
    public OptionDefinitionJsonConverter() { }

    public override OptionDefinition? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var wire = JsonSerializer.Deserialize<WireOption>(ref reader, options);
        if (wire == null) return null;
        var result = new OptionDefinition { Label = wire.Label, Description = wire.Description, Color = wire.Color };
        if (wire.HasValue) result.Value = wire.Value;
        return result;
    }

    public override void Write(Utf8JsonWriter writer, OptionDefinition value, JsonSerializerOptions options)
    {
        var wire = new WireOption { Label = value.Label, Value = value.Value, Description = value.Description, Color = value.Color };
        var element = JsonSerializer.SerializeToElement(wire, options);
        var valueName = options.PropertyNamingPolicy?.ConvertName(nameof(OptionDefinition.Value)) ?? nameof(OptionDefinition.Value);
        writer.WriteStartObject();
        foreach (var property in element.EnumerateObject())
        {
            if (value.HasExplicitValue || !property.NameEquals(valueName)) property.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private sealed class WireOption
    {
        private int _value;
        internal bool HasValue { get; private set; }
        public WireOption() { }
        public string Label { get; set; } = "";
        // Explicit zero must survive a default-value ignore policy; omission is
        // removed by the enclosing converter, never inferred from the number.
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public int Value { get => _value; set { _value = value; HasValue = true; } }
        public string? Description { get; set; }
        public string? Color { get; set; }
    }
}
