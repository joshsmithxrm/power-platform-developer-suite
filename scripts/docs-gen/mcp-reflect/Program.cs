using PPDS.DocsGen.Common;
using PPDS.DocsGen.Mcp;

// Constitution I1: stdout for data; stderr for diagnostics/status.
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
            Console.Error.WriteLine($"error: unknown argument '{args[i]}'");
            return 1;
    }
}

if (string.IsNullOrEmpty(assembly) || string.IsNullOrEmpty(output))
{
    Console.Error.WriteLine("usage: mcp-reflect --assembly <path-to-PPDS.Mcp.dll> --output <dir>");
    return 1;
}

try
{
    var generator = new McpReferenceGenerator();
    var input = new GenerationInput(assembly, output);
    var result = await generator.GenerateAsync(input, CancellationToken.None);

    foreach (var diagnostic in result.Diagnostics)
    {
        Console.Error.WriteLine(diagnostic);
    }

    Directory.CreateDirectory(output);
    foreach (var file in result.Files)
    {
        var path = Path.Combine(output, file.RelativePath);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, file.Contents);
        Console.Out.WriteLine(file.RelativePath);
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
