using System.Text;

namespace PPDS.Cli.Infrastructure.Output;

/// <summary>
/// Makes stored or user-supplied text safe to print to a terminal. Control characters (escape sequences,
/// carriage returns, backspaces, C1 controls) can rewrite what the terminal shows, so a statement echoed
/// "for review" could display something different from what actually runs.
/// </summary>
public static class TerminalText
{
    /// <summary>
    /// Replaces C0 controls (other than newline and tab), DEL, C1 controls, Unicode line/paragraph separators,
    /// bidirectional overrides, isolates and marks, and zero-width characters with a visible <c>\uXXXX</c> form.
    /// Printable text is unchanged.
    /// </summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        StringBuilder? sb = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                // A CRLF line ending is harmless; show it as a plain newline.
                sb ??= new StringBuilder(text.Length + 16).Append(text, 0, i);
            }
            else if (NeedsEscape(c))
            {
                sb ??= new StringBuilder(text.Length + 16).Append(text, 0, i);
                sb.Append("\\u").Append(((int)c).ToString("X4"));
            }
            else
            {
                sb?.Append(c);
            }
        }

        return sb?.ToString() ?? text;
    }

    private static bool NeedsEscape(char c)
        => (c < 0x20 && c != '\n' && c != '\t')
           || c == 0x7F
           || (c >= 0x80 && c <= 0x9F)
           || c == 0x2028 || c == 0x2029          // line / paragraph separators
           || (c >= 0x202A && c <= 0x202E)        // bidirectional embeddings and overrides
           || (c >= 0x2066 && c <= 0x2069)        // bidirectional isolates
           || c == 0x200E || c == 0x200F || c == 0x061C // left-to-right, right-to-left and Arabic letter marks
           || (c >= 0x200B && c <= 0x200D)        // zero-width space, non-joiner, joiner
           || c == 0x2060 || c == 0xFEFF;         // word joiner, zero-width no-break space
}
