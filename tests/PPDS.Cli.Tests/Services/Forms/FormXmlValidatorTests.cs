using System;
using System.Xml.Linq;
using FluentAssertions;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Forms;
using Xunit;

namespace PPDS.Cli.Tests.Services.Forms;

public class FormXmlValidatorTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string NewBraceGuid() => $"{{{Guid.NewGuid():D}}}";

    /// <summary>
    /// Builds a schema-valid form document (conforming to the bundled FormXml.xsd)
    /// whose ids and labelids are all valid brace-format GUIDs. Callers may override
    /// individual ids to inject a specific defect under test.
    /// </summary>
    private static XDocument BuildValidFormXml(
        string? tabId = null,
        string? tabLabelId = null,
        string? sectionId = null,
        string? sectionLabelId = null,
        string? formAttributes = null)
    {
        tabId ??= NewBraceGuid();
        tabLabelId ??= NewBraceGuid();
        sectionId ??= NewBraceGuid();
        sectionLabelId ??= NewBraceGuid();

        // formAttributes lets a caller inject extra attributes onto the root <form>
        // element (e.g. headerdensity, or an unknown future attribute) to exercise
        // the schema's forward-compatibility behaviour.
        var formAttrs = string.IsNullOrEmpty(formAttributes) ? string.Empty : " " + formAttributes;

        return XDocument.Parse($@"<form{formAttrs}>
  <tabs>
    <tab name=""{tabId}"" id=""{tabId}"" IsUserDefined=""0"" locklevel=""0"" showlabel=""1"" expanded=""1"" visible=""1"" availableforphone=""1"" labelid=""{tabLabelId}"">
      <labels><label description=""General"" languagecode=""1033"" /></labels>
      <columns>
        <column width=""100%"">
          <sections>
            <section name=""{sectionId}"" id=""{sectionId}"" IsUserDefined=""0"" locklevel=""0"" showlabel=""1"" visible=""1"" availableforphone=""1"" columns=""1"" labelid=""{sectionLabelId}"">
              <labels><label description=""General"" languagecode=""1033"" /></labels>
              <rows />
            </section>
          </sections>
        </column>
      </columns>
    </tab>
  </tabs>
</form>");
    }

    // ── AC-03: Validate baseline ──────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void Validate_ValidXml_DoesNotThrow()
    {
        // Arrange — AC-03: A well-formed, schema-conforming document with valid
        // brace-format GUIDs passes both XSD and GUID validation.
        var formXml = BuildValidFormXml();

        // Act & Assert
        var act = () => FormXmlValidator.Validate(formXml);
        act.Should().NotThrow();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Validate_InvalidXml_ThrowsWithInvalidFormXml()
    {
        // Arrange — AC-03/AC-05: The structure is schema-valid (FormGuidType permits
        // unbraced GUIDs) but the tab id lacks braces, so the custom GUID check rejects it.
        var formXml = BuildValidFormXml(tabId: "12345678-1234-1234-1234-123456789012");

        // Act
        var act = () => FormXmlValidator.Validate(formXml);

        // Assert
        act.Should().Throw<PpdsValidationException>()
            .Which.ErrorCode.Should().Be(FormErrorCodes.InvalidFormXml);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Validate_SchemaViolation_ThrowsWithInvalidFormXml()
    {
        // Arrange — AC-03/AC-04: A structurally invalid document (tab placed directly
        // under form, bypassing the required <tabs> wrapper) fails XSD validation.
        var formXml = XDocument.Parse($@"<form>
  <tab id=""{NewBraceGuid()}"" name=""t"">
    <labels><label description=""Tab"" languagecode=""1033"" /></labels>
  </tab>
</form>");

        // Act
        var act = () => FormXmlValidator.Validate(formXml);

        // Assert
        act.Should().Throw<PpdsValidationException>()
            .Which.ErrorCode.Should().Be(FormErrorCodes.InvalidFormXml);
    }

    // ── #1203: Modern UCI form attributes pass validation ─────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void Validate_ModernMainFormWithHeaderDensity_DoesNotThrow()
    {
        // Arrange — #1203 AC-1: A modern model-driven (UCI) Main form carries
        // headerdensity on the root <form> element (observed value
        // "HighWithControls"). The bundled XSD must accept it.
        var formXml = BuildValidFormXml(
            formAttributes: @"showImage=""true"" headerdensity=""HighWithControls""");

        // Act & Assert
        var act = () => FormXmlValidator.Validate(formXml);
        act.Should().NotThrow();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Validate_UnknownFutureFormAttribute_DoesNotThrow()
    {
        // Arrange — #1203 AC-3: The schema accepts unknown <form> attributes
        // leniently (xs:anyAttribute processContents="lax") so a future
        // server-side addition does not hard-fail valid forms.
        var formXml = BuildValidFormXml(
            formAttributes: @"somefutureattribute=""whatever""");

        // Act & Assert
        var act = () => FormXmlValidator.Validate(formXml);
        act.Should().NotThrow();
    }

    // ── AC-04: Error message identifies the failing element/position ──────────

    [Fact]
    [Trait("Category", "Unit")]
    public void Validate_SchemaViolation_ErrorMessageIncludesElementAndLine()
    {
        // Arrange — AC-04: An unexpected child element produces a message that names
        // the element and a line/position so the caller can locate the problem.
        var formXml = XDocument.Parse($@"<form>
  <tabs>
    <tab name=""t"" id=""{NewBraceGuid()}"" IsUserDefined=""0"" locklevel=""0"" showlabel=""1"" expanded=""1"" visible=""1"" availableforphone=""1"">
      <labels><label description=""Tab"" languagecode=""1033"" /></labels>
      <columns>
        <column width=""100%"">
          <bogusElement/>
        </column>
      </columns>
    </tab>
  </tabs>
</form>");

        // Act
        var act = () => FormXmlValidator.Validate(formXml);

        // Assert — message references the offending element and a line number
        act.Should().Throw<PpdsValidationException>()
            .Which.Message.Should().ContainAll("bogusElement", "line");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Validate_NonBraceGuid_ErrorMessageIncludesAttribute()
    {
        // Arrange — AC-04: The GUID-format failure message must mention the bad value
        // or the attribute name so the caller can locate the problem.
        const string badGuid = "12345678-1234-1234-1234-123456789012";
        var formXml = BuildValidFormXml(tabId: badGuid);

        // Act
        var act = () => FormXmlValidator.ValidateGuids(formXml);

        // Assert
        act.Should().Throw<PpdsValidationException>()
            .Which.Message.Should().ContainAny(badGuid, "id", "labelid");
    }

    // ── AC-05: Non-brace GUID throws InvalidFormXml ───────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void Validate_NonBraceGuid_ThrowsWithInvalidFormXml()
    {
        // Arrange — AC-05: An id without surrounding braces is rejected with InvalidFormXml.
        var formXml = BuildValidFormXml(sectionId: "12345678-1234-1234-1234-123456789012");

        // Act
        var act = () => FormXmlValidator.Validate(formXml);

        // Assert
        act.Should().Throw<PpdsValidationException>()
            .Which.ErrorCode.Should().Be(FormErrorCodes.InvalidFormXml);
    }

    // ── AC-06: Duplicate GUID throws DuplicateGuid ────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void Validate_DuplicateGuid_ThrowsWithDuplicateGuid()
    {
        // Arrange — AC-06: Two elements sharing the same brace-format id value must
        // be rejected. The document is otherwise schema-valid so the duplicate-GUID
        // check (not the schema) is the rejection gate.
        var sharedGuid = NewBraceGuid();
        var formXml = BuildValidFormXml(tabId: sharedGuid, sectionId: sharedGuid);

        // Act
        var act = () => FormXmlValidator.Validate(formXml);

        // Assert
        act.Should().Throw<PpdsValidationException>()
            .Which.ErrorCode.Should().Be(FormErrorCodes.DuplicateGuid);
    }

    // ── ValidateGuids unit tests ───────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateGuids_BraceFormatGuid_Passes()
    {
        // Arrange — well-formed {xxxxxxxx-...} id/labelid attributes must not throw.
        var braceGuid = NewBraceGuid();
        var labelId = NewBraceGuid();
        var formXml = XDocument.Parse($@"<form>
  <tabs>
    <tab id=""{braceGuid}"" name=""{braceGuid}"" labelid=""{labelId}"" />
  </tabs>
</form>");

        // Act & Assert
        var act = () => FormXmlValidator.ValidateGuids(formXml);
        act.Should().NotThrow();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateGuids_NonBraceGuid_Throws()
    {
        // Arrange — an id value of "abc" is not a GUID at all and must be rejected.
        var formXml = XDocument.Parse(@"<form>
  <tabs>
    <tab id=""abc"" name=""t"" labelid=""{00000000-0000-0000-0000-000000000001}"" />
  </tabs>
</form>");

        // Act
        var act = () => FormXmlValidator.ValidateGuids(formXml);

        // Assert
        act.Should().Throw<PpdsValidationException>()
            .Which.ErrorCode.Should().Be(FormErrorCodes.InvalidFormXml);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateGuids_ControlIdLogicalName_DoesNotThrow()
    {
        // Arrange — a <control> id is the column logical name (xs:string), not a GUID,
        // so it must be exempt from the brace-format check.
        var formXml = XDocument.Parse($@"<form>
  <tabs>
    <tab id=""{NewBraceGuid()}"" name=""t"">
      <columns>
        <column width=""100%"">
          <sections>
            <section id=""{NewBraceGuid()}"" name=""s"">
              <rows><row><cell id=""{NewBraceGuid()}"">
                <control id=""hsl_licensenumber"" datafieldname=""hsl_licensenumber"" />
              </cell></row></rows>
            </section>
          </sections>
        </column>
      </columns>
    </tab>
  </tabs>
</form>");

        // Act & Assert
        var act = () => FormXmlValidator.ValidateGuids(formXml);
        act.Should().NotThrow();
    }
}
