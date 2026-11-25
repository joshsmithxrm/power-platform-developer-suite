using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.Environment;

/// <summary>
/// Reads an environment type typed by a user: a member name in any case, with surrounding spaces ignored. A number
/// (<c>1</c>, <c>"3"</c>) or a list of names (<c>Production,Sandbox</c>) is refused, where
/// <see cref="System.Enum.TryParse{TEnum}(string, bool, out TEnum)"/> would turn it into a member by position or by
/// combining values (#1829). The CLI's environment-config <c>--type</c> option, its environment-type command and the
/// daemon's <c>env/config/set</c> all use this, so they give the same verdict.
/// </summary>
internal static class EnvironmentTypeNames
{
    /// <summary>Whether <paramref name="text"/>, trimmed, is a member name of <see cref="EnvironmentType"/>.</summary>
    public static bool TryParse(string? text, out EnvironmentType type)
        => EnumMemberName.TryParse(text?.Trim(), out type);

    /// <summary>The message for text that names no type; it lists the names.</summary>
    public static string InvalidMessage(string? text)
        => $"Invalid environment type '{text}'. Valid values: {EnumMemberName.List<EnvironmentType>()}.";

    /// <summary>
    /// The type <paramref name="text"/> names, or null when it is null or blank. Anything else throws
    /// <see cref="PpdsException"/> with <see cref="ErrorCodes.Validation.InvalidArguments"/>.
    /// </summary>
    public static EnvironmentType? ParseOptional(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (TryParse(text, out var type))
            return type;
        throw new PpdsException(ErrorCodes.Validation.InvalidArguments, InvalidMessage(text));
    }
}
