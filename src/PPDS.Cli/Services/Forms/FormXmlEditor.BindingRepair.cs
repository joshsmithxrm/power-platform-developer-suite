using System.Text;
using System.Xml;
using System.Xml.Linq;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.Forms;

/// <summary>The planned binding repair: what changes, what is only reported, and the repaired XML text.</summary>
/// <param name="Repaired">Controls whose binding is rewritten, in document order.</param>
/// <param name="Unchanged">Entries reported but not changed, in document order by kind.</param>
/// <param name="RepairedXml">The stored text with only the repaired <c>datafieldname</c> values replaced.</param>
/// <param name="RepairedLayoutIndexes">For each <see cref="Repaired"/> entry, its index among the layout-bound controls
/// (see <see cref="FormXmlEditor.ReadLayoutBindings"/>), used to verify the read-back positively.</param>
internal sealed record BindingRepairPlan(IReadOnlyList<RepairedBinding> Repaired, IReadOnlyList<UnchangedBinding> Unchanged, string RepairedXml,
    IReadOnlyList<int> RepairedLayoutIndexes);

/// <summary>A layout-bound control as read: its <c>id</c> (null when absent) and stored <c>datafieldname</c>.</summary>
internal sealed record LayoutBinding(string? ControlId, string Binding);

internal static partial class FormXmlEditor
{
    private enum ColumnMatch { None, Exact, CaseVariant, Ambiguous }

    /// <summary>
    /// Plans the repair of every layout-bound control whose <c>datafieldname</c> equals exactly one column logical name of
    /// the table ignoring case but differs in case, and produces the repaired text by replacing only those attribute values
    /// in <paramref name="storedXml"/>. Every other character of the stored text (declaration, whitespace, line endings,
    /// CDATA, comments, quoting, character references, attribute order) is kept as it was.
    /// </summary>
    /// <remarks>
    /// A layout-bound control is a <c>&lt;control&gt;</c> with a non-empty <c>datafieldname</c> at
    /// <c>cell/row/rows</c> under a tab section, a tab header/footer, or the form header/footer, on the document's own
    /// layout path from the root. Any other <c>cell/control</c> (for example inside custom-control parameters) is reported
    /// as <see cref="BindingRepairReasons.OutsideFormLayout"/> and never rewritten. Controls with a <c>relationship</c> or
    /// <c>isunbound="true"</c> are never rewritten. A binding that exactly equals a column name is never rewritten; one
    /// that matches several column names ignoring case is reported as ambiguous. Case-variant names in custom-control
    /// <c>datafieldname</c> parameters, <c>event/@attribute</c> and <c>hiddencontrols/data/@datafieldname</c> are reported.
    /// A document with a DOCTYPE is refused.
    /// </remarks>
    internal static BindingRepairPlan PlanBindingRepair(string storedXml, IEnumerable<string> tableColumnNames)
    {
        var exact = new HashSet<string>(tableColumnNames, StringComparer.Ordinal);
        var byFolded = exact.GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);

        (ColumnMatch Kind, string? Canonical) Resolve(string? value)
        {
            if (string.IsNullOrEmpty(value)) return (ColumnMatch.None, null);
            if (exact.Contains(value)) return (ColumnMatch.Exact, value);
            if (!byFolded.TryGetValue(value, out var names)) return (ColumnMatch.None, null);
            return names.Length == 1 ? (ColumnMatch.CaseVariant, names[0]) : (ColumnMatch.Ambiguous, null);
        }

        var doc = ParseStoredXml(storedXml, lineInfo: true);
        var lineStarts = LineStarts(storedXml);
        var repaired = new List<RepairedBinding>();
        var repairedIndexes = new List<int>();
        var unchanged = new List<UnchangedBinding>();
        var patches = new List<(int Start, int Length, string Value)>();

        var layout = LayoutControls(doc);
        var layoutSet = new HashSet<XElement>(layout);
        foreach (var control in doc.Descendants("control").Where(c => !layoutSet.Contains(c) && IsCellControlWithBinding(c)))
        {
            // Not on the form's own layout path (for example a cell/control look-alike inside custom-control parameters).
            var value = (string)control.Attribute("datafieldname")!;
            if (Resolve(value).Kind is ColumnMatch.CaseVariant or ColumnMatch.Ambiguous)
                unchanged.Add(new((string?)control.Attribute("id"), value, BindingRepairReasons.OutsideFormLayout,
                    BindingLocations.OutsideFormLayout, null, Resolve(value).Canonical));
        }

        for (var index = 0; index < layout.Count; index++)
        {
            var control = layout[index];
            var attribute = control.Attribute("datafieldname")!;
            var stored = attribute.Value;
            var controlId = (string?)control.Attribute("id");
            var (location, sectionId) = DescribeLayoutLocation(control);
            var (kind, canonical) = Resolve(stored);

            if (!string.IsNullOrEmpty((string?)control.Attribute("relationship")) || IsTrue((string?)control.Attribute("isunbound")))
            {
                // The binding may name a column reached through a relationship, or none; it is not judged against this table.
                if (kind is ColumnMatch.CaseVariant or ColumnMatch.Ambiguous)
                    unchanged.Add(new(controlId, stored, BindingRepairReasons.RelatedOrUnboundControl, location, sectionId, canonical));
                continue;
            }

            switch (kind)
            {
                case ColumnMatch.None:
                    unchanged.Add(new(controlId, stored, BindingRepairReasons.NoMatchingColumn, location, sectionId, null));
                    continue;
                case ColumnMatch.Ambiguous:
                    unchanged.Add(new(controlId, stored, BindingRepairReasons.AmbiguousColumn, location, sectionId, null));
                    continue;
                case ColumnMatch.Exact:
                    if (ControlIdCaseDiffers(controlId, stored))
                        unchanged.Add(new(controlId, stored, BindingRepairReasons.ControlIdCaseDiffers, location, sectionId, stored));
                    continue;
            }

            var span = LocateAttributeValue(storedXml, lineStarts, attribute);
            if (span.Length != stored.Length || string.CompareOrdinal(storedXml, span.Start, stored, 0, stored.Length) != 0)
            {
                // The stored text spells the value with character references; it is reported, not rewritten.
                unchanged.Add(new(controlId, stored, BindingRepairReasons.BindingNotPlainText, location, sectionId, canonical));
                continue;
            }

            patches.Add((span.Start, span.Length, canonical!));
            repaired.Add(new(controlId, stored, canonical!, location, sectionId, ControlIdCaseDiffers(controlId, canonical!)));
            repairedIndexes.Add(index);
        }

        var controlsByUniqueId = doc.Descendants("control")
            .Where(c => !string.IsNullOrEmpty((string?)c.Attribute("uniqueid")))
            .GroupBy(c => NormalizeGuidText((string)c.Attribute("uniqueid")!), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var parameter in doc.Descendants("controlDescription").Elements("customControl").Elements("parameters").Elements("datafieldname"))
        {
            var (kind, canonical) = Resolve(parameter.Value);
            if (kind is not (ColumnMatch.CaseVariant or ColumnMatch.Ambiguous)) continue;
            var forControl = (string?)parameter.Ancestors("controlDescription").First().Attribute("forControl");
            var owner = forControl is not null && controlsByUniqueId.TryGetValue(NormalizeGuidText(forControl), out var c) && layoutSet.Contains(c) ? c : null;
            var (location, sectionId) = owner is null ? (BindingLocations.ControlDescriptions, null) : DescribeLayoutLocation(owner);
            unchanged.Add(new((string?)owner?.Attribute("id"), parameter.Value, BindingRepairReasons.CustomControlParameterCaseDiffers,
                location, sectionId, canonical));
        }

        foreach (var formEvent in doc.Descendants("event"))
        {
            if (!string.IsNullOrEmpty((string?)formEvent.Attribute("relationship"))) continue;
            var value = (string?)formEvent.Attribute("attribute");
            var (kind, canonical) = Resolve(value);
            if (kind is not (ColumnMatch.CaseVariant or ColumnMatch.Ambiguous)) continue;
            unchanged.Add(new((string?)formEvent.Attribute("control"), value!, BindingRepairReasons.EventAttributeCaseDiffers,
                $"Event '{(string?)formEvent.Attribute("name")}'", null, canonical));
        }

        foreach (var data in doc.Descendants("hiddencontrols").Elements("data"))
        {
            if (!string.IsNullOrEmpty((string?)data.Attribute("relationship"))) continue;
            var value = (string?)data.Attribute("datafieldname");
            var (kind, canonical) = Resolve(value);
            if (kind is not (ColumnMatch.CaseVariant or ColumnMatch.Ambiguous)) continue;
            unchanged.Add(new((string?)data.Attribute("id"), value!, BindingRepairReasons.HiddenDataCaseDiffers,
                BindingLocations.HiddenControls, null, canonical));
        }

        var text = new StringBuilder(storedXml);
        // Every patch has the same length as the value it replaces (an ignore-case match), so offsets stay valid.
        foreach (var (start, length, value) in patches.OrderByDescending(p => p.Start))
            text.Remove(start, length).Insert(start, value);
        return new(repaired, unchanged, text.ToString(), repairedIndexes);
    }

    /// <summary>The id and stored binding of every layout-bound control, in document order (the index space of
    /// <see cref="BindingRepairPlan.RepairedLayoutIndexes"/>).</summary>
    internal static IReadOnlyList<LayoutBinding> ReadLayoutBindings(string storedXml)
        => LayoutControls(ParseStoredXml(storedXml, lineInfo: false))
            .Select(c => new LayoutBinding((string?)c.Attribute("id"), (string)c.Attribute("datafieldname")!)).ToList();

    private static XDocument ParseStoredXml(string? storedXml, bool lineInfo)
    {
        if (string.IsNullOrEmpty(storedXml))
            throw new PpdsException(FormErrorCodes.InvalidFormXml, "The form has no form XML. Nothing was written.");
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new StringReader(storedXml), settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace | (lineInfo ? LoadOptions.SetLineInfo : LoadOptions.None));
        }
        catch (XmlException ex)
        {
            throw new PpdsException(FormErrorCodes.InvalidFormXml,
                $"The stored form XML could not be read for binding repair (a DOCTYPE is not supported): {ex.Message} Nothing was written.", ex);
        }
    }

    /// <summary>Layout-bound controls in document order: <c>cell/control</c> with a binding, on the form's own layout path.</summary>
    private static List<XElement> LayoutControls(XDocument doc)
        => doc.Descendants("control").Where(c => IsCellControlWithBinding(c) && LayoutContainer(c) is not null).ToList();

    private static bool IsCellControlWithBinding(XElement control)
        => control.Parent?.Name.LocalName == "cell" && !string.IsNullOrEmpty((string?)control.Attribute("datafieldname"));

    /// <summary>
    /// The section, tabheader, tabfooter, header or footer that holds the control on the exact layout path from the root
    /// (<c>form/tabs/tab/columns/column/sections/section</c>, <c>form/tabs/tab/tabheader|tabfooter</c>,
    /// <c>form/header|footer</c>, then <c>rows/row/cell/control</c>); null for anything else.
    /// </summary>
    private static XElement? LayoutContainer(XElement control)
    {
        var cell = control.Parent;
        var row = cell?.Parent;
        var rows = row?.Parent;
        var container = rows?.Parent;
        if (cell?.Name.LocalName != "cell" || row?.Name.LocalName != "row" || rows?.Name.LocalName != "rows" || container is null)
            return null;
        var root = control.Document?.Root;
        static bool Is(XElement? element, string name) => element?.Name.LocalName == name;
        switch (container.Name.LocalName)
        {
            case "section":
                var tab = container.Parent?.Parent?.Parent?.Parent;
                return Is(container.Parent, "sections") && Is(container.Parent?.Parent, "column") && Is(container.Parent?.Parent?.Parent, "columns")
                    && Is(tab, "tab") && Is(tab?.Parent, "tabs") && tab?.Parent?.Parent == root ? container : null;
            case "tabheader":
            case "tabfooter":
                return Is(container.Parent, "tab") && Is(container.Parent?.Parent, "tabs") && container.Parent?.Parent?.Parent == root ? container : null;
            case "header":
            case "footer":
                return container.Parent == root ? container : null;
            default:
                return null;
        }
    }

    private static bool IsTrue(string? value) => value is not null && (value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeGuidText(string value) => value.Trim().Trim('{', '}').ToLowerInvariant();

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

    /// <summary>Location of a layout-bound control (see <see cref="LayoutContainer"/>).</summary>
    private static (string Location, string? SectionId) DescribeLayoutLocation(XElement control)
    {
        var container = LayoutContainer(control)
            ?? throw new PpdsException(FormErrorCodes.InvalidFormXml, "A control outside the form layout cannot be described. Nothing was written.");
        switch (container.Name.LocalName)
        {
            case "section":
                var tab = container.Ancestors("tab").First();
                return ($"{DescribeElement(tab)} / {DescribeElement(container)}", (string?)container.Attribute("id"));
            case "tabheader":
                return ($"{DescribeElement(container.Parent!)} / {BindingLocations.TabHeader}", null);
            case "tabfooter":
                return ($"{DescribeElement(container.Parent!)} / {BindingLocations.TabFooter}", null);
            case "header":
                return (BindingLocations.Header, null);
            default:
                return (BindingLocations.Footer, null);
        }
    }

    /// <summary>Character offset at which each 1-based line starts; CRLF, CR and LF each end a line, as the XML reader counts them.</summary>
    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
            else if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }
        return starts;
    }

    /// <summary>
    /// Finds the raw value span of <paramref name="attribute"/> in <paramref name="text"/> from its reader line information.
    /// Throws (so nothing is written) when there is no line information or the text at that position is not the attribute.
    /// </summary>
    internal static (int Start, int Length) LocateAttributeValue(string text, IReadOnlyList<int> lineStarts, XAttribute attribute)
    {
        var info = (IXmlLineInfo)attribute;
        var name = attribute.Name.LocalName;
        if (info.HasLineInfo() && info.LineNumber >= 1 && info.LineNumber <= lineStarts.Count)
        {
            var i = lineStarts[info.LineNumber - 1] + info.LinePosition - 1;
            if (i >= 0 && i + name.Length <= text.Length && string.CompareOrdinal(text, i, name, 0, name.Length) == 0)
            {
                i += name.Length;
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i < text.Length && text[i] == '=')
                {
                    i++;
                    while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                    if (i < text.Length && text[i] is '"' or '\'')
                    {
                        var end = text.IndexOf(text[i], i + 1);
                        if (end > i) return (i + 1, end - i - 1);
                    }
                }
            }
        }
        throw new PpdsException(FormErrorCodes.InvalidFormXml,
            "Could not locate a control binding in the stored form XML text; nothing was written.");
    }

    /// <summary>Line starts for <see cref="LocateAttributeValue"/> (test seam).</summary>
    internal static IReadOnlyList<int> LineStartsFor(string text) => LineStarts(text);
}
