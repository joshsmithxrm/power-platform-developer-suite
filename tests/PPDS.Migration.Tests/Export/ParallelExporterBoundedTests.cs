using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Export;
using PPDS.Migration.Formats;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;
using Xunit;

namespace PPDS.Migration.Tests.Export;

public sealed class ParallelExporterBoundedTests
{
    [Fact]
    public async Task BoundedPagingHonorsRemainingBudget()
    {
        var rows = Enumerable.Range(0, 3).Select(_ => new Entity("account", Guid.NewGuid())).ToArray();
        var harness = new Harness(xml =>
        {
            var count = (int)xml.Root!.Attribute("count")!;
            var page = (int?)xml.Root!.Attribute("page") ?? 1;
            var cookie = (string?)xml.Root.Attribute("paging-cookie");
            var offset = cookie == null ? (page - 1) * count : (int)XElement.Parse(cookie).Attribute("next")!;
            var selected = rows.Skip(offset).Take(count).ToList();
            var more = offset + selected.Count < rows.Length;
            return new EntityCollection(selected)
            {
                MoreRecords = more,
                PagingCookie = more ? new XElement("cookie", new XAttribute("next", offset + selected.Count)).ToString() : null
            };
        });
        var result = await harness.Run(new ExportOptions
            { MaxRecordsPerEntity = 3, PageSize = 2, PageLevelParallelism = 8 });
        Assert.True(result.Success);
        Assert.Equal(3, result.RecordsExported);
        Assert.Equal(new[] { 2, 1 }, harness.Queries.Select(query => (int)query.Root!.Attribute("count")!));
        Assert.Equal(new[] { 1, 2 }, harness.Queries.Select(query => (int)query.Root!.Attribute("page")!));
        Assert.All(harness.Queries, query => Assert.Null(query.Root!.Attribute("aggregate")));
        Assert.Single(harness.Writes);
        Assert.Equal(3, harness.Writes[0].TotalRecordCount);
        Assert.Equal(rows.Select(row => row.Id), harness.Writes[0].EntityData["account"].Select(row => row.Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public async Task BoundedContinuationWithoutCookieIsRefusedBeforeNextQuery(string? cookie)
    {
        var rows = Enumerable.Range(0, 3).Select(_ => new Entity("account", Guid.NewGuid())).ToArray();
        var harness = new Harness(xml =>
        {
            var count = (int)xml.Root!.Attribute("count")!;
            var offset = ((int)xml.Root.Attribute("page")! - 1) * count;
            var selected = rows.Skip(offset).Take(count).ToList();
            return new EntityCollection(selected) { MoreRecords = offset + selected.Count < rows.Length, PagingCookie = cookie };
        });
        var result = await harness.Run(new ExportOptions { MaxRecordsPerEntity = 3, PageSize = 2 });
        Assert.False(result.Success);
        Assert.Single(harness.Queries);
        Assert.Equal(2, (int)harness.Queries[0].Root!.Attribute("count")!);
        Assert.Contains("paging cookie", Assert.Single(result.EntityResults).ErrorMessage);
        Assert.Empty(harness.Writes);
    }

    [Theory]
    [InlineData("oversized-page", 1)]
    [InlineData("empty-more", 1)]
    [InlineData("cap-more", 1)]
    [InlineData("later-overflow", 2)]
    [InlineData("query-fault", 1)]
    public async Task BoundedFailureNeverWritesArtifact(string scenario, int requests)
    {
        var harness = new Harness(xml => scenario switch
        {
            "oversized-page" => Rows(4),
            "empty-more" => Rows(0, true),
            "cap-more" => Rows(3, true),
            "later-overflow" => Rows(2, (int?)xml.Root!.Attribute("page") == 1),
            "query-fault" => throw new IOException("controlled query failure"),
            _ => throw new InvalidOperationException()
        });
        var result = await harness.Run(new ExportOptions { MaxRecordsPerEntity = 3 });
        Assert.False(result.Success);
        Assert.Equal(requests, harness.Queries.Count);
        Assert.Empty(harness.Writes);
    }

    [Fact]
    public async Task OneBoundedEntityFailurePreventsAllWriting()
    {
        var harness = new Harness(xml => Rows((string?)xml.Root!.Element("entity")!.Attribute("name") == "account" ? 1 : 4));
        var result = await harness.Run(new ExportOptions { MaxRecordsPerEntity = 3, DegreeOfParallelism = 1 },
            new MigrationSchema { Entities = new[] { Schema("account"), Schema("contact") } });
        Assert.False(result.Success);
        Assert.Contains(result.EntityResults, entity => entity.Success);
        Assert.Contains(result.EntityResults, entity => !entity.Success);
        Assert.Empty(harness.Writes);
    }

    [Fact]
    public async Task BoundedFailureDoesNotInvokeJsonWriter()
    {
        var harness = new Harness(_ => Rows(4));
        var result = await harness.Run(new ExportOptions { MaxRecordsPerEntity = 3, Format = ExportDataFormat.Json });
        Assert.False(result.Success);
        Assert.Empty(harness.Writes);
    }

    [Fact]
    public async Task RowBudgetIsPerEntity()
    {
        var harness = new Harness(_ => Rows(1));
        var result = await harness.Run(new ExportOptions { MaxRecordsPerEntity = 1 },
            new MigrationSchema { Entities = new[] { Schema("account"), Schema("contact") } });
        Assert.True(result.Success);
        Assert.Equal(2, result.RecordsExported);
        Assert.Single(harness.Writes);
    }

    [Theory]
    [InlineData("relationship")]
    [InlineData("file")]
    [InlineData("image")]
    [InlineData("binary")]
    [InlineData("include-file-data")]
    public async Task ExpansionRejectedBeforePoolAccess(string expansion)
    {
        var harness = new Harness(_ => throw new InvalidOperationException("must not query"));
        var schema = Schema("account");
        if (expansion == "relationship") schema.Relationships = new[] { new RelationshipSchema { Name = "foreign", IsManyToMany = false } };
        else if (expansion != "include-file-data") schema.Fields = new[] { new FieldSchema { LogicalName = "foreign", Type = expansion } };
        var result = await harness.Run(new ExportOptions { MaxRecordsPerEntity = 3, IncludeFileData = expansion == "include-file-data" },
            new MigrationSchema { Entities = new[] { schema } });
        Assert.False(result.Success);
        Assert.Empty(harness.Queries);
        Assert.Equal(0, harness.PoolAccesses);
        Assert.Empty(harness.Writes);
    }

    public static IEnumerable<object[]> ExpansionFragments()
    {
        string[] fragments =
        {
            "<attribute name='entityimage'/>", "<all-attributes/>",
            "<link-entity name='contact' from='parentcustomerid' to='accountid'><attribute name='fullname'/></link-entity>",
            "<entity name='contact'/>", "<fetch><entity name='contact'/></fetch>",
            "<filter><attribute name='entityimage'/></filter>",
            "<filter><condition attribute='accountid' operator='in'><value><link-entity name='contact'/></value></condition></filter>",
            "<filter><condition attribute='name' operator='eq' value='x' entityname='foreign'/></filter>",
            "<filter xmlns='foreign'><condition attribute='name' operator='eq' value='x'/></filter>",
            "<filter type='and'/><attribute name='entityimage'/>",
            "<filter><condition attribute='name' operator='eq' value='x'><value>y</value></condition></filter>",
            "<filter"
        };
        foreach (var fragment in fragments)
        foreach (var format in new[] { ExportDataFormat.Cmt, ExportDataFormat.Json })
        foreach (var schemaFile in new[] { false, true })
            yield return new object[] { fragment, format, schemaFile };
    }

    [Theory]
    [MemberData(nameof(ExpansionFragments))]
    public async Task BoundedFilterExpansionRejectedBeforePoolAccess(string fragment, ExportDataFormat format, bool schemaFile)
    {
        var harness = new Harness(_ => throw new InvalidOperationException("must not query"));
        var entity = Schema("account");
        entity.FetchXmlFilter = fragment;
        var result = await harness.Run(new ExportOptions { MaxRecordsPerEntity = 3, Format = format },
            new MigrationSchema { Entities = new[] { entity } }, schemaFile);
        Assert.False(result.Success);
        Assert.Equal(0, harness.PoolAccesses);
        Assert.Empty(harness.Queries);
        Assert.Empty(harness.Writes);
    }

    [Fact]
    public async Task EveryBoundedSchemaFragmentIsValidatedBeforeAnyPoolAccess()
    {
        var harness = new Harness(_ => Rows(1));
        var second = Schema("contact");
        second.FetchXmlFilter = "<link-entity name='account'/>";
        var result = await harness.Run(new ExportOptions { MaxRecordsPerEntity = 3, DegreeOfParallelism = 1 },
            new MigrationSchema { Entities = new[] { Schema("account"), second } });
        Assert.False(result.Success);
        Assert.Equal(0, harness.PoolAccesses);
        Assert.Empty(harness.Writes);
    }

    [Theory]
    [InlineData("<filter><condition attribute='name' operator='eq' value='fixture'/></filter>")]
    [InlineData("<filter type='and'><filter type='or'><condition attribute='statecode' operator='eq' value='0'/><condition attribute='name' operator='not-null'/></filter></filter>")]
    [InlineData("<filter type='and'><condition attribute='accountid' operator='in'><value>11111111-1111-1111-1111-111111111111</value><value>22222222-2222-2222-2222-222222222222</value></condition></filter>")]
    public async Task BoundedSupportedFilterGrammarReachesQuery(string fragment)
    {
        var harness = new Harness(_ => Rows(1));
        var entity = Schema("account");
        entity.FetchXmlFilter = fragment;
        var result = await harness.Run(new ExportOptions { MaxRecordsPerEntity = 3 },
            new MigrationSchema { Entities = new[] { entity } }, schemaFile: true);
        Assert.True(result.Success);
        Assert.Single(harness.Writes);
        Assert.True(XNode.DeepEquals(XElement.Parse(fragment), Assert.Single(harness.Queries).Root!.Element("entity")!.Element("filter")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonpositiveCapRejectedBeforePoolAccess(int cap)
    {
        var harness = new Harness(_ => throw new InvalidOperationException("must not query"));
        var result = await harness.Run(new ExportOptions { MaxRecordsPerEntity = cap });
        Assert.False(result.Success);
        Assert.Equal(0, harness.PoolAccesses);
        Assert.Empty(harness.Writes);
    }

    private static EntitySchema Schema(string entity) => new()
    {
        LogicalName = entity, PrimaryIdField = entity + "id",
        Fields = new[] { new FieldSchema { LogicalName = entity + "id", Type = "uniqueidentifier" } }
    };

    private static EntityCollection Rows(int count, bool more = false) => new(
        Enumerable.Range(0, count).Select(_ => new Entity("account", Guid.NewGuid())).ToList())
        { MoreRecords = more, PagingCookie = more ? "<cookie/>" : null };

    private sealed class Harness
    {
        private readonly ParallelExporter _exporter;
        public List<XDocument> Queries { get; } = new();
        public List<MigrationData> Writes { get; } = new();
        public int PoolAccesses { get; private set; }

        public Harness(Func<XDocument, EntityCollection> query)
        {
            var client = new Mock<IPooledClient>();
            client.Setup(value => value.RetrieveMultipleAsync(It.IsAny<QueryBase>())).ReturnsAsync((QueryBase request) =>
            {
                var xml = XDocument.Parse(((FetchExpression)request).Query);
                lock (Queries) Queries.Add(xml);
                Assert.Null(xml.Root!.Attribute("aggregate"));
                return query(xml);
            });
            client.Setup(value => value.DisposeAsync()).Returns(ValueTask.CompletedTask);
            var pool = new Mock<IDataverseConnectionPool>();
            pool.Setup(value => value.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => { PoolAccesses++; return client.Object; });
            var writer = new Mock<ICmtDataWriter>();
            writer.Setup(value => value.WriteAsync(It.IsAny<MigrationData>(), It.IsAny<string>(), It.IsAny<IProgressReporter?>(), It.IsAny<CancellationToken>()))
                .Callback<MigrationData, string, IProgressReporter?, CancellationToken>((data, _, _, _) => Writes.Add(data))
                .Returns(Task.CompletedTask);
            var jsonWriter = new Mock<IJsonDataWriter>();
            jsonWriter.Setup(value => value.WriteAsync(It.IsAny<MigrationData>(), It.IsAny<string>(), It.IsAny<IProgressReporter?>(), It.IsAny<CancellationToken>()))
                .Callback<MigrationData, string, IProgressReporter?, CancellationToken>((data, _, _, _) => Writes.Add(data))
                .Returns(Task.CompletedTask);
            _exporter = new ParallelExporter(pool.Object, new CmtSchemaReader(), writer.Object, jsonDataWriter: jsonWriter.Object);
        }

        public async Task<ExportResult> Run(ExportOptions options, MigrationSchema? schema = null, bool schemaFile = false)
        {
            schema ??= new MigrationSchema { Entities = new[] { Schema("account") } };
            var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
            if (!schemaFile) return await _exporter.ExportAsync(schema, output, options);
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                await new CmtSchemaWriter().WriteAsync(schema, path);
                return await _exporter.ExportAsync(path, output, options);
            }
            finally { File.Delete(path); }
        }
    }
}
