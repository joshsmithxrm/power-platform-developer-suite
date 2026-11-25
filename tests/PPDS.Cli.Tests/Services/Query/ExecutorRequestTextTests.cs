using System.Text.RegularExpressions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Tests.Shared;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// Nothing but the one serialisation stands between the FetchXML a write's read is planned with and the request Dataverse
/// receives (#1698, MF-B). The product's <see cref="QueryExecutor"/> (both overloads, with and without a page number and a
/// paging cookie) and <see cref="FetchXmlScanNode"/> (with TOP) run against a connection pool whose client captures the
/// query text of each request; that text must be <see cref="FetchXmlSerialization.RoundTrip(string)"/> of the input with
/// only the paging attributes added, character for character. The inputs are the generator's FetchXML for every admitted
/// compared case of <see cref="DmlValueAdmissionTable"/> (every admitted value kind), and one FetchXML with line breaks
/// in element text, which tells this serialisation from any other.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ExecutorRequestTextTests
{
    private const string Cookie = "<cookie page=\"1\"><accountid last=\"{00000000-0000-0000-0000-000000000001}\" first=\"{00000000-0000-0000-0000-000000000002}\" /></cookie>";

    /// <summary>Element text with a carriage return and a line feed, and an attribute with both: what tells serialisations apart.</summary>
    private const string LineBreaks =
        "<fetch><entity name=\"account\"><attribute name=\"name\" /><filter>" +
        "<condition attribute=\"name\" operator=\"eq\" value=\"a&#13;b&#10;c&#9;d\" />" +
        "<condition attribute=\"description\" operator=\"in\"><value>p&#13;q</value><value>r&#10;s</value><value>t&#13;&#10;u</value></condition>" +
        "</filter></entity></fetch>";

    private static async Task<IReadOnlyList<string>> InputsAsync()
    {
        var inputs = new List<string> { LineBreaks };
        foreach (var c in DmlValueAdmissionTable.CompareCases.Where(c => c.Cell.StartsWith('=')))
        {
            var h = DmlTestHarness.Create(rows: 1);
            await h.Service.ExecuteAsync(new SqlQueryRequest
            {
                Sql = DmlValueAdmissionTable.CompareStatement(c.Literal, DmlValueAdmissionTable.CompareTarget(c.Kind), c.Operator),
                DmlSafety = new DmlSafetyOptions { IsConfirmed = true }
            });
            inputs.Add(Assert.Single(h.Generated));
        }
        Assert.True(inputs.Count > 40, $"only {inputs.Count} inputs");
        return inputs;
    }

    /// <summary>A pool whose clients record the query text of every RetrieveMultiple and answer with no rows.</summary>
    private static IDataverseConnectionPool CapturingPool(List<string> sent)
    {
        string Capture(QueryBase query)
        {
            var text = Assert.IsType<FetchExpression>(query).Query;
            sent.Add(text);
            return text;
        }

        var client = new Mock<IPooledClient>(MockBehavior.Strict);
        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .Returns((QueryBase q, CancellationToken _) => { Capture(q); return Task.FromResult(new EntityCollection()); });
        client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Returns((OrganizationRequest r, CancellationToken _) =>
            {
                Capture(Assert.IsType<RetrieveMultipleRequest>(r).Query);
                var response = new RetrieveMultipleResponse();
                response.Results["EntityCollection"] = new EntityCollection();
                return Task.FromResult<OrganizationResponse>(response);
            });
        client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        client.Setup(c => c.Dispose());

        var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
        pool.Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        return pool.Object;
    }

    /// <summary>
    /// <see cref="FetchXmlSerialization.RoundTrip(string)"/> of <paramref name="input"/>, with <paramref name="removed"/>
    /// taken out of the fetch element's start tag and <paramref name="added"/> appended to it, written here as text.
    /// </summary>
    private static string Expected(string input, string removed, string added)
    {
        var text = FetchXmlSerialization.RoundTrip(input);
        Assert.StartsWith("<fetch", text);
        var end = text.IndexOf('>');
        var startTag = text[..end];
        if (removed.Length > 0)
        {
            Assert.Contains(removed, startTag);
            startTag = startTag.Replace(removed, string.Empty, StringComparison.Ordinal);
        }
        return startTag + added + text[end..];
    }

    /// <summary>The paging attributes the executor adds, escaped as an XML attribute value is.</summary>
    private static string Paging(int page, string? cookie)
        => $" page=\"{page}\"" + (cookie == null ? string.Empty : $" paging-cookie=\"{Escape(cookie)}\"");

    private static string Escape(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    public static TheoryData<bool, int?, string?> Calls => new()
    {
        { false, null, null },
        { false, 2, null },
        { false, 2, Cookie },
        { true, null, null },
        { true, 3, Cookie },
    };

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task TheExecutor_SendsTheRoundTripOfItsInput_WithOnlyThePagingAttributes(bool withOptions, int? page, string? cookie)
    {
        var sent = new List<string>();
        var executor = new QueryExecutor(CapturingPool(sent));
        var options = new QueryExecutionOptions { BypassPlugins = true, BypassFlows = true };
        var wrong = new List<string>();

        foreach (var input in await InputsAsync())
        {
            sent.Clear();
            if (withOptions)
                await executor.ExecuteFetchXmlAsync(input, page, cookie, includeCount: false, options);
            else
                await executor.ExecuteFetchXmlAsync(input, page, cookie, includeCount: false);

            var expected = page == null && cookie == null ? FetchXmlSerialization.RoundTrip(input) : Expected(input, string.Empty, Paging(page ?? 1, cookie));
            var actual = Assert.Single(sent);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                wrong.Add($"input {SentFetchXml.Show(input)}{System.Environment.NewLine}  expected {SentFetchXml.Show(expected)}{System.Environment.NewLine}  sent     {SentFetchXml.Show(actual)}");
        }

        Assert.True(wrong.Count == 0, string.Join(System.Environment.NewLine, wrong));
    }

    /// <summary>
    /// The FetchXML scan with TOP (it turns top into count before the executor pages it): the request is the round trip of
    /// the input with top replaced by count and the page number added, for both executor overloads.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheScanNodeWithTop_SendsTheRoundTripOfItsInput_WithOnlyCountAndPage(bool withOptions)
    {
        var sent = new List<string>();
        var context = new QueryPlanContext(new QueryExecutor(CapturingPool(sent)),
            executionOptions: withOptions ? new QueryExecutionOptions { BypassPlugins = true } : null);
        var wrong = new List<string>();

        foreach (var input in await InputsAsync())
        {
            var withTop = Regex.Replace(input, "^<fetch", "<fetch top=\"7\"");
            sent.Clear();
            await foreach (var _ in new FetchXmlScanNode(withTop, "account").ExecuteAsync(context)) { }

            var expected = Expected(withTop, " top=\"7\"", " count=\"7\" page=\"1\"");
            var actual = Assert.Single(sent);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                wrong.Add($"input {SentFetchXml.Show(withTop)}{System.Environment.NewLine}  expected {SentFetchXml.Show(expected)}{System.Environment.NewLine}  sent     {SentFetchXml.Show(actual)}");
        }

        Assert.True(wrong.Count == 0, string.Join(System.Environment.NewLine, wrong));
    }

    /// <summary>The input that tells serialisations apart is sent with its line breaks kept: a carriage return as a reference.</summary>
    [Fact]
    public async Task LineBreaksInElementText_AreSentAsTheSerialisationWritesThem()
    {
        var sent = new List<string>();
        await new QueryExecutor(CapturingPool(sent)).ExecuteFetchXmlAsync(LineBreaks);

        var text = Assert.Single(sent);
        Assert.Contains("<value>p&#xD;q</value><value>r\ns</value><value>t&#xD;\nu</value>", text);
        Assert.Contains("value=\"a&#xD;b&#xA;c&#x9;d\"", text);
    }
}
