using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.Schema;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.Forms;

/// <summary>
/// Validates formxml against the bundled Dataverse customization XSD and checks GUID format/uniqueness.
/// </summary>
internal static class FormXmlValidator
{
    private static readonly Regex BraceGuidRegex = new(
        @"^\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}$",
        RegexOptions.Compiled);

    /// <summary>
    /// Validates formxml against the bundled XSD schema and checks GUID constraints.
    /// Throws <see cref="PpdsValidationException"/> on failure.
    /// </summary>
    internal static void Validate(XDocument formXml)
    {
        ValidateSchema(formXml);
        ValidateGuids(formXml);
    }

    private static void ValidateSchema(XDocument formXml)
    {
        // Schema validation is mandatory. A load/compile failure indicates a
        // packaging defect and must surface — never silently fall back to
        // GUID-only checks (that would let invalid form XML through).
        XmlSchemaSet schemaSet;
        try
        {
            schemaSet = FormSchemaResources.GetSchemaSet();
        }
        catch (Exception ex)
        {
            throw new PpdsException(
                ErrorCodes.Operation.Internal,
                "Failed to load the bundled Dataverse form schema for validation. " +
                "This is a packaging defect, not a problem with the form XML.",
                ex);
        }

        string? errorMessage = null;
        formXml.Validate(schemaSet, (_, e) =>
        {
            if (errorMessage is not null || e.Severity != XmlSeverityType.Error)
                return;

            var line = e.Exception?.LineNumber ?? 0;
            var position = e.Exception?.LinePosition ?? 0;
            errorMessage =
                $"Form XML schema validation failed at line {line}, position {position}: {e.Message}";
        });

        if (errorMessage is not null)
            throw new PpdsValidationException("formxml", errorMessage)
            {
                ErrorCode = FormErrorCodes.InvalidFormXml
            };
    }

    /// <summary>
    /// Validates that structural <c>id</c> and <c>labelid</c> attributes use brace
    /// format and are unique within the document.
    /// </summary>
    /// <remarks>
    /// The <c>id</c> on a <c>&lt;control&gt;</c> is the column logical name
    /// (<c>xs:string</c> per the schema), not a GUID, so it is excluded from the
    /// brace-format and uniqueness checks. Its <c>labelid</c>, when present, is a
    /// GUID and is still checked.
    /// </remarks>
    internal static void ValidateGuids(XDocument formXml)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in formXml.Descendants())
        {
            var isControl = element.Name.LocalName == "control";

            foreach (var attrName in new[] { "id", "labelid" })
            {
                // A control's id is a logical name, not a GUID — skip it.
                if (isControl && attrName == "id") continue;

                var attr = element.Attribute(attrName);
                if (attr is null) continue;

                var value = attr.Value;
                if (string.IsNullOrEmpty(value)) continue;

                if (!BraceGuidRegex.IsMatch(value))
                {
                    throw new PpdsValidationException("formxml",
                        $"Form XML contains a non-brace-format GUID '{value}' on attribute '{attrName}'. " +
                        $"All id and labelid attributes must use the format {{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}}.")
                    {
                        ErrorCode = FormErrorCodes.InvalidFormXml
                    };
                }

                if (!seen.Add(value))
                {
                    throw new PpdsValidationException("formxml",
                        $"Form XML contains duplicate GUID '{value}'. All id and labelid values must be unique within the document.")
                    {
                        ErrorCode = FormErrorCodes.DuplicateGuid
                    };
                }
            }
        }
    }
}
