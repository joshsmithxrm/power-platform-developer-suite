namespace PPDS.DocsGen.Common;

/// <summary>
/// Result of a generator run — the set of files produced plus any
/// non-fatal diagnostics (fatal failures should throw).
/// </summary>
public sealed record GenerationResult(
    IReadOnlyList<GeneratedFile> Files,
    IReadOnlyList<string> Diagnostics);
