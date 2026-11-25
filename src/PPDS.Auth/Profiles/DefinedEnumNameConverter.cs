using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PPDS.Auth.Profiles;

/// <summary>
/// Reads an enum saved in the environments file only by the name of a defined member (any case), and writes
/// it by name. A number (<c>"type": 1</c>), a numeric string (<c>"42"</c>), a list of names
/// (<c>"Production, Development"</c>) or any other text fails the read with a <see cref="JsonException"/>.
/// </summary>
/// <remarks>
/// The type and protection of an environment decide whether a write needs confirmation. The default
/// <see cref="JsonStringEnumConverter"/> accepts integers, including ones that name no member, which the
/// protection resolver would then read as something weaker than Production (#1829). Failing the read keeps
/// every writer closed: the confirmation gate reports that the policy is unavailable, and SQL DML does not
/// run. PPDS has only ever written names, so a number in the file was not written by PPDS. The environment
/// configuration store turns the failure into an <see cref="EnvironmentConfigFileException"/> that names the file,
/// the entry and the field.
/// </remarks>
internal sealed class DefinedEnumNameConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && EnumMemberName.TryParse<TEnum>(reader.GetString(), out var value))
            return value;

        throw new EnumNameJsonException(
            $"'{typeof(TEnum).Name}' must be one of {EnumMemberName.List<TEnum>()}, written as text.",
            Enum.GetNames<TEnum>());
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value))
            throw new JsonException($"'{typeof(TEnum).Name}' value {value} is not a defined member and is not saved.");
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>A value that names no member, with the names that are accepted (read by <see cref="EnvironmentConfigFileException"/>).</summary>
internal sealed class EnumNameJsonException : JsonException
{
    public EnumNameJsonException(string message, string[] acceptedValues) : base(message)
    {
        AcceptedValues = acceptedValues;
    }

    /// <summary>The member names the field accepts.</summary>
    public string[] AcceptedValues { get; }
}
