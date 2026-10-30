using System.ServiceModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Auth.Pooling;
using PPDS.Auth.Profiles;
using PPDS.Cli.Plugins.Registration;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;

namespace Net48Smoke;

// Continuation of cleanup already approved for run 40997090146. This entry point
// has no register, deploy, account-create, credential-write, or settings path.
internal static class CleanupOnly
{
    internal static void RequireRecordedId(Guid id, string expectedHash) =>
        Scope.Require(Scope.HashValue(id.ToString()) == expectedHash, "CleanupIdNotInOriginalJournal");

    public static async Task<int> Run(string root, string output, string preflightPath)
    {
        Directory.CreateDirectory(output);
        var stdout = Console.Out; var stderr = Console.Error;
        Console.SetOut(TextWriter.Null); Console.SetError(TextWriter.Null);
        var cleanup = "failed"; var phase = "cleanup-binding";
        using var journal = new Evidence(Path.Combine(root, "evidence-public.pem"), output);
        try
        {
            Scope.Require(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
                && Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT") == "1", "IsolatedFirstAttemptRequired");
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "cleanup-id-hashes.json")))!;
            Scope.Require(manifest["originalRun"]!.GetValue<long>() == 40997090146
                && manifest["originalHead"]!.GetValue<string>() == "5d63a24d0ae4d3d1356f7c288d9b8b32155776b5"
                && manifest["assemblyIdHashes"]!.AsArray().Count == 1
                && manifest["typeIdHashes"]!.AsArray().Count == 1, "CleanupManifestMismatch");
            var preflight = JsonNode.Parse(File.ReadAllText(preflightPath))!;
            Scope.Require(preflight["status"]!.GetValue<string>() == "collision"
                && preflight["targetAndApplicationVerified"]!.GetValue<bool>()
                && preflight["organizationType"]!.GetValue<string>() == "Developer"
                && DateTimeOffset.UtcNow - DateTimeOffset.Parse(preflight["checkedAtUtc"]!.GetValue<string>()) < TimeSpan.FromMinutes(5),
                "FreshDeveloperPreflightRequired");
            foreach (var key in new[] { "Package", "Assembly", "Type" })
                Scope.Require(preflight["collisions"]![key]!.GetValue<bool>(), "ExpectedCleanupResourceMissing");
            foreach (var key in new[] { "Step", "Account" })
                Scope.Require(!preflight["collisions"]![key]!.GetValue<bool>(), "UnexpectedCleanupResource");
            var target = Environment.GetEnvironmentVariable("NET48_DATAVERSE_URL")!;
            var app = Environment.GetEnvironmentVariable("NET48_APP_ID")!;
            var tenant = Environment.GetEnvironmentVariable("NET48_TENANT_ID")!;
            Scope.Require(Scope.HashValue(target) == "6dd5d9f084b0b9689223b7924f4ee57324944008ad1c378fbffee9dede14200a"
                && Scope.HashValue(app) == "fa2e7b2d2d02fa62b27718bf268c5d7b064f3f602472a57c901cd61f2f126f13"
                && Scope.HashValue(tenant) == "131876ad90ad84a1506e99b8435235b9d46564275fb852ff7986ee14bfd6a9fe", "TargetMismatch");
            journal.Add("cleanup-code", new { originalRun = 40997090146,
                head = Environment.GetEnvironmentVariable("TESTED_HEAD"),
                reviewedRuntime = "49f09af81328cdfe62e5e570a7ef03186db183a0",
                sourceTree = Environment.GetEnvironmentVariable("TESTED_SOURCE_TREE"),
                harnessSha256 = Scope.Hash(File.ReadAllBytes(typeof(CleanupOnly).Assembly.Location)) });
            journal.Add("cleanup-preflight", preflight);
            var profile = new AuthProfile { Name = "approved-cleanup-memory", AuthMethod = AuthMethod.GitHubFederated,
                ApplicationId = app, TenantId = tenant };
            var source = new ProfileConnectionSource(profile, target, maxPoolSize: 2);
            await using var rawPool = new DataverseConnectionPool([new Program.SourceAdapter(source)],
                new ThrottleTracker(NullLogger<ThrottleTracker>.Instance),
                new ConnectionPoolOptions { MaxPoolSize = 2, EnableValidation = false },
                NullLogger<DataverseConnectionPool>.Instance);
            var readOnlyBoundary = new Boundary(journal.Add);
            var readPool = GuardProxy.Wrap<IDataverseConnectionPool>(rawPool, readOnlyBoundary);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var ct = timeout.Token;
            phase = "cleanup-identity";
            await using (var client = await readPool.GetClientAsync(cancellationToken: ct))
            {
                var who = (WhoAmIResponse)await client.ExecuteAsync(new WhoAmIRequest(), ct);
                Scope.Require(Scope.HashValue(who.OrganizationId.ToString()) ==
                    "eb7fb25db9331b810dcfa6b3a594c12e13a1bd4f8ac914fb876eb1a6b2e5a626", "CallerOrganizationMismatch");
                var caller = await client.RetrieveAsync("systemuser", who.UserId, new ColumnSet("applicationid"), ct);
                Scope.Require(caller.GetAttributeValue<Guid>("applicationid") == Guid.Parse(app), "CallerApplicationMismatch");
            }
            phase = "cleanup-ownership";
            var packages = await Read(readPool, "pluginpackage", "name", Scope.Package, ct,
                "name", "version", "createdon", "modifiedon");
            Scope.Require(packages.Count == 1, "CleanupPackageCountMismatch");
            var package = packages.Single();
            RequireRecordedId(package.Id, manifest["packageIdHash"]!.GetValue<string>());
            Scope.Require(package.GetAttributeValue<string>("name") == Scope.Package
                && package.GetAttributeValue<string>("version") == "1.0.0", "CleanupPackageMetadataMismatch");
            var start = DateTimeOffset.Parse(manifest["startedAt"]!.GetValue<string>()).UtcDateTime.AddSeconds(-30);
            var end = DateTimeOffset.Parse(manifest["endedAt"]!.GetValue<string>()).UtcDateTime.AddSeconds(30);
            foreach (var key in new[] { "createdon", "modifiedon" })
            {
                var value = DateTime.SpecifyKind(package.GetAttributeValue<DateTime>(key), DateTimeKind.Utc);
                Scope.Require(value >= start && value <= end, "CleanupPackageChangedSinceRun");
            }
            // Hash-bound IDs come from the original encrypted journal, not a name-based
            // cleanup allowlist. All ownership checks finish before any delete is enabled.
            var boundary = new Boundary(journal.Add) { PackageId = package.Id };
            boundary.Attempted.Add(("pluginpackage", package.Id));
            var pool = GuardProxy.Wrap<IDataverseConnectionPool>(rawPool, boundary);
            var service = new PluginRegistrationService(pool, Program.Shakedown,
                NullLogger<PluginRegistrationService>.Instance);
            var assemblies = await service.ListAssembliesForPackageAsync(package.Id, ct);
            Scope.Require(assemblies.Count == 1 && assemblies[0].Name == Scope.Assembly, "CleanupAssemblyMismatch");
            RequireRecordedId(assemblies[0].Id, manifest["assemblyIdHashes"]![0]!.GetValue<string>());
            boundary.AssemblyIds.Add(assemblies[0].Id);
            var types = await service.ListTypesForAssemblyAsync(assemblies[0].Id, ct);
            Scope.Require(types.Count == 1 && types[0].TypeName == Scope.PluginType, "CleanupTypeMismatch");
            RequireRecordedId(types[0].Id, manifest["typeIdHashes"]![0]!.GetValue<string>());
            boundary.TypeIds.Add(types[0].Id);
            Scope.Require((await service.ListStepsForTypeAsync(types[0].Id,
                new PluginListOptions { IncludeHidden = true }, ct)).Count == 0, "UnexpectedCleanupStep");
            journal.Add("cleanup-original-ids-verified", new { originalRun = 40997090146,
                packageId = package.Id, assemblyId = assemblies[0].Id, typeId = types[0].Id,
                createdOn = package.GetAttributeValue<DateTime>("createdon"),
                modifiedOn = package.GetAttributeValue<DateTime>("modifiedon"), accounts = 0, steps = 0 });
            phase = "cleanup-delete";
            boundary.Phase = Phase.Cleanup;
            var result = await service.UnregisterPackageAsync(package.Id, force: true, cancellationToken: ct);
            boundary.Phase = Phase.ReadOnly;
            journal.Add("cleanup-unregistered", result);
            phase = "cleanup-absence";
            var recorded = new[] { ("pluginpackage", package.Id), ("pluginassembly", assemblies[0].Id), ("plugintype", types[0].Id) };
            foreach (var (table, id) in recorded)
                Scope.Require((await Read(pool, table, table + "id", id, ct)).Count == 0, "CleanupResidualId");
            foreach (var pair in new[] { ("pluginpackage", "name", Scope.Package),
                ("pluginassembly", "name", Scope.Assembly), ("plugintype", "typename", Scope.PluginType) })
                Scope.Require((await Read(pool, pair.Item1, pair.Item2, pair.Item3, ct)).Count == 0, "CleanupResidualName");
            var accountQuery = new QueryExpression("account") { ColumnSet = new ColumnSet(false), TopCount = 2 };
            accountQuery.Criteria.AddCondition("name", ConditionOperator.BeginsWith, Scope.Prefix);
            await using (var client = await pool.GetClientAsync(cancellationToken: ct))
                Scope.Require((await client.RetrieveMultipleAsync(accountQuery, ct)).Entities.Count == 0, "UnexpectedCleanupAccount");
            journal.Add("cleanup-final-absence", new { passed = true,
                recordedIds = recorded.Select(x => new { table = x.Item1, id = x.Item2 }).ToArray() });
            cleanup = "passed";
        }
        catch (Exception ex)
        {
            journal.Add("cleanup-failure", new { phase, type = ex.GetType().FullName,
                code = ex is InvalidOperationException ? ex.Message : null,
                dataverseCode = ex is FaultException<OrganizationServiceFault> fault ? (int?)fault.Detail.ErrorCode : null });
        }
        finally
        {
            journal.Add("cleanup-final", new { smoke = "failed", cleanup, phase, newSmokeRun = false });
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new
            { schemaVersion = 1, status = "smoke-failed", cleanup, newSmokeRun = false }, Program.Json));
            Console.SetOut(stdout); Console.SetError(stderr);
            Console.WriteLine($"Net48 cleanup continuation: {cleanup}; original smoke: failed.");
        }
        return cleanup == "passed" ? 0 : 1;
    }

    private static async Task<List<Entity>> Read(IDataverseConnectionPool pool, string table,
        string field, object value, CancellationToken ct, params string[] columns)
    {
        var query = new QueryExpression(table) { ColumnSet = new ColumnSet(columns), TopCount = 2 };
        query.Criteria.AddCondition(field, ConditionOperator.Equal, value);
        await using var client = await pool.GetClientAsync(cancellationToken: ct);
        var rows = await client.RetrieveMultipleAsync(query, ct);
        Scope.Require(!rows.MoreRecords, "IncompleteCleanupRead");
        return rows.Entities.ToList();
    }
}