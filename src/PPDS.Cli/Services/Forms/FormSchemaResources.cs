using System;
using System.IO;
using System.Reflection;
using System.Xml;
using System.Xml.Schema;

namespace PPDS.Cli.Services.Forms;

/// <summary>
/// Loads the bundled Dataverse customization XSD files (FormXml.xsd and its
/// transitive includes) from embedded assembly resources and compiles them
/// into a reusable <see cref="XmlSchemaSet"/> for form XML validation.
/// </summary>
internal static class FormSchemaResources
{
    /// <summary>
    /// Logical resource-name prefix for the bundled schemas. Matches
    /// <c>&lt;RootNamespace&gt;.&lt;folder path&gt;</c> for files under
    /// <c>Resources/Schemas/</c> embedded by <c>PPDS.Cli.csproj</c>.
    /// </summary>
    internal const string ResourcePrefix = "PPDS.Cli.Resources.Schemas.";

    /// <summary>
    /// Root schema for form XML. Defines the <c>&lt;form&gt;</c> element and
    /// pulls in the ribbon schemas via <c>xs:include</c>.
    /// </summary>
    internal const string RootSchemaFile = "FormXml.xsd";

    // Synthetic base URI so xs:include schemaLocation values (plain filenames)
    // resolve to absolute URIs the embedded resolver can map back to resources.
    private static readonly Uri BaseUri = new("ppds-xsd:///");

    private static XmlSchemaSet? _schemaSet;
    private static readonly object Lock = new();

    /// <summary>
    /// Returns the compiled <see cref="XmlSchemaSet"/> for Dataverse form XML
    /// validation. Compiled once and cached. Throws if the bundled schema is
    /// missing or fails to compile — schema validation is mandatory, so a
    /// packaging error must surface rather than silently disable validation.
    /// </summary>
    internal static XmlSchemaSet GetSchemaSet()
    {
        if (_schemaSet is not null)
            return _schemaSet;

        lock (Lock)
        {
            if (_schemaSet is not null)
                return _schemaSet;

            var assembly = Assembly.GetExecutingAssembly();
            var resolver = new EmbeddedXsdResolver(assembly);
            var set = new XmlSchemaSet { XmlResolver = resolver };

            var rootResource = ResourcePrefix + RootSchemaFile;
            using var stream = assembly.GetManifestResourceStream(rootResource)
                ?? throw new InvalidOperationException(
                    $"Bundled form schema resource '{rootResource}' was not found. " +
                    "Ensure Resources/Schemas/*.xsd are embedded (see PPDS.Cli.csproj).");

            var settings = new XmlReaderSettings { XmlResolver = resolver };
            using var reader = XmlReader.Create(stream, settings, BaseUri + RootSchemaFile);

            set.Add(null, reader);
            set.Compile();

            _schemaSet = set;
            return _schemaSet;
        }
    }

    /// <summary>
    /// Resolves <c>xs:include</c> references (plain filenames under the
    /// synthetic <c>ppds-xsd:///</c> base) to embedded resource streams.
    /// </summary>
    private sealed class EmbeddedXsdResolver : XmlResolver
    {
        private readonly Assembly _assembly;

        internal EmbeddedXsdResolver(Assembly assembly) => _assembly = assembly;

        public override Uri ResolveUri(Uri? baseUri, string? relativeUri)
            => new(BaseUri, relativeUri ?? string.Empty);

        public override object? GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
        {
            var fileName = Path.GetFileName(absoluteUri.LocalPath);
            var resourceName = ResourcePrefix + fileName;
            return _assembly.GetManifestResourceStream(resourceName)
                ?? throw new FileNotFoundException(
                    $"Embedded form schema '{resourceName}' (referenced as '{fileName}') was not found.");
        }
    }
}
