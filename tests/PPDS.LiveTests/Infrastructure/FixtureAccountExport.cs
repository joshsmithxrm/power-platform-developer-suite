using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using PPDS.LiveTests.Cli;
using PPDS.Migration.Formats;
using PPDS.Migration.Models;

namespace PPDS.LiveTests.Infrastructure;

/// <summary>
/// A small export capability made only from IDs captured after fixture creation.
/// The pending CLI output is never usable by a roundtrip until its raw archive is validated.
/// </summary>
internal sealed class FixtureAccountExport
{
    internal const int MaximumRows = 3;
    private const long MaximumArchiveBytes = 64 * 1024;
    private const long MaximumXmlBytes = 128 * 1024;
    private readonly Guid[] _ids;
    private readonly string[] _fields;

    private FixtureAccountExport(Guid[] ids, bool includeDescription)
    {
        _ids = ids;
        _fields = includeDescription ? ["accountid", "name", "description"] : ["accountid", "name"];
    }

    internal static FixtureAccountExport Create(
        IEnumerable<Guid> creationOwnedIds, IEnumerable<Guid> requestedIds, bool includeDescription = false)
    {
        ArgumentNullException.ThrowIfNull(creationOwnedIds);
        ArgumentNullException.ThrowIfNull(requestedIds);
        var owned = creationOwnedIds.ToArray();
        var requested = requestedIds.ToArray();
        if (owned.Any(id => id == Guid.Empty) || owned.Distinct().Count() != owned.Length ||
            requested.Length is < 1 or > MaximumRows || requested.Any(id => id == Guid.Empty) ||
            requested.Distinct().Count() != requested.Length || requested.Any(id => !owned.Contains(id)))
            throw Refused("missing, duplicate, unknown or over-budget ownership scope");
        return new FixtureAccountExport(requested.OrderBy(id => id).ToArray(), includeDescription);
    }

    internal MigrationSchema CreateSchema() => new()
    {
        Entities = [new EntitySchema
        {
            LogicalName = "account", DisplayName = "Account", PrimaryIdField = "accountid", PrimaryNameField = "name",
            Fields = _fields.Select(name => new FieldSchema
            {
                LogicalName = name, DisplayName = name,
                Type = name == "accountid" ? "uniqueidentifier" : "string", IsPrimaryKey = name == "accountid"
            }).ToArray(),
            Relationships = [],
            FetchXmlFilter = new XElement("filter", new XAttribute("type", "and"),
                new XElement("condition", new XAttribute("attribute", "accountid"), new XAttribute("operator", "in"),
                    _ids.Select(id => new XElement("value", id.ToString("D"))))).ToString(SaveOptions.DisableFormatting)
        }]
    };

    internal async Task<CliResult> RunAsync(
        string schemaPath, string pendingPath, string acceptedPath,
        Func<string[], CancellationToken, Task<CliResult>> dispatch, string? profile = null,
        bool jsonProgress = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (new[] { schemaPath, pendingPath, acceptedPath }.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3 ||
            File.Exists(pendingPath) || File.Exists(acceptedPath))
            throw Refused("artifact paths are not distinct and new");

        await new CmtSchemaWriter().WriteAsync(CreateSchema(), schemaPath, cancellationToken);
        await ValidateSchemaFileAsync(schemaPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var args = new List<string>
        {
            "data", "export", "--schema", schemaPath, "--output", pendingPath,
            "--format", "cmt", "--batch-size", MaximumRows.ToString(), "--parallel", "1", "--page-parallel", "1",
            "--max-records-per-entity", _ids.Length.ToString()
        };
        if (profile != null) args.AddRange(["--profile", profile]);
        if (jsonProgress) args.AddRange(["--output-format", "json"]);
        var result = await dispatch(args.ToArray(), cancellationToken);
        if (!result.Success) throw Refused("CLI export failed");
        await ValidateArtifactAsync(pendingPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(pendingPath, acceptedPath);
        return result;
    }

    internal async Task ValidateSchemaFileAsync(string schemaPath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(schemaPath);
        await ValidateSchemaAsync(await ReadXmlAsync(stream, cancellationToken), cancellationToken);
    }

    private async Task ValidateSchemaAsync(XDocument actual, CancellationToken cancellationToken)
    {
        await using var expectedStream = new MemoryStream();
        await new CmtSchemaWriter().WriteAsync(CreateSchema(), expectedStream, cancellationToken);
        expectedStream.Position = 0;
        var expected = await ReadXmlAsync(expectedStream, cancellationToken);
        if (!XNode.DeepEquals(expected, actual)) throw Refused("schema differs from the fixed owned scope");
    }

    internal async Task ValidateArtifactAsync(string path, CancellationToken cancellationToken = default)
    {
        if (new FileInfo(path).Length > MaximumArchiveBytes) throw Refused("archive exceeds its byte budget");
        using var archive = ZipFile.OpenRead(path);
        string[] entries = ["[Content_Types].xml", "data.xml", "data_schema.xml"];
        if (archive.Entries.Count != entries.Length || archive.Entries.Any(entry => !entries.Contains(entry.FullName)) ||
            archive.Entries.Select(entry => entry.FullName).Distinct().Count() != entries.Length ||
            archive.Entries.Sum(entry => entry.Length) > MaximumXmlBytes)
            throw Refused("archive entries or size exceed the fixed XML-only contract");
        using (var contentTypesStream = archive.GetEntry("[Content_Types].xml")!.Open())
        {
            var contentTypes = await ReadXmlAsync(contentTypesStream, cancellationToken);
            var expected = XDocument.Parse("<?xml version='1.0' encoding='utf-8'?><Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Default Extension='xml' ContentType='application/octet-stream'/></Types>");
            if (!XNode.DeepEquals(expected, contentTypes)) throw Refused("content types differ from the fixed XML-only writer contract");
        }
        using (var schemaStream = archive.GetEntry("data_schema.xml")!.Open())
            await ValidateSchemaAsync(await ReadXmlAsync(schemaStream, cancellationToken), cancellationToken);
        using var dataStream = archive.GetEntry("data.xml")!.Open();
        var data = await ReadXmlAsync(dataStream, cancellationToken);
        var entities = data.Root?.Elements().ToArray() ?? [];
        if (data.Root?.Name != "entities" || HasUnexpectedText(data.Root) ||
            !HasExpectedDataMetadata(data.Root) ||
            entities.Length != 1 || entities[0].Name != "entity" ||
            (string?)entities[0].Attribute("name") != "account") throw Refused("unexpected data entities");
        var entity = entities[0];
        if (HasUnexpectedText(entity) || entity.Attributes().Count() != 2 || (string?)entity.Attribute("displayname") != "Account" ||
            entity.Attributes().Any(attribute => attribute.Name != "name" && attribute.Name != "displayname") ||
            entity.Elements().Any(element => element.Name != "records" && element.Name != "m2mrelationships") ||
            entity.Elements("records").Count() != 1 || entity.Element("records")!.HasAttributes || HasUnexpectedText(entity.Element("records")!) ||
            entity.Elements("m2mrelationships").Count() != 1 ||
            entity.Elements("m2mrelationships").Any(element => element.HasElements || element.HasAttributes || HasUnexpectedText(element)))
            throw Refused("unexpected relationships or record containers");
        var records = entity.Element("records")!.Elements().ToArray();
        if (records.Length != _ids.Length || records.Length > MaximumRows) throw Refused("missing or over-budget rows");
        var seen = new HashSet<Guid>();
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.Name != "record" || HasUnexpectedText(record) || record.Attributes().Any(attribute => attribute.Name != "id") ||
                !Guid.TryParse((string?)record.Attribute("id"), out var id) ||
                !_ids.Contains(id) || !seen.Add(id)) throw Refused("unowned or duplicate result ID");
            var fields = record.Elements().ToArray();
            if (fields.Any(field => field.Name != "field" || !_fields.Contains((string?)field.Attribute("name")) ||
                    field.HasElements || HasUnexpectedText(field) || field.Attributes().Any(attribute => attribute.Name != "name" && attribute.Name != "value")) ||
                fields.Select(field => (string?)field.Attribute("name")).Distinct().Count() != fields.Length)
                throw Refused("unexpected result fields or expansion");
            var primaryKey = fields.SingleOrDefault(field => (string?)field.Attribute("name") == "accountid");
            if (primaryKey != null && (!Guid.TryParse((string?)primaryKey.Attribute("value"), out var fieldId) || fieldId != id))
                throw Refused("result primary key disagrees with its ID");
        }
    }

    private static bool HasUnexpectedText(XElement element) => element.Nodes().Any(node =>
        node is not XElement && (node is not XText text || !string.IsNullOrWhiteSpace(text.Value)));

    private static bool HasExpectedDataMetadata(XElement root) => root.Attributes().Count() == 3 &&
        (string?)root.Attribute(XNamespace.Xmlns + "xsd") == "http://www.w3.org/2001/XMLSchema" &&
        (string?)root.Attribute(XNamespace.Xmlns + "xsi") == "http://www.w3.org/2001/XMLSchema-instance" &&
        DateTime.TryParseExact((string?)root.Attribute("timestamp"), "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static async Task<XDocument> ReadXmlAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                IgnoreWhitespace = true, MaxCharactersInDocument = MaximumXmlBytes
            });
            var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
            if (document.Root == null || document.Nodes().Any(node => !ReferenceEquals(node, document.Root)) ||
                document.DescendantNodes().Any(node => node is XComment or XProcessingInstruction))
                throw Refused("unexpected XML document structure");
            return document;
        }
        catch (XmlException) { throw Refused("invalid XML document"); }
    }

    private static InvalidOperationException Refused(string reason) => new($"Fixture account export refused: {reason}.");
}
