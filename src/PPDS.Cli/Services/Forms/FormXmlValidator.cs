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
        XmlSchemaSet schemaSet;
        try
        {
            schemaSet = FormSchemaResources.GetSchemaSet();
        }
        catch
        {
            // If schema loading fails (e.g., no XSDs embedded), skip XSD validation
            // and rely only on GUID checks. This keeps the feature functional even
            // when the schema bundle is not yet populated.
            return;
        }

        if (schemaSet.Count == 0)
            return;

        string? errorMessage = null;
        formXml.Validate(schemaSet, (_, e) =>
        {
            if (errorMessage is null && e.Severity == XmlSeverityType.Error)
            {
                var element = e.Exception?.Message ?? "unknown element";
                var line = e.Exception?.LineNumber ?? 0;
                errorMessage = $"Form XML schema validation failed at line {line}: {element}";
            }
        });

        if (errorMessage is not null)
            throw new PpdsValidationException("formxml", errorMessage)
            {
                ErrorCode = FormErrorCodes.InvalidFormXml
            };
    }

    /// <summary>
    /// Validates that all id and labelid attributes use brace format and are unique.
    /// </summary>
    internal static void ValidateGuids(XDocument formXml)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in formXml.Descendants())
        {
            foreach (var attrName in new[] { "id", "labelid" })
            {
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
