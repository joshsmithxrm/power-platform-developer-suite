using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PPDS.DocsGen.Smoke;

/// <summary>
/// Extracts fenced <c>csharp</c> code blocks from a ppds-docs checkout,
/// wraps each according to deterministic form-detection rules, and compiles
/// them in-memory with Roslyn against a supplied directory of reference
/// assemblies. Produces a structured failure list with original-markdown
/// file:line coordinates for every CS-diagnostic emitted.
/// </summary>
public static class SmokeRunner
{
    /// <summary>Opening fence: optional leading whitespace, three backticks,
    /// optional space, then literal "csharp" as a word. Captures the rest of
    /// the line (info string) for ignore-marker inspection.</summary>
    private static readonly Regex OpeningFence = new(
        @"^(?<indent>\s*)```\s?csharp\b(?<info>.*)$",
        RegexOptions.Compiled);

    private static readonly string[] StandardUsings =
    {
        "System",
        "System.Linq",
        "System.Threading.Tasks",
        "System.Collections.Generic",
        "PPDS.Dataverse",
        "PPDS.Migration",
        "PPDS.Auth",
        "PPDS.Plugins",
    };

    public record Failure(string File, int Line, string Message);

    public record Summary(
        int Total,
        int Passed,
        int Failed,
        int Skipped,
        IReadOnlyList<Failure> Failures);

    public record Block(
        string File,
        int StartLineInFile,
        bool Skip,
        string Content);

    /// <summary>
    /// Walk <paramref name="docsRoot"/>/docs/ recursively for *.md / *.mdx
    /// files, returning every fenced csharp block found. File order is the
    /// deterministic <see cref="StringComparer.Ordinal"/> sort of the full
    /// path so that summary output is stable across runs and platforms.
    /// </summary>
    public static IReadOnlyList<Block> DiscoverBlocks(string docsRoot)
    {
        var results = new List<Block>();
        var docsDir = Path.Combine(docsRoot, "docs");
        if (!Directory.Exists(docsDir))
        {
            return results;
        }

        var files = Directory.EnumerateFiles(docsDir, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    || p.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal);

        foreach (var path in files)
        {
            var lines = File.ReadAllLines(path);
            ExtractBlocks(path, lines, results);
        }

        return results;
    }

    private static void ExtractBlocks(string path, string[] lines, List<Block> into)
    {
        var i = 0;
        while (i < lines.Length)
        {
            var match = OpeningFence.Match(lines[i]);
            if (!match.Success)
            {
                i++;
                continue;
            }

            var info = match.Groups["info"].Value;
            var skip = info.Contains("// ignore-smoke", StringComparison.Ordinal);

            // Scan forward for a closing fence (``` alone on a line, optionally indented).
            var contentLines = new List<string>();
            var j = i + 1;
            var closed = false;
            while (j < lines.Length)
            {
                if (Regex.IsMatch(lines[j], @"^\s*```\s*$"))
                {
                    closed = true;
                    break;
                }

                contentLines.Add(lines[j]);
                j++;
            }

            if (closed)
            {
                into.Add(new Block(
                    File: path,
                    StartLineInFile: i + 1, // 1-based line of opening fence
                    Skip: skip,
                    Content: string.Join("\n", contentLines)));
                i = j + 1;
            }
            else
            {
                // Unterminated fence — skip to EOF.
                i = lines.Length;
            }
        }
    }

    /// <summary>
    /// Classifies a block into one of three wrapping forms and emits the
    /// corresponding compilable source. Pure function — same input yields
    /// byte-identical output.
    /// </summary>
    public static string WrapBlock(string block)
    {
        var firstMeaningful = FirstNonWhitespaceLine(block);
        if (IsFormA(firstMeaningful))
        {
            return block;
        }

        if (IsFormB(block, firstMeaningful))
        {
            return block;
        }

        // Form C: method-body wrap.
        var sb = new StringBuilder();
        foreach (var u in StandardUsings)
        {
            sb.Append("using ").Append(u).Append(';').Append('\n');
        }

        sb.Append('\n');
        sb.Append("public class SmokeSample\n");
        sb.Append("{\n");
        sb.Append("    public async System.Threading.Tasks.Task Run()\n");
        sb.Append("    {\n");
        sb.Append(block);
        if (!block.EndsWith('\n'))
        {
            sb.Append('\n');
        }

        sb.Append("    }\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>Counts the lines within the wrapping prologue for Form C so
    /// the Roslyn diagnostic's reported line can be back-mapped to the
    /// original markdown coordinate.</summary>
    public static int WrapPrologueLineOffset(string block)
    {
        var firstMeaningful = FirstNonWhitespaceLine(block);
        if (IsFormA(firstMeaningful) || IsFormB(block, firstMeaningful))
        {
            return 0;
        }

        // Form C wrap: N using-lines + 1 blank + "public class..." + "{" +
        // "    public async..." + "    {"  == StandardUsings.Length + 5 lines
        // before the first user line.
        return StandardUsings.Length + 5;
    }

    private static string FirstNonWhitespaceLine(string block)
    {
        foreach (var raw in block.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line.TrimStart();
            }
        }

        return string.Empty;
    }

    private static bool IsFormA(string firstMeaningful) =>
        firstMeaningful.StartsWith("using ", StringComparison.Ordinal)
        || firstMeaningful.StartsWith("namespace ", StringComparison.Ordinal)
        || firstMeaningful.StartsWith("namespace\t", StringComparison.Ordinal);

    private static bool IsFormB(string block, string firstMeaningful)
    {
        if (string.IsNullOrEmpty(firstMeaningful))
        {
            return false;
        }

        if (IsFormA(firstMeaningful))
        {
            return false;
        }

        // A top-level declaration (class / interface / struct / record / enum
        // / delegate) anywhere in the block means this is not a top-level-
        // statement program. Check for a declaration keyword at either start-
        // of-line or after a leading access modifier.
        var pattern = @"(^|\n)\s*(public\s+|internal\s+|private\s+|protected\s+|sealed\s+|static\s+|abstract\s+|partial\s+)*(class|interface|struct|record|enum|delegate)\b";
        if (Regex.IsMatch(block, pattern))
        {
            return false;
        }

        // Heuristic: first line must look like a statement, not a free-standing
        // member definition. We treat it as Form B unless it looks like an
        // expression with no verb (e.g. just "foo"). Practically, anything with
        // a semicolon, a var/Console./await/return token, or an assignment is
        // a statement.
        return true;
    }

    /// <summary>
    /// Runs the end-to-end extract-wrap-compile pipeline and returns a
    /// summary suitable for JSON serialization.
    /// </summary>
    public static Summary Run(string docsRoot, string assembliesDir, TextWriter stderr)
    {
        var blocks = DiscoverBlocks(docsRoot);
        var references = BuildReferences(assembliesDir);

        var failures = new List<Failure>();
        int passed = 0, failed = 0, skipped = 0;

        foreach (var block in blocks)
        {
            if (block.Skip)
            {
                skipped++;
                stderr.WriteLine($"smoke: SKIP {block.File}:{block.StartLineInFile}");
                continue;
            }

            var wrapped = WrapBlock(block.Content);
            var prologueOffset = WrapPrologueLineOffset(block.Content);
            var diagnostics = Compile(wrapped, references);
            var blockFailures = diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error
                    && d.Id.StartsWith("CS", StringComparison.Ordinal))
                .Select(d => ToFailure(d, block, prologueOffset))
                .ToList();

            if (blockFailures.Count == 0)
            {
                passed++;
                stderr.WriteLine($"smoke: PASS {block.File}:{block.StartLineInFile}");
            }
            else
            {
                failed++;
                foreach (var f in blockFailures)
                {
                    stderr.WriteLine($"smoke: FAIL {f.File}:{f.Line} {f.Message}");
                }

                failures.AddRange(blockFailures);
            }
        }

        return new Summary(
            Total: blocks.Count,
            Passed: passed,
            Failed: failed,
            Skipped: skipped,
            Failures: failures);
    }

    private static Failure ToFailure(Diagnostic d, Block block, int prologueOffset)
    {
        // Roslyn line spans are 0-based; original markdown lines are 1-based.
        var roslynLine = d.Location.GetLineSpan().StartLinePosition.Line;
        var withinBlock = roslynLine - prologueOffset;
        if (withinBlock < 0)
        {
            withinBlock = 0;
        }

        // Opening fence is at StartLineInFile; first content line is +1.
        var fileLine = block.StartLineInFile + 1 + withinBlock;
        return new Failure(
            File: block.File,
            Line: fileLine,
            Message: $"{d.Id}: {d.GetMessage()}");
    }

    private static IReadOnlyList<MetadataReference> BuildReferences(string assembliesDir)
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Trusted platform assemblies — pulls in mscorlib / System.Runtime /
        // System.Linq / etc. without us having to guess the SDK install path.
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrEmpty(tpa))
        {
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    continue;
                }

                var name = Path.GetFileName(path);
                if (seen.Add(name))
                {
                    refs.Add(MetadataReference.CreateFromFile(path));
                }
            }
        }

        if (Directory.Exists(assembliesDir))
        {
            foreach (var path in Directory.EnumerateFiles(assembliesDir, "*.dll", SearchOption.TopDirectoryOnly)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path);
                if (seen.Add(name))
                {
                    refs.Add(MetadataReference.CreateFromFile(path));
                }
            }
        }

        return refs;
    }

    private static ImmutableArray<Diagnostic> Compile(
        string source,
        IReadOnlyList<MetadataReference> references)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Latest));

        var compilation = CSharpCompilation.Create(
            assemblyName: "SmokeCompilation",
            syntaxTrees: new[] { tree },
            references: references,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true));

        return compilation.GetDiagnostics();
    }
}
