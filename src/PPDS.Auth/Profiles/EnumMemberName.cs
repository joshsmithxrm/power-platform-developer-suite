using System;

namespace PPDS.Auth.Profiles;

/// <summary>
/// Reads an enum value only from the name of a defined member, in any case. Unlike <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>,
/// it refuses a number (<c>"1"</c>), numeric text and a list of names (<c>"Production, Development"</c>), which
/// <c>Enum.TryParse</c> turns into a member by its position or by combining the members' values (#1829).
/// </summary>
/// <remarks>
/// The environments file, the CLI's environment-config options and the daemon's environment methods all read the
/// environment type and protection through this, so the three cannot disagree on the same text.
/// </remarks>
public static class EnumMemberName
{
    /// <summary>
    /// Returns <see langword="true"/> and the member when <paramref name="text"/> is exactly the name of a defined
    /// member of <typeparamref name="TEnum"/>, compared without case. Surrounding spaces are not trimmed.
    /// </summary>
    /// <typeparam name="TEnum">The enum type.</typeparam>
    /// <param name="text">The text to read.</param>
    /// <param name="value">The member, or the default value when the text names none.</param>
    /// <returns>Whether the text names a member.</returns>
    public static bool TryParse<TEnum>(string? text, out TEnum value) where TEnum : struct, Enum
    {
        if (text is not null)
        {
            foreach (var name in Enum.GetNames<TEnum>())
            {
                if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                {
                    value = Enum.Parse<TEnum>(name);
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    /// <summary>The member names of <typeparamref name="TEnum"/>, separated by commas, for messages.</summary>
    /// <typeparam name="TEnum">The enum type.</typeparam>
    /// <returns>The names in declaration order.</returns>
    public static string List<TEnum>() where TEnum : struct, Enum => string.Join(", ", Enum.GetNames<TEnum>());
}
