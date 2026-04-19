namespace PPDS.DocsGen.Common;

/// <summary>
/// A single file produced by a generator. <see cref="RelativePath"/> is
/// relative to <see cref="GenerationInput.OutputRoot"/>; <see cref="Contents"/>
/// is the full UTF-8 file body.
/// </summary>
public sealed record GeneratedFile(string RelativePath, string Contents);
