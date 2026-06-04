using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.ModelDrivenApps;

/// <summary>
/// Validates sitemap XML against the bundled Dataverse XSD schema.
/// </summary>
public sealed class SitemapXmlValidator
{
    private readonly SitemapSchemaResources _resources;

    /// <summary>
    /// Initializes a new instance of the <see cref="SitemapXmlValidator"/> class.
    /// </summary>
    public SitemapXmlValidator(SitemapSchemaResources resources)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
    }

    /// <summary>
    /// Validates the given sitemap document against the XSD schema.
    /// </summary>
    /// <param name="document">The sitemap XML document.</param>
    /// <exception cref="PpdsValidationException">Thrown when validation fails, with line/element details.</exception>
    public void Validate(XDocument document)
    {
        var schemaSet = _resources.LoadSchemaSet();
        var errors = new List<ValidationError>();

        document.Validate(schemaSet, (_, args) =>
        {
            if (args.Severity == XmlSeverityType.Error || args.Severity == XmlSeverityType.Warning)
            {
                var lineInfo = args.Exception?.LineNumber is > 0
                    ? $" (line {args.Exception.LineNumber}, position {args.Exception.LinePosition})"
                    : string.Empty;

                errors.Add(new ValidationError("sitemapxml", $"{args.Message}{lineInfo}"));
            }
        });

        if (errors.Count > 0)
        {
            throw new PpdsValidationException(errors)
            {
                ErrorCode = ModelDrivenAppErrorCodes.InvalidSitemapXml
            };
        }
    }
}
