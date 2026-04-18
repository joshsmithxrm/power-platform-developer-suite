using System.Text.Json;
using PPDS.DocsGen.Cli;
using PPDS.DocsGen.Common;

// Entry point for the cli-reflect generator. Usage:
//   cli-reflect --assembly <path> --output <dir>
// Writes markdown files under <output>, prints a JSON list of generated file
// paths to stdout (stable sort), and all diagnostics to stderr.

try
{
    var parsed = ParseArgs(args);
    if (parsed is null)
    {
        return 1;
    }

    var (assemblyPath, outputRoot) = parsed.Value;

    Console.Error.WriteLine($"cli-reflect: reading {assemblyPath}");
    var generator = new CliReferenceGenerator();
    var result = await generator.GenerateAsync(
        new GenerationInput(assemblyPath, outputRoot),
        CancellationToken.None);

    Directory.CreateDirectory(outputRoot);
    foreach (var file in result.Files)
    {
        var full = Path.Combine(outputRoot, file.RelativePath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(full, file.Contents);
    }

    foreach (var diag in result.Diagnostics)
    {
        Console.Error.WriteLine($"cli-reflect: {diag}");
    }

    // Emit deterministic JSON list of produced files to stdout (Constitution I1 —
    // stdout carries data, stderr carries status).
    var payload = new
    {
        files = result.Files
            .Select(f => f.RelativePath)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray(),
    };

    Console.WriteLine(JsonSerializer.Serialize(
        payload,
        new JsonSerializerOptions { WriteIndented = false }));

    Console.Error.WriteLine($"cli-reflect: wrote {result.Files.Count} file(s)");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"cli-reflect: error: {ex.Message}");
    return 1;
}

static (string Assembly, string Output)? ParseArgs(string[] args)
{
    string? assembly = null;
    string? output = null;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--assembly" when i + 1 < args.Length:
                assembly = args[++i];
                break;
            case "--output" when i + 1 < args.Length:
                output = args[++i];
                break;
            default:
                Console.Error.WriteLine($"cli-reflect: unknown argument '{args[i]}'");
                return null;
        }
    }

    if (string.IsNullOrWhiteSpace(assembly) || string.IsNullOrWhiteSpace(output))
    {
        Console.Error.WriteLine("Usage: cli-reflect --assembly <path-to-dll> --output <dir>");
        return null;
    }

    return (assembly, output);
}
