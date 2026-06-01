using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using PPDS.Cli.Services.Forms;
using Xunit;

namespace PPDS.Cli.Tests.Services.Forms;

public class FormXmlEditorTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string NewBraceGuid() => $"{{{Guid.NewGuid():D}}}";

    /// <summary>
    /// Builds a minimal valid formxml document with one tab containing one section.
    /// All GUIDs are in brace format as required by Dataverse.
    /// </summary>
    private static XDocument BuildFormXml(
        string tabLabel = "General",
        string sectionLabel = "General",
        string? tabId = null,
        string? tabLabelId = null,
        string? sectionId = null,
        string? sectionLabelId = null)
    {
        tabId ??= NewBraceGuid();
        tabLabelId ??= NewBraceGuid();
        sectionId ??= NewBraceGuid();
        sectionLabelId ??= NewBraceGuid();

        return XDocument.Parse($@"<form>
  <tabs>
    <tab name=""{tabId}"" id=""{tabId}"" IsUserDefined=""0"" locklevel=""0"" showlabel=""1"" expanded=""1"" visible=""1"" availableforphone=""1"" labelid=""{tabLabelId}"">
      <labels><label description=""{tabLabel}"" languagecode=""1033"" /></labels>
      <columns>
        <column width=""100%"">
          <sections>
            <section name=""{sectionId}"" id=""{sectionId}"" IsUserDefined=""0"" locklevel=""0"" showlabel=""1"" visible=""1"" availableforphone=""1"" columns=""1"" labelid=""{sectionLabelId}"">
              <labels><label description=""{sectionLabel}"" languagecode=""1033"" /></labels>
              <rows />
            </section>
          </sections>
        </column>
      </columns>
    </tab>
  </tabs>
</form>");
    }

    // ── Tab tests ─────────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void UpdateTab_NewLabel_RenamesTab()
    {
        // Arrange — AC-09b: UpdateTab with NewLabel renames the label attribute
        var formXml = BuildFormXml(tabLabel: "Original Tab");
        var request = new UpdateTabRequest(
            EntityLogicalName: "account",
            FormName: "Main Form",
            TabLabel: "Original Tab",
            NewLabel: "Renamed Tab");

        // Act
        var result = FormXmlEditor.UpdateTab(formXml, request);

        // Assert
        var labelAttr = result.Descendants("tab")
            .Single()
            .Element("labels")?.Element("label")
            ?.Attribute("description");

        labelAttr.Should().NotBeNull();
        labelAttr!.Value.Should().Be("Renamed Tab");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RemoveTab_RemovesTabAndChildren()
    {
        // Arrange — AC-10: Removing a tab removes it and all child elements
        var formXml = BuildFormXml(tabLabel: "Tab To Remove");

        // Verify setup: one tab with one section before removal
        formXml.Descendants("tab").Should().HaveCount(1);
        formXml.Descendants("section").Should().HaveCount(1);

        // Act
        var result = FormXmlEditor.RemoveTab(formXml, "Tab To Remove");

        // Assert
        result.Descendants("tab").Should().BeEmpty();
        result.Descendants("section").Should().BeEmpty();
    }

    // ── Section tests ─────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void UpdateSection_NewLabel_RenamesSection()
    {
        // Arrange — AC-13b: UpdateSection with NewLabel renames the section label attribute
        var formXml = BuildFormXml(sectionLabel: "Original Section");
        var request = new UpdateSectionRequest(
            EntityLogicalName: "account",
            FormName: "Main Form",
            SectionLabel: "Original Section",
            NewLabel: "Renamed Section");

        // Act
        var result = FormXmlEditor.UpdateSection(formXml, request);

        // Assert
        var labelAttr = result.Descendants("section")
            .Single()
            .Element("labels")?.Element("label")
            ?.Attribute("description");

        labelAttr.Should().NotBeNull();
        labelAttr!.Value.Should().Be("Renamed Section");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RemoveSection_RemovesSectionAndChildren()
    {
        // Arrange — AC-14: Removing a section removes it and any child cells
        var formXml = BuildFormXml(sectionLabel: "Section To Remove");

        // Add a field so there is a child cell to verify is also removed
        FormXmlEditor.AddField(
            formXml,
            sectionLabel: "Section To Remove",
            fieldLogicalName: "name",
            classId: "{4273EDBD-AC1D-40d3-9FB2-095C621B552D}",
            displayName: "Name");

        formXml.Descendants("section").Should().HaveCount(1);
        formXml.Descendants("cell").Should().HaveCount(1);

        // Act
        var result = FormXmlEditor.RemoveSection(formXml, "Section To Remove");

        // Assert
        result.Descendants("section").Should().BeEmpty();
        result.Descendants("cell").Should().BeEmpty();
    }

    // ── Field tests ───────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void AddField_MultipleFields_AppendsInOrder()
    {
        // Arrange — AC-16: AddField appends cells in the order they are added
        var formXml = BuildFormXml(sectionLabel: "Details");
        const string classId = "{4273EDBD-AC1D-40d3-9FB2-095C621B552D}";

        // Act
        FormXmlEditor.AddField(formXml, "Details", "field1", classId, "Field One");
        FormXmlEditor.AddField(formXml, "Details", "field2", classId, "Field Two");

        // Assert
        var controls = formXml.Descendants("control")
            .Where(c => c.Attribute("datafieldname") is not null)
            .ToList();

        controls.Should().HaveCount(2);
        ((string?)controls[0].Attribute("datafieldname")).Should().Be("field1");
        ((string?)controls[1].Attribute("datafieldname")).Should().Be("field2");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RemoveField_ExistingField_RemovesFirstOccurrence()
    {
        // Arrange — AC-19: RemoveField removes the first cell for the given logical name
        var formXml = BuildFormXml(sectionLabel: "Details");
        const string classId = "{4273EDBD-AC1D-40d3-9FB2-095C621B552D}";

        FormXmlEditor.AddField(formXml, "Details", "name", classId, "Name");

        // Confirm the field was added
        formXml.Descendants("control")
            .Where(c => (string?)c.Attribute("datafieldname") == "name")
            .Should().HaveCount(1);

        // Act
        var result = FormXmlEditor.RemoveField(formXml, "name");

        // Assert
        result.Descendants("control")
            .Where(c => (string?)c.Attribute("datafieldname") == "name")
            .Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReorderFields_AuthoritativeList_RemovesUnlistedFields()
    {
        // Arrange — AC-20: ReorderFields uses the provided list as authoritative;
        // fields not listed are dropped and remaining fields appear in the specified order.
        var formXml = BuildFormXml(sectionLabel: "Details");
        const string classId = "{4273EDBD-AC1D-40d3-9FB2-095C621B552D}";

        FormXmlEditor.AddField(formXml, "Details", "field_a", classId, "A");
        FormXmlEditor.AddField(formXml, "Details", "field_b", classId, "B");
        FormXmlEditor.AddField(formXml, "Details", "field_c", classId, "C");

        var orderedFields = new List<string> { "field_c", "field_a" }; // field_b omitted
        var classIds = new Dictionary<string, string>
        {
            ["field_a"] = classId,
            ["field_c"] = classId,
        };

        // Act
        var result = FormXmlEditor.ReorderFields(formXml, "Details", orderedFields, classIds);

        // Assert
        var controls = result.Descendants("control")
            .Where(c => c.Attribute("datafieldname") is not null)
            .ToList();

        controls.Should().HaveCount(2, "field_b was not in the authoritative list and must be removed");
        ((string?)controls[0].Attribute("datafieldname")).Should().Be("field_c");
        ((string?)controls[1].Attribute("datafieldname")).Should().Be("field_a");
    }

    // ── Sub-grid tests ────────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void AddSubgrid_DefaultProperties_GeneratesCorrectXml()
    {
        // Arrange — AC-21: AddSubgrid with defaults produces a cell with the correct classid,
        // RecordsPerPage=5 (max-rows default), and EnableViewPicker=false.
        var formXml = BuildFormXml(sectionLabel: "Related");
        var viewId = new Guid("00000000-0000-0000-0000-000000000001");

        var request = new AddSubgridRequest(
            EntityLogicalName: "account",
            FormName: "Main Form",
            SectionLabel: "Related",
            Label: "My Subgrid",
            TargetEntity: "contact",
            DefaultViewId: viewId);

        // Act
        var result = FormXmlEditor.AddSubgrid(formXml, request);

        // Assert
        var control = result.Descendants("control")
            .Single(c => (string?)c.Attribute("classid") == ClassIdResolver.SubgridClassId);

        ((string?)control.Attribute("classid")).Should().Be("{E7A81278-8635-4d9e-8D4D-59480B391C5B}");

        var parameters = control.Element("parameters");
        parameters.Should().NotBeNull();
        ((string?)parameters!.Element("RecordsPerPage")).Should().Be("5");
        ((string?)parameters.Element("EnableViewPicker")).Should().Be("false");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RemoveSubgrid_ExistingLabel_RemovesControl()
    {
        // Arrange — AC-25: RemoveSubgrid by label removes the subgrid cell from the form.
        var formXml = BuildFormXml(sectionLabel: "Related");
        var viewId = new Guid("00000000-0000-0000-0000-000000000002");

        var request = new AddSubgridRequest(
            EntityLogicalName: "account",
            FormName: "Main Form",
            SectionLabel: "Related",
            Label: "Contacts",
            TargetEntity: "contact",
            DefaultViewId: viewId);

        FormXmlEditor.AddSubgrid(formXml, request);

        // Confirm the subgrid cell exists before removal
        formXml.Descendants("control")
            .Where(c => (string?)c.Attribute("classid") == ClassIdResolver.SubgridClassId)
            .Should().HaveCount(1);

        // Act
        var result = FormXmlEditor.RemoveSubgrid(formXml, "Contacts");

        // Assert — the subgrid control cell must be gone
        result.Descendants("control")
            .Where(c => (string?)c.Attribute("classid") == ClassIdResolver.SubgridClassId)
            .Should().BeEmpty();
    }
}
