using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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
using PPDS.Mcp.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.LiveTests.Flows;

[Trait("Category", "DestructiveE2E")]
[Collection("LiveDataverse")]
public sealed class FlowActivationRoundTripLiveTests(ITestOutputHelper output)
{
    [FlowFixtureOptIn]
    public async Task CandidateCli_ManagedAndUnmanagedActivation_IsVerifiedIsolatedAndIdempotent()
    {
        var url = EnvironmentConfirmationService.NormalizeUrl(Required("PPDS_FLOWS_LIVE_URL"));
        EnvironmentConfirmationService.NormalizeUrl(Required("PPDS_FLOWS_LIVE_CONFIRM")).Should().Be(url);
        var run = Required("PPDS_FLOWS_LIVE_RUN_ID");
        Regex.IsMatch(run, "^[a-f0-9]{8}$").Should().BeTrue();
        var profile = Required("PPDS_FLOWS_LIVE_PROFILE");
        var assembly = Required("PPDS_FLOWS_LIVE_CLI_ASSEMBLY");
        (Path.IsPathFullyQualified(assembly) && File.Exists(assembly) && Path.GetExtension(assembly) == ".dll").Should().BeTrue();
        var candidateHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(assembly)));
        var artifactRoot = Required("PPDS_FLOWS_LIVE_ARTIFACTS");
        Path.IsPathFullyQualified(artifactRoot).Should().BeTrue();
        var directory = Path.Combine(artifactRoot, "flows-" + run);
        Directory.Exists(directory).Should().BeFalse("the reviewed run ID must not be reused silently");
        Directory.CreateDirectory(directory);
        var prefix = "pfl" + run[..4];
        var publisherName = "PPDSFlowsPublisher_" + run;
        var sourceName = "PPDSFlowsSource_" + run;
        var outsideName = "PPDSFlowsOutside_" + run;
        var names = new[] { $"PPDS flow {run} A", $"PPDS flow {run} B", $"PPDS flow {run} outside", $"PPDS flow {run} blocker" };
        var uniqueNames = new[] { "a", "b", "outside", "blocker" }.Select(suffix => $"{prefix}_{run}_{suffix}").ToArray();
        var manifest = new Dictionary<string, object?> { ["runId"] = run, ["environmentUrl"] = url, ["candidateAssembly"] = assembly,
            ["candidateAssemblySha256"] = candidateHash,
            ["publisher"] = publisherName, ["sourceSolution"] = sourceName, ["outsideSolution"] = outsideName,
            ["flowNames"] = names, ["flowUniqueNames"] = uniqueNames, ["state"] = "planned" };
        var unknown = new List<string>();
        var cleanupFailures = new List<string>();
        var owned = new Dictionary<Guid, OwnedFlow>();
        var knownSourceNames = new HashSet<string>(StringComparer.Ordinal);
        Guid publisherId = Guid.Empty, sourceId = Guid.Empty, outsideId = Guid.Empty;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var ct = timeout.Token;
        using var store = new ProfileStore();
        using var credentials = new NativeCredentialStore();
        await using var pools = new McpConnectionPoolManager(profileStore: store);
        using var context = new McpToolContext(pools, store, credentials, new() { Profile = profile, Environment = url });
        await context.InitializeAsync(ct);
        await using var provider = await context.CreateServiceProviderAsync(ct);
        await provider.GetRequiredService<IEnvironmentConfirmationService>().EnsureConfirmedAsync("provision isolated flow activation fixtures", url, ct);
        var pool = provider.GetRequiredService<IDataverseConnectionPool>();
        async Task SaveAsync()
        {
            manifest["confirmedFlows"] = owned.Values.ToArray(); manifest["unknownSubmissions"] = unknown.ToArray();
            await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }
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
            catch { unknown.Add($"Unconfirmed create {entity.LogicalName}: {entity.GetAttributeValue<string>("name") ?? entity.GetAttributeValue<string>("uniquename")}"); throw; }
        }
        async Task DeleteAsync(string table, Guid id)
        {
            await using var client = await pool.GetClientAsync(cancellationToken: ct);
            await client.DeleteAsync(table, id, ct);
        }
        async Task<Entity?> FindSolutionAsync(string name)
        {
            var query = new QueryExpression("solution") { ColumnSet = new("solutionid", "publisherid", "ismanaged", "version"), TopCount = 2 };
            query.Criteria.AddCondition("uniquename", ConditionOperator.Equal, name);
            var rows = await QueryAsync(query); rows.Entities.Count.Should().BeLessThanOrEqualTo(1);
            return rows.Entities.SingleOrDefault();
        }
        async Task VerifySolutionAsync(Guid id, string name)
        {
            var row = await FindSolutionAsync(name); row.Should().NotBeNull(); row!.Id.Should().Be(id);
            row.GetAttributeValue<EntityReference>("publisherid").Id.Should().Be(publisherId);
            row.GetAttributeValue<string>("version").Should().Be("1.0.0.0");
        }
        async Task<Entity> ReadFlowAsync(OwnedFlow item)
        {
            await using var client = await pool.GetClientAsync(cancellationToken: ct);
            var row = await client.RetrieveAsync("workflow", item.Id, new ColumnSet("name", "uniquename", "category", "type", "clientdata", "statecode", "ismanaged"), ct);
            row.GetAttributeValue<string>("name").Should().Be(item.Name);
            if (item.UniqueName.Length > 0) row.GetAttributeValue<string>("uniquename").Should().Be(item.UniqueName);
            row.GetAttributeValue<OptionSetValue>("category").Value.Should().Be(5);
            row.GetAttributeValue<OptionSetValue>("type").Value.Should().Be(1);
            VerifyHarmless(row.GetAttributeValue<string>("clientdata"), item.Blocker ? prefix + "_missing" : null);
            return row;
        }
        async Task VerifyMembershipAsync(OwnedFlow item)
        {
            var solutionId = item.InSource ? sourceId : outsideId;
            await VerifySolutionAsync(solutionId, item.InSource ? sourceName : outsideName);
            var query = new QueryExpression("solutioncomponent") { ColumnSet = new("solutioncomponentid"), TopCount = 1 };
            query.Criteria.AddCondition("solutionid", ConditionOperator.Equal, solutionId);
            query.Criteria.AddCondition("componenttype", ConditionOperator.Equal, 29);
            query.Criteria.AddCondition("objectid", ConditionOperator.Equal, item.Id);
            (await QueryAsync(query)).Entities.Should().ContainSingle();
        }
        async Task DeleteOwnedFlowAsync(OwnedFlow item)
        {
            var row = await ReadFlowAsync(item);
            row.GetAttributeValue<bool>("ismanaged").Should().BeFalse();
            if (item.Associated) await VerifyMembershipAsync(item);
            if (row.GetAttributeValue<OptionSetValue>("statecode").Value != 0)
                await ExecuteAsync(new SetStateRequest { EntityMoniker = new("workflow", item.Id), State = new(0), Status = new(1) });
            await DeleteAsync("workflow", item.Id); owned.Remove(item.Id);
        }
        var template = await File.ReadAllTextAsync(Path.Combine(System.AppContext.BaseDirectory, "Fixtures", "Flows", "manual-compose.json"), ct);
        VerifyHarmless(template, null);
        async Task<OwnedFlow> CreateFlowAsync(string name, bool inSource, bool blocker = false)
        {
            var uniqueName = uniqueNames[Array.IndexOf(names, name)];
            var data = JsonNode.Parse(template)!;
            if (blocker) data["properties"]!["connectionReferences"]!["fixture_missing"] = new JsonObject
            { ["connection"] = new JsonObject { ["connectionReferenceLogicalName"] = prefix + "_missing" },
                ["api"] = new JsonObject { ["name"] = "shared_commondataserviceforapps" }, ["runtimeSource"] = "embedded" };
            var json = data.ToJsonString(); VerifyHarmless(json, blocker ? prefix + "_missing" : null);
            var id = await CreateAsync(new("workflow") { ["name"] = name, ["uniquename"] = uniqueName, ["category"] = new OptionSetValue(5),
                ["type"] = new OptionSetValue(1), ["primaryentity"] = "none", ["clientdata"] = json });
            var item = new OwnedFlow(id, name, "", inSource, false, blocker, false);
            owned[id] = item; await SaveAsync(); // a returned ID is recorded before subsequent reads/association
            var row = await ReadFlowAsync(item);
            // UniqueName is optional: Create does not promise to generate it. Keep the confirmed
            // ID with an empty cleanup selector until the explicitly assigned name is verified.
            row.GetAttributeValue<string>("uniquename").Should().Be(uniqueName);
            item = item with { UniqueName = uniqueName }; owned[id] = item; await SaveAsync();
            await ExecuteAsync(new AddSolutionComponentRequest { ComponentType = 29, ComponentId = id,
                SolutionUniqueName = inSource ? sourceName : outsideName, AddRequiredComponents = false });
            item = item with { Associated = true }; owned[id] = item; await SaveAsync();
            await ReadFlowAsync(item); // association must preserve the assigned stable unique name
            await VerifyMembershipAsync(item);
            if (inSource) knownSourceNames.Add(item.UniqueName);
            return item;
        }
        var sequence = 0;
        async Task<(int Exit, JsonElement Json)> CliAsync(string verb, IEnumerable<string> selection, bool dryRun, string? hash = null)
        {
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(assembly, ct))).Should().Be(candidateHash,
                "the reviewed candidate must not change during the live run");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var arg in new[] { assembly, "flows", verb }.Concat(selection).Concat(new[] { "--profile", profile, "--environment", url,
                "--confirm-env", url, "--output-format", "Json", "--timeout", "60", "--max-flows", "10" })) start.ArgumentList.Add(arg);
            if (dryRun) start.ArgumentList.Add("--dry-run");
            if (hash is not null) { start.ArgumentList.Add("--expected-plan-hash"); start.ArgumentList.Add(hash); }
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start candidate CLI");
            var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
            using var commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct); commandTimeout.CancelAfter(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(commandTimeout.Token); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            var outText = await stdout; var errText = await stderr;
            var label = (++sequence).ToString("D2") + "-" + verb + (dryRun ? "-plan" : "-execute");
            await File.WriteAllTextAsync(Path.Combine(directory, label + ".json"), outText, ct);
            await File.WriteAllTextAsync(Path.Combine(directory, label + ".stderr.txt"), errText, ct);
            var result = JsonDocument.Parse(outText).RootElement.Clone();
            result.GetProperty("data").GetProperty("environmentUrl").GetString().Should().Be(url);
            errText.Should().NotBeNullOrWhiteSpace("long-running phases belong on stderr");
            return (process.ExitCode, result);
        }
        void VerifySelection(JsonElement result, IReadOnlyCollection<OwnedFlow> expected)
        {
            var data = result.GetProperty("data");
            data.GetProperty("flows").EnumerateArray().Select(f => f.GetProperty("id").GetGuid())
                .Should().BeEquivalentTo(expected.Select(f => f.Id), "a plan must contain exactly the confirmed fixture IDs before execution");
            data.GetProperty("excluded").GetArrayLength().Should().Be(0, "an isolated fixture solution must not contain unexpected workflow entries");
        }
        async Task ChangeAsync(string verb, string[] selection, IReadOnlyCollection<OwnedFlow> expected, bool expectUnchanged = false)
        {
            var plan = await CliAsync(verb, selection, true); plan.Exit.Should().Be(0); plan.Json.GetProperty("success").GetBoolean().Should().BeTrue();
            VerifySelection(plan.Json, expected);
            foreach (var item in expected) { await ReadFlowAsync(item); await VerifyMembershipAsync(item); }
            var result = await CliAsync(verb, selection, false, plan.Json.GetProperty("data").GetProperty("planHash").GetString());
            VerifySelection(result.Json, expected);
            result.Exit.Should().Be(0); result.Json.GetProperty("success").GetBoolean().Should().BeTrue();
            result.Json.GetProperty("data").GetProperty("flows").EnumerateArray().Should()
                .OnlyContain(f => f.GetProperty("status").GetString() == (expectUnchanged ? "unchanged" : "succeeded"));
        }
        async Task VerifyStateAsync(OwnedFlow item, int state) => (await ReadFlowAsync(item)).GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(state);
        async Task ExerciseSourceAsync(OwnedFlow outside)
        {
            var sourceFlows = owned.Values.Where(f => f.InSource).ToArray(); sourceFlows.Should().HaveCount(2);
            foreach (var item in sourceFlows) { await ReadFlowAsync(item); await VerifyMembershipAsync(item); await VerifyStateAsync(item, 0); }
            var first = sourceFlows[0];
            await ChangeAsync("enable", ["--id", first.Id.ToString()], [first]); await VerifyStateAsync(first, 1);
            await ChangeAsync("enable", ["--name", first.UniqueName], [first], expectUnchanged: true);
            await ChangeAsync("disable", ["--id", first.Id.ToString()], [first]); await VerifyStateAsync(first, 0);
            await ChangeAsync("enable", ["--all", "--solution", sourceName], sourceFlows);
            foreach (var item in sourceFlows) await VerifyStateAsync(item, 1);
            await VerifyStateAsync(outside, 0);
            await ChangeAsync("disable", ["--all", "--solution", sourceName], sourceFlows);
            foreach (var item in sourceFlows) await VerifyStateAsync(item, 0);
            await VerifyStateAsync(outside, 0);
        }
        await SaveAsync();
        (await FindSolutionAsync(sourceName)).Should().BeNull(); (await FindSolutionAsync(outsideName)).Should().BeNull();
        var publishers = new QueryExpression("publisher") { ColumnSet = new("publisherid"), TopCount = 1 };
        publishers.Criteria.AddCondition("uniquename", ConditionOperator.Equal, publisherName);
        (await QueryAsync(publishers)).Entities.Should().BeEmpty();
        var collisions = new QueryExpression("workflow") { ColumnSet = new("workflowid"), TopCount = 1,
            Criteria = new FilterExpression(LogicalOperator.Or) };
        collisions.Criteria.AddCondition("name", ConditionOperator.In, names.Cast<object>().ToArray());
        collisions.Criteria.AddCondition("uniquename", ConditionOperator.In, uniqueNames.Cast<object>().ToArray());
        (await QueryAsync(collisions)).Entities.Should().BeEmpty();
        var uniqueNameMetadata = (RetrieveAttributeResponse)await ExecuteAsync(new RetrieveAttributeRequest
            { EntityLogicalName = "workflow", LogicalName = "uniquename", RetrieveAsIfPublished = true });
        uniqueNameMetadata.AttributeMetadata.IsValidForCreate.Should().BeTrue(
            "this target must support assigning the fixture's stable unique name during Create; fail before provisioning otherwise");
        var existingReference = new QueryExpression("connectionreference") { ColumnSet = new("connectionreferenceid"), TopCount = 1 };
        existingReference.Criteria.AddCondition("connectionreferencelogicalname", ConditionOperator.Equal, prefix + "_missing");
        (await QueryAsync(existingReference)).Entities.Should().BeEmpty("the blocker must never refer to an existing connection");
        try
        {
            publisherId = await CreateAsync(new("publisher") { ["uniquename"] = publisherName, ["friendlyname"] = publisherName,
                ["customizationprefix"] = prefix, ["customizationoptionvalueprefix"] = 70000 + Convert.ToInt32(run[..4], 16) % 20000 });
            manifest["publisherId"] = publisherId; await SaveAsync();
            async Task<Guid> SolutionAsync(string name) => await CreateAsync(new("solution") { ["uniquename"] = name, ["friendlyname"] = name,
                ["version"] = "1.0.0.0", ["publisherid"] = new EntityReference("publisher", publisherId) });
            sourceId = await SolutionAsync(sourceName); manifest["sourceSolutionId"] = sourceId; await SaveAsync();
            outsideId = await SolutionAsync(outsideName); manifest["outsideSolutionId"] = outsideId; await SaveAsync();
            await CreateFlowAsync(names[0], true); await CreateFlowAsync(names[1], true);
            var outside = await CreateFlowAsync(names[2], false);
            await ExerciseSourceAsync(outside);
            var sourceIds = owned.Values.Where(f => f.InSource).Select(f => f.Id).ToArray();
            var inventoryQuery = new QueryExpression("solutioncomponent") { ColumnSet = new("componenttype", "objectid") };
            inventoryQuery.Criteria.AddCondition("solutionid", ConditionOperator.Equal, sourceId);
            var inventory = await QueryAsync(inventoryQuery);
            inventory.MoreRecords.Should().BeFalse("the complete fixture inventory must be bounded to two flows");
            inventory.Entities.Should().HaveCount(2).And.OnlyContain(c => c.GetAttributeValue<OptionSetValue>("componenttype").Value == 29);
            inventory.Entities.Select(c => c.GetAttributeValue<Guid>("objectid")).Should().BeEquivalentTo(sourceIds);
            foreach (var item in owned.Values.Where(f => f.InSource)) await ReadFlowAsync(item);
            var exported = (ExportSolutionResponse)await ExecuteAsync(new ExportSolutionRequest { SolutionName = sourceName, Managed = true });
            using (var zip = new ZipArchive(new MemoryStream(exported.ExportSolutionFile), ZipArchiveMode.Read))
            {
                var solutionXml = zip.GetEntry("solution.xml") ?? throw new InvalidOperationException("Managed package has no solution.xml");
                using var stream = solutionXml.Open();
                var roots = XDocument.Load(stream).Descendants().Where(e => e.Name.LocalName == "RootComponent").ToArray();
                roots.Should().HaveCount(2).And.OnlyContain(e => (string?)e.Attribute("type") == "29");
                roots.Select(e => Guid.Parse((string?)e.Attribute("id") ?? throw new InvalidOperationException("Root component has no stable ID")))
                    .Should().BeEquivalentTo(sourceIds, "the exported package must not adopt extra components");
            }
            await File.WriteAllBytesAsync(Path.Combine(directory, "managed-fixture.zip"), exported.ExportSolutionFile, ct);
            manifest["managedPackageSha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(exported.ExportSolutionFile));
            await SaveAsync();
            foreach (var item in owned.Values.Where(f => f.InSource).ToArray()) await DeleteOwnedFlowAsync(item);
            await VerifySolutionAsync(sourceId, sourceName); await DeleteAsync("solution", sourceId); sourceId = Guid.Empty;
            var job = Guid.NewGuid(); manifest["importJobId"] = job;
            var unresolvedImport = "Managed source import " + job + " lacks a confirmed validated solution/member inventory";
            unknown.Add(unresolvedImport); await SaveAsync();
            await ExecuteAsync(new ImportSolutionRequest { CustomizationFile = exported.ExportSolutionFile, ImportJobId = job,
                OverwriteUnmanagedCustomizations = false, PublishWorkflows = false });
            var imported = await FindSolutionAsync(sourceName); imported.Should().NotBeNull();
            imported!.GetAttributeValue<bool>("ismanaged").Should().BeTrue();
            imported.GetAttributeValue<EntityReference>("publisherid").Id.Should().Be(publisherId);
            imported.GetAttributeValue<string>("version").Should().Be("1.0.0.0");
            sourceId = imported.Id; manifest["managedSolutionId"] = sourceId;
            var members = new QueryExpression("solutioncomponent") { ColumnSet = new("objectid") };
            members.Criteria.AddCondition("solutionid", ConditionOperator.Equal, sourceId); members.Criteria.AddCondition("componenttype", ConditionOperator.Equal, 29);
            var importedMembers = await QueryAsync(members); importedMembers.Entities.Should().HaveCount(2);
            importedMembers.MoreRecords.Should().BeFalse();
            importedMembers.Entities.Select(m => m.GetAttributeValue<Guid>("objectid")).Should().BeEquivalentTo(sourceIds,
                "only the exact workflow IDs recorded in this managed package may be adopted for validation and cleanup");
            foreach (var member in importedMembers.Entities)
            {
                var id = member.GetAttributeValue<Guid>("objectid");
                await using var client = await pool.GetClientAsync(cancellationToken: ct);
                var row = await client.RetrieveAsync("workflow", id, new ColumnSet("name", "uniquename", "ismanaged", "clientdata"), ct);
                row.GetAttributeValue<bool>("ismanaged").Should().BeTrue();
                knownSourceNames.Should().Contain(row.GetAttributeValue<string>("uniquename"));
                names.Take(2).Should().Contain(row.GetAttributeValue<string>("name"));
                VerifyHarmless(row.GetAttributeValue<string>("clientdata"), null);
                owned[id] = new(id, row.GetAttributeValue<string>("name"), row.GetAttributeValue<string>("uniquename"), true, true, false, true);
            }
            owned.Values.Where(f => f.InSource).Select(f => f.UniqueName).Should().BeEquivalentTo(knownSourceNames);
            unknown.Remove(unresolvedImport); await SaveAsync();
            await ExerciseSourceAsync(outside);
            var blocker = await CreateFlowAsync(names[3], false, blocker: true);
            var blockedPlan = await CliAsync("enable", ["--all", "--solution", outsideName], true);
            blockedPlan.Exit.Should().NotBe(0); blockedPlan.Json.GetProperty("success").GetBoolean().Should().BeFalse();
            VerifySelection(blockedPlan.Json, [outside, blocker]);
            await ReadFlowAsync(outside); await ReadFlowAsync(blocker);
            await VerifyMembershipAsync(outside); await VerifyMembershipAsync(blocker);
            var blocked = await CliAsync("enable", ["--all", "--solution", outsideName], false,
                blockedPlan.Json.GetProperty("data").GetProperty("planHash").GetString());
            VerifySelection(blocked.Json, [outside, blocker]);
            blocked.Exit.Should().NotBe(0); blocked.Json.GetProperty("success").GetBoolean().Should().BeFalse();
            await VerifyStateAsync(outside, 0); await VerifyStateAsync(blocker, 0);
            manifest["state"] = "verified"; await SaveAsync();
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(5)); ct = cleanup.Token;
            async Task CleanAsync(string name, Func<Task> action)
            {
                try { await action(); output.WriteLine("Cleaned: " + name); }
                catch (Exception ex) { cleanupFailures.Add(name + ": " + ex.Message); }
            }
            foreach (var item in owned.Values.Where(f => !f.Managed).ToArray()) await CleanAsync(item.Name, () => DeleteOwnedFlowAsync(item));
            foreach (var item in owned.Values.Where(f => f.Managed).ToArray()) await CleanAsync("disable " + item.Name, async () =>
            {
                var row = await ReadFlowAsync(item); await VerifyMembershipAsync(item);
                if (row.GetAttributeValue<OptionSetValue>("statecode").Value != 0)
                    await ExecuteAsync(new SetStateRequest { EntityMoniker = new("workflow", item.Id), State = new(0), Status = new(1) });
            });
            if (unknown.Count == 0 && cleanupFailures.Count == 0)
            {
                if (sourceId != Guid.Empty) await CleanAsync(sourceName, async () => { await VerifySolutionAsync(sourceId, sourceName); await DeleteAsync("solution", sourceId); });
                if (outsideId != Guid.Empty) await CleanAsync(outsideName, async () => { await VerifySolutionAsync(outsideId, outsideName); await DeleteAsync("solution", outsideId); });
            }
            if (publisherId != Guid.Empty && unknown.Count == 0 && cleanupFailures.Count == 0)
                await CleanAsync(publisherName, async () =>
                {
                    var rows = await QueryAsync(publishers); var row = rows.Entities.Should().ContainSingle().Subject;
                    row.Id.Should().Be(publisherId); await DeleteAsync("publisher", publisherId);
                });
            manifest["cleanupFailures"] = cleanupFailures;
            manifest["cleanup"] = unknown.Count == 0 && cleanupFailures.Count == 0 ? "completed" : "manual-review-required; retained solution/publisher ownership evidence";
            await SaveAsync();
            cleanupFailures.Should().BeEmpty(); unknown.Should().BeEmpty("unconfirmed submissions require manual recovery");
        }
    }

    private sealed record OwnedFlow(Guid Id, string Name, string UniqueName, bool InSource, bool Managed, bool Blocker, bool Associated);
    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException(name + " is required");
    private static void VerifyHarmless(string data, string? missingReference)
    {
        var properties = JsonNode.Parse(data)!["properties"]!;
        var definition = properties["definition"]!;
        var triggers = definition["triggers"]!.AsObject(); triggers.Count.Should().Be(1);
        var trigger = triggers.Single().Value!;
        trigger["type"]!.GetValue<string>().Should().Be("Request"); trigger["kind"]!.GetValue<string>().Should().Be("Button");
        var actions = definition["actions"]!.AsObject(); actions.Count.Should().Be(1);
        actions.Single().Value!["type"]!.GetValue<string>().Should().Be("Compose");
        actions.Single().Value!["inputs"]!.GetValue<string>().Should().Be("PPDS isolated activation fixture");
        var references = properties["connectionReferences"]!.AsObject();
        if (missingReference is null) references.Count.Should().Be(0);
        else
        {
            references.Count.Should().Be(1);
            references.Single().Value!["connection"]!["connectionReferenceLogicalName"]!.GetValue<string>().Should().Be(missingReference);
        }
    }
}

public sealed class FlowFixtureOptInAttribute : FactAttribute
{
    public FlowFixtureOptInAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PPDS_FLOWS_LIVE_CONFIRM")))
            Skip = "Requires explicit isolated-flow fixture approval and PPDS_FLOWS_LIVE_* settings; see Fixtures/Flows/README.md.";
    }
}
