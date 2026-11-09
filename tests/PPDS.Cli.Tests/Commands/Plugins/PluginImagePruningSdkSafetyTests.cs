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
public sealed class PluginImagePruningSdkSafetyTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("assembly", "missing")]
    [InlineData("assembly", "null")]
    [InlineData("assembly", "wrong")]
    [InlineData("package", "missing")]
    [InlineData("package", "null")]
    [InlineData("package", "wrong")]
    [InlineData("type", "missing")]
    [InlineData("type", "null")]
    [InlineData("type", "wrong")]
    [Trait("AC", "AC-105")]
    public async Task MalformedManagedMetadata_NeverBecomesUnmanaged(string kind, string state)
    {
        using var f = new SdkFixture(kind) { ManagedState = state };
        var error = await Record.ExceptionAsync(() => f.Read()); f.VerifyReads(); Proof(f, error);
        AssertProtectionError(error); Assert.Equal(1, f.Requests);
    }

    [Theory]
    [InlineData("assembly", false)]
    [InlineData("assembly", true)]
    [InlineData("package", false)]
    [InlineData("package", true)]
    [InlineData("type", false)]
    [InlineData("type", true)]
    public async Task KnownManagedBoolean_IsPreserved(string kind, bool managed)
    {
        using var f = new SdkFixture(kind) { ManagedState = managed ? "true" : "false" };
        var result = await f.Read(); f.VerifyReads(); Proof(f, null);
        Assert.Equal(managed, result switch { PluginAssemblyInfo a => a.IsManaged, PluginPackageInfo p => p.IsManaged,
            PluginTypeInfo t => t.IsManaged, _ => throw new Xunit.Sdk.XunitException("Unexpected reader DTO") });
        Assert.Equal(1, f.Requests);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("wrong")]
    [Trait("AC", "AC-106")]
    public async Task MalformedAssemblyHiddenMetadata_FailsClosed(string state)
    {
        using var f = new SdkFixture("assembly") { HiddenState = state };
        var error = await Record.ExceptionAsync(() => f.Read()); f.VerifyReads(); Proof(f, error);
        AssertProtectionError(error); Assert.Equal(1, f.Requests);
    }

    [Fact]
    public async Task AssemblyReader_RequestsHiddenManagedProperty()
    {
        using var f = new SdkFixture("assembly"); await f.Read(); f.VerifyReads(); Proof(f, null);
        Assert.Contains("ishidden", f.LastQuery!.ColumnSet.Columns);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("AC", "AC-106")]
    public async Task AssemblyInventory_HonorsIncludeHiddenWithoutChangingDefault(bool? includeHidden)
    {
        using var f = new SdkFixture("assembly") { CatalogMode = true };
        var options = includeHidden is null ? null : new PluginListOptions(IncludeHidden: includeHidden.Value, IncludeMicrosoft: true);
        var rows = await f.Service.ListAssembliesAsync(options: options, cancellationToken: f.Token); f.VerifyReads(); Proof(f, null);
        var filters = f.LastQuery!.Criteria.Conditions.Where(c => c.AttributeName == "ishidden").ToArray();
        Assert.Equal(includeHidden == true ? 0 : 1, filters.Length);
        if (filters.Length != 0) { Assert.Equal(ConditionOperator.Equal, filters[0].Operator); Assert.Equal(false, filters[0].Values.Single()); }
        Assert.Equal(includeHidden == true ? 2 : 1, rows.Count);
    }

    [Theory]
    [InlineData("assembly", false)]
    [InlineData("assembly", true)]
    [InlineData("package", false)]
    [InlineData("package", true)]
    [InlineData("type", false)]
    [InlineData("type", true)]
    [Trait("AC", "AC-107")]
    public async Task ReaderSdkFaultAndCancellation_PreserveOriginalCauseAndToken(string kind, bool cancelled)
    {
        using var f = new SdkFixture(kind);
        Exception original = cancelled ? new OperationCanceledException("Synthetic cancellation", f.Token)
            : new InvalidOperationException(SdkFixture.Marker);
        f.Fault = original; var error = await Record.ExceptionAsync(() => f.Read()); f.VerifyReads(); Proof(f, error);
        Assert.Same(original, error); if (cancelled) Assert.Equal(f.Token, Assert.IsType<OperationCanceledException>(error).CancellationToken);
    }

    [Fact]
    public async Task HiddenAssembly_CannotBecomeAnEmptySuccessfulPlan()
    {
        using var f = new SdkFixture("assembly") { HiddenState = "true" }; var scope = new Scope(f, realAssemblyInventory: true);
        var error = await Record.ExceptionAsync(scope.Plan); f.VerifyReads(); Proof(f, error, scope.Deletes);
        Assert.IsType<PpdsException>(error); Assert.Equal(0, scope.Deletes);
    }

    [Fact]
    public async Task HiddenAssemblyRecheck_StopsBeforeAnyImageDelete()
    {
        using var f = new SdkFixture("assembly"); var scope = new Scope(f, realAssemblyInventory: true);
        var plan = await scope.Plan(); Assert.Single(plan.Images); f.HiddenState = "true";
        var error = await Record.ExceptionAsync(() => PluginImagePruningService.ApplyAsync(scope.Service.Object, plan, f.Token));
        f.VerifyReads(); Proof(f, error, scope.Deletes); Assert.IsType<PpdsException>(error); Assert.Equal(0, scope.Deletes);
    }

    [Theory]
    [InlineData("assembly", "missing")]
    [InlineData("assembly", "null")]
    [InlineData("package", "missing")]
    [InlineData("package", "null")]
    [InlineData("type", "missing")]
    [InlineData("type", "null")]
    [Trait("AC", "AC-105")]
    public async Task MissingManagedStateAtRecheck_StopsBeforeAnyDelete(string kind, string state)
    {
        using var f = new SdkFixture(kind); var scope = new Scope(f);
        var plan = await scope.Plan(); Assert.Single(plan.Images); f.ManagedState = state;
        var error = await Record.ExceptionAsync(() => PluginImagePruningService.ApplyAsync(scope.Service.Object, plan, f.Token));
        f.VerifyReads(); Proof(f, error, scope.Deletes); Assert.Equal(0, scope.Deletes); AssertProtectionError(error);
    }

    private static void AssertProtectionError(Exception? error)
    {
        var coded = Assert.IsType<PpdsException>(error); Assert.Equal(ErrorCodes.Validation.InvalidValue, coded.ErrorCode);
        Assert.Contains("protection metadata", coded.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SdkFixture.Marker, coded.UserMessage);
    }
    private void Proof(SdkFixture f, Exception? error, int deletes = 0) => output.WriteLine("PPDS_PRUNE_SDK_PROOF=" + JsonSerializer.Serialize(new
    { kind = f.Kind, managedState = f.ManagedState, hiddenState = f.HiddenState, requests = f.Requests, validatedQueries = f.ValidatedQueries,
        acquisitions = f.Acquisitions, disposals = f.Disposals, activeLeases = f.Active, deletes, errorType = error?.GetType().FullName,
        code = (error as PpdsException)?.ErrorCode, allQueriesValidated = f.Requests > 0 && f.Requests == f.ValidatedQueries }));

    private sealed class SdkFixture : IDisposable
    {
        internal const string Marker = "SYNTHETIC-UNTRUSTED-PROTECTION-DETAIL";
        private readonly CancellationTokenSource _cancel = new();
        private readonly Mock<IDataverseConnectionPool> _pool = new(MockBehavior.Strict);
        private readonly List<Mock<IPooledClient>> _leases = [];
        internal string Kind { get; }
        internal string ManagedState { get; set; } = "false";
        internal string HiddenState { get; set; } = "false";
        internal bool CatalogMode { get; set; }
        internal Exception? Fault { get; set; }
        internal CancellationToken Token => _cancel.Token;
        internal int Requests, ValidatedQueries, Acquisitions, Disposals, Active;
        internal QueryExpression? LastQuery;
        internal Guid AssemblyId { get; } = Guid.NewGuid();
        internal Guid PackageId { get; } = Guid.NewGuid();
        internal Guid TypeId { get; } = Guid.NewGuid();
        internal PluginRegistrationService Service { get; }
        internal SdkFixture(string kind)
        {
            Kind = kind;
            _pool.Setup(p => p.GetClientAsync(null, null, Token)).ReturnsAsync(() =>
            {
                Assert.Equal(0, Active); Active++; Acquisitions++; var calls = 0; var client = new Mock<IPooledClient>(MockBehavior.Strict); _leases.Add(client);
                client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), Token)).Returns<QueryBase, CancellationToken>((request, token) =>
                {
                    Requests++; calls++; Assert.Equal(1, calls); Assert.Equal(1, Active); Assert.Equal(Token, token);
                    var query = Assert.IsType<QueryExpression>(request); LastQuery = query;
                    var table = Kind switch { "assembly" => "pluginassembly", "package" => "pluginpackage", _ => "plugintype" };
                    Assert.Equal(table, query.EntityName); Assert.Contains("ismanaged", query.ColumnSet.Columns);
                    var id = Kind switch { "assembly" => AssemblyId, "package" => PackageId, _ => TypeId };
                    if (query.Criteria.Conditions.FirstOrDefault(c => c.AttributeName == table + "id") is { } identity)
                    { Assert.Equal(ConditionOperator.Equal, identity.Operator); Assert.Equal(id, identity.Values.Single()); }
                    else Assert.Equal("assembly", Kind);
                    ValidatedQueries++; if (Fault is not null) return Task.FromException<EntityCollection>(Fault);
                    var hiddenFilter = query.Criteria.Conditions.Any(c => c.AttributeName == "ishidden");
                    if (hiddenFilter && HiddenState == "true") return Task.FromResult(new EntityCollection());
                    var entities = new List<Entity> { Row(table, id) };
                    if (CatalogMode && !hiddenFilter) { var hidden = Row(table, Guid.NewGuid()); hidden["ishidden"] = new BooleanManagedProperty(true); entities.Add(hidden); }
                    return Task.FromResult(new EntityCollection(entities));
                });
                client.Setup(c => c.DisposeAsync()).Returns(() => { Assert.Equal(1, Active); Active--; Disposals++; return ValueTask.CompletedTask; });
                return client.Object;
            });
            Service = new PluginRegistrationService(_pool.Object, new InactiveFakeShakedownGuard(), NullLogger<PluginRegistrationService>.Instance);
        }
        private Entity Row(string table, Guid id)
        {
            var row = new Entity(table, id) { ["name"] = Kind == "package" ? "FixturePackage" : "Fixture" };
            if (Kind == "type") { row["typename"] = "Contoso.Handler"; row["pluginassemblyid"] = new EntityReference("pluginassembly", AssemblyId); }
            if (Kind == "assembly") row["packageid"] = new EntityReference("pluginpackage", PackageId);
            if (ManagedState != "missing") row["ismanaged"] = ManagedState switch { "null" => null, "wrong" => Marker, _ => ManagedState == "true" };
            if (HiddenState != "missing") row["ishidden"] = HiddenState switch { "null" => null, "wrong" => true, _ => new BooleanManagedProperty(HiddenState == "true") };
            return row;
        }
        internal async Task<object?> Read() => Kind switch { "assembly" => await Service.GetAssemblyByIdAsync(AssemblyId, Token),
            "package" => await Service.GetPackageByIdAsync(PackageId, Token), _ => await Service.GetPluginTypeByNameOrIdAsync(TypeId.ToString("D"), Token) };
        internal void VerifyReads()
        {
            Assert.True(Requests > 0); Assert.Equal(Requests, ValidatedQueries); Assert.Equal(Requests, Acquisitions); Assert.Equal(Requests, Disposals); Assert.Equal(0, Active);
            foreach (var client in _leases) { client.Verify(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), Token), Times.Once); client.Verify(c => c.DisposeAsync(), Times.Once); client.VerifyNoOtherCalls(); }
            _pool.Verify(p => p.GetClientAsync(null, null, Token), Times.Exactly(Acquisitions)); _pool.VerifyNoOtherCalls();
        }
        public void Dispose() => _cancel.Dispose();
    }

    private sealed class Scope
    {
        internal Mock<IPluginRegistrationService> Service { get; } = new(MockBehavior.Strict);
        internal int Deletes;
        private readonly SdkFixture _sdk;
        private readonly PluginAssemblyConfig _config;
        internal Scope(SdkFixture sdk, bool realAssemblyInventory = false)
        {
            _sdk = sdk;
            var assembly = new PluginAssemblyInfo { Id = sdk.AssemblyId, Name = "Fixture", PackageId = sdk.PackageId, IsManaged = false };
            var package = new PluginPackageInfo { Id = sdk.PackageId, Name = "FixturePackage", IsManaged = false };
            var type = new PluginTypeInfo { Id = sdk.TypeId, TypeName = "Contoso.Handler", AssemblyId = sdk.AssemblyId, IsManaged = false };
            var step = new PluginStepInfo { Id = Guid.NewGuid(), PluginTypeId = type.Id, Name = "Update", Message = "Update", PrimaryEntity = "account", Stage = "PostOperation", Mode = "Synchronous" };
            var image = new PluginImageInfo { Id = Guid.NewGuid(), Name = "Extra", ImageType = "PreImage", StepId = step.Id };
            _config = new() { Name = assembly.Name, Type = "Nuget", OperationIntent = RegistrationOperationIntent.Reconcile,
                Types = [new() { TypeName = type.TypeName, Steps = [new() { Message = step.Message, Entity = step.PrimaryEntity, Stage = step.Stage, Images = [] }] }] };
            Service.Setup(s => s.ListPackagesAsync(package.Name, It.IsAny<PluginListOptions?>(), sdk.Token)).ReturnsAsync([package]);
            Service.Setup(s => s.GetPackageByIdAsync(package.Id, sdk.Token)).Returns(async () => sdk.Kind == "package" ? (PluginPackageInfo?)await sdk.Read() : package);
            Service.Setup(s => s.ListAssembliesAsync(assembly.Name, It.IsAny<PluginListOptions?>(), sdk.Token)).Returns((string? name, PluginListOptions? options, CancellationToken token) => realAssemblyInventory
                ? sdk.Service.ListAssembliesAsync(name, options, token) : Task.FromResult(new List<PluginAssemblyInfo> { assembly }));
            Service.Setup(s => s.GetAssemblyByIdAsync(assembly.Id, sdk.Token)).Returns(async () => sdk.Kind == "assembly" ? (PluginAssemblyInfo?)await sdk.Read() : assembly);
            Service.Setup(s => s.ListTypesForAssemblyAsync(assembly.Id, sdk.Token)).ReturnsAsync([type]);
            Service.Setup(s => s.GetPluginTypeByNameOrIdAsync(type.Id.ToString("D"), sdk.Token)).Returns(async () => sdk.Kind == "type" ? (PluginTypeInfo?)await sdk.Read() : type);
            Service.Setup(s => s.ListStepsForTypeAsync(type.Id, It.IsAny<PluginListOptions?>(), sdk.Token)).ReturnsAsync([step]);
            Service.Setup(s => s.GetStepByNameOrIdAsync(step.Id.ToString("D"), sdk.Token)).ReturnsAsync(step);
            Service.Setup(s => s.ListImagesForStepAsync(step.Id, sdk.Token)).ReturnsAsync([image]);
            Service.Setup(s => s.GetImageByNameOrIdAsync(image.Id.ToString("D"), sdk.Token)).ReturnsAsync(image);
            Service.Setup(s => s.DeleteImageAsync(image.Id, sdk.Token)).Callback(() => Deletes++).Returns(Task.CompletedTask);
        }
        internal Task<PluginImagePruningPlan> Plan() => PluginImagePruningService.PlanAsync(Service.Object, [_config], new Dictionary<string, string> { [_config.Name] = "FixturePackage" }, _sdk.Token);
    }
}
