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

    /// <summary>
    /// Comma-separated list of single-type aliases, used in error messages for commands
    /// that need exactly one type (create). Excludes the multi-type shortcuts.
    /// </summary>
    public const string SingleTypeAliases = "html, css, js, xml, png, jpg, gif, xap, xsl, ico, svg, resx";

    private static readonly Dictionary<string, int[]> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text"] = [1, 2, 3, 4, 9, 11, 12],     // HTML, CSS, JS, XML, XSL, SVG, RESX
        ["image"] = [5, 6, 7, 10, 11],          // PNG, JPG, GIF, ICO, SVG
        ["data"] = [4, 12],                     // XML, RESX
        ["html"] = [1], ["htm"] = [1],
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

    /// <summary>
    /// Resolves a type alias or file extension (without dot) to a single webresourcetype code.
    /// Multi-type shortcuts ("text", "image", "data") are rejected — used by commands that
    /// need exactly one type (create).
    /// </summary>
    /// <param name="aliasOrExtension">Type alias or file extension (case-insensitive).</param>
    /// <param name="code">The matching code when the alias maps to exactly one type.</param>
    /// <returns>True if the alias maps to exactly one type code; false otherwise.</returns>
    public static bool TryGetSingleCode(string aliasOrExtension, out int code)
    {
        if (Map.TryGetValue(aliasOrExtension, out var found) && found.Length == 1)
        {
            code = found[0];
            return true;
        }
        code = 0;
        return false;
    }
}
