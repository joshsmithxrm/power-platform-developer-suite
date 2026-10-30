using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Pooling;

namespace Net48Smoke;

internal enum Phase { ReadOnly, RegisterV1, DeployV1, AccountV1, DeployV2, AccountV2, Cleanup }

// This boundary sees the actual requests emitted by the reviewed PPDS code.
// No writable client is handed to either the CLI command or registration service.
internal sealed class Boundary(Action<string, object> record)
{
    public Phase Phase { get; set; }
    public Guid PackageId { get; } = Guid.NewGuid();
    public Guid StepId { get; } = Guid.NewGuid();
    public Guid FirstAccountId { get; } = Guid.NewGuid();
    public Guid SecondAccountId { get; } = Guid.NewGuid();
    public Guid AssemblyId { get; set; }
    public Guid TypeId { get; set; }
    public Guid MessageId { get; set; }
    public Guid FilterId { get; set; }
    public HashSet<Guid> AssemblyIds { get; } = [];
    public HashSet<Guid> TypeIds { get; } = [];
    public HashSet<(string Table, Guid Id)> Attempted { get; } = [];
    public HashSet<(string Table, Guid Id)> Deleted { get; } = [];
    private readonly HashSet<string> writes = [];
    public int WriteCount => writes.Count;
    private static readonly HashSet<string> ReadTables =
    [
        "pluginpackage", "pluginassembly", "plugintype", "sdkmessageprocessingstep",
        "sdkmessageprocessingstepimage", "sdkmessage", "sdkmessagefilter", "account", "systemuser"
    ];

    public void CheckCall(string name, object?[] args)
    {
        switch (name)
        {
            case "Create": case "CreateAsync": CheckWrite("create", (Entity)args[0]!); return;
            case "Update": case "UpdateAsync": CheckWrite("update", (Entity)args[0]!); return;
            case "Delete": case "DeleteAsync": CheckDelete((string)args[0]!, (Guid)args[1]!); return;
            case "Retrieve": case "RetrieveAsync":
                Scope.Require(ReadTables.Contains((string)args[0]!), "ReadTableOutsideScope"); return;
            case "RetrieveMultiple": case "RetrieveMultipleAsync": CheckQuery((QueryBase)args[0]!); return;
            case "Execute": case "ExecuteAsync":
                var request = (OrganizationRequest)args[0]!;
                switch (request)
                {
                    case CreateRequest create:
                        Scope.Require(create.Parameters.Count == 1 && create.Parameters.Contains("Target"), "ExtraCreateParameters");
                        CheckWrite("create", create.Target); return;
                    case UpdateRequest update:
                        Scope.Require(update.Parameters.Count == 1 && update.Parameters.Contains("Target"), "ExtraUpdateParameters");
                        CheckWrite("update", update.Target); return;
                    case DeleteRequest delete:
                        Scope.Require(delete.Parameters.Count == 1 && delete.Parameters.Contains("Target"), "ExtraDeleteParameters");
                        CheckDelete(delete.Target.LogicalName, delete.Target.Id); return;
                    case RetrieveMultipleRequest read: CheckQuery(read.Query); return;
                    case RetrieveRequest read:
                        Scope.Require(ReadTables.Contains(read.Target.LogicalName), "ReadTableOutsideScope"); return;
                    default:
                        Scope.Require(request.RequestName == "WhoAmI", "ExecuteOutsideScope"); return;
                }
            case "Dispose": case "DisposeAsync": return;
            default:
                Scope.Require(name.StartsWith("get_", StringComparison.Ordinal)
                    && name is not "get_ServiceClient", "ClientMethodOutsideScope"); return;
        }
    }

    private static void CheckQuery(QueryBase query)
    {
        Scope.Require(query is QueryExpression, "QueryTypeOutsideScope");
        var expression = (QueryExpression)query;
        Scope.Require(ReadTables.Contains(expression.EntityName), "ReadTableOutsideScope");
        foreach (var link in expression.LinkEntities) CheckLink(link);
    }
    private static void CheckLink(LinkEntity link)
    {
        Scope.Require(ReadTables.Contains(link.LinkToEntityName), "ReadLinkOutsideScope");
        foreach (var child in link.LinkEntities) CheckLink(child);
    }

    public void CheckWrite(string operation, Entity entity)
    {
        Scope.Require(Phase != Phase.ReadOnly && Phase != Phase.Cleanup, "WritePhaseOutsideScope");
        var isCreate = operation == "create";
        Guid expectedId;
        switch (entity.LogicalName)
        {
            case "pluginpackage":
                Scope.Require(isCreate ? Phase == Phase.RegisterV1 : Phase is Phase.DeployV1 or Phase.DeployV2, "PackageOperationOutsideScope");
                expectedId = PackageId;
                Attributes(entity, isCreate ? ["name", "version", "content"] : ["content"]);
                if (isCreate)
                {
                    Value(entity, "name", Scope.Package); Value(entity, "version", "1.0.0");
                }
                var content = Convert.FromBase64String(entity.GetAttributeValue<string>("content"));
                Scope.Require(Scope.Hash(content) == (Phase == Phase.DeployV2 ? Scope.V2 : Scope.V1), "PackageBytesOutsideScope");
                break;
            case "plugintype":
                Scope.Require(isCreate && Phase == Phase.DeployV1 && AssemblyId != Guid.Empty && TypeId == Guid.Empty, "TypeOperationOutsideScope");
                Attributes(entity, ["pluginassemblyid", "typename", "friendlyname", "name"]);
                Reference(entity, "pluginassemblyid", "pluginassembly", AssemblyId);
                Value(entity, "typename", Scope.PluginType);
                Value(entity, "friendlyname", Scope.PluginType); Value(entity, "name", Scope.PluginType);
                expectedId = Guid.NewGuid(); TypeId = expectedId; TypeIds.Add(expectedId);
                break;
            case "sdkmessageprocessingstep":
                Scope.Require(isCreate ? Phase == Phase.DeployV1 : Phase == Phase.DeployV2, "StepOperationOutsideScope");
                Scope.Require(TypeId != Guid.Empty && MessageId != Guid.Empty && FilterId != Guid.Empty, "StepReferencesNotVerified");
                expectedId = StepId;
                Attributes(entity, ["name", "eventhandler", "sdkmessageid", "stage", "mode", "rank",
                    "supporteddeployment", "invocationsource", "sdkmessagefilterid"]);
                Value(entity, "name", Scope.Step);
                Reference(entity, "eventhandler", "plugintype", TypeId);
                Reference(entity, "sdkmessageid", "sdkmessage", MessageId);
                Reference(entity, "sdkmessagefilterid", "sdkmessagefilter", FilterId);
                Option(entity, "stage", 20); Option(entity, "mode", 0);
                Option(entity, "supporteddeployment", 0); Option(entity, "invocationsource", 0);
                Scope.Require(entity.GetAttributeValue<int>("rank") == 1, "RankOutsideScope");
                break;
            case "account":
                Scope.Require(isCreate && Phase is Phase.AccountV1 or Phase.AccountV2, "AccountOperationOutsideScope");
                expectedId = Phase == Phase.AccountV1 ? FirstAccountId : SecondAccountId;
                Attributes(entity, ["name"]);
                Value(entity, "name", Scope.Prefix + (Phase == Phase.AccountV1 ? "first" : "second"));
                break;
            default: throw new InvalidOperationException("WriteTableOutsideScope");
        }
        Scope.Require(isCreate ? entity.Id == Guid.Empty || entity.Id == expectedId : entity.Id == expectedId, "WriteIdOutsideScope");
        if (isCreate) entity.Id = expectedId;
        // Reserve the ID before sending. An ambiguous network outcome still has a bounded cleanup ID.
        var key = $"{Phase}:{operation}:{entity.LogicalName}";
        Scope.Require(writes.Add(key), "RepeatedWriteOutsideScope");
        Attempted.Add((entity.LogicalName, expectedId));
        record("write-preview-approved", new { phase = Phase.ToString(), operation, table = entity.LogicalName,
            id = expectedId, attributes = entity.Attributes.ToDictionary(x => x.Key,
                x => x.Key == "content" ? (object)Scope.Hash(Convert.FromBase64String((string)x.Value)) : SafeValue(x.Value)) });
    }

    public void CheckDelete(string table, Guid id)
    {
        Scope.Require(Phase == Phase.Cleanup, "DeletePhaseOutsideScope");
        var own = Attempted.Contains((table, id))
            || table == "plugintype" && TypeIds.Contains(id);
        Scope.Require(own && table is "account" or "pluginpackage" or "plugintype" or "sdkmessageprocessingstep",
            "DeleteIdOutsideScope");
        Scope.Require(Deleted.Add((table, id)), "RepeatedDeleteOutsideScope");
        writes.Add($"cleanup:{table}:{id}");
        record("delete-preview-approved", new { table, id });
    }
    private static object? SafeValue(object? value) => value switch
    {
        EntityReference reference => new { table = reference.LogicalName, reference.Id },
        OptionSetValue option => option.Value,
        _ => value
    };
    private static void Attributes(Entity entity, string[] expected)
    {
        var actual = entity.Attributes.Keys.Where(k => k != entity.LogicalName + "id").ToHashSet();
        Scope.Require(actual.SetEquals(expected), "AttributesOutsideScope");
        if (entity.Contains(entity.LogicalName + "id"))
            Scope.Require(entity.GetAttributeValue<Guid>(entity.LogicalName + "id") == entity.Id, "PrimaryKeyMismatch");
    }
    private static void Value(Entity entity, string key, string expected) =>
        Scope.Require(entity.GetAttributeValue<string>(key) == expected, "ValueOutsideScope");
    private static void Reference(Entity entity, string key, string table, Guid id)
    {
        var value = entity.GetAttributeValue<EntityReference>(key);
        Scope.Require(value is not null && value.LogicalName == table && value.Id == id, "ReferenceOutsideScope");
    }
    private static void Option(Entity entity, string key, int expected) =>
        Scope.Require(entity.GetAttributeValue<OptionSetValue>(key)?.Value == expected, "OptionOutsideScope");
}

public class GuardProxy : DispatchProxy
{
    internal object? Target;
    internal Boundary? Boundary;
    internal static T Wrap<T>(T target, Boundary boundary) where T : class
    {
        var proxy = Create<T, GuardProxy>();
        var state = (GuardProxy)(object)proxy;
        state.Target = target; state.Boundary = boundary;
        return proxy;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        var args = arguments ?? [];
        if (Target is null || Boundary is null) throw new InvalidOperationException("UnsupportedServiceCall");
        if (Target is IDataverseConnectionPool)
        {
            if (method!.Name == "GetClientAsync")
                return WrapClient((Task<IPooledClient>)Call(method, args)!, Boundary);
            Scope.Require(method.Name is "GetTotalRecommendedParallelism" or "Dispose" or "DisposeAsync",
                "PoolMethodOutsideScope");
        }
        else Boundary.CheckCall(method!.Name, args);
        return Call(method!, args);
    }
    private object? Call(MethodInfo method, object?[] args)
    {
        try { return method.Invoke(Target, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private static async Task<IPooledClient> WrapClient(Task<IPooledClient> task, Boundary boundary) =>
        Wrap(await task, boundary);
}