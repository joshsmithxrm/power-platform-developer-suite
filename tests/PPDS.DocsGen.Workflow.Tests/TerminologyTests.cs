using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace PPDS.DocsGen.Workflow.Tests;

/// <summary>
/// Enforces Core Requirement 10 / AC-24: the bare word "SDK" may only appear
/// in generated prose when it is part of a Microsoft or Dataverse reference
/// (e.g. "Microsoft SDK", "Dataverse SDK"). Anywhere else we say "libraries"
/// or an equivalent PPDS-native term.
/// </summary>
public class TerminologyTests
{
    // Bare "SDK" token — intentionally case-sensitive so we don't flag
    // unrelated lowercase words that happen to contain those letters.
    private static readonly Regex SdkToken = new(@"\bSDK\b", RegexOptions.Compiled);

    // Hard-coded window of tokens to accept around an SDK occurrence. Five
    // words in either direction matches the plan §2.7 exclusion rule.
    private const int AdjacentWordWindow = 5;

    [Fact]
    public void NoSdkInProse()
    {
        var repoRoot = RepoRoot.Find();
        var fixtureRoots = WorkflowFixtures.ExpectedRoots(repoRoot);
        fixtureRoots.Should().NotBeEmpty(
            "at least one generator's Fixtures/Expected directory must exist — without them there is nothing to validate");

        var violations = new List<string>();
        foreach (var root in fixtureRoots)
        {
            foreach (var md in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
            {
                ScanFile(md, violations);
            }
        }

        violations.Should().BeEmpty(
            "bare \"SDK\" is banned in generated prose (Core Requirement 10). " +
            "Allowed forms: Microsoft SDK, Dataverse SDK, or inside inline/fenced code.\n" +
            string.Join('\n', violations));
    }

    private static void ScanFile(string path, List<string> violations)
    {
        var lines = File.ReadAllLines(path);
        var inFence = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Toggle fenced-code state on any line that starts (after
            // optional indent) with three or more backticks. The fence
            // fence line itself is never scanned for SDK because markdown
            // treats it as the delimiter, not content.
            if (IsFenceDelimiter(line))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                continue;
            }

            // Blockquote lines are skipped wholesale — they are typically
            // quoting Microsoft documentation verbatim and we don't want
            // to police those quotes. Constitution I6 wants deterministic
            // checks, and "quoted content" is exactly where bare SDK is
            // acceptable.
            if (IsBlockquote(line))
            {
                continue;
            }

            foreach (Match match in SdkToken.Matches(line))
            {
                if (IsInsideInlineCode(line, match.Index))
                {
                    continue;
                }

                if (HasAdjacentAllowedWord(line, match.Index, match.Length))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), path);
                violations.Add($"{relative}:{i + 1}: {line.Trim()}");
            }
        }
    }

    private static bool IsFenceDelimiter(string line)
    {
        // Accept up to three leading spaces (CommonMark), then three or more
        // backticks. Anything after the backticks (language tag) is ignored.
        var trimmed = line.TrimStart(' ');
        if (line.Length - trimmed.Length > 3) return false;
        return trimmed.StartsWith("```", StringComparison.Ordinal);
    }

    private static bool IsBlockquote(string line)
    {
        var trimmed = line.TrimStart(' ');
        return trimmed.StartsWith('>');
    }

    /// <summary>
    /// Counts backticks to the left of <paramref name="index"/>. An odd count
    /// means we're sitting inside an inline code span and should skip the
    /// match. We intentionally don't try to match literal backticks inside
    /// double-backtick spans — the generators only emit single-backtick
    /// inline code in prose, and fenced code is already excluded above.
    /// </summary>
    private static bool IsInsideInlineCode(string line, int index)
    {
        var count = 0;
        for (var i = 0; i < index; i++)
        {
            if (line[i] == '`') count++;
        }
        return count % 2 == 1;
    }

    /// <summary>
    /// Returns true when "Microsoft" or "Dataverse" appears within
    /// <see cref="AdjacentWordWindow"/> tokens to either side of the match.
    /// Tokenisation is whitespace-split — good enough because we only care
    /// about literal adjacency of distinctive words, not grammar.
    /// </summary>
    private static bool HasAdjacentAllowedWord(string line, int matchIndex, int matchLength)
    {
        // Build a token list with their starting index into the line so we
        // can locate which token holds the match.
        var tokens = new List<(string Text, int Start)>();
        var i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length) break;

            var start = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
            tokens.Add((line.Substring(start, i - start), start));
        }

        // Find the token index containing the match.
        var matchEnd = matchIndex + matchLength;
        var matchToken = -1;
        for (var t = 0; t < tokens.Count; t++)
        {
            var tokenEnd = tokens[t].Start + tokens[t].Text.Length;
            if (tokens[t].Start <= matchIndex && matchEnd <= tokenEnd)
            {
                matchToken = t;
                break;
            }
        }

        if (matchToken < 0)
        {
            return false;
        }

        var lo = Math.Max(0, matchToken - AdjacentWordWindow);
        var hi = Math.Min(tokens.Count - 1, matchToken + AdjacentWordWindow);
        for (var t = lo; t <= hi; t++)
        {
            if (t == matchToken) continue;
            if (ContainsAllowed(tokens[t].Text)) return true;
        }

        return false;
    }

    private static bool ContainsAllowed(string token)
    {
        // Case-sensitive on the initial capital — "microsoft" lowercase is
        // almost never the proper noun we care about. Token may carry
        // trailing punctuation (e.g. "Microsoft,"), which still matches.
        return token.Contains("Microsoft", StringComparison.Ordinal)
            || token.Contains("Dataverse", StringComparison.Ordinal);
    }
}
