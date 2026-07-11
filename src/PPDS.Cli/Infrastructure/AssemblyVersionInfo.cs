using System.Reflection;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Shared reflection primitive for reading an assembly's MinVer-stamped informational version.
/// Both the CLI diagnostics header (<see cref="Commands.ErrorOutput"/>) and the MCP
/// <c>serverInfo.version</c> resolver read <see cref="AssemblyInformationalVersionAttribute"/>
/// off an assembly; this centralizes that one raw read.
/// </summary>
/// <remarks>
/// Callers apply their own formatting on top: the CLI intentionally keeps the
/// '+&lt;sha&gt;' build-metadata suffix for diagnostics, while MCP strips it. This helper
/// therefore returns the raw attribute value verbatim — no trimming, stripping, or fallback.
/// </remarks>
public static class AssemblyVersionInfo
{
    /// <summary>
    /// Reads the raw <see cref="AssemblyInformationalVersionAttribute.InformationalVersion"/>
    /// from the given assembly, or <c>null</c> when the attribute is absent.
    /// </summary>
    /// <param name="assembly">The assembly to read version metadata from.</param>
    public static string? GetInformationalVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    }
}
