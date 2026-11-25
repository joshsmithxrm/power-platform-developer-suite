using System;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace PPDS.Dataverse.Query;

/// <summary>
/// The one parse and serialisation of FetchXML between the query engine and Dataverse (#1698). The executor
/// (<see cref="QueryExecutor"/>) and the FetchXML scan (its TOP handling) parse the FetchXML they are given, change the
/// paging attributes, and send the text this class writes; tests pass the generator's FetchXML through
/// <see cref="RoundTrip(string)"/> to see the text that is sent.
/// </summary>
/// <remarks>
/// The round trip is not the identity. Parsing drops a text node that is only XML white space, so a <c>value</c> element
/// that is only white space becomes empty; writing turns a carriage return, a line feed, or both together, in element text
/// into the machine's line break (<see cref="Environment.NewLine"/>), which an XML parser reads back as a line feed, so a
/// carriage return in element text reaches Dataverse as a line feed. Attribute values keep tab, carriage return and line
/// feed (they are written as character references). The values the DML gate admits are the ones this round trip keeps.
/// </remarks>
public static class FetchXmlSerialization
{
    /// <summary>Parses FetchXML text as the executor does.</summary>
    /// <param name="fetchXml">The FetchXML.</param>
    /// <returns>The document.</returns>
    public static XDocument Parse(string fetchXml) => XDocument.Parse(fetchXml);

    /// <summary>
    /// Writes <paramref name="document"/> as the text sent to Dataverse: no declaration, no indentation, line breaks in
    /// element text as <see cref="Environment.NewLine"/> (the same text as <c>ToString(SaveOptions.DisableFormatting)</c>).
    /// </summary>
    /// <param name="document">The document.</param>
    /// <returns>The FetchXML text.</returns>
    public static string Serialize(XDocument document) => Serialize(document, Environment.NewLine);

    /// <summary>
    /// <see cref="Serialize(XDocument)"/> with the line break of another machine, so a test on one operating system can show
    /// what is sent from another (<c>"\n"</c> on Linux and macOS, <c>"\r\n"</c> on Windows).
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="newLine">The line break written for a line break in element text.</param>
    /// <returns>The FetchXML text.</returns>
    public static string Serialize(XDocument document, string newLine)
    {
        ArgumentNullException.ThrowIfNull(document);
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = false,
            NewLineHandling = NewLineHandling.Replace,
            NewLineChars = newLine
        };
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        using (var writer = XmlWriter.Create(text, settings))
        {
            foreach (var node in document.Nodes())
                node.WriteTo(writer);
        }
        return text.ToString();
    }

    /// <summary>The text sent to Dataverse for <paramref name="fetchXml"/> when nothing in it is changed: parsed and written again.</summary>
    /// <param name="fetchXml">The FetchXML.</param>
    /// <returns>The FetchXML text that is sent.</returns>
    public static string RoundTrip(string fetchXml) => Serialize(Parse(fetchXml));

    /// <summary><see cref="RoundTrip(string)"/> with the line break of another machine (see <see cref="Serialize(XDocument, string)"/>).</summary>
    /// <param name="fetchXml">The FetchXML.</param>
    /// <param name="newLine">The line break written for a line break in element text.</param>
    /// <returns>The FetchXML text that is sent from that machine.</returns>
    public static string RoundTrip(string fetchXml, string newLine) => Serialize(Parse(fetchXml), newLine);
}
