using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Services.Environment;
using PPDS.Dataverse.Pooling;
using PPDS.LiveTests.Infrastructure;
using PPDS.Mcp.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.LiveTests.Flows;

/// <summary>Separately approved, bounded missing-binding gate. Never invokes a flow.</summary>
[Trait("Category", "DestructiveE2E")]
[Collection("LiveDataverse")]
public sealed class FlowBindingPreflightLiveTests(ITestOutputHelper output)
{
    private const string Target = "https://orgcabef92d.crm.dynamics.com";
    private const string Run = "6d83b1f0";
    private const string Prefix = "pfl6d83";
    private const string Publisher = "PPDSFlowsBindingPublisher_" + Run;
    private const string Solution = "PPDSFlowsBinding_" + Run;
    private const string Reference = Prefix + "_" + Run + "_unbound";
    private const string ReferenceDisplay = "PPDS unbound reference " + Run;
    private const string Connector = "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps";
    private static readonly string[] Names = [$"PPDS flow {Run} control", $"PPDS flow {Run} blocker"];
    private static readonly string[] UniqueNames = [$"{Prefix}_{Run}_control", $"{Prefix}_{Run}_blocker"];

    [FlowBindingFixtureOptIn]
    public async Task CandidateCli_UnboundReference_BlocksWholeBatchWithoutStateWrites()
    {
        EnvironmentConfirmationService.NormalizeUrl(Required("PPDS_FLOW_BINDING_LIVE_CONFIRM")).Should().Be(Target);
        Required("PPDS_FLOW_BINDING_LIVE_RUN_ID").Should().Be(Run);
        var assembly = Required("PPDS_FLOW_BINDING_LIVE_CLI_ASSEMBLY");
        Path.IsPathFullyQualified(assembly).Should().BeTrue();
        Path.GetExtension(assembly).Should().Be(".dll");
        var candidateHash = Required("PPDS_FLOW_BINDING_LIVE_CLI_SHA256").ToUpperInvariant();
        (candidateHash.Length == 64 && candidateHash.All(Uri.IsHexDigit)).Should().BeTrue();
        async Task CheckBinaryAsync() => Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(assembly))).Should().Be(candidateHash);
        await CheckBinaryAsync();
        var root = Required("PPDS_FLOW_BINDING_LIVE_ARTIFACTS");
        Path.IsPathFullyQualified(root).Should().BeTrue();
        var directory = Path.Combine(root, "flow-binding-" + Run);
        Directory.Exists(directory).Should().BeFalse("evidence must not be overwritten");
        Directory.CreateDirectory(directory);
        var manifest = new Dictionary<string, object?> { ["runId"] = Run, ["environmentUrl"] = Target, ["profile"] = "PPDS",
            ["candidateAssembly"] = assembly, ["candidateAssemblySha256"] = candidateHash, ["publisher"] = Publisher,
            ["solution"] = Solution, ["reference"] = Reference, ["connectorId"] = Connector, ["flowNames"] = Names,
            ["flowUniqueNames"] = UniqueNames, ["state"] = "planned" };
        var unknown = new List<string>();
        var rejected = new List<object>();
        var cleanupFailures = new List<string>();
        var owned = new Dictionary<string, Guid>();
        var failure = new FixtureFailureEvidence();
        var processInterruption = new FixtureProcessInterruption();
        var verifiedNames = new HashSet<Guid>();
        var deleted = new List<object>();
        Guid Id(string key) => owned.GetValueOrDefault(key);
        async Task SaveAsync()
        {
            manifest["confirmedIds"] = owned; manifest["unknownSubmissions"] = unknown;
            manifest["rejectedSubmissions"] = rejected; manifest["primaryFailure"] = failure.Primary;
            manifest["cliProcessInterruption"] = processInterruption.Evidence;
            manifest["cleanupFailures"] = cleanupFailures; manifest["verifiedDeletions"] = deleted;
            await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = deadline.Token;
        using var store = new ProfileStore();
        using var credentials = new NativeCredentialStore();
        await using var pools = new McpConnectionPoolManager(profileStore: store);
        using var context = new McpToolContext(pools, store, credentials, new() { Profile = "PPDS", Environment = Target });
        await context.InitializeAsync(ct);
        await using var provider = await context.CreateServiceProviderAsync(ct);
        var guard = provider.GetRequiredService<IEnvironmentConfirmationService>();
        guard.Context.EnvironmentUrl.Should().Be(Target);
        await guard.EnsureConfirmedAsync("provision isolated unbound-reference fixture", Target, ct);
        var pool = provider.GetRequiredService<IDataverseConnectionPool>();
        async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request)
        {
            await using var client = await pool.GetClientAsync(cancellationToken: ct);
            return await client.ExecuteAsync(request, ct);
        }
        async Task<EntityCollection> QueryAsync(QueryExpression query)
        {
            query.PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 };
            await using var client = await pool.GetClientAsync(cancellationToken: ct);
            var result = await client.RetrieveMultipleAsync(query, ct);
            result.MoreRecords.Should().BeFalse("ownership verification requires a complete bounded inventory");
            return result;
        }
        async Task<Entity> ReadAsync(string table, Guid id, params string[] columns)
        {
            id.Should().NotBeEmpty("only confirmed returned IDs may be read for cleanup");
            await using var client = await pool.GetClientAsync(cancellationToken: ct);
            return await client.RetrieveAsync(table, id, new ColumnSet(columns), ct);
        }
        async Task AbsentAsync(string table, string attribute, params object[] values)
        {
            var query = new QueryExpression(table) { ColumnSet = new ColumnSet(false) };
            query.Criteria.AddCondition(attribute, ConditionOperator.In, values);
            (await QueryAsync(query)).Entities.Should().BeEmpty("fixture collisions and deleted IDs must be absent");
        }
        async Task CreateAsync(string key, Entity entity, bool inSolution = false)
        {
            var pending = "Unconfirmed Create " + key;
            unknown.Add(pending); await SaveAsync();
            Guid id;
            try
            {
                var request = new CreateRequest { Target = entity };
                if (inSolution) request["SolutionUniqueName"] = Solution;
                id = ((CreateResponse)await ExecuteAsync(request)).id;
            }
            catch (Exception exception)
            {
                var detail = FixtureFailureEvidence.Describe(exception);
                if (detail.SubmissionOutcome == "server-rejected") { unknown.Remove(pending); rejected.Add(new { key, failure = detail }); }
                throw;
            }
            id.Should().NotBeEmpty();
            owned.Add(key, id); unknown.Remove(pending); await SaveAsync();
        }
        async Task VerifyPublisherAsync(params Guid[] expectedSolutions)
        {
            var row = await ReadAsync("publisher", Id("publisher"), "uniquename", "customizationprefix");
            row.GetAttributeValue<string>("uniquename").Should().Be(Publisher);
            row.GetAttributeValue<string>("customizationprefix").Should().Be(Prefix);
            var query = new QueryExpression("solution") { ColumnSet = new("solutionid") };
            query.Criteria.AddCondition("publisherid", ConditionOperator.Equal, Id("publisher"));
            (await QueryAsync(query)).Entities.Select(e => e.Id).Should().BeEquivalentTo(expectedSolutions);
        }
        async Task VerifySolutionAsync()
        {
            var row = await ReadAsync("solution", Id("solution"), "uniquename", "version", "ismanaged", "publisherid");
            row.GetAttributeValue<string>("uniquename").Should().Be(Solution);
            row.GetAttributeValue<string>("version").Should().Be("1.0.0.0");
            row.GetAttributeValue<bool>("ismanaged").Should().BeFalse();
            row.GetAttributeValue<EntityReference>("publisherid").Id.Should().Be(Id("publisher"));
        }
        async Task VerifyInventoryAsync(bool complete)
        {
            await VerifySolutionAsync();
            var query = new QueryExpression("solutioncomponent") { ColumnSet = new("objectid", "componenttype") };
            query.Criteria.AddCondition("solutionid", ConditionOperator.Equal, Id("solution"));
            var rows = (await QueryAsync(query)).Entities;
            var expected = owned.Where(p => p.Key is "control" or "blocker" or "reference").Select(p => p.Value).ToArray();
            rows.Select(r => r.GetAttributeValue<Guid>("objectid")).Should().OnlyContain(id => expected.Contains(id));
            if (complete) rows.Select(r => r.GetAttributeValue<Guid>("objectid")).Should().BeEquivalentTo(expected);
            foreach (var row in rows.Where(r => r.GetAttributeValue<Guid>("objectid") != Id("reference")))
                row.GetAttributeValue<OptionSetValue>("componenttype").Value.Should().Be(29);
        }
        async Task VerifyReferenceAsync()
        {
            var row = await ReadAsync("connectionreference", Id("reference"), "connectionreferencelogicalname", "connectionreferencedisplayname",
                "connectorid", "connectionid", "statecode", "ismanaged");
            row.GetAttributeValue<string>("connectionreferencelogicalname").Should().Be(Reference);
            row.GetAttributeValue<string>("connectionreferencedisplayname").Should().Be(ReferenceDisplay);
            row.GetAttributeValue<string>("connectorid").Should().Be(Connector);
            row.GetAttributeValue<string>("connectionid").Should().BeNullOrEmpty();
            row.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0);
            row.GetAttributeValue<bool>("ismanaged").Should().BeFalse();
        }
        async Task<Entity> ReadFlowAsync(string key)
        {
            var index = key == "control" ? 0 : 1;
            var row = await ReadAsync("workflow", Id(key), "name", "uniquename", "category", "type", "clientdata", "statecode", "statuscode", "ismanaged", "versionnumber");
            row.GetAttributeValue<string>("name").Should().Be(Names[index]);
            if (verifiedNames.Contains(row.Id)) row.GetAttributeValue<string>("uniquename").Should().Be(UniqueNames[index]);
            row.GetAttributeValue<OptionSetValue>("category").Value.Should().Be(5);
            row.GetAttributeValue<OptionSetValue>("type").Value.Should().Be(1);
            row.GetAttributeValue<bool>("ismanaged").Should().BeFalse();
            VerifyHarmless(row.GetAttributeValue<string>("clientdata"), key == "blocker");
            return row;
        }
        async Task<Dictionary<Guid, long>> SnapshotAsync()
        {
            await VerifyPublisherAsync(Id("solution")); await VerifyInventoryAsync(complete: true); await VerifyReferenceAsync();
            var result = new Dictionary<Guid, long>();
            foreach (var key in new[] { "control", "blocker" })
            {
                var row = await ReadFlowAsync(key);
                row.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0);
                row.GetAttributeValue<long?>("versionnumber").Should().NotBeNull("version readback detects unexpected candidate writes");
                result[row.Id] = row.GetAttributeValue<long>("versionnumber");
            }
            return result;
        }
        async Task<JsonElement> CliAsync(bool dryRun, string? hash = null)
        {
            await CheckBinaryAsync();
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in new[] { assembly, "flows", "enable", "--all", "--solution", Solution, "--profile", "PPDS", "--environment", Target,
                "--confirm-env", Target, "--output-format", "Json", "--timeout", "30", "--max-flows", "2" }) start.ArgumentList.Add(arg);
            if (dryRun) start.ArgumentList.Add("--dry-run");
            if (hash is not null) { start.ArgumentList.Add("--expected-plan-hash"); start.ArgumentList.Add(hash); }
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start pinned candidate");
            return await processInterruption.ObserveAsync(process, async () =>
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct); bounded.CancelAfter(TimeSpan.FromMinutes(2));
                var stdout = process.StandardOutput.ReadToEndAsync(bounded.Token);
                var stderr = process.StandardError.ReadToEndAsync(bounded.Token);
                await process.WaitForExitAsync(bounded.Token);
                var outText = await stdout; var errText = await stderr;
                var label = dryRun ? "01-plan" : "02-execute";
                await File.WriteAllTextAsync(Path.Combine(directory, label + ".json"), outText, ct);
                await File.WriteAllTextAsync(Path.Combine(directory, label + ".stderr.txt"), errText, ct);
                process.ExitCode.Should().NotBe(0); errText.Should().NotBeNullOrWhiteSpace();
                var json = JsonDocument.Parse(outText).RootElement.Clone();
                VerifyBlockedResult(json, dryRun, Id("control"), Id("blocker"));
                return json.GetProperty("data");
            });
        }
        async Task DeleteAsync(string key, string table, string idAttribute)
        {
            var id = Id(key);
            await guard.EnsureConfirmedAsync("cleanup isolated unbound-reference fixture", Target, ct);
            manifest["cleanupSubmission"] = new { key, table, id, status = "submitting" }; await SaveAsync();
            await using (var client = await pool.GetClientAsync(cancellationToken: ct)) await client.DeleteAsync(table, id, ct);
            await AbsentAsync(table, idAttribute, id);
            owned.Remove(key); deleted.Add(new { key, table, id, status = "verified-deleted" });
            manifest["cleanupSubmission"] = null; await SaveAsync();
        }
        await SaveAsync();
        await AbsentAsync("publisher", "uniquename", Publisher);
        await AbsentAsync("solution", "uniquename", Solution);
        await AbsentAsync("connectionreference", "connectionreferencelogicalname", Reference);
        await AbsentAsync("connectionreference", "connectionreferencedisplayname", ReferenceDisplay);
        await AbsentAsync("workflow", "name", Names.Cast<object>().ToArray());
        await AbsentAsync("workflow", "uniquename", UniqueNames.Cast<object>().ToArray());
        foreach (var (table, column) in new[] { ("workflow", "uniquename"), ("connectionreference", "connectionreferencelogicalname"),
            ("connectionreference", "connectionreferencedisplayname"), ("connectionreference", "connectorid") })
        {
            var metadata = (RetrieveAttributeResponse)await ExecuteAsync(new RetrieveAttributeRequest { EntityLogicalName = table, LogicalName = column, RetrieveAsIfPublished = true });
            metadata.AttributeMetadata.IsValidForCreate.Should().BeTrue("fail before provisioning if required fixture attributes cannot be created");
        }
        var template = await File.ReadAllTextAsync(Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "Flows", "manual-compose.json"), ct);
        VerifyHarmless(template, false);
        try
        {
            await CreateAsync("publisher", new("publisher") { ["uniquename"] = Publisher, ["friendlyname"] = Publisher,
                ["customizationprefix"] = Prefix, ["customizationoptionvalueprefix"] = 78123 });
            await VerifyPublisherAsync();
            await CreateAsync("solution", new("solution") { ["uniquename"] = Solution, ["friendlyname"] = Solution,
                ["version"] = "1.0.0.0", ["publisherid"] = new EntityReference("publisher", Id("publisher")) });
            await VerifyPublisherAsync(Id("solution")); await VerifyInventoryAsync(complete: true);
            await CreateAsync("reference", new("connectionreference") { ["connectionreferencelogicalname"] = Reference,
                ["connectionreferencedisplayname"] = ReferenceDisplay, ["connectorid"] = Connector }, inSolution: true);
            await VerifyReferenceAsync(); await VerifyInventoryAsync(complete: true);
            foreach (var key in new[] { "control", "blocker" })
            {
                var index = key == "control" ? 0 : 1;
                var data = JsonNode.Parse(template)!;
                if (index == 1) data["properties"]!["connectionReferences"]!["fixture_unbound"] = new JsonObject
                { ["connection"] = new JsonObject { ["connectionReferenceLogicalName"] = Reference },
                    ["api"] = new JsonObject { ["name"] = "shared_commondataserviceforapps" }, ["runtimeSource"] = "embedded" };
                VerifyHarmless(data.ToJsonString(), index == 1);
                await CreateAsync(key, new("workflow") { ["name"] = Names[index], ["uniquename"] = UniqueNames[index],
                    ["category"] = new OptionSetValue(5), ["type"] = new OptionSetValue(1), ["primaryentity"] = "none", ["clientdata"] = data.ToJsonString() });
                (await ReadFlowAsync(key)).GetAttributeValue<string>("uniquename").Should().Be(UniqueNames[index]);
                verifiedNames.Add(Id(key));
                await ExecuteAsync(new AddSolutionComponentRequest { ComponentId = Id(key), ComponentType = 29, SolutionUniqueName = Solution, AddRequiredComponents = false });
                await VerifyInventoryAsync(complete: true);
            }
            var before = await SnapshotAsync(); manifest["versionsBefore"] = before; await SaveAsync();
            var plan = await CliAsync(true); (await SnapshotAsync()).Should().BeEquivalentTo(before);
            var planHash = plan.GetProperty("planHash").GetString()!;
            planHash.Length.Should().Be(64); planHash.All(Uri.IsHexDigit).Should().BeTrue();
            var result = await CliAsync(false, planHash); result.GetProperty("planHash").GetString().Should().Be(planHash);
            var after = await SnapshotAsync(); after.Should().BeEquivalentTo(before);
            manifest["versionsAfter"] = after; manifest["state"] = "verified"; await SaveAsync();
        }
        catch (Exception exception)
        {
            failure.CapturePrimary(exception); manifest["state"] = "failed";
            if (processInterruption.RequiresManualReview)
                unknown.Add("Dispatched CLI outcome is unknown; process exit does not prove remote rollback. All fixture cleanup writes are suppressed.");
            output.WriteLine("Primary fixture failure: " + JsonSerializer.Serialize(failure.Primary));
            try { await SaveAsync(); } catch (Exception save) { cleanupFailures.Add("Pre-cleanup ledger: " + save.Message); }
        }
        finally
        {
            try
            {
                await processInterruption.RunCleanupIfSafeAsync(async () =>
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(5)); ct = cleanup.Token;
                    foreach (var key in new[] { "blocker", "control" }.Where(key => Id(key) != Guid.Empty))
                    {
                        var row = await ReadFlowAsync(key); await VerifyInventoryAsync(complete: false);
                        if (row.GetAttributeValue<OptionSetValue>("statecode").Value != 0)
                        {
                            await guard.EnsureConfirmedAsync("disable isolated fixture during cleanup", Target, ct);
                            await ExecuteAsync(new SetStateRequest { EntityMoniker = new("workflow", row.Id), State = new(0), Status = new(1) });
                        }
                        await DeleteAsync(key, "workflow", "workflowid");
                    }
                    if (unknown.Count == 0 && cleanupFailures.Count == 0)
                    {
                        if (Id("reference") != Guid.Empty)
                        { await VerifyReferenceAsync(); await VerifyInventoryAsync(complete: false); await DeleteAsync("reference", "connectionreference", "connectionreferenceid"); }
                        if (Id("solution") != Guid.Empty)
                        { await VerifyInventoryAsync(complete: true); await VerifyPublisherAsync(Id("solution")); await DeleteAsync("solution", "solution", "solutionid"); }
                        if (Id("publisher") != Guid.Empty)
                        { await VerifyPublisherAsync(); await DeleteAsync("publisher", "publisher", "publisherid"); }
                    }
                });
            }
            catch (Exception exception) { cleanupFailures.Add(exception.GetType().Name + ": " + exception.Message); }
            manifest["cleanup"] = processInterruption.RequiresManualReview
                ? "manual-review-required; interrupted CLI outcome unknown; all Dataverse cleanup writes suppressed"
                : owned.Count == 0 && unknown.Count == 0 && cleanupFailures.Count == 0 ? "completed" : "manual-review-required; retained confirmed IDs and ownership evidence";
            try { await SaveAsync(); } catch (Exception save) { cleanupFailures.Add("Final ledger: " + save.Message); }
            output.WriteLine("Fixture cleanup: " + manifest["cleanup"] + "; " + JsonSerializer.Serialize(cleanupFailures));
        }
        failure.ThrowIfFailed(cleanupFailures, unknown);
        owned.Should().BeEmpty("all confirmed resources must have verified cleanup before the gate passes");
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException(name + " is required");
    private static void VerifyHarmless(string json, bool blocker)
    {
        var properties = JsonNode.Parse(json)!["properties"]!;
        var triggers = properties["definition"]!["triggers"]!.AsObject(); triggers.Count.Should().Be(1);
        triggers.Single().Value!["type"]!.GetValue<string>().Should().Be("Request");
        triggers.Single().Value!["kind"]!.GetValue<string>().Should().Be("Button");
        var actions = properties["definition"]!["actions"]!.AsObject(); actions.Count.Should().Be(1);
        actions.Single().Value!["type"]!.GetValue<string>().Should().Be("Compose");
        actions.Single().Value!["inputs"]!.GetValue<string>().Should().Be("PPDS isolated activation fixture");
        var refs = properties["connectionReferences"]!.AsObject();
        refs.Count.Should().Be(blocker ? 1 : 0);
        if (blocker) refs.Single().Value!["connection"]!["connectionReferenceLogicalName"]!.GetValue<string>().Should().Be(Reference);
    }
    private static void VerifyBlockedResult(JsonElement json, bool dryRun, Guid controlId, Guid blockerId)
    {
        json.GetProperty("success").GetBoolean().Should().BeFalse();
        var data = json.GetProperty("data"); data.GetProperty("environmentUrl").GetString().Should().Be(Target);
        data.GetProperty("solution").GetString().Should().Be(Solution); data.GetProperty("dryRun").GetBoolean().Should().Be(dryRun);
        data.GetProperty("errorCode").GetString().Should().Be("Flow.PreflightBlocked");
        data.GetProperty("excluded").GetArrayLength().Should().Be(0);
        var flows = data.GetProperty("flows").EnumerateArray().ToArray();
        flows.Select(f => f.GetProperty("id").GetGuid()).Should().BeEquivalentTo(new[] { controlId, blockerId });
        var control = flows.Single(f => f.GetProperty("id").GetGuid() == controlId);
        control.GetProperty("status").GetString().Should().Be(dryRun ? "would-change" : "not-attempted");
        control.GetProperty("references").GetArrayLength().Should().Be(0);
        var blocker = flows.Single(f => f.GetProperty("id").GetGuid() == blockerId);
        blocker.GetProperty("status").GetString().Should().Be("blocked");
        blocker.GetProperty("errorCode").GetString().Should().Be("Flow.ConnectionReferenceBlocked");
        var references = blocker.GetProperty("references").EnumerateArray().ToArray(); references.Should().ContainSingle();
        references[0].GetProperty("logicalName").GetString().Should().Be(Reference);
        references[0].GetProperty("status").GetString().Should().Be("unbound");
        references[0].GetProperty("connectorId").GetString().Should().Be(Connector);
        if (references[0].TryGetProperty("connectionId", out var connection)) connection.ValueKind.Should().Be(JsonValueKind.Null);
        flows.Should().OnlyContain(f => f.GetProperty("phase").GetString() == "preflight");
    }
}

public sealed class FlowBindingFixtureOptInAttribute : FactAttribute
{
    public FlowBindingFixtureOptInAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PPDS_FLOW_BINDING_LIVE_CONFIRM")))
            Skip = "Requires separate approval of the unbound-reference inventory; see Fixtures/Flows/binding-amendment.md.";
    }
}
