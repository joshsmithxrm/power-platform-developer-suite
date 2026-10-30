using System.Reflection;
using System.ServiceModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Auth.Pooling;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Plugins.Extraction;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;

namespace Net48Smoke;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    internal static readonly ShakedownGuard Shakedown = new(new SystemEnvironment(), new SystemFileSystem(),
        new SystemClock(), NullLogger<ShakedownGuard>.Instance);

    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args.ElementAtOrDefault(1) ?? "scripts/ci/net48-smoke");
        if (args.Length == 4 && args[0] == "--approved-cleanup") return await CleanupOnly.Run(root, Path.GetFullPath(args[2]), args[3]);
        if (args.FirstOrDefault() == "--self-test") return SelfTests.Run(root);
        if (args.Length != 4 || args[0] != "--approved-live") return 64;
        var output = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(output);
        var stdout = Console.Out; var stderr = Console.Error;
        Console.SetOut(TextWriter.Null); Console.SetError(TextWriter.Null);
        var status = "failed"; var cleanup = "not-needed"; var phase = "binding";
        Evidence? journal = null;
        DataverseConnectionPool? rawPool = null;
        IDataverseConnectionPool? pool = null;
        PluginRegistrationService? service = null;
        Boundary? boundary = null;
        try
        {
            journal = new Evidence(Path.Combine(root, "evidence-public.pem"), output);
            boundary = new Boundary(journal.Add);
            var target = RequiredEnvironment("NET48_DATAVERSE_URL");
            var app = RequiredEnvironment("NET48_APP_ID");
            var tenant = RequiredEnvironment("NET48_TENANT_ID");
            Scope.Require(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
                && RequiredEnvironment("GITHUB_RUN_ATTEMPT") == "1", "IsolatedFirstAttemptRequired");
            Scope.Require(Scope.HashValue(target) == "6dd5d9f084b0b9689223b7924f4ee57324944008ad1c378fbffee9dede14200a"
                && Scope.HashValue(app) == "fa2e7b2d2d02fa62b27718bf268c5d7b064f3f602472a57c901cd61f2f126f13"
                && Scope.HashValue(tenant) == "131876ad90ad84a1506e99b8435235b9d46564275fb852ff7986ee14bfd6a9fe", "TargetMismatch");
            var preflight = JsonNode.Parse(File.ReadAllText(args[3]))!;
            Scope.Require(preflight["status"]!.GetValue<string>() == "passed"
                && preflight["organizationType"]!.GetValue<string>() == "Developer"
                && DateTimeOffset.UtcNow - DateTimeOffset.Parse(preflight["checkedAtUtc"]!.GetValue<string>()) < TimeSpan.FromMinutes(5),
                "FreshDeveloperPreflightRequired");
            journal.Add("preflight", preflight);
            journal.Add("code-and-fixtures", new { head = RequiredEnvironment("TESTED_HEAD"),
                reviewedRuntime = "49f09af81328cdfe62e5e570a7ef03186db183a0",
                sourceTree = RequiredEnvironment("TESTED_SOURCE_TREE"),
                cliSha256 = Scope.Hash(File.ReadAllBytes(typeof(DeployCommand).Assembly.Location)),
                harnessSha256 = Scope.Hash(File.ReadAllBytes(typeof(Program).Assembly.Location)),
                fixtures = new[] { Scope.V1, Scope.V2 } });
            var config1 = PrepareConfig(root, output, "v1");
            var config2 = PrepareConfig(root, output, "v2");
            phase = "authentication";
            var profile = new AuthProfile { Name = "approved-smoke-memory", AuthMethod = AuthMethod.GitHubFederated,
                ApplicationId = app, TenantId = tenant };
            var source = new ProfileConnectionSource(profile, target, maxPoolSize: 2);
            rawPool = new DataverseConnectionPool([new SourceAdapter(source)],
                new ThrottleTracker(NullLogger<ThrottleTracker>.Instance),
                new ConnectionPoolOptions { MaxPoolSize = 2, EnableValidation = false },
                NullLogger<DataverseConnectionPool>.Instance);
            pool = GuardProxy.Wrap<IDataverseConnectionPool>(rawPool, boundary);
            service = new PluginRegistrationService(pool, Shakedown, NullLogger<PluginRegistrationService>.Instance);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var ct = timeout.Token;
            phase = "identity-and-collisions";
            await using (var client = await pool.GetClientAsync(cancellationToken: ct))
            {
                var who = (WhoAmIResponse)await client.ExecuteAsync(new WhoAmIRequest(), ct);
                Scope.Require(Scope.HashValue(who.OrganizationId.ToString()) ==
                    "eb7fb25db9331b810dcfa6b3a594c12e13a1bd4f8ac914fb876eb1a6b2e5a626", "CallerOrganizationMismatch");
                var caller = await client.RetrieveAsync("systemuser", who.UserId, new ColumnSet("applicationid"), ct);
                Scope.Require(caller.GetAttributeValue<Guid>("applicationid") == Guid.Parse(app), "CallerApplicationMismatch");
                journal.Add("identity", new { target, who.OrganizationId, who.UserId, application = app,
                    version = client.ConnectedOrgVersion?.ToString() });
            }
            await AssertNoCollisions(pool, ct);
            foreach (var pair in new[] { ("pluginpackage", boundary.PackageId), ("sdkmessageprocessingstep", boundary.StepId),
                ("account", boundary.FirstAccountId), ("account", boundary.SecondAccountId) })
                Scope.Require((await ById(pool, pair.Item1, pair.Item2, ct)).Count == 0, "PreallocatedIdCollision");
            boundary.MessageId = await service.GetSdkMessageIdAsync("Create", ct) ?? throw new InvalidOperationException("MissingMessage");
            boundary.FilterId = await service.GetSdkMessageFilterIdAsync(boundary.MessageId, "account", cancellationToken: ct)
                ?? throw new InvalidOperationException("MissingFilter");
            journal.Add("reserved-ids", new { boundary.PackageId, boundary.StepId, boundary.FirstAccountId, boundary.SecondAccountId });
            phase = "register-v1-preview";
            await Deploy(service, boundary, journal, config1, true, true, ct);
            phase = "register-v1";
            boundary.Phase = Phase.RegisterV1;
            var packageId = await service.UpsertPackageAsync(Scope.Package,
                File.ReadAllBytes(PackagePath(root, "v1")), cancellationToken: ct);
            boundary.Phase = Phase.ReadOnly;
            Scope.Require(packageId == boundary.PackageId, "CreatedPackageIdMismatch");
            await Discover(service, pool, boundary, journal, ct, waitForAssembly: true);
            await VerifyPackage(pool, boundary, Scope.V1, journal, ct);
            phase = "deploy-v1-preview";
            await Deploy(service, boundary, journal, config1, true, true, ct);
            phase = "deploy-v1";
            boundary.Phase = Phase.DeployV1;
            await Deploy(service, boundary, journal, config1, false, true, ct);
            boundary.Phase = Phase.ReadOnly;
            await Discover(service, pool, boundary, journal, ct);
            await VerifyStep(pool, boundary, ct);
            phase = "execute-v1";
            await CreateAndVerifyAccount(pool, boundary, journal, false, ct);
            phase = "deploy-v2-preview";
            await Deploy(service, boundary, journal, config2, true, false, ct);
            phase = "deploy-v2";
            boundary.Phase = Phase.DeployV2;
            await Deploy(service, boundary, journal, config2, false, false, ct);
            boundary.Phase = Phase.ReadOnly;
            await Discover(service, pool, boundary, journal, ct);
            await VerifyPackage(pool, boundary, Scope.V2, journal, ct);
            await VerifyStep(pool, boundary, ct);
            journal.Add("identity-stability", new { boundary.PackageId, boundary.AssemblyId, boundary.TypeId,
                boundary.StepId, packageVersion = "1.0.0", packageAndStepStable = true });
            // Allow sandbox package cache propagation without creating extra verification accounts.
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
            phase = "execute-v2";
            await CreateAndVerifyAccount(pool, boundary, journal, true, ct);
            status = "passed";
        }
        catch (Exception ex)
        {
            journal?.Add("failure", Failure(phase, ex));
        }
        finally
        {
            if (boundary?.Attempted.Count > 0 && pool is not null && service is not null && journal is not null)
            {
                cleanup = "failed";
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                try
                {
                    await Cleanup(service, pool, boundary, journal, cleanupTimeout.Token);
                    cleanup = "passed";
                }
                catch (Exception ex) { journal.Add("cleanup-failure", Failure("cleanup", ex)); }
            }
            if (cleanup == "failed") status = "failed";
            journal?.Add("final", new { status, cleanup, phase, writeRequests = boundary?.WriteCount ?? 0 });
            if (rawPool is not null) await rawPool.DisposeAsync();
            journal?.Dispose();
            Console.SetOut(stdout); Console.SetError(stderr);
            File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new
            { schemaVersion = 1, status, cleanup, source = "PR1450 plus PR1469", boundedRun = true }, Json));
            Console.WriteLine($"Approved net48 smoke: {status}; cleanup: {cleanup}.");
        }
        return status == "passed" && cleanup == "passed" ? 0 : 1;
    }

    internal static string PackagePath(string root, string version) =>
        Path.Combine(root, "fixtures", version, Scope.Package + ".1.0.0.nupkg");

    internal static string PrepareConfig(string root, string output, string version)
    {
        var package = PackagePath(root, version);
        var hash = version == "v1" ? Scope.V1 : Scope.V2;
        Scope.Require(Scope.Hash(File.ReadAllBytes(package)) == hash, "FixtureHashMismatch");
        var assembly = NupkgExtractor.Extract(package);
        assembly.PackagePath = Path.GetFullPath(package);
        var actual = JsonSerializer.SerializeToNode(assembly, Json);
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "expected-assembly.json")))!;
        expected["packagePath"] = assembly.PackagePath; expected["packageContentSha256"] = hash;
        Scope.Require(JsonNode.DeepEquals(actual, expected), "ExtractedScopeMismatch");
        var config = new PluginRegistrationConfig { Assemblies = [assembly] };
        config.Validate();
        Directory.CreateDirectory(output);
        var path = Path.Combine(output, version + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(config, Json));
        return path;
    }

    private static async Task Deploy(IPluginRegistrationService service, Boundary boundary, Evidence journal,
        string config, bool dryRun, bool create, CancellationToken ct)
    {
        var before = boundary.WriteCount;
        if (dryRun) Scope.Require(boundary.Phase == Phase.ReadOnly, "DryRunPhaseMismatch");
        Func<CancellationToken, Task<ServiceProvider>> factory = _ => Task.FromResult(new ServiceCollection()
            .AddSingleton(service).AddSingleton(DispatchProxy.Create<ICustomApiService, GuardProxy>())
            .BuildServiceProvider());
        using var output = new StringWriter();
        var previous = Console.Out;
        int exitCode;
        try
        {
            Console.SetOut(output);
            var method = typeof(DeployCommand).GetMethod("ExecuteAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            var task = (Task<int>)method.Invoke(null, [new FileInfo(config), null, null, null, false, dryRun,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json, Quiet = true }, ct, factory, null])!;
            exitCode = await task;
        }
        finally { Console.SetOut(previous); }
        var text = output.ToString();
        journal.Add(dryRun ? "cli-dry-run" : "cli-deploy", new { exitCode, output = text });
        Scope.Require(exitCode == 0, "DeployCommandFailed");
        var envelope = JsonNode.Parse(text)!;
        var results = envelope["data"]!.AsArray();
        Scope.Require(envelope["success"]!.GetValue<bool>() && results.Count == 1, "UnexpectedDeploymentResults");
        var result = results[0]!;
        Scope.Require(result["success"]!.GetValue<bool>() && result["assemblyName"]!.GetValue<string>() == Scope.Assembly,
            "DeploymentResultMismatch");
        foreach (var key in new[] { "stepsDeleted", "imagesCreated", "imagesUpdated", "imagesDeleted" })
            Scope.Require(result[key]!.GetValue<int>() == 0, "DeploymentScopeMismatch");
        Scope.Require(result["stepsCreated"]!.GetValue<int>() == (create ? 1 : 0)
            && result["stepsUpdated"]!.GetValue<int>() == (create ? 0 : 1)
            && result["warnings"]!.AsArray().Count == 0, "DeploymentPlanMismatch");
        Scope.Require(!dryRun || boundary.WriteCount == before, "DryRunAttemptedWrite");
    }

    private static async Task<List<Entity>> Query(IDataverseConnectionPool pool, QueryExpression query, CancellationToken ct)
    {
        await using var client = await pool.GetClientAsync(cancellationToken: ct);
        var result = await client.RetrieveMultipleAsync(query, ct);
        Scope.Require(!result.MoreRecords, "IncompleteQueryResult");
        return result.Entities.ToList();
    }
    private static QueryExpression Where(string table, string field, object value, params string[] columns)
    {
        var query = new QueryExpression(table) { ColumnSet = new ColumnSet(columns) };
        query.Criteria.AddCondition(field, ConditionOperator.Equal, value);
        return query;
    }
    private static Task<List<Entity>> ById(IDataverseConnectionPool pool, string table, Guid id, CancellationToken ct,
        params string[] columns) => Query(pool, Where(table, table + "id", id, columns), ct);

    private static async Task AssertNoCollisions(IDataverseConnectionPool pool, CancellationToken ct)
    {
        var package = Where("pluginpackage", "name", Scope.Package);
        package.Criteria.FilterOperator = LogicalOperator.Or;
        package.Criteria.AddCondition("uniquename", ConditionOperator.Equal, Scope.Package);
        var account = new QueryExpression("account") { ColumnSet = new ColumnSet(false) };
        account.Criteria.AddCondition("name", ConditionOperator.BeginsWith, Scope.Prefix);
        foreach (var query in new[] { package, Where("pluginassembly", "name", Scope.Assembly),
            Where("plugintype", "typename", Scope.PluginType), account })
            Scope.Require((await Query(pool, query, ct)).Count == 0, "ResourceCollisionOrResidual");
    }

    private static async Task Discover(PluginRegistrationService service, IDataverseConnectionPool pool,
        Boundary boundary, Evidence journal, CancellationToken ct, bool waitForAssembly = false)
    {
        List<PluginAssemblyInfo> assemblies = [];
        for (var attempt = 0; attempt < (waitForAssembly ? 30 : 1); attempt++)
        {
            assemblies = await service.ListAssembliesForPackageAsync(boundary.PackageId, ct);
            if (assemblies.Any(x => x.Name == Scope.Assembly)) break;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        if (boundary.Phase == Phase.Cleanup && assemblies.Count == 0) return;
        Scope.Require(assemblies.Count is >= 1 and <= 2 && assemblies.Count(x => x.Name == Scope.Assembly) == 1
            && assemblies.All(x => x.Name == Scope.Assembly || x.Name == "PPDS.Plugins"), "PackageChildrenMismatch");
        var assemblyId = assemblies.Single(x => x.Name == Scope.Assembly).Id;
        Scope.Require(boundary.AssemblyId == Guid.Empty || boundary.AssemblyId == assemblyId, "AssemblyIdentityChanged");
        boundary.AssemblyId = assemblyId;
        foreach (var assembly in assemblies)
        {
            boundary.AssemblyIds.Add(assembly.Id);
            var types = await service.ListTypesForAssemblyAsync(assembly.Id, ct);
            Scope.Require(types.Count <= 1 && types.All(x => assembly.Id == assemblyId && x.TypeName == Scope.PluginType),
                "PackageTypesMismatch");
            foreach (var type in types)
            {
                Scope.Require(boundary.TypeId == Guid.Empty || boundary.TypeId == type.Id, "TypeIdentityChanged");
                boundary.TypeId = type.Id; boundary.TypeIds.Add(type.Id);
                var steps = await service.ListStepsForTypeAsync(type.Id, new PluginListOptions { IncludeHidden = true }, ct);
                Scope.Require(steps.Count <= 1 && steps.All(x => x.Id == boundary.StepId && x.Name == Scope.Step
                    && boundary.Attempted.Contains(("sdkmessageprocessingstep", x.Id))), "UnownedPackageStep");
                foreach (var step in steps)
                {
                    Scope.Require((await service.ListImagesForStepAsync(step.Id, ct)).Count == 0, "UnexpectedStepImage");
                    var rows = await ById(pool, "sdkmessageprocessingstep", step.Id, ct, "sdkmessageprocessingstepsecureconfigid");
                    Scope.Require(rows.Count == 1 && rows[0].GetAttributeValue<EntityReference>("sdkmessageprocessingstepsecureconfigid") is null,
                        "UnexpectedSecureConfiguration");
                }
            }
        }
        journal.Add("owned-descendants", new { packageId = boundary.PackageId,
            assemblyIds = boundary.AssemblyIds.ToArray(), typeIds = boundary.TypeIds.ToArray(), stepId = boundary.StepId });
    }

    private static async Task VerifyPackage(IDataverseConnectionPool pool, Boundary boundary, string hash,
        Evidence journal, CancellationToken ct)
    {
        var rows = await ById(pool, "pluginpackage", boundary.PackageId, ct, "name", "version", "content");
        Scope.Require(rows.Count == 1 && rows[0].GetAttributeValue<string>("name") == Scope.Package
            && rows[0].GetAttributeValue<string>("version") == "1.0.0", "PackageReadbackMismatch");
        var contentVerified = VerifyOptionalPackageContent(rows[0].GetAttributeValue<string>("content"), hash);
        journal.Add("package-readback", new { id = boundary.PackageId, name = Scope.Package, version = "1.0.0",
            uploadedSha256 = hash, contentReadback = contentVerified ? "verified" : "not-returned" });
    }

    // Dataverse can omit package content on retrieve. The request boundary has
    // already checked and journaled the exact uploaded bytes. Metadata/ID checks
    // and both execution markers remain required; verify content when returned.
    internal static bool VerifyOptionalPackageContent(string? content, string expectedHash)
    {
        Scope.Require(expectedHash is Scope.V1 or Scope.V2, "ExpectedPackageHashOutsideScope");
        if (string.IsNullOrEmpty(content)) return false;
        var actual = Scope.Hash(Convert.FromBase64String(content));
        Scope.Require(actual == expectedHash, "PackageReadbackHashMismatch");
        return true;
    }

    private static async Task VerifyPackageOwnershipForCleanup(IDataverseConnectionPool pool,
        Boundary boundary, CancellationToken ct)
    {
        // Cleanup must not request, decode, or depend on package content. The
        // ID was reserved and journaled before create; only that ID can delete.
        var rows = await ById(pool, "pluginpackage", boundary.PackageId, ct, "name", "version");
        Scope.Require(rows.Count == 1 && rows[0].GetAttributeValue<string>("name") == Scope.Package
            && rows[0].GetAttributeValue<string>("version") == "1.0.0", "CleanupPackageOwnershipMismatch");
    }
    private static async Task VerifyStep(IDataverseConnectionPool pool, Boundary boundary, CancellationToken ct)
    {
        var rows = await ById(pool, "sdkmessageprocessingstep", boundary.StepId, ct,
            "name", "eventhandler", "sdkmessageid", "sdkmessagefilterid", "stage", "mode", "rank", "statecode");
        Scope.Require(rows.Count == 1, "StepAbsent");
        var step = rows[0];
        Scope.Require(step.GetAttributeValue<string>("name") == Scope.Step
            && step.GetAttributeValue<EntityReference>("eventhandler")?.Id == boundary.TypeId
            && step.GetAttributeValue<EntityReference>("sdkmessageid")?.Id == boundary.MessageId
            && step.GetAttributeValue<EntityReference>("sdkmessagefilterid")?.Id == boundary.FilterId
            && step.GetAttributeValue<OptionSetValue>("stage")?.Value == 20
            && step.GetAttributeValue<OptionSetValue>("mode")?.Value == 0
            && step.GetAttributeValue<int>("rank") == 1
            && step.GetAttributeValue<OptionSetValue>("statecode")?.Value == 0, "StepReadbackMismatch");
    }
    private static async Task CreateAndVerifyAccount(IDataverseConnectionPool pool, Boundary boundary, Evidence journal,
        bool second, CancellationToken ct)
    {
        var expectedId = second ? boundary.SecondAccountId : boundary.FirstAccountId;
        var expectedName = Scope.Prefix + (second ? "second" : "first");
        var expectedMarker = Scope.Prefix + (second ? "v2" : "v1");
        Shakedown.EnsureCanMutate("approved-smoke.account.create");
        boundary.Phase = second ? Phase.AccountV2 : Phase.AccountV1;
        await using (var client = await pool.GetClientAsync(cancellationToken: ct))
        {
            var id = await client.CreateAsync(new Entity("account") { ["name"] = expectedName }, ct);
            Scope.Require(id == expectedId, "CreatedAccountIdMismatch");
        }
        boundary.Phase = Phase.ReadOnly;
        var rows = await ById(pool, "account", expectedId, ct, "name", "description");
        Scope.Require(rows.Count == 1 && rows[0].GetAttributeValue<string>("name") == expectedName
            && rows[0].GetAttributeValue<string>("description") == expectedMarker, "ExecutionMarkerMismatch");
        journal.Add("execution-verified", new { id = expectedId, name = expectedName,
            marker = rows[0].GetAttributeValue<string>("description") });
    }

    internal static async Task Cleanup(PluginRegistrationService service, IDataverseConnectionPool pool, Boundary boundary,
        Evidence journal, CancellationToken ct)
    {
        boundary.Phase = Phase.Cleanup;
        var failures = 0;
        foreach (var pair in new[] { (boundary.FirstAccountId, "first"), (boundary.SecondAccountId, "second") })
        {
            if (!boundary.Attempted.Contains(("account", pair.Item1))) continue;
            try
            {
                var rows = await ById(pool, "account", pair.Item1, ct, "name");
                if (rows.Count == 0) continue;
                Scope.Require(rows.Count == 1 && rows[0].GetAttributeValue<string>("name") == Scope.Prefix + pair.Item2,
                    "CleanupAccountOwnershipMismatch");
                Shakedown.EnsureCanMutate("approved-smoke.account.delete");
                await using var client = await pool.GetClientAsync(cancellationToken: ct);
                await client.DeleteAsync("account", pair.Item1, ct);
                journal.Add("account-deleted", new { id = pair.Item1 });
            }
            catch (Exception ex) { failures++; journal.Add("cleanup-account-failure", Failure("cleanup-account", ex)); }
        }
        if (boundary.Attempted.Contains(("pluginpackage", boundary.PackageId)))
        {
            try
            {
                if ((await ById(pool, "pluginpackage", boundary.PackageId, ct)).Count != 0)
                {
                    await VerifyPackageOwnershipForCleanup(pool, boundary, ct);
                    await Discover(service, pool, boundary, journal, ct);
                    var result = await service.UnregisterPackageAsync(boundary.PackageId, force: true, cancellationToken: ct);
                    journal.Add("package-unregistered", result);
                }
            }
            catch (Exception ex) { failures++; journal.Add("cleanup-package-failure", Failure("cleanup-package", ex)); }
        }
        boundary.Phase = Phase.ReadOnly;
        var ids = boundary.Attempted.Concat(boundary.AssemblyIds.Select(id => ("pluginassembly", id)))
            .Concat(boundary.TypeIds.Select(id => ("plugintype", id))).Distinct().ToArray();
        var residual = new List<object>();
        foreach (var (table, id) in ids)
            if ((await ById(pool, table, id, ct)).Count != 0) residual.Add(new { table, id });
        journal.Add("final-id-absence", new { checkedIds = ids.Select(x => new { table = x.Item1, id = x.Item2 }).ToArray(), residual });
        Scope.Require(residual.Count == 0 && failures == 0, "CleanupIncomplete");
        await AssertNoCollisions(pool, ct);
        journal.Add("final-name-absence", new { passed = true });
    }
    private static object Failure(string phase, Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null } wrapper) exception = wrapper.InnerException!;
        return new { phase, type = exception.GetType().FullName,
            code = exception is InvalidOperationException ? exception.Message : null,
            dataverseCode = exception is FaultException<OrganizationServiceFault> fault ? (int?)fault.Detail.ErrorCode : null,
            dataverseMessage = exception is FaultException<OrganizationServiceFault> dv ? dv.Detail.Message : null };
    }
    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException("RequiredRunnerInputMissing");

    internal sealed class SourceAdapter(ProfileConnectionSource source) : IConnectionSource
    {
        public string Name => source.Name;
        public int MaxPoolSize => source.MaxPoolSize;
        public ServiceClient GetSeedClient() => source.GetSeedClient();
        public void InvalidateSeed() => source.InvalidateSeed();
        public void Dispose() => source.Dispose();
    }
}
