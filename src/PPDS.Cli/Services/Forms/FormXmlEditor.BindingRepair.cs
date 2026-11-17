using System.Xml.Linq;

namespace PPDS.Cli.Services.Forms;

internal static partial class FormXmlEditor
{
    /// <summary>
    /// Rewrites, in place, the <c>datafieldname</c> of every bound control whose binding equals a column logical name of the
    /// table ignoring case but differs in case, to that exact logical name. Only that attribute is changed: control ids,
    /// cells, labels and every other attribute and element are left as they are.
    /// </summary>
    /// <remarks>
    /// A bound control is a <c>&lt;control&gt;</c> directly inside a <c>&lt;cell&gt;</c> with a non-empty
    /// <c>datafieldname</c> attribute, wherever the cell sits: tab sections, the form header and the form footer.
    /// <c>&lt;hiddencontrols&gt;/&lt;data&gt;</c> entries are not controls and are not examined. A binding that matches no
    /// column is reported and left alone. A control whose <c>id</c> spells the column's logical name in a different case
    /// (bare, with a <c>header_</c>/<c>footer_</c> prefix, or with a numeric suffix) is reported and its id is not changed.
    /// </remarks>
    /// <param name="formXml">The form XML; mutated in place.</param>
    /// <param name="tableColumnNames">The logical names of the table's columns, as stored in metadata.</param>
    internal static (IReadOnlyList<RepairedBinding> Repaired, IReadOnlyList<UnchangedBinding> Unchanged) RepairBindings(
        XDocument formXml, IEnumerable<string> tableColumnNames)
    {
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in tableColumnNames)
            columns.TryAdd(column, column);

        var repaired = new List<RepairedBinding>();
        var unchanged = new List<UnchangedBinding>();
        // Snapshot first: the loop mutates attributes only, but a list keeps document order explicit.
        var bound = formXml.Descendants("control")
            .Where(control => control.Parent?.Name.LocalName == "cell" && !string.IsNullOrEmpty((string?)control.Attribute("datafieldname")))
            .ToList();
        foreach (var control in bound)
        {
            var stored = (string)control.Attribute("datafieldname")!;
            var controlId = (string?)control.Attribute("id");
            var (location, sectionId) = DescribeControlLocation(control);
            if (!columns.TryGetValue(stored, out var canonical))
            {
                unchanged.Add(new(controlId, stored, BindingRepairReasons.NoMatchingColumn, location, sectionId, null));
                continue;
            }

            if (!string.Equals(stored, canonical, StringComparison.Ordinal))
            {
                control.SetAttributeValue("datafieldname", canonical);
                repaired.Add(new(controlId, stored, canonical, location, sectionId));
            }

            if (ControlIdCaseDiffers(controlId, canonical))
                unchanged.Add(new(controlId, stored, BindingRepairReasons.ControlIdCaseDiffers, location, sectionId, canonical));
        }

        return (repaired, unchanged);
    }

    /// <summary>
    /// True when <paramref name="controlId"/> is the column's logical name — bare, with a <c>header_</c>/<c>footer_</c>
    /// prefix (any case), or followed only by digits — spelled in a different case. Ids built otherwise are not judged.
    /// </summary>
    internal static bool ControlIdCaseDiffers(string? controlId, string canonical)
    {
        if (string.IsNullOrEmpty(controlId)) return false;
        foreach (var prefix in new[] { "", "header_", "footer_" })
        {
            if (!controlId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = controlId[prefix.Length..];
            if (rest.Length < canonical.Length || !rest.StartsWith(canonical, StringComparison.OrdinalIgnoreCase)) continue;
            if (!rest[canonical.Length..].All(char.IsAsciiDigit)) continue;
            return !string.Equals(rest[..canonical.Length], canonical, StringComparison.Ordinal);
        }
        return false;
    }

    private static (string Location, string? SectionId) DescribeControlLocation(XElement control)
    {
        foreach (var ancestor in control.Ancestors())
        {
            switch (ancestor.Name.LocalName)
            {
                case "section":
                    var tab = ancestor.Ancestors("tab").FirstOrDefault();
                    return (tab is null ? DescribeElement(ancestor) : $"{DescribeElement(tab)} / {DescribeElement(ancestor)}",
                        (string?)ancestor.Attribute("id"));
                case "header":
                    return ("Header", null);
                case "footer":
                    return ("Footer", null);
            }
        }
        return ("(unknown)", null);
    }

}
