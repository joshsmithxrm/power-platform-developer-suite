using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.Schema;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.Forms;

/// <summary>
/// Validates formxml against the bundled Dataverse customization XSD (which covers
/// GUID format via FormGuidType) and checks cross-element GUID uniqueness.
/// </summary>
internal static class FormXmlValidator
{
    // A GUID value, with braces optional — mirrors the bundled XSD's FormGuidType
    // pattern (\{?...\}?). Used only to decide which id/labelid values participate
    // in the cross-element uniqueness check; format itself is validated by the XSD.
    private static readonly Regex GuidValueRegex = new(
        @"^\{?[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}?$",
        RegexOptions.Compiled);

    // The GUID-bearing structural attributes. Hoisted so the per-element loop in
    // ValidateGuids does not re-allocate the array for every descendant.
    private static readonly string[] GuidAttributeNames = { "id", "labelid" };

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
    /// Validates uniqueness of GUID-valued instance <c>id</c> and <c>labelid</c>
    /// attributes, excluding reusable custom-control type IDs at their schema-defined path.
    /// </summary>
    /// <remarks>
    /// GUID <em>format</em> is intentionally not re-checked here. The bundled XSD is
    /// the single source of truth for format: it types real GUID ids as
    /// <c>FormGuidType</c> (whose pattern <c>\{?...\}?</c> accepts braced and unbraced
    /// GUIDs alike) and types logical-name ids — on <c>&lt;control&gt;</c>,
    /// <c>&lt;data&gt;</c> and <c>&lt;dependency&gt;</c> — as <c>xs:string</c>. The
    /// previous hand-rolled brace-format check both duplicated the schema and drifted
    /// out of sync with it (#1209): it rejected valid unbraced GUIDs and real
    /// logical-name ids (e.g. <c>fullname</c>, <c>absoluteurl</c>). The one constraint
    /// the schema cannot express is cross-element uniqueness, so that is all we keep.
    /// Only GUID-shaped values participate; logical-name ids are plain strings that
    /// may legitimately repeat across a form. A customControl type ID at the exact
    /// root form/controlDescriptions/controlDescription/customControl path is also reusable;
    /// its format is still checked by the XSD. This does not exclude descendant parameters.
    /// </remarks>
    internal static void ValidateGuids(XDocument formXml)
    {
        ArgumentNullException.ThrowIfNull(formXml);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in formXml.Descendants())
        {
            foreach (var attrName in GuidAttributeNames)
            {
                if (attrName == "id" && IsReusableControlType(element, formXml)) continue;

                var attr = element.Attribute(attrName);
                if (attr is null) continue;

                var value = attr.Value;
                if (string.IsNullOrEmpty(value)) continue;

                // Logical-name ids (e.g. <data id="fullname">) are not GUIDs and may
                // repeat — they do not participate in GUID uniqueness.
                if (!GuidValueRegex.IsMatch(value)) continue;

                // Normalise brace form so "{G}" and "G" collide as the same GUID.
                var normalized = value.Trim('{', '}');
                if (!seen.Add(normalized))
                {
                    throw new PpdsValidationException("formxml",
                        $"Form XML contains duplicate instance GUID '{value}'. Instance id and labelid GUID values must be unique within the document.")
                    {
                        ErrorCode = FormErrorCodes.DuplicateGuid
                    };
                }
            }
        }
    }

    private static bool IsReusableControlType(XElement element, XDocument document)
    {
        var description = element.Parent;
        var descriptions = description?.Parent;
        var form = descriptions?.Parent;
        // XName equality deliberately requires the unqualified schema names. Anchor at the
        // document root so lookalike paths inside opaque parameters are not exempted.
        return element.Name == "customControl"
            && description?.Name == "controlDescription"
            && descriptions?.Name == "controlDescriptions"
            && form?.Name == "form"
            && ReferenceEquals(form, document.Root);
    }
}
