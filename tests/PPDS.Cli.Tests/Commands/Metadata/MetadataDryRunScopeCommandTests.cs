using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Metadata;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.TestHelpers;
using PPDS.Dataverse.Metadata.Authoring;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.Cli.Tests.Commands.Metadata;

[Trait("Category", "Unit")]
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class MetadataDryRunScopeCommandTests(ITestOutputHelper output)
{
    // All existing non-delete dry-run command routes, including deprecated delegating aliases.
    public static IEnumerable<object[]> Routes()
    {
        var cases = new (string Route, string Method, string Scope, string Extra)[]
        {
            ("entity create", "CreateTableAsync", "input-and-publisher", "--name new_table --display-name Table --plural-name Tables --ownership UserOwned"),
            ("table create", "CreateTableAsync", "input-and-publisher", "--name new_table --display-name Table --plural-name Tables --ownership UserOwned"),
            ("entity update", "UpdateTableAsync", "required-inputs", "--entity new_table --display-name Renamed"),
            ("table update", "UpdateTableAsync", "required-inputs", "--entity new_table --display-name Renamed"),
            ("attribute create", "CreateColumnAsync", "input-and-publisher", "--entity new_table --name new_column --display-name Column --type String"),
            ("column create", "CreateColumnAsync", "input-and-publisher", "--entity new_table --name new_column --display-name Column --type String"),
            ("attribute update", "UpdateColumnAsync", "required-inputs", "--entity new_table --column new_column --display-name Renamed"),
            ("column update", "UpdateColumnAsync", "required-inputs", "--entity new_table --column new_column --display-name Renamed"),
            ("relationship create", "CreateOneToManyAsync", "input-and-publisher", "--from new_table --to new_child --type one-to-many --name new_rel --lookup-name new_parent --lookup-display-name Parent"),
            ("relationship create", "CreateManyToManyAsync", "input-and-publisher", "--from new_table --to new_child --type many-to-many --name new_rel"),
            ("relationship update", "UpdateRelationshipAsync", "required-inputs", "--name new_rel --cascade-delete Restrict"),
            ("optionset create", "CreateGlobalChoiceAsync", "input-and-publisher", "--name new_choice --display-name Choice --options Label=17"),
            ("choice create", "CreateGlobalChoiceAsync", "input-and-publisher", "--name new_choice --display-name Choice --options Label=17"),
            ("optionset update", "UpdateGlobalChoiceAsync", "required-inputs", "--name new_choice --display-name Renamed"),
            ("choice update", "UpdateGlobalChoiceAsync", "required-inputs", "--name new_choice --display-name Renamed"),
            ("optionset add-option", "AddOptionValueAsync", "required-inputs", "--name new_choice --label Label"),
            ("optionset update-option", "UpdateOptionValueAsync", "option-existence", "--name new_choice --value 17 --new-label Renamed"),
            ("optionset remove-option", "DeleteOptionValueAsync", "option-existence", "--name new_choice --value 17"),
            ("key create", "CreateKeyAsync", "input-and-publisher", "--entity new_table --name new_key --display-name Key --attributes new_column"),
            ("entity add-statusreason", "AddStatusReasonAsync", "option-data-and-draft-value", "--entity new_table --label Label --state-code 0"),
            ("attribute add-option", "AddColumnOptionAsync", "option-data-and-draft-value", "--entity new_table --column new_column --option Label"),
            ("attribute update-option", "UpdateColumnOptionAsync", "option-existence", "--entity new_table --column new_column --value 17 --new-label Renamed"),
            ("attribute remove-option", "RemoveColumnOptionAsync", "option-existence", "--entity new_table --column new_column --value 17")
        };
        foreach (var c in cases)
            foreach (var json in new[] { false, true }) yield return [c.Route, c.Method, c.Scope, c.Extra, json];
    }

    public static IEnumerable<object[]> HelpRoutes() => Routes()
        .DistinctBy(row => (string)row[0])
        .Select(row => new object[] { row[0], row[2] });

    [Theory]
    [MemberData(nameof(HelpRoutes))]
    [Trait("AC", "AC-122")]
    public void DryRunHelp_StatesCompletedChecksAndUncheckedConstraints(string route, string scope)
    {
        var command = MetadataCommandGroup.Create(_ => throw new InvalidOperationException("Help must not initialize a connection."));
        foreach (var segment in route.Split(' '))
            command = command.Subcommands.Single(child => child.Name == segment);
        var help = command.Options.Single(option => option.Name == "--dry-run").Description!;

        Assert.Contains("dependencies", help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(route.EndsWith("remove-option", StringComparison.Ordinal) ? "deletion feasibility" : "server feasibility", help);
        Assert.Contains("not checked", help);
        Assert.Contains("No changes persisted", help);
        Assert.DoesNotContain("Validation passed", help);
        switch (scope)
        {
            case "input-and-publisher":
                Assert.Contains("inputs and solution publisher information only", help);
                Assert.Contains("Target existence", help);
                break;
            case "required-inputs":
                Assert.Contains("required inputs only", help);
                Assert.Contains("Target existence", help);
                Assert.DoesNotContain("publisher", help);
                if (route == "optionset add-option")
                    Assert.Contains("Dataverse assigns the final value only when changes are applied", help);
                break;
            case "option-existence":
                Assert.Contains("target option existence only", help);
                break;
            case "option-data-and-draft-value":
                Assert.Contains("Read option metadata", help);
                Assert.Contains("provisional", help);
                break;
            default:
                throw new InvalidOperationException($"Unrecognized preview scope: {scope}");
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    [Trait("AC", "AC-122")]
    public async Task RealHandlers_ReportOnlyCompletedChecksAndPreserveData(string route, string method, string scope, string extra, bool json)
    {
        var service = new Mock<IMetadataAuthoringService>(MockBehavior.Strict);
        CancellationToken actualToken = default;
        var providerCalls = 0; var serviceCalls = 0; var bound = false; var preconditions = false;
        Task<T> Reply<T>(object request, CancellationToken token, T result)
        {
            Observe(request, token); return Task.FromResult(result);
        }
        Task Done(object request, CancellationToken token) { Observe(request, token); return Task.CompletedTask; }
        void Observe(object request, CancellationToken token)
        {
            Assert.True(bound); Assert.Equal(actualToken, token); serviceCalls++;
            Assert.True(Assert.IsType<bool>(request.GetType().GetProperty("DryRun")!.GetValue(request)));
        }
        service.Setup(s => s.CreateTableAsync(It.IsAny<CreateTableRequest>(), It.IsAny<IMetadataAuthoringProgressReporter?>(), It.IsAny<CancellationToken>()))
            .Returns((CreateTableRequest r, IMetadataAuthoringProgressReporter? _, CancellationToken t) => Reply(r, t, new CreateTableResult { LogicalName = "new_table", WasDryRun = true, ValidationMessages = [] }));
        service.Setup(s => s.UpdateTableAsync(It.IsAny<UpdateTableRequest>(), It.IsAny<IMetadataAuthoringProgressReporter?>(), It.IsAny<CancellationToken>()))
            .Returns((UpdateTableRequest r, IMetadataAuthoringProgressReporter? _, CancellationToken t) => Done(r, t));
        service.Setup(s => s.CreateColumnAsync(It.IsAny<CreateColumnRequest>(), It.IsAny<IMetadataAuthoringProgressReporter?>(), It.IsAny<CancellationToken>()))
            .Returns((CreateColumnRequest r, IMetadataAuthoringProgressReporter? _, CancellationToken t) => Reply(r, t, new CreateColumnResult { LogicalName = "new_column", WasDryRun = true, ValidationMessages = [] }));
        service.Setup(s => s.UpdateColumnAsync(It.IsAny<UpdateColumnRequest>(), It.IsAny<IMetadataAuthoringProgressReporter?>(), It.IsAny<CancellationToken>()))
            .Returns((UpdateColumnRequest r, IMetadataAuthoringProgressReporter? _, CancellationToken t) => Done(r, t));
        service.Setup(s => s.CreateOneToManyAsync(It.IsAny<CreateOneToManyRequest>(), It.IsAny<IMetadataAuthoringProgressReporter?>(), It.IsAny<CancellationToken>()))
            .Returns((CreateOneToManyRequest r, IMetadataAuthoringProgressReporter? _, CancellationToken t) => Reply(r, t, new CreateRelationshipResult { SchemaName = "new_rel", WasDryRun = true, ValidationMessages = [] }));
        service.Setup(s => s.CreateManyToManyAsync(It.IsAny<CreateManyToManyRequest>(), It.IsAny<IMetadataAuthoringProgressReporter?>(), It.IsAny<CancellationToken>()))
            .Returns((CreateManyToManyRequest r, IMetadataAuthoringProgressReporter? _, CancellationToken t) => Reply(r, t, new CreateRelationshipResult { SchemaName = "new_rel", WasDryRun = true, ValidationMessages = [] }));
        service.Setup(s => s.UpdateRelationshipAsync(It.IsAny<UpdateRelationshipRequest>(), It.IsAny<IMetadataAuthoringProgressReporter?>(), It.IsAny<CancellationToken>()))
            .Returns((UpdateRelationshipRequest r, IMetadataAuthoringProgressReporter? _, CancellationToken t) => Done(r, t));
        service.Setup(s => s.CreateGlobalChoiceAsync(It.IsAny<CreateGlobalChoiceRequest>(), It.IsAny<IMetadataAuthoringProgressReporter?>(), It.IsAny<CancellationToken>()))
            .Returns((CreateGlobalChoiceRequest r, IMetadataAuthoringProgressReporter? _, CancellationToken t) => Reply(r, t, new CreateChoiceResult { Name = "new_choice", WasDryRun = true, ValidationMessages = [] }));
        service.Setup(s => s.UpdateGlobalChoiceAsync(It.IsAny<UpdateGlobalChoiceRequest>(), It.IsAny<IMetadataAuthoringProgressReporter?>(), It.IsAny<CancellationToken>()))
            .Returns((UpdateGlobalChoiceRequest r, IMetadataAuthoringProgressReporter? _, CancellationToken t) => Done(r, t));
        service.Setup(s => s.AddOptionValueAsync(It.IsAny<AddOptionValueRequest>(), It.IsAny<CancellationToken>()))
            .Returns((AddOptionValueRequest r, CancellationToken t) => Reply(r, t, 0));
        service.Setup(s => s.UpdateOptionValueAsync(It.IsAny<UpdateOptionValueRequest>(), It.IsAny<CancellationToken>()))
            .Returns((UpdateOptionValueRequest r, CancellationToken t) => Done(r, t));
        service.Setup(s => s.DeleteOptionValueAsync(It.IsAny<DeleteOptionValueRequest>(), It.IsAny<CancellationToken>()))
            .Returns((DeleteOptionValueRequest r, CancellationToken t) => Done(r, t));
        service.Setup(s => s.CreateKeyAsync(It.IsAny<CreateKeyRequest>(), It.IsAny<IMetadataAuthoringProgressReporter?>(), It.IsAny<CancellationToken>()))
            .Returns((CreateKeyRequest r, IMetadataAuthoringProgressReporter? _, CancellationToken t) => Reply(r, t, new CreateKeyResult { SchemaName = "new_key", WasDryRun = true, ValidationMessages = [] }));
        service.Setup(s => s.AddStatusReasonAsync(It.IsAny<AddStatusReasonRequest>(), It.IsAny<IMetadataAuthoringProgressReporter?>(), It.IsAny<CancellationToken>()))
            .Returns((AddStatusReasonRequest r, IMetadataAuthoringProgressReporter? _, CancellationToken t) => Reply(r, t, 1000001));
        service.Setup(s => s.AddColumnOptionAsync(It.IsAny<AddColumnOptionRequest>(), It.IsAny<CancellationToken>()))
            .Returns((AddColumnOptionRequest r, CancellationToken t) => Reply(r, t, 1000001));
        service.Setup(s => s.UpdateColumnOptionAsync(It.IsAny<UpdateColumnOptionRequest>(), It.IsAny<CancellationToken>()))
            .Returns((UpdateColumnOptionRequest r, CancellationToken t) => Done(r, t));
        service.Setup(s => s.RemoveColumnOptionAsync(It.IsAny<RemoveColumnOptionRequest>(), It.IsAny<CancellationToken>()))
            .Returns((RemoveColumnOptionRequest r, CancellationToken t) => Done(r, t));

        Task<ServiceProvider> Provider(CancellationToken token)
        {
            Assert.False(bound); bound = true; actualToken = token; providerCalls++;
            return Task.FromResult(new ServiceCollection().AddSingleton(service.Object)
                .AddSingleton(new ResolvedConnectionInfo { Profile = new AuthProfile { Name = "synthetic-only" }, EnvironmentUrl = "https://fixture.example.test" })
                .BuildServiceProvider());
        }
        var args = (route + " --solution SyntheticSolution " + extra + " --dry-run" + (json ? " --output-format json" : "")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        using var stdout = new StringWriter(); using var stderr = new StringWriter(); using var cts = new CancellationTokenSource();
        var oldOut = Console.Out; var oldError = Console.Error; int? exit = null;
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            var parsed = MetadataCommandGroup.Create(Provider).Parse(args); Assert.Empty(parsed.Errors);
            exit = await parsed.InvokeAsync(cancellationToken: cts.Token);
            Assert.Equal(1, providerCalls); Assert.Equal(1, serviceCalls);
            Assert.Equal(method, Assert.Single(service.Invocations).Method.Name); preconditions = true;
            Assert.Equal(0, exit);
            if (json)
            {
                using var doc = JsonDocument.Parse(stdout.ToString()); var root = doc.RootElement;
                Assert.True(root.GetProperty("success").GetBoolean()); Assert.Equal("1.0", root.GetProperty("version").GetString());
                var data = root.GetProperty("data"); Assert.Equal(JsonValueKind.Object, data.ValueKind);
                var preview = root.TryGetProperty("metadata", out var meta) ? meta.GetProperty("preview") : data.GetProperty("preview");
                Assert.Equal(scope, preview.GetProperty("validationScope").GetString());
                Assert.False(preview.GetProperty("dependenciesChecked").GetBoolean());
                if (scope == "option-existence" && !root.TryGetProperty("metadata", out _))
                    Assert.False(preview.GetProperty("deletionFeasibilityChecked").GetBoolean());
                else
                {
                    Assert.False(preview.GetProperty("serverFeasibilityChecked").GetBoolean());
                    Assert.False(preview.GetProperty("changesPersisted").GetBoolean());
                    if (method == "AddOptionValueAsync") { Assert.False(data.TryGetProperty("value", out _)); Assert.Contains("SDK assignment", preview.GetProperty("optionValueAllocation").GetString()); }
                    if (scope == "option-data-and-draft-value") { Assert.Equal(1000001, data.GetProperty("value").GetInt32()); Assert.Equal("provisional", preview.GetProperty("optionValueAllocation").GetString()); }
                }
                if (method.StartsWith("Create", StringComparison.Ordinal)) Assert.True(data.GetProperty("wasDryRun").GetBoolean());
                else Assert.True(data.GetProperty("dryRun").GetBoolean());
            }
            else
            {
                Assert.Empty(stdout.ToString());
                Assert.Contains("No changes persisted", stderr.ToString());
                Assert.Contains("not checked", stderr.ToString());
                if (method is not "DeleteOptionValueAsync" and not "RemoveColumnOptionAsync") Assert.Contains(scope, stderr.ToString());
                Assert.DoesNotContain("Validation passed", stderr.ToString());
            }
        }
        finally
        {
            Console.SetOut(oldOut); Console.SetError(oldError);
            output.WriteLine("PPDS_MD_WIDER_CASE_PROOF=" + JsonSerializer.Serialize(new { route, method, json, exit, providerCalls, serviceCalls, linkedTokenBound = bound, preconditions, stdout = stdout.ToString(), stderr = stderr.ToString() }));
        }
    }

    [Theory]
    [InlineData("choice add-option --name new_choice --label Label --value 17", "AddOptionValueAsync")]
    [InlineData("choice update-option --name new_choice --value 17 --label Renamed", "UpdateOptionValueAsync")]
    [InlineData("choice remove-option --name new_choice --value 17 --force", "DeleteOptionValueAsync")]
    [InlineData("entity update-statusreason --entity new_table --value 17 --new-label Renamed", "UpdateStatusReasonAsync")]
    [InlineData("entity remove-statusreason --entity new_table --value 17 --force", "RemoveStatusReasonAsync")]
    [Trait("AC", "AC-122")]
    public async Task ExistingWrappersWithoutDryRun_KeepFlagsAndForwardActualToken(string args, string method)
    {
        var service = new Mock<IMetadataAuthoringService>(MockBehavior.Strict); CancellationToken actual = default;
        var providers = 0; var calls = 0;
        Task Done(object request, CancellationToken ct)
        {
            Assert.Equal(1, providers); Assert.Equal(actual, ct); calls++;
            var dryRun = request.GetType().GetProperty("DryRun"); if (dryRun != null) Assert.False((bool)dryRun.GetValue(request)!);
            return Task.CompletedTask;
        }
        service.Setup(s => s.AddOptionValueAsync(It.IsAny<AddOptionValueRequest>(), It.IsAny<CancellationToken>())).Returns(async (AddOptionValueRequest r, CancellationToken ct) => { await Done(r, ct); return 17; });
        service.Setup(s => s.UpdateOptionValueAsync(It.IsAny<UpdateOptionValueRequest>(), It.IsAny<CancellationToken>())).Returns((UpdateOptionValueRequest r, CancellationToken ct) => Done(r, ct));
        service.Setup(s => s.DeleteOptionValueAsync(It.IsAny<DeleteOptionValueRequest>(), It.IsAny<CancellationToken>())).Returns((DeleteOptionValueRequest r, CancellationToken ct) => Done(r, ct));
        service.Setup(s => s.UpdateStatusReasonAsync(It.IsAny<UpdateStatusReasonRequest>(), It.IsAny<CancellationToken>())).Returns((UpdateStatusReasonRequest r, CancellationToken ct) => Done(r, ct));
        service.Setup(s => s.RemoveStatusReasonAsync(It.IsAny<RemoveStatusReasonRequest>(), It.IsAny<CancellationToken>())).Returns((RemoveStatusReasonRequest r, CancellationToken ct) => Done(r, ct));
        Task<ServiceProvider> Provider(CancellationToken ct) { actual = ct; providers++; return Task.FromResult(new ServiceCollection().AddSingleton(service.Object).BuildServiceProvider()); }
        var command = MetadataCommandGroup.Create(Provider);
        Assert.NotEmpty(command.Parse((args + " --solution SyntheticSolution --dry-run").Split(' ')).Errors);
        using var stdout = new StringWriter(); using var stderr = new StringWriter(); using var cts = new CancellationTokenSource();
        var oldOut = Console.Out; var oldError = Console.Error;
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            var parsed = command.Parse((args + " --solution SyntheticSolution --output-format json").Split(' ')); Assert.Empty(parsed.Errors);
            var exit = await parsed.InvokeAsync(cancellationToken: cts.Token); Assert.Equal(1, providers); Assert.Equal(1, calls);
            Assert.Equal(method, Assert.Single(service.Invocations).Method.Name); Assert.Equal(0, exit);
            using var doc = JsonDocument.Parse(stdout.ToString()); Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.False(doc.RootElement.TryGetProperty("metadata", out _));
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); output.WriteLine("PPDS_MD_WRAPPER_PROOF=" + JsonSerializer.Serialize(new { args, method, providers, calls, stdout = stdout.ToString(), stderr = stderr.ToString() })); }
    }

    [Fact]
    [Trait("AC", "AC-121")]
    public async Task ServiceWithoutOptionalCapability_FailsClosedBeforeAnyDeleteCall()
    {
        var service = new Mock<IMetadataAuthoringService>(MockBehavior.Strict); var providers = 0;
        Task<ServiceProvider> Provider(CancellationToken _) { providers++; return Task.FromResult(new ServiceCollection().AddSingleton(service.Object).BuildServiceProvider()); }
        using var stdout = new StringWriter(); var oldOut = Console.Out;
        try
        {
            Console.SetOut(stdout);
            var parsed = MetadataCommandGroup.Create(Provider).Parse(["entity", "delete", "--entity", "new_table", "--solution", "SyntheticSolution", "--force", "--output-format", "json"]); Assert.Empty(parsed.Errors);
            var exit = await parsed.InvokeAsync(); Assert.Equal(1, providers); Assert.Empty(service.Invocations); Assert.NotEqual(0, exit);
            using var doc = JsonDocument.Parse(stdout.ToString()); Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("Operation.NotSupported", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally { Console.SetOut(oldOut); }
    }
}
