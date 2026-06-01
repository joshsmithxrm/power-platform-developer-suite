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
    /// Builds a minimal form document whose ids and labelids are all valid brace-format GUIDs.
    /// </summary>
    private static XDocument BuildValidFormXml(
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
    <tab name=""{tabId}"" id=""{tabId}"" IsUserDefined=""0"" locklevel=""0"" showlabel=""1"" expanded=""1"" visible=""1"" availableForPhone=""1"">
      <labels><label description=""General"" languagecode=""1033"" /></labels>
      <labelid>{tabLabelId}</labelid>
      <displayconditionxml/>
      <columns>
        <column factoryType=""STANDARD"" width=""1fr"">
          <sections>
            <section name=""{sectionId}"" id=""{sectionId}"" IsUserDefined=""0"" locklevel=""0"" showlabel=""1"" visible=""1"" expanded=""1"" availableForPhone=""1"" columns=""1"">
              <labels><label description=""General"" languagecode=""1033"" /></labels>
              <labelid>{sectionLabelId}</labelid>
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
        // Arrange — AC-03: A well-formed document with valid brace-format GUIDs passes validation.
        var formXml = BuildValidFormXml();

        // Act & Assert
        var act = () => FormXmlValidator.Validate(formXml);
        act.Should().NotThrow();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Validate_InvalidXml_ThrowsWithInvalidFormXml()
    {
        // Arrange — AC-03: Because the placeholder XSD accepts anything, GUID validation is the
        // primary rejection gate. A non-brace GUID triggers the InvalidFormXml error code.
        var formXml = XDocument.Parse($@"<form>
  <tabs>
    <tab id=""12345678-1234-1234-1234-123456789012"" name=""t"" IsUserDefined=""0"" locklevel=""0"" showlabel=""1"" expanded=""1"" visible=""1"" availableForPhone=""1"">
      <labels><label description=""Tab"" languagecode=""1033"" /></labels>
      <labelid>{NewBraceGuid()}</labelid>
      <displayconditionxml/>
      <columns />
    </tab>
  </tabs>
</form>");

        // Act
        var act = () => FormXmlValidator.Validate(formXml);

        // Assert
        act.Should().Throw<PpdsValidationException>()
            .Which.ErrorCode.Should().Be(FormErrorCodes.InvalidFormXml);
    }

    // ── AC-04: Error message mentions the bad value ───────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void Validate_InvalidXml_ErrorMessageIncludesElement()
    {
        // Arrange — AC-04: When a non-brace GUID is present the error message must mention
        // either the bad value or the attribute name so the caller can locate the problem.
        const string badGuid = "12345678-1234-1234-1234-123456789012";
        var formXml = XDocument.Parse($@"<form>
  <tabs>
    <tab id=""{badGuid}"" name=""t"" IsUserDefined=""0"" locklevel=""0"" showlabel=""1"" expanded=""1"" visible=""1"" availableForPhone=""1"">
      <labels><label description=""Tab"" languagecode=""1033"" /></labels>
      <labelid>{NewBraceGuid()}</labelid>
      <displayconditionxml/>
      <columns />
    </tab>
  </tabs>
</form>");

        // Act
        var act = () => FormXmlValidator.ValidateGuids(formXml);

        // Assert — message must reference the bad GUID value or the attribute name "id"
        act.Should().Throw<PpdsValidationException>()
            .Which.Message.Should().ContainAny(badGuid, "id", "labelid");
    }

    // ── AC-05: Non-brace GUID throws InvalidFormXml ───────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void Validate_NonBraceGuid_ThrowsWithInvalidFormXml()
    {
        // Arrange — AC-05: An id without surrounding braces is rejected with InvalidFormXml.
        var formXml = XDocument.Parse($@"<form>
  <tabs>
    <tab id=""12345678-1234-1234-1234-123456789012"" name=""t"" IsUserDefined=""0"" locklevel=""0"" showlabel=""1"" expanded=""1"" visible=""1"" availableForPhone=""1"">
      <labels><label description=""Tab"" languagecode=""1033"" /></labels>
      <labelid>{NewBraceGuid()}</labelid>
      <displayconditionxml/>
      <columns />
    </tab>
  </tabs>
</form>");

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
        // Arrange — AC-06: Two elements sharing the same id value must be rejected.
        var sharedGuid = NewBraceGuid();
        var formXml = XDocument.Parse($@"<form>
  <tabs>
    <tab id=""{sharedGuid}"" name=""{sharedGuid}"" IsUserDefined=""0"" locklevel=""0"" showlabel=""1"" expanded=""1"" visible=""1"" availableForPhone=""1"">
      <labels><label description=""Tab"" languagecode=""1033"" /></labels>
      <labelid>{NewBraceGuid()}</labelid>
      <displayconditionxml/>
      <columns>
        <column factoryType=""STANDARD"" width=""1fr"">
          <sections>
            <section id=""{sharedGuid}"" name=""{NewBraceGuid()}"" IsUserDefined=""0"" locklevel=""0"" showlabel=""1"" visible=""1"" expanded=""1"" availableForPhone=""1"" columns=""1"">
              <labels><label description=""General"" languagecode=""1033"" /></labels>
              <labelid>{NewBraceGuid()}</labelid>
              <rows />
            </section>
          </sections>
        </column>
      </columns>
    </tab>
  </tabs>
</form>");

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
        // Arrange — well-formed {xxxxxxxx-...} ids must not throw.
        var braceGuid = NewBraceGuid();
        var labelId = NewBraceGuid();
        var formXml = XDocument.Parse($@"<form>
  <tabs>
    <tab id=""{braceGuid}"" name=""{braceGuid}"">
      <labelid>{labelId}</labelid>
    </tab>
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
    <tab id=""abc"" name=""t"">
      <labelid>{00000000-0000-0000-0000-000000000001}</labelid>
    </tab>
  </tabs>
</form>");

        // Act
        var act = () => FormXmlValidator.ValidateGuids(formXml);

        // Assert
        act.Should().Throw<PpdsValidationException>()
            .Which.ErrorCode.Should().Be(FormErrorCodes.InvalidFormXml);
    }
}
