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
/// <para>
/// The text written is the same on every machine (<see cref="NewLineHandling.Entitize"/>): a carriage return is written as
/// the character reference <c>&amp;#xD;</c>, in element text and in attribute values; a line feed is written as itself in
/// element text and as <c>&amp;#xA;</c> in an attribute value; a tab as itself in element text and as <c>&amp;#x9;</c> in
/// an attribute value. An XML reader that normalises line breaks and one that does not read the same characters back.
/// The machine's line break (<see cref="Environment.NewLine"/>) is never written for a line break in a value. Before
/// (#1698), the writer replaced each line break in element text with the machine's line break, so a line feed in an IN
/// value was sent from Windows as a carriage return and a line feed, and a carriage return as a line feed.
/// </para>
/// <para>
/// The round trip is still not the identity: parsing drops a text node that is only XML white space, so a <c>value</c>
/// element that is only white space becomes empty. The DML gate refuses such an IN value, and an IN value with a line
/// break, whose reading by Dataverse is not established.
/// </para>
/// </remarks>
public static class FetchXmlSerialization
{
    /// <summary>Parses FetchXML text as the executor does.</summary>
    /// <param name="fetchXml">The FetchXML.</param>
    /// <returns>The document.</returns>
    public static XDocument Parse(string fetchXml) => XDocument.Parse(fetchXml);

    /// <summary>
    /// Writes <paramref name="document"/> as the text sent to Dataverse: no declaration, no indentation, and a carriage return
    /// as a character reference (see the remarks), the same text on every machine.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <returns>The FetchXML text.</returns>
    public static string Serialize(XDocument document) => Serialize(document, Environment.NewLine);

    /// <summary>
    /// <see cref="Serialize(XDocument)"/> with the line break of another machine set as the writer's line break, so a test on
    /// one operating system can show that the text sent from another (<c>"\n"</c> on Linux and macOS, <c>"\r\n"</c> on
    /// Windows) is the same: the writer never writes its line break for a line break in a value.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="newLine">The writer's line break (the machine's <see cref="Environment.NewLine"/>).</param>
    /// <returns>The FetchXML text.</returns>
    public static string Serialize(XDocument document, string newLine)
    {
        ArgumentNullException.ThrowIfNull(document);
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = false,
            // Entitize, not Replace: Replace writes the machine's line break for each line break in element text.
            NewLineHandling = NewLineHandling.Entitize,
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
    /// <param name="newLine">The writer's line break (the machine's <see cref="Environment.NewLine"/>).</param>
    /// <returns>The FetchXML text that is sent from that machine.</returns>
    public static string RoundTrip(string fetchXml, string newLine) => Serialize(Parse(fetchXml), newLine);
}
