using System.Reflection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Pooling;

namespace Net48Smoke;

internal static class SelfTests
{
    public static int Run(string root)
    {
        var count = 0;
        void Reject(Action action)
        {
            try { action(); }
            catch (InvalidOperationException) { count++; return; }
            throw new Exception("Expected request rejection");
        }
        Boundary Fresh() => new((_, _) => { });
        Entity Account(string name) => new("account") { ["name"] = name };
        var readOnly = Fresh();
        foreach (var phase in Enum.GetValues<Phase>())
        {
            var b = Fresh(); b.Phase = phase;
            Reject(() => b.CheckWrite("create", new Entity("contact") { ["lastname"] = "outside" }));
            Reject(() => b.CheckCall("ExecuteAsync", [new OrganizationRequest("PublishAllXml")]));
            Reject(() => b.CheckCall("ExecuteAsync", [new ExecuteMultipleRequest()]));
            Reject(() => b.CheckCall("AssociateAsync", []));
            Reject(() => b.CheckCall("GetRawWebApiAsync", []));
            Reject(() => b.CheckCall("Clone", []));
        }
        Reject(() => readOnly.CheckWrite("create", Account(Scope.Prefix + "first")));
        Reject(() => readOnly.CheckDelete("account", readOnly.FirstAccountId));
        Reject(() => readOnly.CheckCall("RetrieveMultipleAsync", [new QueryExpression("contact")]));
        Reject(() => readOnly.CheckCall("RetrieveMultipleAsync", [new FetchExpression("<fetch/>")]));
        var account = Fresh(); account.Phase = Phase.AccountV1;
        Reject(() => account.CheckWrite("create", Account("unapproved")));
        var extra = Account(Scope.Prefix + "first"); extra["description"] = "unexpected";
        Reject(() => account.CheckWrite("create", extra));
        var wrongId = Account(Scope.Prefix + "first"); wrongId.Id = Guid.NewGuid();
        Reject(() => account.CheckWrite("create", wrongId));
        var target = Account(Scope.Prefix + "first");
        account.CheckWrite("create", target);
        Scope.Require(target.Id == account.FirstAccountId && account.Attempted.Contains(("account", target.Id)), "IdNotReserved");
        count++;
        Reject(() => account.CheckWrite("create", Account(Scope.Prefix + "first")));
        account.Phase = Phase.Cleanup;
        Reject(() => account.CheckDelete("account", Guid.NewGuid()));
        Reject(() => account.CheckDelete("pluginassembly", target.Id));
        account.CheckDelete("account", target.Id); count++;
        Reject(() => account.CheckDelete("account", target.Id));
        Reject(() => account.CheckWrite("create", Account(Scope.Prefix + "second")));
        var package = Fresh(); package.Phase = Phase.RegisterV1;
        var bytes = File.ReadAllBytes(Program.PackagePath(root, "v1"));
        var packageEntity = new Entity("pluginpackage") { ["name"] = Scope.Package,
            ["version"] = "1.0.0", ["content"] = Convert.ToBase64String(bytes) };
        var request = new CreateRequest { Target = packageEntity };
        request.Parameters.Add("SolutionUniqueName", "unapproved");
        Reject(() => package.CheckCall("ExecuteAsync", [request]));
        request.Parameters.Remove("SolutionUniqueName");
        package.CheckCall("ExecuteAsync", [request]);
        Scope.Require(packageEntity.Id == package.PackageId, "PackageIdNotReserved"); count++;
        var update = new Entity("pluginpackage", package.PackageId) { ["content"] = Convert.ToBase64String(bytes) };
        package.Phase = Phase.DeployV2;
        Reject(() => package.CheckWrite("update", update));
        update["content"] = Convert.ToBase64String(File.ReadAllBytes(Program.PackagePath(root, "v2")));
        package.CheckWrite("update", update); count++;

        // Exercise both asynchronous SDK surfaces through the proxy. Rejected calls
        // must never reach the fake transport, rather than merely returning an error later.
        var transport = DispatchProxy.Create<IPooledClient, FakeClient>();
        var fake = (FakeClient)(object)transport;
        var proxyBoundary = Fresh();
        var guarded = GuardProxy.Wrap(transport, proxyBoundary);
        Reject(() => guarded.CreateAsync(Account(Scope.Prefix + "first"), default).GetAwaiter().GetResult());
        Reject(() => guarded.ExecuteAsync(new CreateRequest { Target = Account(Scope.Prefix + "first") }, default).GetAwaiter().GetResult());
        Reject(() => guarded.DeleteAsync("account", Guid.NewGuid(), default).GetAwaiter().GetResult());
        Scope.Require(fake.Calls == 0, "RejectedRequestReachedTransport"); count++;
        proxyBoundary.Phase = Phase.AccountV1;
        var created = guarded.CreateAsync(Account(Scope.Prefix + "first"), default).GetAwaiter().GetResult();
        Scope.Require(created == proxyBoundary.FirstAccountId && fake.Calls == 1, "ApprovedRequestNotForwarded"); count++;
        proxyBoundary.Phase = Phase.Cleanup;
        guarded.DeleteAsync("account", created, default).GetAwaiter().GetResult();
        Scope.Require(fake.Calls == 2, "CleanupNotForwarded"); count++;

        var output = Path.Combine(root, "obj", "offline-config");
        foreach (var version in new[] { "v1", "v2" })
        {
            Program.PrepareConfig(root, output, version); count++;
        }

        // Exercise the actual registration service against the guarded fake transport.
        var serviceBoundary = Fresh();
        var serviceTransport = DispatchProxy.Create<IPooledClient, FakeClient>();
        var transportState = (FakeClient)(object)serviceTransport;
        var fakePool = DispatchProxy.Create<IDataverseConnectionPool, FakePool>();
        ((FakePool)(object)fakePool).Client = serviceTransport;
        var servicePool = GuardProxy.Wrap(fakePool, serviceBoundary);
        var safety = new PPDS.Cli.Infrastructure.Safety.ShakedownGuard(
            new PPDS.Cli.Infrastructure.Safety.SystemEnvironment(),
            new PPDS.Cli.Infrastructure.Safety.SystemFileSystem(),
            new PPDS.Cli.Infrastructure.Safety.SystemClock(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PPDS.Cli.Infrastructure.Safety.ShakedownGuard>.Instance);
        var service = new PPDS.Cli.Plugins.Registration.PluginRegistrationService(servicePool, safety,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PPDS.Cli.Plugins.Registration.PluginRegistrationService>.Instance);
        serviceBoundary.Phase = Phase.RegisterV1;
        var packageId = service.UpsertPackageAsync(Scope.Package, bytes).GetAwaiter().GetResult();
        Scope.Require(packageId == serviceBoundary.PackageId, "RealPackageCreateFailed"); count++;
        transportState.Package = new Entity("pluginpackage", packageId) { ["name"] = Scope.Package, ["version"] = "1.0.0" };
        serviceBoundary.Phase = Phase.DeployV1;
        Scope.Require(service.UpsertPackageAsync(Scope.Package, bytes).GetAwaiter().GetResult() == packageId, "RealPackageUpdateFailed"); count++;
        serviceBoundary.AssemblyId = Guid.NewGuid();
        var typeId = service.UpsertPluginTypeAsync(serviceBoundary.AssemblyId, Scope.PluginType).GetAwaiter().GetResult();
        Scope.Require(typeId == serviceBoundary.TypeId, "RealTypeCreateFailed"); count++;
        serviceBoundary.MessageId = Guid.NewGuid(); serviceBoundary.FilterId = Guid.NewGuid();
        var config = PPDS.Cli.Plugins.Extraction.NupkgExtractor.Extract(Program.PackagePath(root, "v1"));
        var stepConfig = config.Types.Single().Steps.Single();
        var stepId = service.UpsertStepAsync(typeId, "pluginType", stepConfig, serviceBoundary.MessageId, serviceBoundary.FilterId)
            .GetAwaiter().GetResult();
        Scope.Require(stepId == serviceBoundary.StepId, "RealStepCreateFailed"); count++;
        transportState.Step = new Entity("sdkmessageprocessingstep", stepId) { ["statecode"] = new OptionSetValue(0) };
        serviceBoundary.Phase = Phase.DeployV2;
        Scope.Require(service.UpsertPackageAsync(Scope.Package, File.ReadAllBytes(Program.PackagePath(root, "v2"))).GetAwaiter().GetResult() == packageId,
            "RealPackageV2UpdateFailed"); count++;
        Scope.Require(service.UpsertStepAsync(typeId, "pluginType", stepConfig, serviceBoundary.MessageId, serviceBoundary.FilterId)
            .GetAwaiter().GetResult() == stepId, "RealStepUpdateFailed"); count++;
        serviceBoundary.Phase = Phase.ReadOnly;
        Reject(() => service.UpsertPackageAsync(Scope.Package, bytes).GetAwaiter().GetResult());
        using (var evidence = new Evidence(Path.Combine(root, "evidence-public.pem"), output))
            evidence.Add("offline-encryption-probe", new { passed = true });
        count++;

        var recordedId = Guid.NewGuid();
        CleanupOnly.RequireRecordedId(recordedId, Scope.HashValue(recordedId.ToString())); count++;
        Reject(() => CleanupOnly.RequireRecordedId(Guid.NewGuid(), Scope.HashValue(recordedId.ToString())));
        var cleanupOnlyBoundary = new Boundary((_, _) => { }) { PackageId = recordedId, Phase = Phase.Cleanup };
        cleanupOnlyBoundary.Attempted.Add(("pluginpackage", recordedId));
        Reject(() => cleanupOnlyBoundary.CheckDelete("pluginpackage", Guid.NewGuid()));
        cleanupOnlyBoundary.CheckDelete("pluginpackage", recordedId); count++;
        Scope.Require(!Program.VerifyOptionalPackageContent(null, Scope.V1), "AbsentContentShouldRemainUnverified"); count++;
        Scope.Require(!Program.VerifyOptionalPackageContent("", Scope.V1), "EmptyContentShouldRemainUnverified"); count++;
        Scope.Require(Program.VerifyOptionalPackageContent(Convert.ToBase64String(bytes), Scope.V1), "ReturnedContentMustMatch"); count++;
        Reject(() => Program.VerifyOptionalPackageContent(Convert.ToBase64String(bytes), Scope.V2));
        Reject(() => Program.VerifyOptionalPackageContent(null, "unapproved-hash"));
        Reject(() => Program.VerifyOptionalPackageContent(Convert.ToBase64String([1, 2, 3]), Scope.V1));

        // Exercise the real full-smoke cleanup path and production unregistration
        // service. A transport that rejects content retrieval must still clean
        // up the recorded package/type and verify the assembly's cascade delete.
        foreach (var wrongOwner in new[] { false, true })
        {
            var cleanupBoundary = Fresh();
            cleanupBoundary.Attempted.Add(("pluginpackage", cleanupBoundary.PackageId));
            var cleanupTransport = DispatchProxy.Create<IPooledClient, FakeClient>();
            var cleanupState = (FakeClient)(object)cleanupTransport;
            var assemblyId = Guid.NewGuid(); var pluginTypeId = Guid.NewGuid();
            cleanupState.Package = new Entity("pluginpackage", cleanupBoundary.PackageId)
            {
                ["name"] = wrongOwner ? "outside-scope" : Scope.Package, ["version"] = "1.0.0",
                ["content"] = "unreadable-content-must-not-control-cleanup"
            };
            cleanupState.Assembly = new Entity("pluginassembly", assemblyId)
            {
                ["name"] = Scope.Assembly, ["version"] = "1.0.0.0",
                ["packageid"] = new EntityReference("pluginpackage", cleanupBoundary.PackageId)
            };
            cleanupState.PluginType = new Entity("plugintype", pluginTypeId)
            {
                ["typename"] = Scope.PluginType,
                ["pluginassemblyid"] = new EntityReference("pluginassembly", assemblyId)
            };
            cleanupState.RejectPackageContentRead = true;
            var cleanupPoolTarget = DispatchProxy.Create<IDataverseConnectionPool, FakePool>();
            ((FakePool)(object)cleanupPoolTarget).Client = cleanupTransport;
            var cleanupPool = GuardProxy.Wrap(cleanupPoolTarget, cleanupBoundary);
            var cleanupService = new PPDS.Cli.Plugins.Registration.PluginRegistrationService(cleanupPool, safety,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PPDS.Cli.Plugins.Registration.PluginRegistrationService>.Instance);
            using var cleanupEvidence = new Evidence(Path.Combine(root, "evidence-public.pem"),
                Path.Combine(output, wrongOwner ? "wrong-owner" : "content-unavailable"));
            if (wrongOwner)
            {
                Reject(() => Program.Cleanup(cleanupService, cleanupPool, cleanupBoundary, cleanupEvidence, default)
                    .GetAwaiter().GetResult());
                Scope.Require(cleanupState.DeletedRequests.Count == 0, "OwnershipMismatchDeletedResources"); count++;
            }
            else
            {
                Program.Cleanup(cleanupService, cleanupPool, cleanupBoundary, cleanupEvidence, default)
                    .GetAwaiter().GetResult();
                Scope.Require(cleanupState.DeletedRequests.SequenceEqual(new[]
                    { ("plugintype", pluginTypeId), ("pluginpackage", cleanupBoundary.PackageId) }), "UnexpectedCleanupDeletes"); count++;
                Scope.Require(cleanupState.Package is null && cleanupState.Assembly is null && cleanupState.PluginType is null,
                    "CleanupResidualResources"); count++;
            }
            Scope.Require(cleanupState.PackageContentReads == 0, "CleanupRequestedPackageContent"); count++;
        }
        Console.WriteLine($"Offline scope/transport/fixture checks: {count} passed.");
        return 0;
    }
}

public class FakeClient : DispatchProxy
{
    public int Calls { get; private set; }
    public Entity? Package { get; set; }
    public Entity? Step { get; set; }
    public Entity? Assembly { get; set; }
    public Entity? PluginType { get; set; }
    public bool RejectPackageContentRead { get; set; }
    public int PackageContentReads { get; private set; }
    public List<(string Table, Guid Id)> DeletedRequests { get; } = [];
    private EntityCollection Read(QueryExpression query)
    {
        if (query.EntityName == "pluginpackage" && (query.ColumnSet.AllColumns || query.ColumnSet.Columns.Contains("content")))
        {
            PackageContentReads++;
            if (RejectPackageContentRead) throw new InvalidOperationException("PackageContentReadUnsupported");
        }
        var entity = query.EntityName == "pluginpackage" ? Package
            : query.EntityName == "sdkmessageprocessingstep" ? Step
            : query.EntityName == "pluginassembly" ? Assembly
            : query.EntityName == "plugintype" ? PluginType : null;
        return entity is null ? new EntityCollection() : new EntityCollection([entity]);
    }
    private Task Delete(string table, Guid id)
    {
        DeletedRequests.Add((table, id));
        if (table == "plugintype" && PluginType?.Id == id) PluginType = null;
        if (table == "sdkmessageprocessingstep" && Step?.Id == id) Step = null;
        if (table == "pluginpackage" && Package?.Id == id) { Package = null; Assembly = null; }
        return Task.CompletedTask;
    }
    private static OrganizationResponse Execute(OrganizationRequest request)
    {
        if (request is CreateRequest create)
        {
            var response = new CreateResponse(); response.Results["id"] = create.Target.Id; return response;
        }
        if (request is UpdateRequest) return new UpdateResponse();
        throw new InvalidOperationException("UnexpectedExecute");
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        Calls++;
        return method!.Name switch
        {
            "CreateAsync" => Task.FromResult(((Entity)args![0]!).Id),
            "DeleteAsync" => Delete((string)args![0]!, (Guid)args[1]!),
            "UpdateAsync" => Task.CompletedTask,
            "DisposeAsync" => ValueTask.CompletedTask,
            "RetrieveMultipleAsync" => Task.FromResult(Read((QueryExpression)args![0]!)),
            "ExecuteAsync" => Task.FromResult(Execute((OrganizationRequest)args![0]!)),
            _ => throw new InvalidOperationException("UnexpectedFakeCall")
        };
    }
}

public class FakePool : DispatchProxy
{
    public IPooledClient? Client { get; set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args) =>
        method!.Name == "GetClientAsync" ? Task.FromResult(Client!) : throw new InvalidOperationException("UnexpectedPoolCall");
}
