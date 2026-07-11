using System.Reflection;

namespace PPDS.Mcp.Infrastructure;

/// <summary>
/// Resolves the version PPDS reports as <c>serverInfo.version</c> in the MCP
/// <c>initialize</c> handshake.
/// </summary>
/// <remarks>
/// PPDS.Mcp is versioned with MinVer (<c>MinVerTagPrefix=Mcp-v</c> in
/// <c>PPDS.Mcp.csproj</c>). MinVer stamps <see cref="AssemblyInformationalVersionAttribute"/>
/// and <see cref="AssemblyFileVersionAttribute"/> from git tags/describe, but it deliberately
/// leaves the classic <see cref="AssemblyName.Version"/> (AssemblyVersion) fixed at
/// <c>{major}.0.0.0</c> for binding-compatibility reasons. The ModelContextProtocol SDK
/// (v0.2.0-preview.3) falls back to the entry assembly's AssemblyVersion whenever
/// <c>McpServerOptions.ServerInfo</c> is left unset, so without this helper the handshake
/// always reports "1.0.0.0" regardless of the real package version. See #1273.
/// </remarks>
internal static class ServerVersion
{
    /// <summary>Reported when no version metadata is available at all.</summary>
    internal const string Unknown = "0.0.0";

    /// <summary>
    /// Resolves the reportable version from the given assembly's version attributes.
    /// </summary>
    /// <param name="assembly">
    /// The assembly to read version metadata from — pass <c>Assembly.GetExecutingAssembly()</c>
    /// from Program.cs to resolve ppds-mcp-server's own MinVer-stamped version.
    /// </param>
    internal static string Resolve(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        var fileVersion = assembly
            .GetCustomAttribute<AssemblyFileVersionAttribute>()?
            .Version;

        return Resolve(informationalVersion, fileVersion);
    }

    /// <summary>
    /// Pure resolution logic, separated from assembly reflection so it is directly unit
    /// testable. Strips MinVer's '+&lt;metadata&gt;' build-metadata suffix (e.g. a git SHA)
    /// from the informational version — e.g. "1.0.2-alpha.0.5+abc1234" becomes
    /// "1.0.2-alpha.0.5" — since build metadata is not meaningful to MCP clients. Falls back
    /// to <paramref name="fileVersion"/>, then <see cref="Unknown"/>, when the informational
    /// version is missing or blank.
    /// </summary>
    /// <param name="informationalVersion">
    /// The assembly's <see cref="AssemblyInformationalVersionAttribute"/> value, if present.
    /// </param>
    /// <param name="fileVersion">
    /// The assembly's <see cref="AssemblyFileVersionAttribute"/> value, if present.
    /// </param>
    internal static string Resolve(string? informationalVersion, string? fileVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var metadataIndex = informationalVersion.IndexOf('+');
            return metadataIndex >= 0
                ? informationalVersion[..metadataIndex]
                : informationalVersion;
        }

        return string.IsNullOrWhiteSpace(fileVersion) ? Unknown : fileVersion;
    }
}
