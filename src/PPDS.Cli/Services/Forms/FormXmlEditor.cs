using System.Collections.Generic;
using System.Xml.Linq;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.Forms;

/// <summary>
/// Pure static XML manipulation helpers for Dataverse formxml.
/// No I/O — takes XDocument, returns XDocument.
/// </summary>
internal static class FormXmlEditor
{
    // ── Tabs ──────────────────────────────────────────────────────────────

    internal static XDocument AddTab(XDocument formXml, AddTabRequest request)
    {
        var form = RequireForm(formXml);
        var tabId = NewBraceGuid();
        var labelId = NewBraceGuid();

        var columns = new XElement("columns");
        for (var i = 0; i < request.Columns; i++)
        {
            columns.Add(new XElement("column",
                new XAttribute("factoryType", "STANDARD"),
                new XAttribute("width", "1fr")));
        }

        var tab = new XElement("tab",
            new XAttribute("name", tabId),
            new XAttribute("id", tabId),
            new XAttribute("IsUserDefined", "0"),
            new XAttribute("locklevel", "0"),
            new XAttribute("showlabel", request.ShowLabel ? "1" : "0"),
            new XAttribute("expanded", request.Expanded ? "1" : "0"),
            new XAttribute("visible", request.Visible ? "1" : "0"),
            new XAttribute("availableForPhone", request.AvailableOnPhone ? "1" : "0"),
            new XElement("labels",
                new XElement("label",
                    new XAttribute("description", request.Label),
                    new XAttribute("languagecode", "1033"))),
            new XElement("labelid", labelId),
            new XElement("displayconditionxml"),
            columns);

        form.Add(tab);
        return formXml;
    }

    internal static XDocument UpdateTab(XDocument formXml, UpdateTabRequest request)
    {
        var tab = RequireTab(formXml, request.TabLabel);

        if (request.NewLabel is not null)
        {
            var labelEl = tab.Element("labels")?.Element("label");
            labelEl?.SetAttributeValue("description", request.NewLabel);
        }

        if (request.ShowLabel.HasValue)
            tab.SetAttributeValue("showlabel", request.ShowLabel.Value ? "1" : "0");
        if (request.Expanded.HasValue)
            tab.SetAttributeValue("expanded", request.Expanded.Value ? "1" : "0");
        if (request.Visible.HasValue)
            tab.SetAttributeValue("visible", request.Visible.Value ? "1" : "0");
        if (request.AvailableOnPhone.HasValue)
            tab.SetAttributeValue("availableForPhone", request.AvailableOnPhone.Value ? "1" : "0");
        if (request.Columns.HasValue)
        {
            var columns = tab.Element("columns");
            if (columns is not null)
            {
                columns.RemoveAll();
                for (var i = 0; i < request.Columns.Value; i++)
                {
                    columns.Add(new XElement("column",
                        new XAttribute("factoryType", "STANDARD"),
                        new XAttribute("width", "1fr")));
                }
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
                $"Tab '{request.TabLabel}' has no columns element.");

        var sectionId = NewBraceGuid();
        var labelId = NewBraceGuid();

        var section = new XElement("section",
            new XAttribute("name", sectionId),
            new XAttribute("id", sectionId),
            new XAttribute("IsUserDefined", "0"),
            new XAttribute("locklevel", "0"),
            new XAttribute("showlabel", request.ShowLabel ? "1" : "0"),
            new XAttribute("visible", request.Visible ? "1" : "0"),
            new XAttribute("expanded", request.Expanded ? "1" : "0"),
            new XAttribute("availableForPhone", request.AvailableOnPhone ? "1" : "0"),
            new XAttribute("columns", request.Columns.ToString()),
            new XElement("labels",
                new XElement("label",
                    new XAttribute("description", request.Label),
                    new XAttribute("languagecode", "1033"))),
            new XElement("labelid", labelId),
            new XElement("rows"));

        column.Add(section);
        return formXml;
    }

    internal static XDocument UpdateSection(XDocument formXml, UpdateSectionRequest request)
    {
        var section = RequireSection(formXml, request.SectionLabel);

        if (request.NewLabel is not null)
        {
            var labelEl = section.Element("labels")?.Element("label");
            labelEl?.SetAttributeValue("description", request.NewLabel);
        }

        if (request.ShowLabel.HasValue)
            section.SetAttributeValue("showlabel", request.ShowLabel.Value ? "1" : "0");
        if (request.Expanded.HasValue)
            section.SetAttributeValue("expanded", request.Expanded.Value ? "1" : "0");
        if (request.Visible.HasValue)
            section.SetAttributeValue("visible", request.Visible.Value ? "1" : "0");
        if (request.AvailableOnPhone.HasValue)
            section.SetAttributeValue("availableForPhone", request.AvailableOnPhone.Value ? "1" : "0");
        if (request.Columns.HasValue)
            section.SetAttributeValue("columns", request.Columns.Value.ToString());

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
        var rows = section.Element("rows");
        if (rows is null)
        {
            rows = new XElement("rows");
            section.Add(rows);
        }

        var cellId = NewBraceGuid();
        var cell = BuildFieldCell(cellId, fieldLogicalName, classId, displayName);

        // Wrap in a row element
        var row = new XElement("row", cell);
        rows.Add(row);
        return formXml;
    }

    internal static XDocument RemoveField(XDocument formXml, string fieldLogicalName)
    {
        // Remove first occurrence of the field control
        var control = formXml.Descendants("control")
            .FirstOrDefault(c => string.Equals(
                (string?)c.Attribute("datafieldname"), fieldLogicalName,
                StringComparison.OrdinalIgnoreCase));

        control?.Parent?.Remove(); // Remove the enclosing <cell>
        return formXml;
    }

    internal static XDocument ReorderFields(XDocument formXml, string sectionLabel, IReadOnlyList<string> orderedFields, IDictionary<string, string> classIdsByField)
    {
        var section = RequireSection(formXml, sectionLabel);
        var rows = section.Element("rows");
        if (rows is null) return formXml;

        // Build lookup of existing cells by field logical name
        var existingCells = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in rows.Descendants("cell"))
        {
            var control = cell.Element("control");
            if (control is null) continue;
            var dfn = (string?)control.Attribute("datafieldname");
            if (!string.IsNullOrEmpty(dfn) && !existingCells.ContainsKey(dfn))
                existingCells[dfn] = cell;
        }

        // Remove all rows, then rebuild in the specified order
        rows.RemoveAll();

        foreach (var fieldName in orderedFields)
        {
            XElement cell;
            if (existingCells.TryGetValue(fieldName, out var existing))
            {
                cell = existing;
            }
            else
            {
                var classId = classIdsByField.TryGetValue(fieldName, out var cid) ? cid : string.Empty;
                cell = BuildFieldCell(NewBraceGuid(), fieldName, classId, fieldName);
            }

            rows.Add(new XElement("row", cell));
        }

        return formXml;
    }

    // ── Sub-grids ─────────────────────────────────────────────────────────

    internal static XDocument AddSubgrid(XDocument formXml, AddSubgridRequest request)
    {
        var section = RequireSection(formXml, request.SectionLabel);
        var rows = section.Element("rows");
        if (rows is null)
        {
            rows = new XElement("rows");
            section.Add(rows);
        }

        var cellId = NewBraceGuid();
        var controlId = NewBraceGuid();

        var parameters = new XElement("parameters",
            new XElement("ViewId", $"{{{request.DefaultViewId:D}}}"),
            new XElement("IsUserView", "false"),
            new XElement("TargetEntityType", request.TargetEntity),
            new XElement("RelationshipName", request.Relationship ?? string.Empty),
            new XElement("MaxRowsCount", request.MaxRows.ToString()),
            new XElement("EnableViewPicker", "false"),
            new XElement("RecordsPerPage", request.MaxRows.ToString()),
            new XElement("HideSearchBox", request.HideSearchBox ? "1" : "0"));

        var control = new XElement("control",
            new XAttribute("id", controlId),
            new XAttribute("classid", ClassIdResolver.SubgridClassId),
            new XAttribute("isunresolved", "false"),
            new XAttribute("disabled", "false"),
            parameters);

        var cell = new XElement("cell",
            new XAttribute("id", cellId),
            new XAttribute("showlabel", request.HideLabel ? "0" : "1"),
            new XAttribute("locklevel", "0"),
            new XElement("labels",
                new XElement("label",
                    new XAttribute("description", request.Label),
                    new XAttribute("languagecode", "1033"))),
            control);

        rows.Add(new XElement("row", cell));
        return formXml;
    }

    internal static XDocument RemoveSubgrid(XDocument formXml, string label)
    {
        // Find control cell whose label matches
        foreach (var cell in formXml.Descendants("cell").ToList())
        {
            var control = cell.Element("control");
            if (control is null) continue;
            if ((string?)control.Attribute("classid") != ClassIdResolver.SubgridClassId) continue;

            var cellLabel = (string?)cell.Element("labels")?.Element("label")?.Attribute("description");
            if (string.Equals(cellLabel, label, StringComparison.OrdinalIgnoreCase))
            {
                cell.Parent?.Remove(); // remove enclosing <row>
                return formXml;
            }
        }

        return formXml;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static XElement RequireForm(XDocument formXml)
    {
        return formXml.Root
            ?? throw new PpdsException(FormErrorCodes.InvalidFormXml, "Form XML has no root element.");
    }

    private static XElement RequireTab(XDocument formXml, string tabLabel)
    {
        var tab = formXml.Descendants("tab")
            .FirstOrDefault(t => LabelMatches(t, tabLabel));

        return tab ?? throw new PpdsException(FormErrorCodes.TabNotFound,
            $"Tab '{tabLabel}' not found in form XML.");
    }

    private static XElement RequireSection(XDocument formXml, string sectionLabel)
    {
        var section = formXml.Descendants("section")
            .FirstOrDefault(s => LabelMatches(s, sectionLabel));

        return section ?? throw new PpdsException(FormErrorCodes.SectionNotFound,
            $"Section '{sectionLabel}' not found in form XML.");
    }

    private static bool LabelMatches(XElement element, string label)
    {
        var descAttr = element.Element("labels")?.Element("label")?.Attribute("description");
        return string.Equals((string?)descAttr, label, StringComparison.OrdinalIgnoreCase);
    }

    private static string NewBraceGuid() => $"{{{Guid.NewGuid():D}}}";

    private static XElement BuildFieldCell(string cellId, string fieldLogicalName, string classId, string displayName)
    {
        return new XElement("cell",
            new XAttribute("id", cellId),
            new XAttribute("showlabel", "1"),
            new XAttribute("locklevel", "0"),
            new XElement("labels",
                new XElement("label",
                    new XAttribute("description", displayName),
                    new XAttribute("languagecode", "1033"))),
            new XElement("control",
                new XAttribute("id", fieldLogicalName),
                new XAttribute("classid", classId),
                new XAttribute("datafieldname", fieldLogicalName),
                new XAttribute("disabled", "false")));
    }
}
