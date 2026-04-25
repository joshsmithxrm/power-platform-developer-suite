namespace PPDS.Cli.Commands.WebResources;

/// <summary>
/// Type-shortcut mappings shared by web-resource CLI commands (list, pull).
/// "text" and "image" expand to multiple type codes; individual aliases map to
/// a single Dataverse webresourcetype value (1-12).
/// </summary>
internal static class WebResourceTypeMap
{
    /// <summary>
    /// Comma-separated list of supported aliases, used in error messages.
    /// </summary>
    public const string SupportedAliases = "text, image, data, js, css, html, xml, png, jpg, gif, svg, ico, xsl, resx";

    private static readonly Dictionary<string, int[]> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text"] = [1, 2, 3, 4, 9, 11, 12],     // HTML, CSS, JS, XML, XSL, SVG, RESX
        ["image"] = [5, 6, 7, 10, 11],          // PNG, JPG, GIF, ICO, SVG
        ["data"] = [4, 12],                     // XML, RESX
        ["html"] = [1],
        ["css"] = [2],
        ["js"] = [3], ["javascript"] = [3],
        ["xml"] = [4],
        ["png"] = [5],
        ["jpg"] = [6], ["jpeg"] = [6],
        ["gif"] = [7],
        ["xap"] = [8],
        ["xsl"] = [9], ["xslt"] = [9],
        ["ico"] = [10],
        ["svg"] = [11],
        ["resx"] = [12],
    };

    /// <summary>
    /// Resolves a type alias to the matching set of webresourcetype codes.
    /// </summary>
    /// <param name="alias">Type alias (case-insensitive).</param>
    /// <param name="codes">The matching codes when the alias is recognised.</param>
    /// <returns>True if the alias is recognised; false otherwise.</returns>
    public static bool TryGetCodes(string alias, out int[]? codes)
    {
        if (Map.TryGetValue(alias, out var found))
        {
            codes = found;
            return true;
        }
        codes = null;
        return false;
    }
}
