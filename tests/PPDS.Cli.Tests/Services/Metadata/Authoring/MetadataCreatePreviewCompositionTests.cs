using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Metadata;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

// Actual CLI handler -> actual service, with a recording interface seam and strictly synthetic SDK/pool.
[Trait("Category", "Unit")]
public sealed class MetadataCreatePreviewCompositionTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var route in new[] { "choice", "choices", "global" })
            foreach (var scenario in new[] { "omitted", "explicit-zero", "mixed", "missing-omitted", "missing-explicit" })
                foreach (var json in new[] { false, true }) yield return [route, scenario, json];
        foreach (var route in new[] { "nonchoice", "global-reference", "global" })
            foreach (var json in new[] { false, true }) yield return [route, route == "global" ? "empty" : "control", json];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    [Trait("AC", "AC-108")]
    [Trait("AC", "AC-122")]
    public async Task InitialCreate_ReportsOnlyCompletedProvisionalAllocation(string route, string scenario, bool json)
    {
        using var f = new Fixture(route, scenario);
        var providers = 0;
        Task<ServiceProvider> Provider(CancellationToken token)
        {
            Assert.Equal(0, providers++); f.Bind(token);
            return Task.FromResult(new ServiceCollection().AddSingleton(f.Router.Object)
                .AddSingleton(new ResolvedConnectionInfo { Profile = new AuthProfile { Name = "synthetic-only" }, EnvironmentUrl = "https://fixture.example.test" })
                .BuildServiceProvider());
        }

        var args = Arguments(route, scenario);
        if (json) args.AddRange(["--output-format", "json"]);
        using var stdout = new StringWriter(); using var stderr = new StringWriter(); using var cts = new CancellationTokenSource();
        var oldOut = Console.Out; var oldError = Console.Error; int? exit = null; var validated = false;
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            var parsed = MetadataCommandGroup.Create(Provider).Parse(args.ToArray()); Assert.Empty(parsed.Errors);
            exit = await parsed.InvokeAsync(cancellationToken: cts.Token);
            Assert.Equal(1, providers); Assert.Equal(1, f.ServiceCalls); f.AssertCompleted();
            if (scenario == "missing-omitted")
            {
                Assert.NotEqual(0, exit); Assert.NotNull(f.Failure);
                Assert.Equal(MetadataErrorCodes.MissingRequiredField, f.Failure.ErrorCode);
                Assert.DoesNotContain("[Dry-Run] Checks:", stderr.ToString());
                if (json)
                {
                    using var doc = JsonDocument.Parse(stdout.ToString()); var root = doc.RootElement;
                    Assert.False(root.GetProperty("success").GetBoolean());
                    Assert.Equal(MetadataErrorCodes.MissingRequiredField, root.GetProperty("error").GetProperty("code").GetString());
                    Assert.False(root.TryGetProperty("metadata", out _));
                }
                else { Assert.Empty(stdout.ToString()); Assert.Contains("no option value prefix", stderr.ToString()); }
            }
            else
            {
                Assert.Equal(0, exit); Assert.Null(f.Failure);
                var allocation = scenario is "omitted" or "mixed" ? "provisional" : "not-performed";
                var warning = scenario switch
                {
                    "explicit-zero" or "mixed" => "OptionValueOutsidePublisherRange",
                    "missing-explicit" => "OptionValueRangeUnavailable",
                    _ => null
                };
                if (json)
                {
                    using var doc = JsonDocument.Parse(stdout.ToString()); var root = doc.RootElement;
                    Assert.True(root.GetProperty("success").GetBoolean()); Assert.Equal("1.0", root.GetProperty("version").GetString());
                    var data = root.GetProperty("data"); Assert.True(data.GetProperty("wasDryRun").GetBoolean());
                    var preview = root.GetProperty("metadata").GetProperty("preview");
                    Assert.Equal("input-and-publisher", preview.GetProperty("validationScope").GetString());
                    Assert.True(preview.GetProperty("publisherPrefixChecked").GetBoolean());
                    Assert.Equal(allocation, preview.GetProperty("optionValueAllocation").GetString());
                    foreach (var name in new[] { "targetExistenceChecked", "targetOptionsRead", "dependenciesChecked", "serverFeasibilityChecked", "changesPersisted" })
                        Assert.False(preview.GetProperty(name).GetBoolean());
                    foreach (var name in new[] { "options", "value", "values" }) Assert.False(data.TryGetProperty(name, out _));
                    var messages = data.GetProperty("validationMessages").EnumerateArray().ToArray();
                    if (warning == null) Assert.Empty(messages);
                    else Assert.Equal(warning, Assert.Single(messages).GetProperty("rule").GetString());
                }
                else
                {
                    Assert.Empty(stdout.ToString());
                    Assert.Contains("Checks: input-and-publisher.", stderr.ToString());
                    Assert.Contains("Option value allocation: " + allocation + ".", stderr.ToString());
                    Assert.Contains("Target existence not checked", stderr.ToString());
                    Assert.Contains("Dependencies and server feasibility not checked", stderr.ToString());
                    Assert.Contains("No changes persisted", stderr.ToString());
                    if (warning != null) Assert.Contains("[" + warning + "]", stderr.ToString());
                }
            }
            Assert.DoesNotContain("created successfully", stderr.ToString());
            Assert.DoesNotContain("Validation passed", stderr.ToString()); validated = true;
        }
        finally
        {
            Console.SetOut(oldOut); Console.SetError(oldError);
            output.WriteLine("PPDS_CHOICE_COMPOSITION_PROOF=" + JsonSerializer.Serialize(new
            { route, scenario, json, exit, providers, f.ServiceCalls, f.Reads, f.Acquired, f.Returned, f.CallerPreserved, validated, stdout = stdout.ToString(), stderr = stderr.ToString() }));
        }
    }

    private static List<string> Arguments(string route, string scenario)
    {
        var args = route == "global"
            ? new List<string> { "choice", "create", "--name", "new_Choice", "--display-name", "Choice" }
            : new List<string> { "attribute", "create", "--entity", "new_table", "--name", "new_Choice", "--display-name", "Choice", "--type", route == "nonchoice" ? "String" : route == "choices" ? "Choices" : "Choice" };
        args.AddRange(["--solution", "SyntheticSolution", "--dry-run"]);
        if (route == "global-reference") args.AddRange(["--choice", "new_Existing"]);
        else if (route != "nonchoice")
        {
            var options = scenario switch
            {
                "omitted" => new[] { "One", "Two" },
                "explicit-zero" or "missing-explicit" => new[] { "Zero:0" },
                "mixed" => new[] { "Zero:0", "Auto", "Reserved:718980000" },
                "missing-omitted" => new[] { "Auto" },
                "empty" => Array.Empty<string>(),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario))
            };
            if (route == "global") args.AddRange(["--options", options.Length == 0 ? ",," : string.Join(",", options.Select(o => o.Replace(':', '=')))]);
            else foreach (var option in options) args.AddRange(["--option", option]);
        }
        return args;
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly Guid PublisherId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private readonly string _route, _scenario;
        private readonly Mock<IDataverseConnectionPool> _pool = new(MockBehavior.Strict);
        private readonly Mock<ICachedMetadataProvider> _cache = new(MockBehavior.Strict);
        private readonly Mock<IShakedownGuard> _guard = new(MockBehavior.Strict);
        private readonly DataverseMetadataAuthoringService _service;
        private readonly List<(int Id, Mock<IPooledClient> Client)> _clients = [];
        private CancellationToken _token; private bool _bound; private int _active;
        internal Mock<IMetadataAuthoringService> Router { get; } = new(MockBehavior.Strict);
        internal List<string> Reads { get; } = [];
        internal int Acquired { get; private set; }
        internal int Returned { get; private set; }
        internal int ServiceCalls { get; private set; }
        internal bool CallerPreserved { get; private set; }
        internal MetadataValidationException? Failure { get; private set; }

        internal Fixture(string route, string scenario)
        {
            _route = route; _scenario = scenario;
            _guard.Setup(g => g.EnsureCanMutate(route == "global" ? "metadata.globalChoice.create" : "metadata.column.create"));
            _service = new DataverseMetadataAuthoringService(_pool.Object, new SchemaValidator(), _guard.Object, cacheProvider: _cache.Object);
            Router.Setup(s => s.CreateColumnAsync(It.IsAny<CreateColumnRequest>(), null, It.IsAny<CancellationToken>()))
                .Returns(async (CreateColumnRequest request, IMetadataAuthoringProgressReporter? reporter, CancellationToken token) =>
                {
                    Assert.NotEqual("global", _route); Observe(token, request.Options);
                    Assert.Equal("SyntheticSolution", request.SolutionUniqueName); Assert.Equal("new_table", request.EntityLogicalName);
                    Assert.Equal("new_Choice", request.SchemaName); Assert.True(request.DryRun); Assert.False(request.Publish);
                    Assert.Equal(route == "nonchoice" ? SchemaColumnType.String : route == "choices" ? SchemaColumnType.Choices : SchemaColumnType.Choice, request.ColumnType);
                    Assert.Equal(route == "global-reference" ? "new_Existing" : null, request.OptionSetName);
                    return await Preserve(request.Options, () => _service.CreateColumnAsync(request, reporter, token));
                });
            Router.Setup(s => s.CreateGlobalChoiceAsync(It.IsAny<CreateGlobalChoiceRequest>(), null, It.IsAny<CancellationToken>()))
                .Returns(async (CreateGlobalChoiceRequest request, IMetadataAuthoringProgressReporter? reporter, CancellationToken token) =>
                {
                    Assert.Equal("global", _route); Observe(token, request.Options);
                    Assert.Equal("SyntheticSolution", request.SolutionUniqueName); Assert.Equal("new_Choice", request.SchemaName); Assert.True(request.DryRun);
                    return await Preserve(request.Options, () => _service.CreateGlobalChoiceAsync(request, reporter, token));
                });
            _pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
                .Returns<DataverseClientOptions?, string?, CancellationToken>((_, _, token) =>
                {
                    Assert.True(_bound); Assert.Equal(_token, token); Assert.Equal(0, _active); _active++;
                    var id = ++Acquired; var used = false; var client = new Mock<IPooledClient>(MockBehavior.Strict); _clients.Add((id, client));
                    client.Setup(c => c.DisposeAsync()).Callback(() => { Assert.Equal(1, _active); Assert.True(used); _active--; Returned++; }).Returns(ValueTask.CompletedTask);
                    client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
                        .Returns<QueryBase, CancellationToken>((query, ct) =>
                        {
                            Assert.Equal(_token, ct); Assert.Equal(1, _active); Assert.False(used); used = true;
                            var q = Assert.IsType<QueryExpression>(query); Assert.False(q.ColumnSet.AllColumns); Assert.Empty(q.Criteria.Filters);
                            var condition = Assert.Single(q.Criteria.Conditions); Assert.Equal(ConditionOperator.Equal, condition.Operator);
                            var numeric = id == 4; var solution = id is 1 or 3;
                            Assert.InRange(id, 1, 4); Assert.Equal(solution ? "solution" : "publisher", q.EntityName);
                            Assert.Equal(solution ? "publisherid" : numeric ? "customizationoptionvalueprefix" : "customizationprefix", Assert.Single(q.ColumnSet.Columns));
                            Assert.Equal(solution ? "uniquename" : "publisherid", condition.AttributeName);
                            if (solution) Assert.Equal("SyntheticSolution", Assert.Single(condition.Values));
                            else Assert.Equal(PublisherId, Assert.Single(condition.Values));
                            Reads.Add(q.EntityName + ":" + Assert.Single(q.ColumnSet.Columns));
                            var row = solution ? new Entity("solution") { ["publisherid"] = new EntityReference("publisher", PublisherId) } : new Entity("publisher", PublisherId);
                            if (!solution && !numeric) row["customizationprefix"] = "new";
                            if (numeric && !_scenario.StartsWith("missing-", StringComparison.Ordinal)) row["customizationoptionvalueprefix"] = 71898;
                            return Task.FromResult(new EntityCollection([row]));
                        });
                    // No Execute/Create/Update/Delete setup: every write, publication or extra SDK route fails.
                    return Task.FromResult(client.Object);
                });
        }

        internal void Bind(CancellationToken token) { Assert.False(_bound); _bound = true; _token = token; }

        private void Observe(CancellationToken token, OptionDefinition[]? options)
        {
            Assert.True(_bound); Assert.Equal(_token, token); Assert.Equal(0, ServiceCalls++);
            if (_route is "nonchoice" or "global-reference") Assert.Null(options);
            else
            {
                Assert.NotNull(options);
                Assert.Equal(_scenario == "empty" ? 0 : _scenario == "omitted" ? 2 : _scenario == "mixed" ? 3 : 1, options.Length);
                var labels = _scenario switch
                {
                    "omitted" => new[] { "One", "Two" }, "mixed" => new[] { "Zero", "Auto", "Reserved" },
                    "explicit-zero" or "missing-explicit" => new[] { "Zero" }, "empty" => Array.Empty<string>(),
                    _ => new[] { "Auto" }
                };
                var presence = _scenario switch
                {
                    "mixed" => new[] { true, false, true }, "explicit-zero" or "missing-explicit" => new[] { true },
                    "omitted" => new[] { false, false }, "empty" => Array.Empty<bool>(), _ => new[] { false }
                };
                Assert.Equal(labels, options.Select(o => o.Label)); Assert.Equal(presence, options.Select(o => o.HasExplicitValue));
                if (_scenario is "explicit-zero" or "missing-explicit" or "mixed") Assert.Equal(0, options[0].Value);
                if (_scenario == "mixed") Assert.Equal(718980000, options[2].Value);
            }
        }

        private async Task<T> Preserve<T>(OptionDefinition[]? options, Func<Task<T>> action)
        {
            var before = JsonSerializer.Serialize(options); var identities = options?.ToArray();
            try { return await action(); }
            catch (MetadataValidationException ex) { Failure = ex; throw; }
            finally
            {
                Assert.Equal(before, JsonSerializer.Serialize(options));
                if (identities != null) for (var i = 0; i < identities.Length; i++) Assert.Same(identities[i], options![i]);
                CallerPreserved = true;
            }
        }

        internal void AssertCompleted()
        {
            var count = _route is "nonchoice" or "global-reference" || _scenario == "empty" ? 2 : 4;
            Assert.Equal(count, Acquired); Assert.Equal(count, Returned); Assert.Equal(0, _active); Assert.True(CallerPreserved);
            Assert.Equal(new[] { "solution:publisherid", "publisher:customizationprefix", "solution:publisherid", "publisher:customizationoptionvalueprefix" }.Take(count), Reads);
            _pool.Verify(p => p.GetClientAsync(null, null, _token), Times.Exactly(count)); _pool.VerifyNoOtherCalls();
            foreach (var (_, client) in _clients)
            {
                client.Verify(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), _token), Times.Once);
                client.Verify(c => c.DisposeAsync(), Times.Once); client.VerifyNoOtherCalls();
            }
            _cache.VerifyNoOtherCalls();
            _guard.Verify(g => g.EnsureCanMutate(_route == "global" ? "metadata.globalChoice.create" : "metadata.column.create"), Times.Once); _guard.VerifyNoOtherCalls();
            if (_route == "global") Router.Verify(s => s.CreateGlobalChoiceAsync(It.IsAny<CreateGlobalChoiceRequest>(), null, _token), Times.Once);
            else Router.Verify(s => s.CreateColumnAsync(It.IsAny<CreateColumnRequest>(), null, _token), Times.Once);
            Router.VerifyNoOtherCalls();
        }

        public void Dispose() { }
    }
}
