using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services.Plugins;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Pooling;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.Cli.Tests.Commands.Plugins;

[Trait("Category", "Unit")]
public sealed class PluginImagePruningNullProtectionTests(ITestOutputHelper output)
{
    private static readonly (string Kind, string Field)[] Fields =
        [("step", "ismanaged"), ("step", "ishidden"), ("step", "iscustomizable"), ("image", "ismanaged"), ("image", "iscustomizable")];
    public static IEnumerable<object[]> ReaderCases => Fields.SelectMany(f => new[] { "missing", "null", "wrong", "safe", "protected" }
        .Select(state => new object[] { f.Kind, f.Field, state }));
    public static IEnumerable<object[]> InitialCases => Fields.SelectMany(f => new[] { "missing", "null" }
        .Select(state => new object[] { f.Kind, f.Field, state }));
    public static IEnumerable<object[]> FreshCases => Fields.SelectMany(f => new[] { "first-recheck", "final-recheck" }
        .Select(phase => new object[] { f.Kind, f.Field, phase }));

    [Theory, MemberData(nameof(ReaderCases)), Trait("AC", "AC-108")]
    public async Task ActualReader_TracksProtectionAvailabilityAndTypedValue(string kind, string field, string state)
    {
        using var f = new Fixture(kind, field, state);
        object? value = null; var error = await Record.ExceptionAsync(async () => value = await f.Read());
        f.VerifyReads(); Proof(nameof(ActualReader_TracksProtectionAvailabilityAndTypedValue), f, error);
        Assert.Equal(1, f.Reads);
        if (state == "wrong") { Assert.IsType<InvalidCastException>(error); return; }
        Assert.Null(error);
        var unavailable = value switch { PluginStepInfo s => s.UnavailableProperties, PluginImageInfo i => i.UnavailableProperties,
            _ => throw new Xunit.Sdk.XunitException("Selected actual reader did not return its DTO") };
        var property = field switch { "ismanaged" => "isManaged", "ishidden" => "isHidden", _ => "isCustomizable" };
        Assert.Equal(state is "missing" or "null", unavailable.Contains(property));
        if (state is "safe" or "protected")
        {
            var flag = value switch { PluginStepInfo s => field switch { "ismanaged" => s.IsManaged, "ishidden" => s.IsHidden, _ => s.IsCustomizable },
                PluginImageInfo i => field == "ismanaged" ? i.IsManaged : i.IsCustomizable, _ => false };
            Assert.Equal(field == "iscustomizable" ? state == "safe" : state == "protected", flag);
        }
    }

    [Theory, MemberData(nameof(InitialCases)), Trait("AC", "AC-108")]
    public async Task Plan_RejectsUnavailableActualProtectionBeforeAnyDelete(string kind, string field, string state)
    {
        using var f = new Fixture(kind, field, state); var scope = new Scope(f);
        var error = await Record.ExceptionAsync(scope.Plan); f.VerifyReads();
        Proof(nameof(Plan_RejectsUnavailableActualProtectionBeforeAnyDelete), f, error, scope.Deletes);
        Assert.Equal(1, f.Reads); Assert.Equal(0, scope.Deletes); AssertIncomplete(error);
    }

    [Theory, MemberData(nameof(FreshCases)), Trait("AC", "AC-108")]
    public async Task Apply_RejectsNewNullProtectionAtActualFreshRead(string kind, string field, string phase)
    {
        using var f = new Fixture(kind, field, "safe"); var scope = new Scope(f);
        var plan = await scope.Plan(); Assert.Single(plan.Images); Assert.Equal(1, f.Reads);
        f.State = "null"; f.CorruptReads = 0; f.CorruptAt = phase == "first-recheck" ? 2 : kind == "step" ? 4 : 3;
        var error = await Record.ExceptionAsync(() => PluginImagePruningService.ApplyAsync(scope.Service.Object, plan, f.Token));
        f.VerifyReads(); Proof(nameof(Apply_RejectsNewNullProtectionAtActualFreshRead), f, error, scope.Deletes, phase, true);
        Assert.Equal(f.CorruptAt, f.Reads); Assert.Equal(1, f.CorruptReads); Assert.Equal(0, scope.Deletes); AssertIncomplete(error);
    }

    [Theory]
    [InlineData("assembly-by-id")]
    [InlineData("package-by-id")]
    [InlineData("type-by-id")]
    [InlineData("assembly-list-name")]
    public async Task ParentSdkQuery_UsesExactDeclaredScope(string role)
    {
        var kind = role.Split('-')[0]; using var f = new Fixture(kind, "ismanaged", "safe") { ListMode = role == "assembly-list-name" };
        var value = await f.Read(); f.VerifyReads(); Proof(nameof(ParentSdkQuery_UsesExactDeclaredScope), f, null, phase: role);
        Assert.NotNull(value); Assert.Equal(1, f.Reads);
        if (f.ListMode) Assert.Single(Assert.IsType<List<PluginAssemblyInfo>>(value));
    }

    private static void AssertIncomplete(Exception? error)
    {
        var coded = Assert.IsType<PpdsException>(error); Assert.Equal(ErrorCodes.Validation.InvalidValue, coded.ErrorCode);
        Assert.Contains("incomplete", coded.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Fixture.Marker, coded.UserMessage);
    }
    private void Proof(string method, Fixture f, Exception? error, int deletes = 0, string phase = "reader", bool planProved = false) =>
        output.WriteLine("PPDS_PRUNE_NULL_PROOF=" + JsonSerializer.Serialize(new { method, kind = f.Kind, field = f.Field, state = f.State,
            phase, corruptAt = f.CorruptAt, corruptReads = f.CorruptReads, reads = f.Reads, validatedQueries = f.ValidatedQueries,
            acquisitions = f.Acquisitions, disposals = f.Disposals, activeLeases = f.Active, deletes, planProved,
            errorType = error?.GetType().FullName, code = (error as PpdsException)?.ErrorCode, queryScopes = f.QueryScopes }));

    private sealed class Fixture : IDisposable
    {
        internal const string Marker = "SYNTHETIC-WRONG-PROTECTION-TYPE";
        private readonly CancellationTokenSource _cancel = new();
        private readonly Mock<IDataverseConnectionPool> _pool = new(MockBehavior.Strict);
        private readonly List<Mock<IPooledClient>> _leases = [];
        internal string Kind { get; }
        internal string Field { get; }
        internal string State { get; set; }
        internal bool ListMode { get; set; }
        internal int CorruptAt = 1, Reads, CorruptReads, ValidatedQueries, Acquisitions, Disposals, Active;
        internal List<object> QueryScopes { get; } = [];
        internal Guid AssemblyId { get; } = Guid.NewGuid();
        internal Guid PackageId { get; } = Guid.NewGuid();
        internal Guid TypeId { get; } = Guid.NewGuid();
        internal Guid StepId { get; } = Guid.NewGuid();
        internal Guid ImageId { get; } = Guid.NewGuid();
        internal CancellationToken Token => _cancel.Token;
        internal PluginRegistrationService Service { get; }
        internal Fixture(string kind, string field, string state)
        {
            Kind = kind; Field = field; State = state;
            _pool.Setup(p => p.GetClientAsync(null, null, Token)).ReturnsAsync(() =>
            {
                Assert.Equal(0, Active); Active++; Acquisitions++; var calls = 0;
                var client = new Mock<IPooledClient>(MockBehavior.Strict); _leases.Add(client);
                client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), Token)).Returns<QueryBase, CancellationToken>((request, token) =>
                {
                    Reads++; calls++; Assert.Equal(1, calls); Assert.Equal(1, Active); Assert.Equal(Token, token);
                    var q = Assert.IsType<QueryExpression>(request); var table = Table;
                    Assert.Equal(table, q.EntityName); Assert.False(q.ColumnSet.AllColumns); Assert.Contains("ismanaged", q.ColumnSet.Columns);
                    Assert.Empty(q.Criteria.Filters); var condition = Assert.Single(q.Criteria.Conditions);
                    Assert.Equal(ConditionOperator.Equal, condition.Operator);
                    Assert.Equal(ListMode ? "name" : table + "id", condition.AttributeName);
                    Assert.Equal(ListMode ? (object)"Fixture" : Id, Assert.Single(condition.Values));
                    if (Kind == "step")
                    {
                        foreach (var column in new[] { "stage", "mode", "ishidden", "iscustomizable", "eventhandler" }) Assert.Contains(column, q.ColumnSet.Columns);
                        Assert.Contains(q.LinkEntities, link => link.EntityAlias == "message" && link.LinkToEntityName == "sdkmessage" && link.Columns.Columns.Contains("name"));
                    }
                    if (Kind == "image") foreach (var column in new[] { "name", "imagetype", "iscustomizable", "sdkmessageprocessingstepid" }) Assert.Contains(column, q.ColumnSet.Columns);
                    if (Kind == "assembly" && !ListMode) Assert.Contains("ishidden", q.ColumnSet.Columns);
                    if (Kind == "type") Assert.Contains("pluginassemblyid", q.ColumnSet.Columns);
                    ValidatedQueries++;
                    QueryScopes.Add(new { table, key = condition.AttributeName, value = condition.Values.Single().ToString(), ordinal = Reads, tokenValidated = true });
                    var row = Row();
                    if (Kind is "step" or "image" && Reads == CorruptAt)
                    {
                        CorruptReads++;
                        if (State == "missing") row.Attributes.Remove(Field);
                        else if (State == "null") row[Field] = null;
                        else if (State == "wrong") row[Field] = Marker;
                        else row[Field] = TypedFlag(Field == "iscustomizable" ? State == "safe" : State == "protected");
                    }
                    return Task.FromResult(new EntityCollection([row]));
                });
                client.Setup(c => c.DisposeAsync()).Returns(() => { Assert.Equal(1, Active); Active--; Disposals++; return ValueTask.CompletedTask; });
                return client.Object;
            });
            Service = new PluginRegistrationService(_pool.Object, new InactiveFakeShakedownGuard(), NullLogger<PluginRegistrationService>.Instance);
        }
        private string Table => Kind switch { "step" => "sdkmessageprocessingstep", "image" => "sdkmessageprocessingstepimage", _ => "plugin" + Kind };
        private Guid Id => Kind switch { "step" => StepId, "image" => ImageId, "assembly" => AssemblyId, "package" => PackageId, _ => TypeId };
        private object TypedFlag(bool value) => Field == "ismanaged" ? value : new BooleanManagedProperty(value);
        private Entity Row()
        {
            var e = new Entity(Table, Id) { ["name"] = Kind == "package" ? "FixturePackage" : Kind == "image" ? "Extra" : "Fixture", ["ismanaged"] = false };
            if (Kind == "assembly") { e["packageid"] = new EntityReference("pluginpackage", PackageId); e["ishidden"] = new BooleanManagedProperty(false); }
            if (Kind == "type") { e["typename"] = "Contoso.Handler"; e["pluginassemblyid"] = new EntityReference("pluginassembly", AssemblyId); }
            if (Kind == "step")
            {
                e["message.name"] = new AliasedValue("sdkmessage", "name", "Update");
                e["filter.primaryobjecttypecode"] = new AliasedValue("sdkmessagefilter", "primaryobjecttypecode", "account");
                e["stage"] = new OptionSetValue(40); e["mode"] = new OptionSetValue(0); e["rank"] = 1;
                e["eventhandler"] = new EntityReference("plugintype", TypeId); e["ishidden"] = new BooleanManagedProperty(false);
            }
            if (Kind == "image") { e["imagetype"] = new OptionSetValue(0); e["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", StepId); }
            if (Kind is "step" or "image") e["iscustomizable"] = new BooleanManagedProperty(true);
            return e;
        }
        internal async Task<object?> Read() => Kind switch
        {
            "step" => await Service.GetStepByNameOrIdAsync(StepId.ToString("D"), Token),
            "image" => await Service.GetImageByNameOrIdAsync(ImageId.ToString("D"), Token),
            "assembly" when ListMode => await Service.ListAssembliesAsync("Fixture", new PluginListOptions(IncludeHidden: true, IncludeMicrosoft: true), Token),
            "assembly" => await Service.GetAssemblyByIdAsync(AssemblyId, Token),
            "package" => await Service.GetPackageByIdAsync(PackageId, Token),
            _ => await Service.GetPluginTypeByNameOrIdAsync(TypeId.ToString("D"), Token)
        };
        internal void VerifyReads()
        {
            Assert.True(Reads > 0); Assert.Equal(Reads, ValidatedQueries); Assert.Equal(Reads, Acquisitions); Assert.Equal(Reads, Disposals); Assert.Equal(0, Active);
            foreach (var client in _leases) { client.Verify(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), Token), Times.Once); client.Verify(c => c.DisposeAsync(), Times.Once); client.VerifyNoOtherCalls(); }
            _pool.Verify(p => p.GetClientAsync(null, null, Token), Times.Exactly(Acquisitions)); _pool.VerifyNoOtherCalls();
        }
        public void Dispose() => _cancel.Dispose();
    }

    private sealed class Scope
    {
        internal Mock<IPluginRegistrationService> Service { get; } = new(MockBehavior.Strict);
        internal int Deletes;
        private readonly Fixture _f;
        private readonly PluginAssemblyConfig _config;
        internal Scope(Fixture f)
        {
            _f = f;
            var assembly = new PluginAssemblyInfo { Id = f.AssemblyId, Name = "Fixture", PackageId = f.PackageId, IsManaged = false };
            var package = new PluginPackageInfo { Id = f.PackageId, Name = "FixturePackage", IsManaged = false };
            var type = new PluginTypeInfo { Id = f.TypeId, TypeName = "Contoso.Handler", AssemblyId = f.AssemblyId, IsManaged = false };
            var step = new PluginStepInfo { Id = f.StepId, PluginTypeId = f.TypeId, Name = "Fixture", Message = "Update", PrimaryEntity = "account", Stage = "PostOperation", Mode = "Synchronous" };
            var image = new PluginImageInfo { Id = f.ImageId, Name = "Extra", ImageType = "PreImage", StepId = f.StepId };
            _config = new() { Name = assembly.Name, Type = "Nuget", OperationIntent = RegistrationOperationIntent.Reconcile,
                Types = [new() { TypeName = type.TypeName, Steps = [new() { Message = step.Message, Entity = step.PrimaryEntity, Stage = step.Stage, Images = [] }] }] };
            Service.Setup(s => s.ListPackagesAsync(package.Name, It.IsAny<PluginListOptions?>(), f.Token)).ReturnsAsync([package]);
            Service.Setup(s => s.ValidatePackageResolutionAsync(It.IsAny<PluginPackageResolution>(), f.Token, null)).Returns(Task.CompletedTask);
            Service.Setup(s => s.GetPackageByIdAsync(package.Id, f.Token)).ReturnsAsync(package);
            Service.Setup(s => s.GetAssemblyIdForPackageAsync(package.Id, assembly.Name, f.Token)).ReturnsAsync(assembly.Id);
            Service.Setup(s => s.ListAssembliesAsync(assembly.Name, It.IsAny<PluginListOptions?>(), f.Token)).ReturnsAsync([assembly]);
            Service.Setup(s => s.GetAssemblyByIdAsync(assembly.Id, f.Token)).ReturnsAsync(assembly);
            Service.Setup(s => s.ListTypesForAssemblyAsync(f.AssemblyId, f.Token)).ReturnsAsync([type]);
            Service.Setup(s => s.GetPluginTypeByNameOrIdAsync(type.Id.ToString("D"), f.Token)).ReturnsAsync(type);
            Service.Setup(s => s.ListStepsForTypeAsync(type.Id, It.IsAny<PluginListOptions?>(), f.Token)).ReturnsAsync([step]);
            Service.Setup(s => s.GetStepByNameOrIdAsync(step.Id.ToString("D"), f.Token)).Returns(async () => f.Kind == "step" ? (PluginStepInfo?)await f.Read() : step);
            Service.Setup(s => s.ListImagesForStepAsync(step.Id, f.Token)).ReturnsAsync([image]);
            Service.Setup(s => s.GetImageByNameOrIdAsync(image.Id.ToString("D"), f.Token)).Returns(async () => f.Kind == "image" ? (PluginImageInfo?)await f.Read() : image);
            Service.Setup(s => s.DeleteImageAsync(image.Id, f.Token)).Callback(() => Deletes++).Returns(Task.CompletedTask);
        }
        internal Task<PluginImagePruningPlan> Plan() => PluginImagePruningService.PlanAsync(Service.Object, [_config],
            new Dictionary<string, PluginPackageResolution>
            { [_config.Name] = PluginPackageResolutionTestFactory.Create("FixturePackage", _f.PackageId) }, _f.Token);
    }
}
