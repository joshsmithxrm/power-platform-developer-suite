namespace PPDS.DocsGen.Common;

/// <summary>
/// Shared contract across the C#-based docs-gen reference generators
/// (<c>cli-reflect</c>, <c>libs-reflect</c>, <c>mcp-reflect</c>).
/// </summary>
/// <remarks>
/// The Node-based <c>ext-reflect</c> does not implement this interface — its
/// input is JSON rather than a .NET assembly — but follows the same shape
/// conceptually. See <c>specs/docs-generation.md</c> Core Types.
/// </remarks>
public interface IReferenceGenerator
{
    /// <summary>
    /// Generates reference documentation for the assembly identified by
    /// <paramref name="input"/>. The returned files are paths relative to
    /// <see cref="GenerationInput.OutputRoot"/>; callers are responsible for
    /// writing them to disk.
    /// </summary>
    Task<GenerationResult> GenerateAsync(GenerationInput input, CancellationToken ct);
}
