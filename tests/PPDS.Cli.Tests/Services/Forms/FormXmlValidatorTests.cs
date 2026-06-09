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
    public void Validate_MalformedGuidOnGuidTypedId_ThrowsWithInvalidFormXml()
    {
        // Arrange — #1209 AC-4: A genuinely malformed id (not a GUID at all) on a
        // GUID-typed attribute (<tab>.id is FormGuidType) must still be rejected.
        // Format is now enforced by the XSD's FormGuidType pattern, not a custom check.
        var formXml = BuildValidFormXml(tabId: "not-a-guid");

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
    public void Validate_DuplicateGuid_ErrorMessageIncludesValue()
    {
        // Arrange — the duplicate-GUID failure message must mention the offending
        // value so the caller can locate it. (Format failures are now surfaced by the
        // schema, which has its own line/position message — see schema-violation test.)
        var sharedGuid = NewBraceGuid();
        var formXml = BuildValidFormXml(tabId: sharedGuid, sectionId: sharedGuid);

        // Act
        var act = () => FormXmlValidator.ValidateGuids(formXml);

        // Assert
        act.Should().Throw<PpdsValidationException>()
            .Which.Message.Should().ContainAll(sharedGuid, "duplicate");
    }

    // ── #1209: Unbraced GUIDs and logical-name ids round-trip ─────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void Validate_UnbracedGuidOnSectionId_DoesNotThrow()
    {
        // Arrange — #1209 AC-3: Real modern forms carry valid but *unbraced* GUIDs on
        // id/labelid (observed on <section>). The XSD's FormGuidType (\{?...\}?) accepts
        // them, and the custom check no longer over-rejects them.
        var formXml = BuildValidFormXml(sectionId: "8d5e5d54-cae9-42ac-a610-85e840196095");

        // Act & Assert
        var act = () => FormXmlValidator.Validate(formXml);
        act.Should().NotThrow();
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
    public void ValidateGuids_UnbracedGuid_DoesNotThrow()
    {
        // Arrange — #1209 AC-3: ValidateGuids itself must tolerate unbraced GUIDs
        // (format is the schema's job). Braced and unbraced forms are still treated
        // as the same GUID for uniqueness.
        var formXml = XDocument.Parse(@"<form>
  <tabs>
    <tab id=""8d5e5d54-cae9-42ac-a610-85e840196095"" name=""t"" labelid=""{00000000-0000-0000-0000-000000000001}"" />
  </tabs>
</form>");

        // Act & Assert
        var act = () => FormXmlValidator.ValidateGuids(formXml);
        act.Should().NotThrow();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateGuids_ControlIdLogicalName_DoesNotThrow()
    {
        // Arrange — a <control> id is the column logical name (xs:string), not a GUID,
        // so it must not participate in GUID validation.
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

    // ── #1209: Real modern-form fragments (data / dependency logical-name ids) ──

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateGuids_DataAndDependencyLogicalNameIds_DoNotThrow()
    {
        // Arrange — #1209 AC-2: Real Microsoft model-driven forms carry logical names
        // (not GUIDs) on <data>.id and <dependency>.id. The XSD types these as
        // xs:string; ValidateGuids must ignore them rather than demand a GUID. These
        // fragments are taken verbatim from a live Contact Main form's <formjsdata> /
        // dependency blocks.
        var formXml = XDocument.Parse(@"<form>
  <formjsdata>
    <data id=""fullname"" />
    <data id=""relationshipdata"" />
    <data id=""mobileofflineprofileitemid"" />
  </formjsdata>
  <DataSource>
    <dependencies>
      <dependency id=""absoluteurl"" />
      <dependency id=""parentsite"" />
      <dependency id=""relativeurl"" />
      <dependency id=""parentsiteorlocation"" />
    </dependencies>
  </DataSource>
</form>");

        // Act & Assert — note: this exercises ValidateGuids directly (the GUID gate),
        // independent of the surrounding schema structure.
        var act = () => FormXmlValidator.ValidateGuids(formXml);
        act.Should().NotThrow();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateGuids_RepeatedLogicalNameIds_DoNotCollide()
    {
        // Arrange — #1209: logical-name ids are plain strings that may legitimately
        // repeat across a form (e.g. the same dependency referenced in two blocks).
        // They must not trip the GUID-uniqueness check.
        var formXml = XDocument.Parse(@"<form>
  <dependencies>
    <dependency id=""absoluteurl"" />
  </dependencies>
  <somethingelse>
    <dependency id=""absoluteurl"" />
  </somethingelse>
</form>");

        // Act & Assert
        var act = () => FormXmlValidator.ValidateGuids(formXml);
        act.Should().NotThrow();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateGuids_BracedAndUnbracedSameGuid_AreDuplicates()
    {
        // Arrange — #1209: "{G}" and "G" denote the same GUID, so they must collide
        // under the uniqueness check after brace normalisation.
        var formXml = XDocument.Parse(@"<form>
  <tabs>
    <tab id=""{8d5e5d54-cae9-42ac-a610-85e840196095}"" name=""t"">
      <columns><column width=""100%""><sections>
        <section id=""8d5e5d54-cae9-42ac-a610-85e840196095"" name=""s""><rows /></section>
      </sections></column></columns>
    </tab>
  </tabs>
</form>");

        // Act
        var act = () => FormXmlValidator.ValidateGuids(formXml);

        // Assert
        act.Should().Throw<PpdsValidationException>()
            .Which.ErrorCode.Should().Be(FormErrorCodes.DuplicateGuid);
    }
}
