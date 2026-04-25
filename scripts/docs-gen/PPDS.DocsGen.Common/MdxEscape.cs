using System.Text;

namespace PPDS.DocsGen.Common;

/// <summary>
/// Shared utility used by all docs-gen generators to produce MDX-safe markdown.
/// </summary>
/// <remarks>
/// MDX parses angle brackets as JSX and curly braces as expressions. Any
/// <c>&lt;</c>, <c>&gt;</c>, <c>{</c>, or <c>}</c> in prose that is not
/// already inside a fenced code block or inline-code span must be escaped or
/// strict-MDX will reject the document (see AC-23).
/// </remarks>
public static class MdxEscape
{
    /// <summary>
    /// HTML-entity-encodes <c>&amp;</c>, <c>&lt;</c>, and <c>&gt;</c> outside
    /// fenced code blocks and inline-code spans. Regions inside fences or
    /// backticks are passed through verbatim so that code samples render
    /// unchanged.
    /// </summary>
    /// <remarks>
    /// Walks the input tracking two nested states:
    /// <list type="number">
    /// <item>Whether we are inside a fenced code block (toggled by a line
    /// whose first non-whitespace characters are three backticks).</item>
    /// <item>While outside a fence, whether we are inside an inline-code span
    /// (toggled by each <c>`</c>).</item>
    /// </list>
    /// Outside both, <c>&amp;</c> is escaped first (ordering matters — otherwise
    /// later escapes would double-escape their own ampersands).
    /// </remarks>
    public static string Prose(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return raw;
        }

        var sb = new StringBuilder(raw.Length);
        var inFence = false;
        var inInlineCode = false;
        var i = 0;

        while (i < raw.Length)
        {
            // Fence toggle: a line beginning (start-of-input or just after '\n')
            // whose first non-whitespace run starts with ```. Accept optional
            // leading spaces and an optional info string on the opening fence.
            if (IsAtLineStart(raw, i) && IsFenceMarker(raw, i, out var lineEnd))
            {
                // Pass the whole fence marker line through verbatim.
                sb.Append(raw, i, lineEnd - i);
                inFence = !inFence;
                // If we were tracking inline code, a fence resets the line and
                // inline-code state cannot cross a newline anyway.
                inInlineCode = false;
                i = lineEnd;
                continue;
            }

            var c = raw[i];

            if (inFence)
            {
                sb.Append(c);
                i++;
                continue;
            }

            // Outside a fence: backticks toggle inline-code state. Inline code
            // does not span newlines in CommonMark; reset on newline.
            if (c == '\n')
            {
                inInlineCode = false;
                sb.Append(c);
                i++;
                continue;
            }

            if (c == '`')
            {
                inInlineCode = !inInlineCode;
                sb.Append(c);
                i++;
                continue;
            }

            if (inInlineCode)
            {
                sb.Append(c);
                i++;
                continue;
            }

            // Prose region — escape. Order matters: & first.
            switch (c)
            {
                case '&':
                    sb.Append("&amp;");
                    break;
                case '<':
                    sb.Append("&lt;");
                    break;
                case '>':
                    sb.Append("&gt;");
                    break;
                case '{':
                    sb.Append("\\{");
                    break;
                case '}':
                    sb.Append("\\}");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Escapes <c>&lt;</c> and <c>&gt;</c> for use in markdown headings and
    /// other single-line prose contexts where fenced/inline code blocks are
    /// not expected. This is lighter-weight than <see cref="Prose"/> because
    /// headings never contain fenced blocks or inline-code spans — only the
    /// raw angle brackets need escaping.
    /// </summary>
    public static string Heading(string raw)
    {
        return Prose(raw);
    }

    /// <summary>
    /// Canonical, deterministic inline-code wrapper (AC-19). For any given
    /// input, produces exactly one output string that parses correctly under
    /// CommonMark and MDX.
    /// </summary>
    /// <remarks>
    /// CommonMark inline-code delimiters must contain a run of backticks that
    /// is not present anywhere in the content. The adaptive rule:
    /// <list type="bullet">
    /// <item>Find the longest run of consecutive backticks in <paramref name="raw"/> (call it <c>N</c>).</item>
    /// <item>Use <c>N+1</c> backticks as the delimiter — guaranteed not to appear inside the content.</item>
    /// <item>If the content starts or ends with a backtick, pad that side with a single space so the first/last backtick is part of the content, not a longer delimiter run.</item>
    /// </list>
    /// This handles arbitrary backtick patterns safely (single, double, triple,
    /// or longer runs) without ever falling back to a fenced block — inline code
    /// is never multi-line in generator output.
    /// </remarks>
    public static string InlineCode(string raw)
    {
        raw ??= string.Empty;

        if (raw.IndexOf('`') < 0)
        {
            return "`" + raw + "`";
        }

        var maxRun = 0;
        var currentRun = 0;
        foreach (var c in raw)
        {
            if (c == '`')
            {
                currentRun++;
                if (currentRun > maxRun) maxRun = currentRun;
            }
            else
            {
                currentRun = 0;
            }
        }

        var delimiter = new string('`', maxRun + 1);
        var padStart = raw[0] == '`' ? " " : string.Empty;
        var padEnd = raw[^1] == '`' ? " " : string.Empty;

        return delimiter + padStart + raw + padEnd + delimiter;
    }

    private static bool IsAtLineStart(string s, int index)
    {
        if (index == 0)
        {
            return true;
        }

        return s[index - 1] == '\n';
    }

    /// <summary>
    /// Detects whether the line beginning at <paramref name="index"/> is a
    /// fence marker (three or more consecutive backticks, optionally preceded
    /// by whitespace, optionally followed by an info string, terminated by a
    /// newline or end-of-input). Returns the index immediately past the
    /// terminating newline (or end-of-input) in <paramref name="lineEnd"/>.
    /// </summary>
    private static bool IsFenceMarker(string s, int index, out int lineEnd)
    {
        lineEnd = index;
        var j = index;

        // Optional leading whitespace (spaces/tabs). CommonMark allows up to
        // three spaces of indent; we accept any run of spaces/tabs.
        while (j < s.Length && (s[j] == ' ' || s[j] == '\t'))
        {
            j++;
        }

        // Need at least three backticks.
        var tickStart = j;
        while (j < s.Length && s[j] == '`')
        {
            j++;
        }

        if (j - tickStart < 3)
        {
            return false;
        }

        // Consume to end of line (inclusive of the \n).
        while (j < s.Length && s[j] != '\n')
        {
            j++;
        }

        if (j < s.Length && s[j] == '\n')
        {
            j++;
        }

        lineEnd = j;
        return true;
    }
}
