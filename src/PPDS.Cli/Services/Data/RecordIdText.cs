using System;

namespace PPDS.Cli.Services.Data;

/// <summary>
/// The one spelling of a record id that the data commands accept, checked character by character before anything is
/// parsed (#1915). .NET's GUID parser accepts more than it should for a delete: <c>+</c> or <c>0x</c> inside a block is
/// read as hex digits (so <c>+ccccccc-…</c> becomes <c>0ccccccc-…</c>, a different record), even in its exact "D" mode.
/// So the characters decide, never the parser.
/// </summary>
internal static class RecordIdText
{
    /// <summary>
    /// The rule, as stated in the option help: 36 characters, hexadecimal digits (either case) with hyphens at positions
    /// 9, 14, 19 and 24 (8-4-4-4-12), optionally inside one pair of braces; nothing else, no white space.
    /// </summary>
    public const string Rule =
        "an id is 36 characters, hexadecimal digits with hyphens as 8-4-4-4-12 (either letter case), optionally in " +
        "one pair of braces; nothing else, no spaces";

    /// <summary>
    /// Reads <paramref name="text"/> as a record id under <see cref="Rule"/>. The empty id (all zeros) is not a record
    /// id and is refused.
    /// </summary>
    /// <param name="text">The text, exactly as given (it is not trimmed).</param>
    /// <param name="id">The id when the text is one.</param>
    /// <returns>True when the text is exactly one record id.</returns>
    public static bool TryParse(string? text, out Guid id)
    {
        id = Guid.Empty;
        if (text is null)
        {
            return false;
        }

        var span = text.AsSpan();
        if (span.Length == 38)
        {
            if (span[0] != '{' || span[37] != '}')
            {
                return false;
            }

            span = span[1..37];
        }

        if (span.Length != 36)
        {
            return false;
        }

        for (var i = 0; i < 36; i++)
        {
            var c = span[i];
            var hyphen = i is 8 or 13 or 18 or 23;
            if (hyphen ? c != '-' : !char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        // Only now, with the characters known to be exactly 8-4-4-4-12 hex digits, does the parser convert them.
        id = Guid.ParseExact(span, "D");
        return id != Guid.Empty;
    }
}
