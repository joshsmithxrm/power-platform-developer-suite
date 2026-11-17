using System.Text;
using System.Xml;
using System.Xml.Linq;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.Forms;

/// <summary>The planned binding repair: what changes, what is only reported, and the repaired XML text.</summary>
/// <param name="Repaired">Controls whose binding is rewritten, in document order.</param>
/// <param name="Unchanged">Entries reported but not changed, in document order by kind.</param>
/// <param name="RepairedXml">The stored text with only the repaired <c>datafieldname</c> values replaced.</param>
internal sealed record BindingRepairPlan(IReadOnlyList<RepairedBinding> Repaired, IReadOnlyList<UnchangedBinding> Unchanged, string RepairedXml);

internal static partial class FormXmlEditor
{
    /// <summary>
    /// Plans the repair of every bound control whose <c>datafieldname</c> equals a column logical name of the table ignoring
    /// case but differs in case, and produces the repaired text by replacing only those attribute values in
    /// <paramref name="storedXml"/>. Every other character of the stored text (declaration, whitespace, line endings,
    /// CDATA, comments, quoting, entity spelling, attribute order) is kept as it was.
    /// </summary>
    /// <remarks>
    /// A bound control is a <c>&lt;control&gt;</c> directly inside a <c>&lt;cell&gt;</c> with a non-empty
    /// <c>datafieldname</c>, wherever the cell sits: tab sections, tab headers/footers, the form header and footer.
    /// Controls with a <c>relationship</c> or <c>isunbound="true"</c> are never rewritten, because their binding may not
    /// name a column of this table; a case-variant one is reported. Case-variant column names in custom-control
    /// <c>datafieldname</c> parameters, <c>event/@attribute</c> and <c>hiddencontrols/data/@datafieldname</c> are reported,
    /// never rewritten.
    /// </remarks>
    internal static BindingRepairPlan PlanBindingRepair(string storedXml, IEnumerable<string> tableColumnNames)
    {
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in tableColumnNames)
            columns.TryAdd(column, column);

        var doc = XDocument.Parse(storedXml, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        var lineStarts = LineStarts(storedXml);
        var repaired = new List<RepairedBinding>();
        var unchanged = new List<UnchangedBinding>();
        var patches = new List<(int Start, int Length, string Value)>();

        string? CaseVariant(string? value)
            => !string.IsNullOrEmpty(value) && columns.TryGetValue(value, out var canonical)
               && !string.Equals(value, canonical, StringComparison.Ordinal) ? canonical : null;

        foreach (var control in doc.Descendants("control").Where(IsBoundControl).ToList())
        {
            var attribute = control.Attribute("datafieldname")!;
            var stored = attribute.Value;
            var controlId = (string?)control.Attribute("id");
            var (location, sectionId) = DescribeControlLocation(control);
            columns.TryGetValue(stored, out var match);

            if (!string.IsNullOrEmpty((string?)control.Attribute("relationship")) || IsTrue((string?)control.Attribute("isunbound")))
            {
                // The binding may name a column reached through a relationship, or none; it is not judged against this table.
                if (CaseVariant(stored) is { } related)
                    unchanged.Add(new(controlId, stored, BindingRepairReasons.RelatedOrUnboundControl, location, sectionId, related));
                continue;
            }

            if (match is null)
            {
                unchanged.Add(new(controlId, stored, BindingRepairReasons.NoMatchingColumn, location, sectionId, null));
                continue;
            }

            var idDiffers = ControlIdCaseDiffers(controlId, match);
            if (string.Equals(stored, match, StringComparison.Ordinal))
            {
                if (idDiffers)
                    unchanged.Add(new(controlId, stored, BindingRepairReasons.ControlIdCaseDiffers, location, sectionId, match));
                continue;
            }

            var span = LocateAttributeValue(storedXml, lineStarts, attribute);
            if (span.Length != stored.Length || string.CompareOrdinal(storedXml, span.Start, stored, 0, stored.Length) != 0)
            {
                // The stored text spells the value with character or entity references; it is reported, not rewritten.
                unchanged.Add(new(controlId, stored, BindingRepairReasons.BindingNotPlainText, location, sectionId, match));
                continue;
            }

            patches.Add((span.Start, span.Length, match));
            repaired.Add(new(controlId, stored, match, location, sectionId, idDiffers));
        }

        var controlsByUniqueId = doc.Descendants("control")
            .Where(c => !string.IsNullOrEmpty((string?)c.Attribute("uniqueid")))
            .GroupBy(c => NormalizeGuidText((string)c.Attribute("uniqueid")!), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var parameter in doc.Descendants("controlDescription").Elements("customControl").Elements("parameters").Elements("datafieldname"))
        {
            if (CaseVariant(parameter.Value) is not { } canonical) continue;
            var forControl = (string?)parameter.Ancestors("controlDescription").First().Attribute("forControl");
            var owner = forControl is not null && controlsByUniqueId.TryGetValue(NormalizeGuidText(forControl), out var c) ? c : null;
            var (location, sectionId) = owner is null ? ("Control descriptions", null) : DescribeControlLocation(owner);
            unchanged.Add(new((string?)owner?.Attribute("id"), parameter.Value, BindingRepairReasons.CustomControlParameterCaseDiffers,
                location, sectionId, canonical));
        }

        foreach (var formEvent in doc.Descendants("event"))
        {
            if (!string.IsNullOrEmpty((string?)formEvent.Attribute("relationship"))) continue;
            var value = (string?)formEvent.Attribute("attribute");
            if (CaseVariant(value) is not { } canonical) continue;
            unchanged.Add(new((string?)formEvent.Attribute("control"), value!, BindingRepairReasons.EventAttributeCaseDiffers,
                $"Event '{(string?)formEvent.Attribute("name")}'", null, canonical));
        }

        foreach (var data in doc.Descendants("hiddencontrols").Elements("data"))
        {
            if (!string.IsNullOrEmpty((string?)data.Attribute("relationship"))) continue;
            var value = (string?)data.Attribute("datafieldname");
            if (CaseVariant(value) is not { } canonical) continue;
            unchanged.Add(new((string?)data.Attribute("id"), value!, BindingRepairReasons.HiddenDataCaseDiffers, "Hidden controls", null, canonical));
        }

        var text = new StringBuilder(storedXml);
        foreach (var (start, length, value) in patches.OrderByDescending(p => p.Start))
            text.Remove(start, length).Insert(start, value);
        return new(repaired, unchanged, text.ToString());
    }

    private static bool IsBoundControl(XElement control)
        => control.Parent?.Name.LocalName == "cell" && !string.IsNullOrEmpty((string?)control.Attribute("datafieldname"));

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
                case "tabheader":
                case "tabfooter":
                    var owner = ancestor.Ancestors("tab").FirstOrDefault();
                    var part = ancestor.Name.LocalName == "tabheader" ? "Tab header" : "Tab footer";
                    return (owner is null ? part : $"{DescribeElement(owner)} / {part}", null);
                case "header":
                    return ("Header", null);
                case "footer":
                    return ("Footer", null);
            }
        }
        return ("(unknown)", null);
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
    /// Finds the raw value span of <paramref name="attribute"/> in the stored text from its reader line information.
    /// Throws (so nothing is written) when the text at that position is not the expected attribute.
    /// </summary>
    private static (int Start, int Length) LocateAttributeValue(string text, IReadOnlyList<int> lineStarts, XAttribute attribute)
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
}
