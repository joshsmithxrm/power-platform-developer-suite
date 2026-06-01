using System.IO;
using System.Reflection;
using System.Xml.Schema;

namespace PPDS.Cli.Services.Forms;

/// <summary>
/// Loads bundled Dataverse customization XSD files from embedded assembly resources.
/// </summary>
internal static class FormSchemaResources
{
    private static XmlSchemaSet? _schemaSet;
    private static readonly object Lock = new();

    /// <summary>
    /// Returns the compiled <see cref="XmlSchemaSet"/> for Dataverse form XML validation.
    /// The set is compiled once and cached.
    /// </summary>
    internal static XmlSchemaSet GetSchemaSet()
    {
        if (_schemaSet is not null)
            return _schemaSet;

        lock (Lock)
        {
            if (_schemaSet is not null)
                return _schemaSet;

            var set = new XmlSchemaSet();
            var assembly = Assembly.GetExecutingAssembly();

            foreach (var resourceName in assembly.GetManifestResourceNames())
            {
                if (!resourceName.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase))
                    continue;

                using var stream = assembly.GetManifestResourceStream(resourceName)!;
                using var reader = new StreamReader(stream);
                set.Add(null, System.Xml.XmlReader.Create(reader));
            }

            set.Compile();
            _schemaSet = set;
            return _schemaSet;
        }
    }
}
