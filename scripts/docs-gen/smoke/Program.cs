using System.Text.Json;
using PPDS.DocsGen.Smoke;

// Entry point for the smoke tool. Usage:
//   smoke --docs-path <dir> --assemblies <dir>
// Walks <docs-path>/docs/ for fenced csharp blocks, wraps and compiles each
// against the DLLs in <assemblies> + the runtime assemblies, and prints a
// stable JSON summary to stdout (Constitution I1 — stdout for data). All
// human-oriented progress and failure lines go to stderr.

try
{
    var parsed = ParseArgs(args);
    if (parsed is null)
    {
        return 1;
    }

    var (docsPath, assembliesDir) = parsed.Value;

    if (!Directory.Exists(docsPath))
    {
        Console.Error.WriteLine($"smoke: --docs-path '{docsPath}' does not exist");
        return 1;
    }

    if (!Directory.Exists(assembliesDir))
    {
        Console.Error.WriteLine($"smoke: --assemblies '{assembliesDir}' does not exist");
        return 1;
    }

    var summary = SmokeRunner.Run(docsPath, assembliesDir, Console.Error);

    var payload = new
    {
        total = summary.Total,
        passed = summary.Passed,
        failed = summary.Failed,
        skipped = summary.Skipped,
        failures = summary.Failures
            .Select(f => new { file = f.File, line = f.Line, message = f.Message })
            .ToArray(),
    };

    Console.WriteLine(JsonSerializer.Serialize(
        payload,
        new JsonSerializerOptions { WriteIndented = false }));

    Console.Error.WriteLine(
        $"smoke: total={summary.Total} passed={summary.Passed} failed={summary.Failed} skipped={summary.Skipped}");

    return summary.Failed == 0 ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"smoke: error: {ex.Message}");
    return 1;
}

static (string DocsPath, string AssembliesDir)? ParseArgs(string[] args)
{
    string? docs = null;
    string? assemblies = null;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--docs-path" when i + 1 < args.Length:
                docs = args[++i];
                break;
            case "--assemblies" when i + 1 < args.Length:
                assemblies = args[++i];
                break;
            default:
                Console.Error.WriteLine($"smoke: unknown argument '{args[i]}'");
                return null;
        }
    }

    if (string.IsNullOrWhiteSpace(docs) || string.IsNullOrWhiteSpace(assemblies))
    {
        Console.Error.WriteLine(
            "Usage: smoke --docs-path <dir> --assemblies <dir>");
        return null;
    }

    return (docs, assemblies);
}
