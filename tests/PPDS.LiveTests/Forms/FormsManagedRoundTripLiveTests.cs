using System.IO.Pipelines;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Services.Environment;
using PPDS.Dataverse.Pooling;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.LiveTests.Forms;

/// <summary>Explicitly opted-in, run-owned managed fixtures. See Fixtures/Forms/README.md before running.</summary>
[Trait("Category", "DestructiveE2E")]
[Collection("LiveDataverse")]
public sealed class FormsManagedRoundTripLiveTests(ITestOutputHelper output)
{
    [FormsFixtureOptIn]
    public async Task ManagedAndUnmanagedForms_CompleteMcpRoundTrip_AndLockedFormRejects()
    {
        var url = EnvironmentConfirmationService.NormalizeUrl(Required("PPDS_FORMS_LIVE_URL"));
        EnvironmentConfirmationService.NormalizeUrl(Required("PPDS_FORMS_LIVE_CONFIRM")).Should().Be(url);
        var runId = Required("PPDS_FORMS_LIVE_RUN_ID");
        Regex.IsMatch(runId, "^[a-f0-9]{8}$").Should().BeTrue("a reviewed unique eight-digit hex run ID is required");
        var artifactRoot = Required("PPDS_FORMS_LIVE_ARTIFACTS");
        Path.IsPathFullyQualified(artifactRoot).Should().BeTrue();
        var artifactDirectory = Path.Combine(artifactRoot, "forms-" + runId);
        Directory.Exists(artifactDirectory).Should().BeFalse("a run ID cannot be silently reused");
        Directory.CreateDirectory(artifactDirectory);
        var profileName = Required("PPDS_FORMS_LIVE_PROFILE");
        var prefix = "pfr" + runId[..4];
        var sourceName = "PPDSFormsSource_" + runId;
        var changesName = "PPDSFormsChanges_" + runId;
        var publisherName = "PPDSFormsPublisher_" + runId;
        var editable = prefix + "_editable";
        var locked = prefix + "_locked";
        var createdTables = new Dictionary<string, Guid>();
        var unknownSubmissions = new List<string>();
        var manifest = new Dictionary<string, object?> { ["runId"] = runId, ["environmentUrl"] = url,
            ["publisher"] = publisherName, ["prefix"] = prefix, ["sourceSolution"] = sourceName,
            ["changesSolution"] = changesName, ["tables"] = new[] { editable, locked }, ["state"] = "planned" };
        async Task SaveManifestAsync() => await File.WriteAllTextAsync(Path.Combine(artifactDirectory, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        await SaveManifestAsync();
        using var store = new ProfileStore();
        using var credentials = new NativeCredentialStore();
        await using var pools = new McpConnectionPoolManager(profileStore: store);
        using var context = new McpToolContext(pools, store, credentials, new() { Profile = profileName, Environment = url });
        await context.InitializeAsync();
        await using var provider = await context.CreateServiceProviderAsync();
        await provider.GetRequiredService<IEnvironmentConfirmationService>().EnsureConfirmedAsync("provision isolated form validation fixtures", url);
        var pool = provider.GetRequiredService<IDataverseConnectionPool>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var ct = timeout.Token;
        async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request)
        {
            await using var client = await pool.GetClientAsync(cancellationToken: ct);
            return await client.ExecuteAsync(request, ct);
        }
        async Task<EntityCollection> QueryAsync(QueryExpression query)
        {
            await using var client = await pool.GetClientAsync(cancellationToken: ct);
            return await client.RetrieveMultipleAsync(query, ct);
        }
        async Task<Guid> CreateAsync(Entity entity)
        {
            await using var client = await pool.GetClientAsync(cancellationToken: ct);
            try { return await client.CreateAsync(entity, ct); }
            catch
            {
                unknownSubmissions.Add($"Create {entity.LogicalName} '{entity.GetAttributeValue<string>("uniquename")}' has no confirmed returned ID; inspect manually.");
                throw;
            }
        }
        async Task DeleteAsync(string entity, Guid id)
        {
            await using var client = await pool.GetClientAsync(cancellationToken: ct);
            await client.DeleteAsync(entity, id, ct);
        }
        async Task<Entity?> FindSolutionAsync(string name)
        {
            var q = new QueryExpression("solution") { ColumnSet = new("solutionid", "publisherid", "ismanaged", "version"), TopCount = 2 };
            q.Criteria.AddCondition("uniquename", ConditionOperator.Equal, name);
            var rows = await QueryAsync(q);
            rows.Entities.Count.Should().BeLessThanOrEqualTo(1);
            return rows.Entities.SingleOrDefault();
        }
        // Collision checks precede every write. Existing shared fixtures are never adopted.
        (await FindSolutionAsync(sourceName)).Should().BeNull();
        (await FindSolutionAsync(changesName)).Should().BeNull();
        var publisherQuery = new QueryExpression("publisher") { ColumnSet = new("publisherid"), TopCount = 1 };
        publisherQuery.Criteria.AddCondition("uniquename", ConditionOperator.Equal, publisherName);
        (await QueryAsync(publisherQuery)).Entities.Should().BeEmpty();
        var metadata = (RetrieveAllEntitiesResponse)await ExecuteAsync(new RetrieveAllEntitiesRequest { EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = true });
        metadata.EntityMetadata.Should().NotContain(m => m.LogicalName.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase));
        Guid publisherId = Guid.Empty;
        Guid sourceId = Guid.Empty;
        Guid changesId = Guid.Empty;
        var cleanupFailures = new List<string>();
        async Task VerifySolutionAsync(Guid id, string name, bool? managed = null)
        {
            var solution = await FindSolutionAsync(name);
            solution.Should().NotBeNull();
            solution!.Id.Should().Be(id, "deletion requires the recorded solution ID");
            solution.GetAttributeValue<EntityReference>("publisherid").Id.Should().Be(publisherId);
            if (managed.HasValue) solution.GetAttributeValue<bool>("ismanaged").Should().Be(managed.Value);
        }
        async Task DeleteOwnedTableAsync(string table, Guid metadataId)
        {
            var response = (RetrieveEntityResponse)await ExecuteAsync(new RetrieveEntityRequest
                { LogicalName = table, EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = true });
            response.EntityMetadata.MetadataId.Should().Be(metadataId, "a same-name table must never be adopted for cleanup");
            response.EntityMetadata.LogicalName.Should().Be(table);
            response.EntityMetadata.IsManaged.Should().BeFalse();
            await VerifySolutionAsync(sourceId, sourceName, managed: false);
            var membership = new QueryExpression("solutioncomponent") { ColumnSet = new("solutioncomponentid"), TopCount = 1 };
            membership.Criteria.AddCondition("solutionid", ConditionOperator.Equal, sourceId);
            membership.Criteria.AddCondition("objectid", ConditionOperator.Equal, metadataId);
            membership.Criteria.AddCondition("componenttype", ConditionOperator.Equal, 1);
            (await QueryAsync(membership)).Entities.Should().ContainSingle("the recorded table must still belong to this run's publisher/source solution");
            await ExecuteAsync(new DeleteEntityRequest { LogicalName = table });
            createdTables.Remove(table);
        }
        try
        {
            manifest["state"] = "provisioning"; await SaveManifestAsync();
            publisherId = await CreateAsync(new("publisher") { ["uniquename"] = publisherName, ["friendlyname"] = publisherName,
                ["customizationprefix"] = prefix, ["customizationoptionvalueprefix"] = 70000 + Convert.ToInt32(runId[..4], 16) % 20000 });
            manifest["publisherId"] = publisherId; await SaveManifestAsync();
            sourceId = await CreateAsync(new("solution") { ["uniquename"] = sourceName, ["friendlyname"] = sourceName,
                ["version"] = "1.0.0.0", ["publisherid"] = new EntityReference("publisher", publisherId) });
            manifest["sourceSolutionId"] = sourceId; await SaveManifestAsync();
            var formIds = new Dictionary<string, Guid>();
            foreach (var table in new[] { editable, locked })
            {
                CreateEntityResponse created;
                try
                {
                    created = (CreateEntityResponse)await ExecuteAsync(new CreateEntityRequest
                    {
                    Entity = new EntityMetadata { SchemaName = table, DisplayName = new Label("PPDS form fixture " + table, 1033),
                        DisplayCollectionName = new Label("PPDS form fixtures " + table, 1033), OwnershipType = OwnershipTypes.UserOwned,
                        IsActivity = false, IsCustomizable = new BooleanManagedProperty(true) },
                    PrimaryAttribute = new StringAttributeMetadata { SchemaName = prefix + "_name", DisplayName = new Label("Name", 1033),
                        MaxLength = 100, RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None) },
                    HasActivities = false, HasNotes = true, SolutionUniqueName = sourceName
                    });
                }
                catch
                {
                    unknownSubmissions.Add($"Create table '{table}' has no confirmed metadata ID; inspect manually without name-based deletion.");
                    throw;
                }
                createdTables[table] = created.EntityId;
                manifest["createdTableMetadataIds"] = new Dictionary<string, Guid>(createdTables);
                await SaveManifestAsync();
                foreach (var field in new[] { "unmanagedprobe", "managedprobe" })
                    await ExecuteAsync(new CreateAttributeRequest { EntityName = table, SolutionUniqueName = sourceName,
                        Attribute = new StringAttributeMetadata { SchemaName = prefix + "_" + field, DisplayName = new Label(field, 1033), MaxLength = 100 } });
                var query = new QueryExpression("systemform") { ColumnSet = new("formid", "formxml"), TopCount = 2 };
                query.Criteria.AddCondition("objecttypecode", ConditionOperator.Equal, table);
                query.Criteria.AddCondition("type", ConditionOperator.Equal, 2);
                var forms = (RetrieveUnpublishedMultipleResponse)await ExecuteAsync(new RetrieveUnpublishedMultipleRequest { Query = query });
                forms.EntityCollection.Entities.Should().ContainSingle("new fixture table must have one Main form");
                var form = forms.EntityCollection.Entities.Single();
                formIds[table] = form.Id;
                var xml = await File.ReadAllTextAsync(Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "Forms", "timeline-form.xml"), ct);
                xml = xml.Replace("datafieldname=\"name\"", $"datafieldname=\"{prefix}_name\"");
                await using (var client = await pool.GetClientAsync(cancellationToken: ct))
                    await client.UpdateAsync(new Entity("systemform", form.Id) { ["formxml"] = xml,
                        ["iscustomizable"] = new BooleanManagedProperty(table == editable) }, ct);
                await ExecuteAsync(new AddSolutionComponentRequest { ComponentType = 60, ComponentId = form.Id,
                    SolutionUniqueName = sourceName, AddRequiredComponents = false });
                await ExecuteAsync(new PublishXmlRequest { ParameterXml = $"<importexportxml><entities><entity>{table}</entity></entities></importexportxml>" });
            }
            manifest["formIds"] = formIds; await SaveManifestAsync();
            await using (var protocol = await ProtocolFixture.CreateAsync(context, ct))
                await RoundTripAsync(protocol.Client, editable, formIds[editable], prefix + "_unmanagedprobe", null, false, url, artifactDirectory, ct);
            var exported = (ExportSolutionResponse)await ExecuteAsync(new ExportSolutionRequest { SolutionName = sourceName, Managed = true });
            await File.WriteAllBytesAsync(Path.Combine(artifactDirectory, "managed-fixture.zip"), exported.ExportSolutionFile, ct);
            manifest["state"] = "managed-exported"; await SaveManifestAsync();
            foreach (var table in createdTables.ToArray()) await DeleteOwnedTableAsync(table.Key, table.Value);
            await VerifySolutionAsync(sourceId, sourceName, managed: false);
            await DeleteAsync("solution", sourceId);
            sourceId = Guid.Empty;
            var importJobId = Guid.NewGuid();
            manifest["importJobId"] = importJobId;
            manifest["managedPackageSha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(exported.ExportSolutionFile));
            await SaveManifestAsync();
            var unresolvedImport = $"Managed import '{sourceName}', job '{importJobId}', lacks a confirmed validated solution ID; inspect manually without name-based deletion.";
            unknownSubmissions.Add(unresolvedImport);
            try
            {
                await ExecuteAsync(new ImportSolutionRequest { CustomizationFile = exported.ExportSolutionFile,
                    ImportJobId = importJobId, OverwriteUnmanagedCustomizations = false, PublishWorkflows = false });
            }
            catch
            {
                throw;
            }
            var imported = await FindSolutionAsync(sourceName);
            imported.Should().NotBeNull();
            imported!.GetAttributeValue<bool>("ismanaged").Should().BeTrue();
            imported.GetAttributeValue<EntityReference>("publisherid").Id.Should().Be(publisherId);
            imported.GetAttributeValue<string>("version").Should().Be("1.0.0.0");
            // Adopt only after a successful synchronous import of this recorded package, never after an exception.
            sourceId = imported.Id;
            unknownSubmissions.Remove(unresolvedImport);
            manifest["managedSolutionId"] = sourceId;
            manifest["state"] = "managed-imported"; await SaveManifestAsync();
            changesId = await CreateAsync(new("solution") { ["uniquename"] = changesName, ["friendlyname"] = changesName,
                ["version"] = "1.0.0.0", ["publisherid"] = new EntityReference("publisher", publisherId) });
            manifest["changesSolutionId"] = changesId; await SaveManifestAsync();
            await using (var protocol = await ProtocolFixture.CreateAsync(context, ct))
            {
                await RoundTripAsync(protocol.Client, editable, formIds[editable], prefix + "_managedprobe", changesName, true, url, artifactDirectory, ct);
                var lockedDraft = await GetDraftAsync(protocol.Client, locked, formIds[locked], ct);
                var response = await protocol.Client.CallToolAsync("ppds_forms_add_field", new Dictionary<string, object?>
                {
                    ["entityLogicalName"] = locked, ["formId"] = formIds[locked], ["sectionId"] = SectionId,
                    ["fieldLogicalName"] = prefix + "_managedprobe", ["expectedDraftHash"] = lockedDraft.GetProperty("draftHash").GetString(),
                    ["confirmEnvironment"] = url
                }, cancellationToken: ct);
                response.IsError.Should().BeTrue();
                string.Join("", response.Content.Select(c => c.Text)).Should().Contain("Forms.FormNotCustomizable");
                var after = await GetDraftAsync(protocol.Client, locked, formIds[locked], ct);
                after.GetProperty("draftHash").GetString().Should().Be(lockedDraft.GetProperty("draftHash").GetString());
            }
            manifest["state"] = "verified"; await SaveManifestAsync();
        }
        finally
        {
            // Use a fresh bounded token for cleanup even if the test timed out.
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            ct = cleanupTimeout.Token;
            async Task CleanupAsync(string description, Func<Task> action)
            {
                try { await action(); output.WriteLine("Cleaned: " + description); }
                catch (Exception ex) { cleanupFailures.Add(description + ": " + ex.Message); }
            }
            if (changesId != Guid.Empty) await CleanupAsync(changesName, async () =>
                { await VerifySolutionAsync(changesId, changesName, managed: false); await DeleteAsync("solution", changesId); });
            foreach (var table in createdTables.ToArray())
                await CleanupAsync(table.Key, () => DeleteOwnedTableAsync(table.Key, table.Value));
            if (sourceId != Guid.Empty && createdTables.Count == 0 && unknownSubmissions.Count == 0)
                await CleanupAsync(sourceName, async () =>
                {
                    await VerifySolutionAsync(sourceId, sourceName);
                    await DeleteAsync("solution", sourceId);
                });
            else if (sourceId != Guid.Empty)
                cleanupFailures.Add("Retained source solution because a table could not be verified/removed or a submission is unresolved; preserve membership evidence for recovery.");
            if (publisherId != Guid.Empty && unknownSubmissions.Count == 0 && cleanupFailures.Count == 0) await CleanupAsync(publisherName, async () =>
            {
                var query = new QueryExpression("publisher") { ColumnSet = new("publisherid", "customizationprefix"), TopCount = 2 };
                query.Criteria.AddCondition("uniquename", ConditionOperator.Equal, publisherName);
                var found = (await QueryAsync(query)).Entities.Should().ContainSingle().Subject;
                found.Id.Should().Be(publisherId);
                found.GetAttributeValue<string>("customizationprefix").Should().Be(prefix);
                await DeleteAsync("publisher", publisherId);
            });
            else if (publisherId != Guid.Empty)
                cleanupFailures.Add("Retained publisher because ownership recovery is incomplete; inspect the recorded IDs and unknown submissions.");
            manifest["cleanupFailures"] = cleanupFailures;
            manifest["unknownSubmissions"] = unknownSubmissions;
            manifest["cleanup"] = cleanupFailures.Count == 0 && unknownSubmissions.Count == 0 ? "completed" : "manual-review-required";
            await SaveManifestAsync();
            cleanupFailures.Should().BeEmpty("all run-owned fixtures must be removed; inspect manifest for any recovery work");
            unknownSubmissions.Should().BeEmpty("unconfirmed submissions require manual recovery and cannot be marked cleaned");
        }
    }

    private static readonly Guid SectionId = new("44444444-4444-4444-4444-444444444444");
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException(name + " is required");
    private static async Task<JsonElement> CallAsync(IMcpClient client, string name, Dictionary<string, object?> args, CancellationToken ct)
    {
        var response = await client.CallToolAsync(name, args, cancellationToken: ct);
        response.IsError.Should().BeFalse(string.Join("", response.Content.Select(c => c.Text)));
        return JsonDocument.Parse(response.Content.Single(c => c.Type == "text").Text!).RootElement.Clone();
    }
    private static async Task<JsonElement> GetDraftAsync(IMcpClient client, string table, Guid formId, CancellationToken ct)
        => (await CallAsync(client, "ppds_forms_get", new() { ["entityLogicalName"] = table, ["formId"] = formId, ["unpublished"] = true }, ct)).GetProperty("form");
    private static async Task RoundTripAsync(IMcpClient client, string table, Guid formId, string field, string? solution, bool managed,
        string url, string artifacts, CancellationToken ct)
    {
        var list = await CallAsync(client, "ppds_forms_list", new() { ["entityLogicalName"] = table }, ct);
        list.GetProperty("forms").EnumerateArray().Should().Contain(f => f.GetProperty("id").GetGuid() == formId);
        var draft = await GetDraftAsync(client, table, formId, ct);
        draft.GetProperty("isManaged").GetBoolean().Should().Be(managed);
        var beforeXml = draft.GetProperty("formXml").GetString()!;
        var args = new Dictionary<string, object?> { ["entityLogicalName"] = table, ["formId"] = formId,
            ["sectionId"] = SectionId, ["fieldLogicalName"] = field, ["expectedDraftHash"] = draft.GetProperty("draftHash").GetString(),
            ["solutionUniqueName"] = solution, ["confirmEnvironment"] = url, ["dryRun"] = true };
        (await CallAsync(client, "ppds_forms_add_field", args, ct)).GetProperty("status").GetString().Should().Be("planned");
        (await GetDraftAsync(client, table, formId, ct)).GetProperty("draftHash").GetString().Should().Be(draft.GetProperty("draftHash").GetString());
        args["dryRun"] = false;
        var added = await CallAsync(client, "ppds_forms_add_field", args, ct);
        added.GetProperty("status").GetString().Should().Be("saved-unpublished");
        var after = await GetDraftAsync(client, table, formId, ct);
        var afterXml = after.GetProperty("formXml").GetString()!;
        XNode.DeepEquals(XDocument.Parse(beforeXml).Descendants("parameters").Single(), XDocument.Parse(afterXml).Descendants("parameters").Single()).Should().BeTrue();
        var publishedBefore = (await CallAsync(client, "ppds_forms_get", new() { ["entityLogicalName"] = table, ["formId"] = formId }, ct)).GetProperty("form");
        publishedBefore.GetProperty("formXml").GetString().Should().NotContain("datafieldname=\"" + field + "\"");
        args["expectedDraftHash"] = after.GetProperty("draftHash").GetString();
        (await CallAsync(client, "ppds_forms_add_field", args, ct)).GetProperty("status").GetString().Should().Be("unchanged");
        var publication = await CallAsync(client, "ppds_forms_publish", new() { ["entityLogicalName"] = table, ["formId"] = formId,
            ["expectedDraftHash"] = after.GetProperty("draftHash").GetString(), ["confirmPublishEntity"] = table, ["confirmEnvironment"] = url }, ct);
        publication.GetProperty("status").GetString().Should().Be("published");
        await File.WriteAllTextAsync(Path.Combine(artifacts, managed ? "managed-roundtrip.json" : "unmanaged-roundtrip.json"),
            JsonSerializer.Serialize(new { list, draft, added, after, publication }), ct);
    }

    private sealed class ProtocolFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime;
        private readonly IMcpServer _server;
        private readonly Task _task;
        public IMcpClient Client { get; private set; } = null!;
        private ProtocolFixture(McpToolContext context, CancellationToken ct)
        {
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tools = new McpServerPrimitiveCollection<McpServerTool>();
            var instance = new FormsTools(context);
            foreach (var method in typeof(FormsTools).GetMethods().Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null))
                tools.Add(McpServerTool.Create(method, instance));
            var requests = new Pipe(); var responses = new Pipe();
            _server = McpServerFactory.Create(new StreamServerTransport(requests.Reader.AsStream(), responses.Writer.AsStream()),
                new() { ServerInfo = new() { Name = "forms-live-fixture", Version = "1" }, Capabilities = new() { Tools = new() { ToolCollection = tools } } });
            _task = _server.RunAsync(_lifetime.Token);
            _transport = new StreamClientTransport(requests.Writer.AsStream(), responses.Reader.AsStream());
        }
        private readonly StreamClientTransport _transport;
        public static async Task<ProtocolFixture> CreateAsync(McpToolContext context, CancellationToken ct)
        {
            var fixture = new ProtocolFixture(context, ct);
            fixture.Client = await McpClientFactory.CreateAsync(fixture._transport, cancellationToken: ct);
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync(); _lifetime.Cancel();
            try { await _task; } catch (OperationCanceledException) { }
            await _server.DisposeAsync(); _lifetime.Dispose();
        }
    }
}

public sealed class FormsFixtureOptInAttribute : FactAttribute
{
    public FormsFixtureOptInAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PPDS_FORMS_LIVE_CONFIRM")))
            Skip = "Requires explicit isolated-fixture approval and PPDS_FORMS_LIVE_* settings; see Fixtures/Forms/README.md.";
    }
}
