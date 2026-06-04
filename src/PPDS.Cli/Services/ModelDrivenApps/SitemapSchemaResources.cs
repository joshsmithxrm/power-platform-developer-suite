using System.Net;
using System.Reflection;
using System.Xml;
using System.Xml.Schema;

namespace PPDS.Cli.Services.ModelDrivenApps;

/// <summary>
/// Loads Dataverse sitemap XSD schemas from embedded resources.
/// </summary>
public sealed class SitemapSchemaResources
{
    private static readonly string ResourcePrefix = "PPDS.Cli.Resources.Schemas";
    private readonly Lazy<XmlSchemaSet> _schemaSet;

    public SitemapSchemaResources()
    {
        _schemaSet = new Lazy<XmlSchemaSet>(() =>
        {
            var assembly = typeof(SitemapSchemaResources).Assembly;
            var schemaSet = new XmlSchemaSet
            {
                XmlResolver = new EmbeddedXsdResolver(assembly, ResourcePrefix)
            };

            using var stream = assembly.GetManifestResourceStream($"{ResourcePrefix}.SiteMap.xsd")
                ?? throw new InvalidOperationException(
                    "Embedded resource 'SiteMap.xsd' not found. Ensure the file is marked as EmbeddedResource in the project.");

            using var reader = XmlReader.Create(stream);
            schemaSet.Add(null, reader);
            schemaSet.Compile();

            return schemaSet;
        });
    }

    /// <summary>
    /// Returns the compiled <see cref="XmlSchemaSet"/> for the SiteMap XSD, compiled once on first access.
    /// </summary>
    public XmlSchemaSet LoadSchemaSet() => _schemaSet.Value;

    private sealed class EmbeddedXsdResolver : XmlResolver
    {
        private readonly Assembly _assembly;
        private readonly string _resourcePrefix;

        internal EmbeddedXsdResolver(Assembly assembly, string resourcePrefix)
        {
            _assembly = assembly;
            _resourcePrefix = resourcePrefix;
        }

        public override ICredentials? Credentials { set { } }

        public override Uri ResolveUri(Uri? baseUri, string? relativeUri)
            => new($"urn:embedded:{relativeUri ?? string.Empty}");

        public override object? GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
        {
            const string prefix = "urn:embedded:";
            var fileName = absoluteUri.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal)
                ? absoluteUri.AbsoluteUri[prefix.Length..]
                : Path.GetFileName(absoluteUri.LocalPath);

            return _assembly.GetManifestResourceStream($"{_resourcePrefix}.{fileName}");
        }
    }
}
