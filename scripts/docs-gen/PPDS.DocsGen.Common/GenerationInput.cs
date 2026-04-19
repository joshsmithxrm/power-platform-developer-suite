namespace PPDS.DocsGen.Common;

/// <summary>
/// Input to an <see cref="IReferenceGenerator"/>: the assembly to reflect
/// over and the output root relative to which <see cref="GeneratedFile"/>
/// paths are expressed.
/// </summary>
public sealed record GenerationInput(string SourceAssemblyPath, string OutputRoot);
