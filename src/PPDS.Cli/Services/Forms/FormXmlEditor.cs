using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.Forms;

/// <summary>
/// Pure static XML manipulation helpers for Dataverse formxml.
/// No I/O — takes XDocument, returns XDocument.
/// </summary>
/// <remarks>
/// Emits structure conforming to the bundled Dataverse FormXml.xsd:
/// <c>form &gt; tabs &gt; tab &gt; columns &gt; column &gt; sections &gt; section &gt; rows &gt; row &gt; cell &gt; control</c>.
/// GUIDs use brace format; <c>labelid</c> is an attribute (not a child element);
/// the phone-visibility attribute is <c>availableforphone</c> (lowercase).
/// </remarks>
internal static class FormXmlEditor
{
    private const string DefaultLanguageCode = "1033";

    // ── Tabs ──────────────────────────────────────────────────────────────

    internal static XDocument AddTab(XDocument formXml, AddTabRequest request)
    {
        var form = RequireForm(formXml);
        var tabs = form.Element("tabs");
        if (tabs is null)
        {
            tabs = new XElement("tabs");
            form.Add(tabs);
        }

        var tabId = NewBraceGuid();
        var labelId = NewBraceGuid();

        var tab = new XElement("tab",
            new XAttribute("name", tabId),
            new XAttribute("id", tabId),
            new XAttribute("IsUserDefined", "0"),
            new XAttribute("locklevel", "0"),
            new XAttribute("showlabel", Bool(request.ShowLabel)),
            new XAttribute("expanded", Bool(request.Expanded)),
            new XAttribute("visible", Bool(request.Visible)),
            new XAttribute("availableforphone", Bool(request.AvailableOnPhone)),
            new XAttribute("labelid", labelId),
            Labels(request.Label),
            BuildColumns(request.Columns));

        tabs.Add(tab);
        return formXml;
    }

    internal static XDocument UpdateTab(XDocument formXml, UpdateTabRequest request)
    {
        var tab = RequireTab(formXml, request.TabLabel);

        if (request.NewLabel is not null)
            SetLabel(tab, request.NewLabel);

        if (request.ShowLabel.HasValue)
            tab.SetAttributeValue("showlabel", Bool(request.ShowLabel.Value));
        if (request.Expanded.HasValue)
            tab.SetAttributeValue("expanded", Bool(request.Expanded.Value));
        if (request.Visible.HasValue)
            tab.SetAttributeValue("visible", Bool(request.Visible.Value));
        if (request.AvailableOnPhone.HasValue)
            tab.SetAttributeValue("availableforphone", Bool(request.AvailableOnPhone.Value));
        if (request.Columns.HasValue)
        {
            // Re-balance column widths while preserving every existing section.
            // Collect sections from ALL current columns (not just the first) and
            // move them into the first column of the new layout — otherwise a tab
            // that already had sections in columns 2/3 would silently lose them.
            var columns = tab.Element("columns");
            if (columns is not null)
            {
                var preserved = columns.Descendants("section")
                    .Select(s => new XElement(s))
                    .ToList();
                var newColumns = BuildColumns(request.Columns.Value);
                if (preserved.Count > 0)
                    newColumns.Element("column")!.Element("sections")!.Add(preserved);
                columns.ReplaceWith(newColumns);
            }
        }

        return formXml;
    }

    internal static XDocument RemoveTab(XDocument formXml, string tabLabel)
    {
        var tab = RequireTab(formXml, tabLabel);
        tab.Remove();
        return formXml;
    }

    // ── Sections ──────────────────────────────────────────────────────────

    internal static XDocument AddSection(XDocument formXml, AddSectionRequest request)
    {
        var tab = RequireTab(formXml, request.TabLabel);
        var column = tab.Element("columns")?.Element("column")
            ?? throw new PpdsException(FormErrorCodes.TabNotFound,
                $"Tab '{request.TabLabel}' has no column to hold sections.");

        var sections = column.Element("sections");
        if (sections is null)
        {
            sections = new XElement("sections");
            column.Add(sections);
        }

        var sectionId = NewBraceGuid();
        var labelId = NewBraceGuid();

        var section = new XElement("section",
            new XAttribute("name", sectionId),
            new XAttribute("id", sectionId),
            new XAttribute("IsUserDefined", "0"),
            new XAttribute("locklevel", "0"),
            new XAttribute("showlabel", Bool(request.ShowLabel)),
            new XAttribute("visible", Bool(request.Visible)),
            new XAttribute("availableforphone", Bool(request.AvailableOnPhone)),
            new XAttribute("columns", request.Columns.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("labelid", labelId),
            Labels(request.Label),
            new XElement("rows"));

        sections.Add(section);
        return formXml;
    }

    internal static XDocument UpdateSection(XDocument formXml, UpdateSectionRequest request)
    {
        var section = RequireSection(formXml, request.SectionLabel);

        if (request.NewLabel is not null)
            SetLabel(section, request.NewLabel);

        if (request.ShowLabel.HasValue)
            section.SetAttributeValue("showlabel", Bool(request.ShowLabel.Value));
        if (request.Visible.HasValue)
            section.SetAttributeValue("visible", Bool(request.Visible.Value));
        if (request.AvailableOnPhone.HasValue)
            section.SetAttributeValue("availableforphone", Bool(request.AvailableOnPhone.Value));
        if (request.Columns.HasValue)
            section.SetAttributeValue("columns", request.Columns.Value.ToString(CultureInfo.InvariantCulture));

        return formXml;
    }

    internal static XDocument RemoveSection(XDocument formXml, string sectionLabel)
    {
        var section = RequireSection(formXml, sectionLabel);
        section.Remove();
        return formXml;
    }

    // ── Fields ────────────────────────────────────────────────────────────

    internal static XDocument AddField(XDocument formXml, string sectionLabel, string fieldLogicalName, string classId, string displayName)
    {
        var section = RequireSection(formXml, sectionLabel);
        var rows = EnsureRows(section);
        rows.Add(new XElement("row", BuildFieldCell(fieldLogicalName, classId, displayName)));
        return formXml;
    }

    internal static XDocument RemoveField(XDocument formXml, string fieldLogicalName)
    {
        var control = formXml.Descendants("control")
            .FirstOrDefault(c => string.Equals(
                (string?)c.Attribute("datafieldname"), fieldLogicalName,
                StringComparison.OrdinalIgnoreCase));

        // Remove only the target cell; drop the row only if it becomes empty.
        // Removing the whole row would delete sibling cells in multi-column layouts.
        RemoveCellAndPruneRow(control?.Parent);
        return formXml;
    }

    internal static XDocument ReorderFields(XDocument formXml, string sectionLabel, IReadOnlyList<string> orderedFields, IDictionary<string, string> classIdsByField)
    {
        var section = RequireSection(formXml, sectionLabel);
        var rows = section.Element("rows");
        if (rows is null) return formXml;

        // Index existing field cells by logical name so we can preserve their
        // generated GUIDs and any authored attributes when reordering. Cells that
        // are not bound fields (sub-grids, web resources, notes, spacers) are kept
        // and re-appended after the ordered fields — reordering must not drop them.
        var existingCells = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        var nonFieldCells = new List<XElement>();
        foreach (var cell in rows.Descendants("cell"))
        {
            var dfn = (string?)cell.Element("control")?.Attribute("datafieldname");
            if (!string.IsNullOrEmpty(dfn))
            {
                if (!existingCells.ContainsKey(dfn))
                    existingCells[dfn] = cell;
            }
            else
            {
                nonFieldCells.Add(cell);
            }
        }

        rows.RemoveAll();

        foreach (var fieldName in orderedFields)
        {
            var cell = existingCells.TryGetValue(fieldName, out var existing)
                ? existing
                : BuildFieldCell(
                    fieldName,
                    classIdsByField.TryGetValue(fieldName, out var cid) ? cid : string.Empty,
                    fieldName);

            rows.Add(new XElement("row", cell));
        }

        foreach (var cell in nonFieldCells)
            rows.Add(new XElement("row", cell));

        return formXml;
    }

    // ── Sub-grids ─────────────────────────────────────────────────────────

    internal static XDocument AddSubgrid(XDocument formXml, AddSubgridRequest request)
    {
        var section = RequireSection(formXml, request.SectionLabel);
        var rows = EnsureRows(section);

        var cellId = NewBraceGuid();
        var controlId = NewBraceGuid();

        // Parameter element names must come from the schema's allowed set
        // (FormXmlControlType/parameters). There is no MaxRowsCount/HideSearchBox;
        // row count maps to RecordsPerPage and search-box visibility to EnableQuickFind.
        var parameters = new XElement("parameters",
            new XElement("ViewId", $"{{{request.DefaultViewId:D}}}"),
            new XElement("IsUserView", "false"),
            new XElement("EnableViewPicker", "false"),
            new XElement("TargetEntityType", request.TargetEntity),
            new XElement("RecordsPerPage", request.MaxRows.ToString(CultureInfo.InvariantCulture)),
            new XElement("EnableQuickFind", request.HideSearchBox ? "false" : "true"));

        if (!string.IsNullOrEmpty(request.Relationship))
            parameters.Add(new XElement("RelationshipName", request.Relationship));

        var control = new XElement("control",
            new XAttribute("id", controlId),
            new XAttribute("classid", ClassIdResolver.SubgridClassId),
            new XAttribute("indicationOfSubgrid", "true"),
            new XAttribute("disabled", "false"),
            parameters);

        var cell = new XElement("cell",
            new XAttribute("id", cellId),
            new XAttribute("showlabel", Bool(!request.HideLabel)),
            new XAttribute("locklevel", "0"),
            new XAttribute("availableforphone", Bool(!request.HideOnPhone)),
            Labels(request.Label),
            control);

        rows.Add(new XElement("row", cell));
        return formXml;
    }

    internal static XDocument RemoveSubgrid(XDocument formXml, string label)
    {
        foreach (var cell in formXml.Descendants("cell").ToList())
        {
            var control = cell.Element("control");
            if (control is null) continue;
            if ((string?)control.Attribute("classid") != ClassIdResolver.SubgridClassId) continue;

            var cellLabel = (string?)cell.Element("labels")?.Element("label")?.Attribute("description");
            if (string.Equals(cellLabel, label, StringComparison.OrdinalIgnoreCase))
            {
                // Remove only this cell; drop the row only if it becomes empty so
                // sibling cells in a multi-column row are preserved.
                RemoveCellAndPruneRow(cell);
                return formXml;
            }
        }

        return formXml;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static XElement RequireForm(XDocument formXml)
        => formXml.Root
           ?? throw new PpdsException(FormErrorCodes.InvalidFormXml, "Form XML has no root element.");

    private static XElement RequireTab(XDocument formXml, string tabLabel)
        => formXml.Descendants("tab").FirstOrDefault(t => LabelMatches(t, tabLabel))
           ?? throw new PpdsException(FormErrorCodes.TabNotFound, $"Tab '{tabLabel}' not found in form XML.");

    private static XElement RequireSection(XDocument formXml, string sectionLabel)
        => formXml.Descendants("section").FirstOrDefault(s => LabelMatches(s, sectionLabel))
           ?? throw new PpdsException(FormErrorCodes.SectionNotFound, $"Section '{sectionLabel}' not found in form XML.");

    private static bool LabelMatches(XElement element, string label)
    {
        var descAttr = element.Element("labels")?.Element("label")?.Attribute("description");
        return string.Equals((string?)descAttr, label, StringComparison.OrdinalIgnoreCase);
    }

    private static void SetLabel(XElement element, string label)
    {
        // Create the labels/label structure if absent so a rename never silently
        // no-ops on a tab/section that lacks an authored label.
        var labels = element.Element("labels");
        if (labels is null)
        {
            labels = new XElement("labels");
            element.AddFirst(labels);
        }

        var labelElement = labels.Element("label");
        if (labelElement is null)
        {
            labelElement = new XElement("label", new XAttribute("languagecode", DefaultLanguageCode));
            labels.Add(labelElement);
        }

        labelElement.SetAttributeValue("description", label);
    }

    /// <summary>
    /// Removes <paramref name="cell"/> from its parent row, then removes the row
    /// itself only if it has no remaining cells. Preserves sibling cells in
    /// multi-column rows.
    /// </summary>
    private static void RemoveCellAndPruneRow(XElement? cell)
    {
        if (cell is null) return;
        var row = cell.Parent;
        cell.Remove();
        if (row is not null && !row.HasElements)
            row.Remove();
    }

    private static XElement EnsureRows(XElement section)
    {
        var rows = section.Element("rows");
        if (rows is null)
        {
            rows = new XElement("rows");
            section.Add(rows);
        }
        return rows;
    }

    /// <summary>
    /// Builds a <c>&lt;columns&gt;</c> element with <paramref name="count"/> columns,
    /// each carrying a percentage width that sums to 100 and an empty
    /// <c>&lt;sections&gt;</c> container.
    /// </summary>
    private static XElement BuildColumns(int count)
    {
        if (count < 1) count = 1;
        var columns = new XElement("columns");
        var baseWidth = 100 / count;
        for (var i = 0; i < count; i++)
        {
            // Give the last column the remainder so widths sum to exactly 100.
            var width = i == count - 1 ? 100 - baseWidth * (count - 1) : baseWidth;
            columns.Add(new XElement("column",
                new XAttribute("width", $"{width.ToString(CultureInfo.InvariantCulture)}%"),
                new XElement("sections")));
        }
        return columns;
    }

    private static XElement Labels(string description)
        => new("labels",
            new XElement("label",
                new XAttribute("description", description),
                new XAttribute("languagecode", DefaultLanguageCode)));

    private static XElement BuildFieldCell(string fieldLogicalName, string classId, string displayName)
        => new("cell",
            new XAttribute("id", NewBraceGuid()),
            new XAttribute("showlabel", "1"),
            new XAttribute("locklevel", "0"),
            Labels(displayName),
            new XElement("control",
                // control id is the field logical name (xs:string), not a GUID.
                new XAttribute("id", fieldLogicalName),
                new XAttribute("classid", classId),
                new XAttribute("datafieldname", fieldLogicalName),
                new XAttribute("disabled", "false")));

    private static string Bool(bool value) => value ? "1" : "0";

    private static string NewBraceGuid() => $"{{{Guid.NewGuid():D}}}";
}
