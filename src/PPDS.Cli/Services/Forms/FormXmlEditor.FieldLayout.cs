using System.Xml.Linq;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.Forms;

internal static partial class FormXmlEditor
{
    // Strict reorder replaces bound-cell slots only: row/container attributes, spacers,
    // subgrids and every unbound cell retain their positions and complete nested content.
    internal static bool ReorderFieldsStrict(XDocument doc, string sectionSelector, IReadOnlyList<string> fields)
    {
        var cells = LayoutCells(RequireSection(doc, sectionSelector));
        var bound = cells.Where(cell => Binding(cell) is not null).ToList();
        var byName = UniqueBindings(bound);
        if (fields.Count != bound.Count || fields.Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Count
            || fields.Any(field => !byName.ContainsKey(field)))
            throw new PpdsException(FormErrorCodes.InvalidFieldOrder, "Strict reorder requires every existing field exactly once; it cannot add or remove fields.");
        var ordered = fields.Select(field => byName[field]).ToList();
        if (bound.SequenceEqual(ordered)) return false;
        foreach (var cell in bound.Where((cell, index) => cell != ordered[index])) RequireUnitSpan(cell);
        var slots = bound.Select(_ => new XComment("field-slot")).ToArray();
        for (var i = 0; i < bound.Count; i++) bound[i].ReplaceWith(slots[i]);
        for (var i = 0; i < ordered.Count; i++) slots[i].ReplaceWith(ordered[i]);
        return true;
    }

    internal static bool MoveField(XDocument doc, MoveFieldRequest request, string field, string? before)
    {
        var source = RequireSection(doc, request.FromSection);
        var target = RequireSection(doc, request.ToSection);
        var sourceCells = LayoutCells(source);
        var targetCells = source == target ? sourceCells : LayoutCells(target);
        var sources = sourceCells.Where(cell => IsBinding(cell, field)).ToList();
        var targets = targetCells.Where(cell => IsBinding(cell, field)).ToList();
        if (sources.Count > 1 || targets.Count > 1)
            throw new PpdsException(FormErrorCodes.AmbiguousField, $"Column '{field}' has multiple controls in a selected section.");
        List<XElement> anchors = before is null ? [] : targetCells.Where(cell => IsBinding(cell, before)).ToList();
        if (before is not null && (anchors.Count != 1 || string.Equals(field, before, StringComparison.OrdinalIgnoreCase)))
            throw new PpdsException(FormErrorCodes.InvalidFieldOrder, "The before-field anchor must identify one different field in the target section.");
        var anchor = anchors.SingleOrDefault();
        if (anchor is not null && anchor.Parent!.Elements("cell").Count() != 1)
            throw new PpdsException(FormErrorCodes.UnsupportedLayout, "The before-field anchor must occupy its own row; mixed rows are not flattened.");
        if (sources.Count == 0)
        {
            if (source == target || targets.Count != 1)
                throw new PpdsException(FormErrorCodes.FieldNotInSection, $"Column '{field}' is not in the source section.");
            // A retry may find the exact desired state. No provenance or successful earlier write is claimed.
            return PositionMovedCell(targets[0], target, anchor);
        }
        if (source != target && targets.Count != 0)
            throw new PpdsException(FormErrorCodes.FieldAlreadyInTarget, $"Column '{field}' is already in the target section; existing controls will not be replaced.");
        if (source == target && before is null) return false;
        return PositionMovedCell(sources[0], target, anchor);
    }

    private static bool PositionMovedCell(XElement cell, XElement target, XElement? anchor)
    {
        var row = cell.Parent!;
        var rows = row.Parent!;
        var targetRows = target.Element("rows");
        if (rows == targetRows && (anchor is null || row.Elements("cell").Count() == 1 && row.ElementsAfterSelf("row").FirstOrDefault() == anchor.Parent))
            return false;
        RequireUnitSpan(cell);
        if (row.Elements("cell").Count() == 1) RequireUncrossedRowBoundary(rows, row);
        else if (cell.ElementsAfterSelf("cell").Any(sibling => RowSpan(sibling) > 1))
            throw new PpdsException(FormErrorCodes.UnsupportedLayout, "Moving a cell before a spanning sibling requires an explicit layout edit.");
        if (targetRows is not null) RequireUncrossedRowBoundary(targetRows, anchor?.Parent);
        // A whole row carries its authored attributes. A cell leaving a mixed row
        // leaves that row and its siblings intact and receives a new plain target row.
        XElement movedRow;
        if (row.Elements("cell").Count() == 1)
        {
            movedRow = row;
            row.Remove();
        }
        else
        {
            cell.Remove();
            movedRow = new XElement("row", cell);
        }
        targetRows ??= EnsureRows(target); // rows/row may legally be empty in the bundled schema.
        if (anchor is null) targetRows.Add(movedRow);
        else anchor.Parent!.AddBeforeSelf(movedRow);
        return true;
    }

    private static void RequireUncrossedRowBoundary(XElement rows, XElement? nextRow)
    {
        var preceding = rows.Elements("row").TakeWhile(row => row != nextRow).ToList();
        for (var index = 0; index < preceding.Count; index++)
            if (preceding[index].Elements("cell").Any(cell => RowSpan(cell) > preceding.Count - index))
                throw new PpdsException(FormErrorCodes.UnsupportedLayout, "Moving rows across an active row span requires an explicit layout edit.");
    }

    private static int RowSpan(XElement cell)
    {
        var value = (string?)cell.Attribute("rowspan");
        if (value is null) return 1;
        if (int.TryParse(value, out var span) && span > 0) return span;
        throw new PpdsException(FormErrorCodes.UnsupportedLayout, "Row span geometry must be a positive integer for an explicit field move.");
    }

    private static List<XElement> LayoutCells(XElement section)
    {
        var rows = section.Element("rows");
        if (rows is null) return [];
        if (rows.Elements().Any(element => element.Name != "row")
            || rows.Elements("row").Any(row => row.Elements().Any(element => element.Name != "cell")))
            throw new PpdsException(FormErrorCodes.UnsupportedLayout, "Only direct section rows and cells can be moved or strictly reordered.");
        return rows.Elements("row").SelectMany(row => row.Elements("cell")).ToList();
    }

    private static Dictionary<string, XElement> UniqueBindings(IEnumerable<XElement> cells)
    {
        var result = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in cells)
            if (!result.TryAdd(Binding(cell)!, cell))
                throw new PpdsException(FormErrorCodes.AmbiguousField, "A selected section has multiple controls for the same field.");
        return result;
    }

    private static string? Binding(XElement cell) => (string?)cell.Element("control")?.Attribute("datafieldname") is { Length: > 0 } value ? value : null;
    private static bool IsBinding(XElement cell, string field) => string.Equals(Binding(cell), field, StringComparison.OrdinalIgnoreCase);
    private static void RequireUnitSpan(XElement cell)
    {
        if (string.Equals((string?)cell.Element("control")?.Attribute("classid"), ClassIdResolver.SubgridClassId, StringComparison.OrdinalIgnoreCase)
            || new[] { "rowspan", "colspan" }.Any(name => cell.Attribute(name) is { } attribute && attribute.Value != "1"))
            throw new PpdsException(FormErrorCodes.UnsupportedLayout, "Moving spanning field cells requires an explicit layout edit; existing spans are preserved.");
    }
}
