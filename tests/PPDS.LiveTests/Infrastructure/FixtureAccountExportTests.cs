using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Pooling;
using PPDS.LiveTests.Cli;
using PPDS.Migration.Export;
using PPDS.Migration.Formats;
using Xunit;

namespace PPDS.LiveTests.Infrastructure;

/// <summary>Entirely offline: real exporter/query construction/CMT writer, fake pool, private local artifacts.</summary>
public sealed class FixtureAccountExportTests
{
    [Theory]
    [InlineData("empty")]
    [InlineData("empty-id")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("excess")]
    [InlineData("missing-owner")]
    public void InvalidOwnershipCannotCreateAnExportCapability(string scenario)
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var requested = scenario switch
        {
            "empty" => [], "empty-id" => new[] { Guid.Empty }, "unknown" => new[] { Guid.NewGuid() },
            "duplicate" => new[] { ids[0], ids[0] }, "excess" => ids, _ => new[] { ids[0] }
        };
        Assert.Throws<InvalidOperationException>(() => FixtureAccountExport.Create(
            scenario == "missing-owner" ? [] : ids, requested));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualExporterQueriesOnlyOwnedIdsAndAllowedColumns(bool description)
    {
        using var files = new Artifacts();
        var owned = Account(Guid.NewGuid(), "owned");
        owned["description"] = "fixture description";
        owned["telephone1"] = "dummy-unrelated-field";
        var unrelated = Account(Guid.NewGuid(), "dummy-unrelated-row");
        var backend = new Backend([owned, unrelated]);
        var scope = FixtureAccountExport.Create([owned.Id], [owned.Id], description);
        await scope.RunAsync(files.Schema, files.Pending, files.Accepted, backend.ExportAsync, "offline-profile", jsonProgress: true);
        Assert.True(File.Exists(files.Accepted));
        Assert.False(File.Exists(files.Pending));
        Assert.Single(backend.DataQueries);
        var data = await new CmtDataReader(new CmtSchemaReader()).ReadAsync(files.Accepted);
        var row = Assert.Single(data.EntityData["account"]);
        Assert.Equal(owned.Id, row.Id);
        Assert.Equal("owned", row.GetAttributeValue<string>("name"));
        Assert.Equal(description, row.Contains("description"));
        Assert.False(row.Contains("telephone1"));
        using var zip = ZipFile.OpenRead(files.Accepted);
        using var stream = zip.GetEntry("data.xml")!.Open();
        using var reader = new StreamReader(stream);
        var xml = await reader.ReadToEndAsync();
        Assert.DoesNotContain("dummy-unrelated", xml);
        Assert.Empty(data.RelationshipData.SelectMany(pair => pair.Value));
        Assert.Empty(data.FileData);
        Assert.Contains("--profile", backend.Arguments!);
        Assert.Contains("offline-profile", backend.Arguments!);
        Assert.Contains("--output-format", backend.Arguments!);
    }

    [Fact]
    public async Task OwnedRowsAcrossMultiplePagesAreAcceptedWithoutTruncation()
    {
        using var files = new Artifacts();
        var rows = Enumerable.Range(0, 3).Select(index => Account(Guid.NewGuid(), "owned-" + index)).ToArray();
        var backend = new Backend(rows) { OverridePage = page => Page(page == 1 ? rows.Take(2) : rows.Skip(2), page == 1) };
        var ids = rows.Select(row => row.Id).ToArray();
        await FixtureAccountExport.Create(ids, ids).RunAsync(files.Schema, files.Pending, files.Accepted, backend.ExportAsync);
        Assert.Equal(new[] { 3, 1 }, backend.DataQueries.Select(query => (int)query.Root!.Attribute("count")!));
        var data = await new CmtDataReader(new CmtSchemaReader()).ReadAsync(files.Accepted);
        Assert.Equal(ids.OrderBy(id => id), data.EntityData["account"].Select(row => row.Id).OrderBy(id => id));
    }

    [Theory]
    [InlineData("foreign-row")]
    [InlineData("foreign-field")]
    [InlineData("empty-id")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("overflow")]
    [InlineData("paging-duplicate")]
    [InlineData("paging-overflow")]
    public async Task ActualExporterCannotProduceAcceptedArtifactFromAdversarialResponses(string scenario)
    {
        using var files = new Artifacts();
        var owned = Account(Guid.NewGuid(), "owned");
        var backend = new Backend([owned]);
        backend.OverridePage = page => scenario switch
        {
            "foreign-row" => Page([Account(Guid.NewGuid(), "foreign")]),
            "foreign-field" => Page([new Entity("account", owned.Id) { ["name"] = "owned", ["telephone1"] = "dummy-extra-field" }]),
            "empty-id" => Page([Account(Guid.Empty, "empty")]),
            "duplicate" => Page([owned, owned]),
            "missing" => Page([]),
            "overflow" => Page([owned, owned, owned, owned]),
            "paging-duplicate" => Page([owned], page == 1),
            "paging-overflow" => Page(page == 1 ? [owned] : [owned, owned, owned], page == 1),
            _ => throw new InvalidOperationException()
        };
        var scope = FixtureAccountExport.Create([owned.Id], [owned.Id]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.RunAsync(files.Schema, files.Pending, files.Accepted, backend.ExportAsync));
        Assert.False(File.Exists(files.Accepted));
        Assert.NotEmpty(backend.DataQueries);
    }

    [Theory]
    [InlineData("fields")]
    [InlineData("relationship")]
    [InlineData("filter")]
    [InlineData("entity")]
    public async Task BroadenedSchemaFailsClosed(string mutation)
    {
        using var files = new Artifacts();
        var id = Guid.NewGuid();
        var scope = FixtureAccountExport.Create([id], [id]);
        await new CmtSchemaWriter().WriteAsync(scope.CreateSchema(), files.Schema);
        var xml = XDocument.Load(files.Schema);
        var entity = xml.Root!.Element("entity")!;
        switch (mutation)
        {
            case "fields": entity.Element("fields")!.Add(new XElement("field", new XAttribute("name", "telephone1"))); break;
            case "relationship": entity.Add(new XElement("relationships", new XElement("relationship"))); break;
            case "filter": entity.Element("filter")!.Remove(); break;
            case "entity": xml.Root.Add(new XElement(entity)); break;
        }
        xml.Save(files.Schema);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ValidateSchemaFileAsync(files.Schema));
        Assert.False(File.Exists(files.Accepted));
    }

    [Theory]
    [InlineData("binary-entry")]
    [InlineData("duplicate-entry")]
    [InlineData("schema-field")]
    [InlineData("relationship")]
    [InlineData("record-attribute")]
    [InlineData("primary-key")]
    [InlineData("duplicate-field")]
    [InlineData("byte-overflow")]
    public async Task RawArtifactExpansionIsRejectedBeforePromotion(string mutation)
    {
        using var files = new Artifacts();
        var owned = Account(Guid.NewGuid(), "owned");
        var backend = new Backend([owned]);
        var scope = FixtureAccountExport.Create([owned.Id], [owned.Id]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.RunAsync(
            files.Schema, files.Pending, files.Accepted, async (args, token) =>
            {
                var result = await backend.ExportAsync(args, token);
                using var archive = ZipFile.Open(files.Pending, ZipArchiveMode.Update);
                if (mutation is "binary-entry" or "duplicate-entry")
                {
                    archive.CreateEntry(mutation == "binary-entry" ? "files/account/foreign.bin" : "data.xml");
                    return result;
                }
                var entry = archive.GetEntry(mutation == "schema-field" ? "data_schema.xml" : "data.xml")!;
                XDocument xml;
                using (var input = entry.Open()) xml = XDocument.Load(input);
                var record = xml.Descendants("record").SingleOrDefault();
                switch (mutation)
                {
                    case "schema-field": xml.Descendants("fields").Single().Add(new XElement("field", new XAttribute("name", "telephone1"))); break;
                    case "relationship": xml.Descendants("m2mrelationships").Single().Add(new XElement("relationship")); break;
                    case "record-attribute": record!.SetAttributeValue("foreign", "dummy-secret-field"); break;
                    case "primary-key": record!.Elements("field").Single(field => (string?)field.Attribute("name") == "accountid").SetAttributeValue("value", Guid.NewGuid()); break;
                    case "duplicate-field": record!.Add(new XElement(record.Elements("field").First())); break;
                    case "byte-overflow": record!.Elements("field").Single(field => (string?)field.Attribute("name") == "name").SetAttributeValue("value", new string('x', 150_000)); break;
                }
                var entryName = entry.FullName;
                entry.Delete();
                using (var output = archive.CreateEntry(entryName).Open()) xml.Save(output);
                return result;
            }));
        Assert.False(File.Exists(files.Accepted));
    }

    public static IEnumerable<object[]> XmlPayloadCases()
    {
        foreach (var entry in new[] { "[Content_Types].xml", "data_schema.xml", "data.xml" })
        foreach (var mutation in new[] { "comment-before", "comment-after", "pi-before", "pi-after", "binary-nul", "malformed" })
            yield return new object[] { entry, mutation };
        yield return new object[] { "[Content_Types].xml", "unexpected-contract" };
        foreach (var mutation in new[] { "extra-namespace", "namespace-uri", "timestamp", "displayname" })
            yield return new object[] { "data.xml", mutation };
    }

    [Theory]
    [MemberData(nameof(XmlPayloadCases))]
    public async Task EveryXmlEntryRejectsUnrelatedDocumentPayloadWithoutLeakingParserText(string entryName, string mutation)
    {
        const string secret = "dummy-secret-unrelated-payload";
        using var files = new Artifacts();
        var row = Account(Guid.NewGuid(), "owned");
        var backend = new Backend([row]);
        var scope = FixtureAccountExport.Create([row.Id], [row.Id]);
        var mutated = false;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => scope.RunAsync(
            files.Schema, files.Pending, files.Accepted, async (args, token) =>
            {
                var result = await backend.ExportAsync(args, token);
                using var archive = ZipFile.Open(files.Pending, ZipArchiveMode.Update);
                var entry = archive.GetEntry(entryName)!;
                XDocument xml;
                using (var input = entry.Open()) xml = XDocument.Load(input);
                entry.Delete();
                using var output = archive.CreateEntry(entryName).Open();
                if (mutation is "binary-nul" or "malformed")
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(mutation == "binary-nul" ? "\0" + secret : "<" + secret);
                    await output.WriteAsync(bytes, token);
                    mutated = true;
                    return result;
                }
                switch (mutation)
                {
                    case "unexpected-contract": xml.Descendants().Single(element => element.Name.LocalName == "Default").SetAttributeValue("ContentType", secret); break;
                    case "extra-namespace": xml.Root!.SetAttributeValue(XNamespace.Xmlns + "foreign", secret); break;
                    case "namespace-uri": xml.Root!.SetAttributeValue(XNamespace.Xmlns + "xsd", secret); break;
                    case "timestamp": xml.Root!.SetAttributeValue("timestamp", secret); break;
                    case "displayname": xml.Root!.Element("entity")!.SetAttributeValue("displayname", secret); break;
                    default:
                        XNode payload = mutation.StartsWith("comment", StringComparison.Ordinal)
                            ? new XComment(secret) : new XProcessingInstruction("foreign", secret);
                        if (mutation.EndsWith("before", StringComparison.Ordinal)) xml.Root!.AddBeforeSelf(payload);
                        else xml.Root!.AddAfterSelf(payload);
                        break;
                }
                xml.Save(output);
                mutated = true;
                return result;
            }));
        Assert.StartsWith("Fixture account export refused:", failure.Message);
        Assert.DoesNotContain(secret, failure.ToString());
        Assert.Null(failure.InnerException);
        Assert.True(mutated, "the actual export must reach the adversarial artifact boundary");
        Assert.False(File.Exists(files.Accepted));
    }

    [Fact]
    public async Task ExportFailurePreservesPrimaryAndRetainsOwnedPaths()
    {
        using var files = new Artifacts();
        var id = Guid.NewGuid();
        var scope = FixtureAccountExport.Create([id], [id]);
        var primary = new IOException("controlled primary");
        var actual = await Assert.ThrowsAsync<IOException>(() => scope.RunAsync(
            files.Schema, files.Pending, files.Accepted, async (_, token) =>
            {
                await File.WriteAllTextAsync(files.Pending, "owned partial output", token);
                throw primary;
            }));
        Assert.Same(primary, actual);
        Assert.True(File.Exists(files.Schema));
        Assert.True(File.Exists(files.Pending));
        Assert.False(File.Exists(files.Accepted));
    }

    [Fact]
    public async Task CancellationIsPassedToDispatchAndNeverAcceptsArtifact()
    {
        using var files = new Artifacts();
        using var cancellation = new CancellationTokenSource();
        var id = Guid.NewGuid();
        var scope = FixtureAccountExport.Create([id], [id]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.RunAsync(
            files.Schema, files.Pending, files.Accepted, (_, token) =>
            {
                Assert.Equal(cancellation.Token, token);
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("unreachable");
            }, cancellationToken: cancellation.Token));
        Assert.False(File.Exists(files.Accepted));
    }

    [Fact]
    public async Task ExportFixtureDisposerRetainsOwnershipAfterInterruption()
    {
        var fixture = new DataExportCommandE2ETests();
        var config = Path.GetDirectoryName(fixture.InterruptionEvidencePath)!;
        var ids = (List<Guid>)typeof(DataExportCommandE2ETests).GetField("_createdAccountIds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture)!;
        var id = Guid.NewGuid();
        ids.Add(id);
        var calls = 0;
        fixture.DeleteAccountsAsync = (_, _) => { calls++; throw new InvalidOperationException("must not invoke SDK"); };
        var primary = new IOException("controlled interruption");
        try
        {
            Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() => fixture.RunCommandAsync(
                () => new ExitedProcess(), _ => Task.FromException<CliResult>(primary), default)));
            await fixture.DisposeAsync();
            Assert.Equal(0, calls);
            using var evidence = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(fixture.InterruptionEvidencePath));
            Assert.Equal(id, evidence.RootElement.GetProperty("ownedRecords").GetProperty("account")[0].GetGuid());
        }
        finally { Directory.Delete(config, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulExportBodyCannotHideCleanupFailureOrRetry(bool reportingFails)
    {
        var fixture = new DataExportCommandE2ETests();
        var config = Path.GetDirectoryName(fixture.InterruptionEvidencePath)!;
        var cleanupEvidence = Path.Combine(config, "data-export-cleanup.json");
        var artifact = Path.Combine(config, "owned-output.zip");
        var ids = (List<Guid>)typeof(DataExportCommandE2ETests).GetField("_createdAccountIds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture)!;
        var id = Guid.NewGuid();
        ids.Add(id);
        var files = (List<string>)typeof(CliE2ETestBase).GetProperty("CreatedFiles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture)!;
        files.Add(artifact);
        var calls = 0;
        fixture.DeleteAccountsAsync = (_, _) => { calls++; throw new IOException("dummy-secret-SDK-message"); };
        try
        {
            await File.WriteAllTextAsync(artifact, "owned output");
            if (reportingFails) Directory.CreateDirectory(cleanupEvidence);
            // xUnit records a DisposeAsync failure separately even when the body succeeded.
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DisposeAsync());
            Assert.Contains(typeof(IOException).FullName!, failure.Message);
            Assert.DoesNotContain("dummy-secret", failure.ToString());
            Assert.True(File.Exists(artifact));
            Assert.True(Directory.Exists(config));
            Assert.Equal(id, Assert.Single(ids));
            if (!reportingFails)
            {
                var text = await File.ReadAllTextAsync(cleanupEvidence);
                Assert.DoesNotContain("dummy-secret", text);
                using var evidence = System.Text.Json.JsonDocument.Parse(text);
                Assert.Equal(id, evidence.RootElement.GetProperty("ownedAccountIds")[0].GetGuid());
                Assert.Equal(typeof(IOException).FullName, evidence.RootElement.GetProperty("failureType").GetString());
            }
            var repeated = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DisposeAsync());
            Assert.DoesNotContain("dummy-secret", repeated.ToString());
            Assert.Equal(1, calls);
            Assert.True(File.Exists(artifact));
        }
        finally { Directory.Delete(config, recursive: true); }
    }

    [Fact]
    public async Task SuccessfulOwnedCleanupCompletesOnceAndRemovesLocalArtifacts()
    {
        var fixture = new DataExportCommandE2ETests();
        var config = Path.GetDirectoryName(fixture.InterruptionEvidencePath)!;
        var ids = (List<Guid>)typeof(DataExportCommandE2ETests).GetField("_createdAccountIds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture)!;
        var id = Guid.NewGuid();
        ids.Add(id);
        var calls = 0;
        fixture.DeleteAccountsAsync = (_, ownedIds) =>
        {
            Assert.Equal(id, Assert.Single(ownedIds));
            calls++;
            return Task.CompletedTask;
        };
        await fixture.DisposeAsync();
        Assert.False(Directory.Exists(config));
        Assert.Empty(ids);
        await fixture.DisposeAsync();
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task UncertainCreateRetainsNameAndArtifactsWithoutAdoptionOrCleanup(bool reportingFails, bool emptyReceipt)
    {
        var fixture = new DataExportCommandE2ETests();
        var config = Path.GetDirectoryName(fixture.InterruptionEvidencePath)!;
        var evidencePath = Path.Combine(config, "data-export-create.json");
        var artifact = Path.Combine(config, "owned-schema.xml");
        var ids = (List<Guid>)typeof(DataExportCommandE2ETests).GetField("_createdAccountIds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture)!;
        var files = (List<string>)typeof(CliE2ETestBase).GetProperty("CreatedFiles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture)!;
        files.Add(artifact);
        string? submittedName = null;
        var creates = 0;
        var deletes = 0;
        fixture.CreateAccountAsync = (_, name) =>
        {
            submittedName = name;
            creates++;
            // Simulates a request that may have committed before the response failed.
            if (!emptyReceipt) throw new IOException("dummy-secret-create-response");
            return Task.FromResult(Guid.Empty);
        };
        fixture.DeleteAccountsAsync = (_, _) => { deletes++; throw new InvalidOperationException("must never clean an uncertain create"); };
        try
        {
            await File.WriteAllTextAsync(artifact, "owned schema");
            if (reportingFails) Directory.CreateDirectory(evidencePath);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateTestAccountAsync());
            Assert.StartsWith("PPDS_E2EExport_", submittedName);
            Assert.Contains("unknown outcome", failure.Message);
            Assert.Contains(submittedName!, failure.Message);
            Assert.DoesNotContain("dummy-secret", failure.ToString());
            Assert.Null(failure.InnerException);
            Assert.Empty(ids);
            Assert.Throws<InvalidOperationException>(() => FixtureAccountExport.Create(ids, [Guid.NewGuid()]));
            if (!reportingFails)
            {
                var text = await File.ReadAllTextAsync(evidencePath);
                Assert.DoesNotContain("dummy-secret", text);
                using var evidence = System.Text.Json.JsonDocument.Parse(text);
                Assert.Equal(submittedName, evidence.RootElement.GetProperty("possibleAccountName").GetString());
                Assert.Equal("unknown", evidence.RootElement.GetProperty("creationOutcome").GetString());
                Assert.Empty(evidence.RootElement.GetProperty("ownedAccountIds").EnumerateArray());
                Assert.Equal(emptyReceipt ? typeof(InvalidOperationException).FullName : typeof(IOException).FullName,
                    evidence.RootElement.GetProperty("failureType").GetString());
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateTestAccountAsync());
            var disposal = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DisposeAsync());
            Assert.Contains(submittedName!, disposal.Message);
            Assert.DoesNotContain("dummy-secret", disposal.ToString());
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DisposeAsync());
            Assert.Equal(1, creates);
            Assert.Equal(0, deletes);
            Assert.True(File.Exists(artifact));
            Assert.True(Directory.Exists(config));
        }
        finally { Directory.Delete(config, recursive: true); }
    }

    [Fact]
    public async Task ConfirmedCreateEstablishesOnlyReturnedIdAndOwnedCleanupStillRuns()
    {
        var fixture = new DataExportCommandE2ETests();
        var config = Path.GetDirectoryName(fixture.InterruptionEvidencePath)!;
        var id = Guid.NewGuid();
        fixture.CreateAccountAsync = (_, name) =>
        {
            Assert.StartsWith("PPDS_E2EExport_", name);
            return Task.FromResult(id);
        };
        var deletes = 0;
        fixture.DeleteAccountsAsync = (_, ids) =>
        {
            Assert.Equal(id, Assert.Single(ids));
            deletes++;
            return Task.CompletedTask;
        };
        try
        {
            Assert.Equal(id, await fixture.CreateTestAccountAsync());
            var ids = (List<Guid>)typeof(DataExportCommandE2ETests).GetField("_createdAccountIds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture)!;
            Assert.Equal(id, Assert.Single(ids));
            await fixture.DisposeAsync();
            Assert.Equal(1, deletes);
            Assert.False(Directory.Exists(config));
        }
        finally { if (Directory.Exists(config)) Directory.Delete(config, recursive: true); }
    }

    private sealed class ExitedProcess : IFixtureProcess
    {
        public bool HasExited => true;
        public void Kill() => throw new InvalidOperationException("already exited");
        public Task WaitForExitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static Entity Account(Guid id, string name) => new("account", id) { ["accountid"] = id, ["name"] = name };
    private static EntityCollection Page(IEnumerable<Entity> rows, bool more = false) => new(rows.ToList())
        { MoreRecords = more, PagingCookie = more ? "<cookie/>" : null };

    private sealed class Backend(Entity[] dataset)
    {
        public Func<int, EntityCollection>? OverridePage { get; set; }
        public List<XDocument> DataQueries { get; } = [];
        public string[]? Arguments { get; private set; }

        public async Task<CliResult> ExportAsync(string[] args, CancellationToken cancellationToken)
        {
            Arguments = args;
            var client = OfflineProxy.Create<IPooledClient>((method, values) => method.Name switch
            {
                "RetrieveMultipleAsync" => Task.FromResult(Query((FetchExpression)values![0]!)),
                "DisposeAsync" => ValueTask.CompletedTask,
                "Dispose" => null,
                _ => throw new InvalidOperationException("Unexpected offline client operation: " + method.Name)
            });
            var pool = OfflineProxy.Create<IDataverseConnectionPool>((method, _) => method.Name switch
            {
                "GetClientAsync" => Task.FromResult(client),
                _ => throw new InvalidOperationException("Unexpected offline pool operation: " + method.Name)
            });
            var exporter = new ParallelExporter(pool, new CmtSchemaReader(), new CmtDataWriter());
            string Value(string key) => args[Array.IndexOf(args, key) + 1];
            var result = await exporter.ExportAsync(Value("--schema"), Value("--output"), new ExportOptions
            {
                DegreeOfParallelism = 1, PageLevelParallelism = int.Parse(Value("--page-parallel")),
                PageSize = int.Parse(Value("--batch-size")), IncludeFileData = false,
                MaxRecordsPerEntity = int.Parse(Value("--max-records-per-entity"))
            }, cancellationToken: cancellationToken);
            return new CliResult { ExitCode = result.Success ? 0 : 1 };
        }

        private EntityCollection Query(FetchExpression fetch)
        {
            var xml = XDocument.Parse(fetch.Query);
            if ((string?)xml.Root!.Attribute("aggregate") == "true")
                return Page([new Entity("account") { ["cnt"] = new AliasedValue("account", "accountid", dataset.Length) }]);
            DataQueries.Add(xml);
            var entity = xml.Root.Element("entity")!;
            Assert.Equal("account", (string?)entity.Attribute("name"));
            Assert.Empty(entity.Elements("link-entity"));
            Assert.Empty(entity.Elements("all-attributes"));
            var page = (int?)xml.Root.Attribute("page") ?? 1;
            if (OverridePage != null) return OverridePage(page);
            var condition = Assert.Single(entity.Descendants("condition"));
            Assert.Equal("accountid", (string?)condition.Attribute("attribute"));
            Assert.Equal("in", (string?)condition.Attribute("operator"));
            var ids = condition.Elements("value").Select(value => Guid.Parse(value.Value)).ToHashSet();
            var columns = entity.Elements("attribute").Select(attribute => (string)attribute.Attribute("name")!).ToHashSet();
            return Page(dataset.Where(row => ids.Contains(row.Id)).Select(row =>
            {
                var projected = new Entity(row.LogicalName, row.Id);
                foreach (var (key, value) in row.Attributes.Where(pair => columns.Contains(pair.Key))) projected[key] = value;
                return projected;
            }));
        }
    }

    public class OfflineProxy : DispatchProxy
    {
        private Func<MethodInfo, object?[]?, object?> _invoke = null!;
        internal static T Create<T>(Func<MethodInfo, object?[]?, object?> invoke) where T : class
        {
            var proxy = Create<T, OfflineProxy>();
            ((OfflineProxy)(object)proxy)._invoke = invoke;
            return proxy;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _invoke(targetMethod!, args);
    }

    private sealed class Artifacts : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "ppds-offline-export-" + Guid.NewGuid().ToString("N"));
        public string Schema => Path.Combine(_directory, "schema.xml");
        public string Pending => Path.Combine(_directory, "pending.zip");
        public string Accepted => Path.Combine(_directory, "accepted.zip");
        public Artifacts() => Directory.CreateDirectory(_directory);
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
