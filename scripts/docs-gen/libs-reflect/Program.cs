using PPDS.DocsGen.Common;
using PPDS.DocsGen.Libs;

// Constitution I1: stdout is for deterministic data; diagnostics go to stderr.
// This tool writes no stdout in the success path — it writes files to disk.

var assembliesDir = GetArg(args, "--assemblies");
var outputRoot = GetArg(args, "--output");
var packagesCsv = GetArg(args, "--packages", required: false);

if (assembliesDir is null || outputRoot is null)
{
    Console.Error.WriteLine("Usage: libs-reflect --assemblies <dir> --output <dir> [--packages <csv>]");
    return 1;
}

assembliesDir = Path.GetFullPath(assembliesDir);
outputRoot = Path.GetFullPath(outputRoot);

if (!Directory.Exists(assembliesDir))
{
    Console.Error.WriteLine($"libs-reflect: --assemblies directory does not exist: {assembliesDir}");
    return 1;
}

var defaultPackages = new[] { "PPDS.Dataverse", "PPDS.Migration", "PPDS.Auth", "PPDS.Plugins" };
var packages = string.IsNullOrWhiteSpace(packagesCsv)
    ? defaultPackages
    : packagesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var generator = new LibraryReferenceGenerator();
var exitCode = 0;

foreach (var package in packages.OrderBy(p => p, StringComparer.Ordinal))
{
    var dllPath = Path.Combine(assembliesDir, package + ".dll");
    if (!File.Exists(dllPath))
    {
        Console.Error.WriteLine($"libs-reflect: skipping {package} — assembly not found at {dllPath}");
        exitCode = 1;
        continue;
    }

    GenerationResult result;
    try
    {
        result = await generator.GenerateAsync(new GenerationInput(dllPath, outputRoot), CancellationToken.None);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"libs-reflect: failed to generate for {package}: {ex.Message}");
        exitCode = 1;
        continue;
    }

    foreach (var file in result.Files)
    {
        var outPath = Path.Combine(outputRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        await File.WriteAllTextAsync(outPath, file.Contents, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    foreach (var diag in result.Diagnostics)
    {
        Console.Error.WriteLine($"libs-reflect: {diag}");
    }
}

return exitCode;

static string? GetArg(string[] args, string name, bool required = true)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name) return args[i + 1];
    }
    return required ? null : null;
}
