using System.Text;
using System.Xml;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// What Dataverse receives for a FetchXML text (#1698, MF-A): the characters of the text sent, which must be the same with
/// the line break of Linux and of Windows, and the condition values in it as two kinds of XML reader read them, one that
/// normalises line breaks and one that does not. A test never decides what is sent by reading it with one normalising
/// reader alone: that reader reads a carriage return and line feed written as such the same as a line feed.
/// </summary>
internal static class SentFetchXml
{
    /// <summary>
    /// The text sent for <paramref name="fetchXml"/> (the executor's serialisation, <see cref="FetchXmlSerialization"/>),
    /// asserted to be the same characters with the line break of Linux and of Windows.
    /// </summary>
    public static string Sent(string fetchXml)
    {
        var linux = FetchXmlSerialization.RoundTrip(fetchXml, "\n");
        var windows = FetchXmlSerialization.RoundTrip(fetchXml, "\r\n");
        Assert.True(string.Equals(linux, windows, StringComparison.Ordinal),
            $"The text sent differs by machine.{System.Environment.NewLine}Linux:   {Show(linux)}{System.Environment.NewLine}Windows: {Show(windows)}");
        Assert.Equal(linux, FetchXmlSerialization.RoundTrip(fetchXml));
        return linux;
    }

    /// <summary>
    /// The values of the conditions on <paramref name="column"/> in the text <paramref name="sent"/>: each condition's
    /// <c>value</c> attribute, then the text of each of its <c>value</c> elements, in order. Asserted to be the same for a
    /// reader that normalises line breaks and for one that does not.
    /// </summary>
    public static IReadOnlyList<string> Values(string sent, string column)
    {
        List<string> normalising;
        using (var reader = XmlReader.Create(new StringReader(sent), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
            normalising = Read(reader, column);
        List<string> raw;
        using (var reader = new XmlTextReader(new StringReader(sent)) { Normalization = false, DtdProcessing = DtdProcessing.Prohibit })
            raw = Read(reader, column);
        Assert.True(raw.SequenceEqual(normalising, StringComparer.Ordinal),
            $"Readers disagree on {column}: without normalisation {string.Join(", ", raw.Select(Show))}; with {string.Join(", ", normalising.Select(Show))}");
        return raw;
    }

    private static List<string> Read(XmlReader reader, string column)
    {
        var values = new List<string>();
        var inCondition = false;
        StringBuilder? value = null;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element when reader.Name == "condition":
                    inCondition = reader.GetAttribute("attribute") == column;
                    if (inCondition && reader.GetAttribute("value") is { } attribute)
                        values.Add(attribute);
                    if (reader.IsEmptyElement)
                        inCondition = false;
                    break;
                case XmlNodeType.Element when inCondition && reader.Name == "value":
                    if (reader.IsEmptyElement)
                        values.Add(string.Empty);
                    else
                        value = new StringBuilder();
                    break;
                case XmlNodeType.Text or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace or XmlNodeType.CDATA when value != null:
                    value.Append(reader.Value);
                    break;
                case XmlNodeType.EndElement when reader.Name == "value" && value != null:
                    values.Add(value.ToString());
                    value = null;
                    break;
                case XmlNodeType.EndElement when reader.Name == "condition":
                    inCondition = false;
                    break;
            }
        }
        return values;
    }

    /// <summary>Each character outside printable ASCII as <c>&lt;U+XXXX&gt;</c>.</summary>
    public static string Show(string? text) => text is null ? "<null>" : string.Concat(text.Select(c => c is < ' ' or > '~' ? $"<U+{(int)c:X4}>" : c.ToString()));
}
