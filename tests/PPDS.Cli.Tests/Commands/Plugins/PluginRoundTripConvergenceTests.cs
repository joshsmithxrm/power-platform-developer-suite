using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services;
using PPDS.Cli.Services.Plugins;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

/// <summary>
/// #1519 criteria 5-6 and #1520 criterion 7, offline: the actual deploy and diff command pipelines run
/// the actual <see cref="PluginRegistrationService"/> step/image write and read code against a small
/// stateful in-memory store of step, image, secure-configuration and user rows. Updates merge into the
/// stored row exactly as sent, so an unsent attribute keeps its earlier value — the platform behavior
/// that hid the original defect. Reads honour the query's ColumnSet and its link entities (message,
/// filter and impersonating user rows are modelled), so a list query that stops requesting a compared
/// column makes these tests fail. Assembly, type and SDK message/filter ID lookups are fixed stubs. This
/// does not prove Dataverse server behavior; live convergence remains unobserved.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PluginRoundTripConvergenceTests
{
    // Each case: how to set the value in the registration file, the stored attribute it lands in,
    // the value written, and the PPDS version 2.0 default for that attribute.
    public static TheoryData<string> Properties() => new()
    {
        "filteringAttributes", "unsecureConfiguration", "description", "runAsUser", "asyncAutoDelete",
        "canBeBypassed", "canUseReadOnlyConnection", "imageAttributes", "imageDescription"
    };

    // Under 1.0, diff compares these two when omitted (PPDS 1.4.2 parity); the rest are advisory only.
    public static TheoryData<string> Version1ComparedProperties() => new() { "filteringAttributes", "imageAttributes" };

    public static TheoryData<string> Version1AdvisoryProperties() => new()
    {
        "unsecureConfiguration", "description", "runAsUser", "asyncAutoDelete",
        "canBeBypassed", "canUseReadOnlyConnection", "imageDescription"
    };

    [Theory]
    [Trait("AC", "AC-133")]
    [MemberData(nameof(Properties))]
    public async Task Version2_ValueThenOmission_ConvergesStoredRowToDefaultAndDiffMatches(string property)
    {
        using var store = new InMemoryRegistrationStore("2.0");
        var spec = PropertySpec.For(property, store.UserId);

        store.SetConfigured(spec, include: true);
        Assert.Equal(0, await store.DeployAsync());
        Assert.Equal(spec.Value, store.StoredValue(spec));

        store.SetConfigured(spec, include: false);
        var exit = await store.DeployAsync();
        Assert.True(exit == 0, store.LastDeployOutput);
        Assert.Equal(spec.Version2Default, store.StoredValue(spec));
        AssertDeployVerificationMatched(store.LastDeployOutput);

        var (diffExit, diffOutput) = await store.DiffAsync(json: true);
        Assert.Equal(0, diffExit);
        using var diff = JsonDocument.Parse(diffOutput);
        Assert.Equal("match", diff.RootElement.GetProperty("metadata").GetProperty("status").GetString());
        Assert.Equal(0, store.Deletes);
    }

    [Theory]
    [Trait("AC", "AC-138")]
    [MemberData(nameof(Version1ComparedProperties))]
    public async Task Version1_ValueThenOmission_ComparedAttributeSetIsPreservedAndDiffReportsDriftWithGuidance(string property)
    {
        using var store = new InMemoryRegistrationStore("1.0");
        var spec = PropertySpec.For(property, store.UserId);

        store.SetConfigured(spec, include: true);
        Assert.Equal(0, await store.DeployAsync());
        store.SetConfigured(spec, include: false);
        Assert.Equal(0, await store.DeployAsync()); // deploy's exit code and 1.0 preservation are unchanged
        Assert.Equal(spec.Value, store.StoredValue(spec));

        // Deploy's notice still lists the preserved value.
        using (var deploy = JsonDocument.Parse(store.LastDeployOutput))
        {
            var row = Assert.Single(deploy.RootElement.GetProperty("data").EnumerateArray());
            Assert.Contains(row.GetProperty("preservedLegacyValues").EnumerateArray(), v =>
                v.GetProperty("path").GetString() == spec.Path(store) && v.GetProperty("property").GetString() == spec.Field);
            Assert.False(row.TryGetProperty("reconciliationStatus", out var status) && status.ValueKind != JsonValueKind.Null,
                "1.0 deploy runs no post-deploy verification");
        }

        var (diffExit, diffOutput) = await store.DiffAsync(json: true);
        Assert.Equal(1, diffExit);
        using (var diff = JsonDocument.Parse(diffOutput))
        {
            Assert.Equal("drift", diff.RootElement.GetProperty("metadata").GetProperty("status").GetString());
            var assembly = diff.RootElement.GetProperty("data")[0];
            var modified = Assert.Single(assembly.GetProperty(spec.OnImage ? "modifiedImages" : "modifiedSteps").EnumerateArray());
            var change = Assert.Single(modified.GetProperty("changes").EnumerateArray());
            Assert.Equal(spec.Field, change.GetProperty("property").GetString());
            Assert.Equal("(none)", change.GetProperty("expected").GetString());
            Assert.Equal(spec.Value, change.GetProperty("actual").GetString());
            Assert.False(assembly.TryGetProperty("preservedLegacyValues", out _)); // drift, not also advisory
        }
        var (textExit, text) = await store.DiffAsync(json: false);
        Assert.Equal(1, textExit);
        Assert.Contains($"{spec.Field}: (none) -> {spec.Value}", text);
        Assert.Contains(PluginLegacyPreservationService.ComparedOmissionIntro, text);
        Assert.Contains(spec.OnImage ? PluginLegacyPreservationService.ImageOption : PluginLegacyPreservationService.FilterableStepOption, text);
        Assert.Equal(0, store.Deletes);
    }

    [Theory]
    [Trait("AC", "AC-138")]
    [MemberData(nameof(Version1ComparedProperties))]
    public async Task Version1_ExplicitNullAttributeSet_FollowingTheGuidanceClearsTheStoredValue(string property)
    {
        using var store = new InMemoryRegistrationStore("1.0");
        var spec = PropertySpec.For(property, store.UserId);
        store.SetConfigured(spec, include: true);
        Assert.Equal(0, await store.DeployAsync());

        // The shape the model serialiser writes: the property present as an explicit null.
        store.SetExplicitNull(spec);
        Assert.Equal(0, await store.DeployAsync());
        Assert.Equal(spec.Value, store.StoredValue(spec)); // 1.0 preserves it
        var (exit, text) = await store.DiffAsync(json: false);
        Assert.Equal(1, exit);
        Assert.Contains("must be removed too", text);

        // Listing it in resetFields while the null is still present is rejected, before any write.
        store.SetResetFields(spec);
        Assert.NotEqual(0, await store.DeployAsync());
        Assert.Equal(spec.Value, store.StoredValue(spec));
        Assert.Equal(3, (await store.DiffAsync(json: true)).Exit);

        // Following the guidance: remove the property and keep it in resetFields.
        store.SetConfigured(spec, include: false);
        Assert.Equal(0, await store.DeployAsync());
        Assert.Null(store.StoredValue(spec));
        var (diffExit, diffOutput) = await store.DiffAsync(json: true);
        Assert.Equal(0, diffExit);
        using var diff = JsonDocument.Parse(diffOutput);
        Assert.Equal("match", diff.RootElement.GetProperty("metadata").GetProperty("status").GetString());
        Assert.Equal(0, store.Deletes);
    }

    [Theory]
    [Trait("AC", "AC-138")]
    [MemberData(nameof(Version1ComparedProperties))]
    public async Task Version1_ResetFields_ClearsComparedAttributeSetAndDiffMatches(string property)
    {
        using var store = new InMemoryRegistrationStore("1.0");
        var spec = PropertySpec.For(property, store.UserId);

        store.SetConfigured(spec, include: true);
        Assert.Equal(0, await store.DeployAsync());
        store.SetConfigured(spec, include: false);
        store.SetResetFields(spec);
        Assert.Equal(0, await store.DeployAsync());
        Assert.Null(store.StoredValue(spec)); // resetFields under 1.0 clears the stored value

        var (diffExit, diffOutput) = await store.DiffAsync(json: true);
        Assert.Equal(0, diffExit);
        using var diff = JsonDocument.Parse(diffOutput);
        Assert.Equal("match", diff.RootElement.GetProperty("metadata").GetProperty("status").GetString());
        Assert.Equal(0, store.Deletes);
    }

    [Theory]
    [Trait("AC", "AC-133")]
    [MemberData(nameof(Version1AdvisoryProperties))]
    public async Task Version1_ValueThenOmission_PreservesStoredValueDiffMatchesAndNoticeListsIt(string property)
    {
        using var store = new InMemoryRegistrationStore("1.0");
        var spec = PropertySpec.For(property, store.UserId);

        store.SetConfigured(spec, include: true);
        Assert.Equal(0, await store.DeployAsync());
        Assert.Equal(spec.Value, store.StoredValue(spec));

        store.SetConfigured(spec, include: false);
        Assert.Equal(0, await store.DeployAsync());
        Assert.Equal(spec.Value, store.StoredValue(spec)); // legacy contract: omission preserves

        // Deploy reports the preserved difference from the rows it already read.
        using (var deploy = JsonDocument.Parse(store.LastDeployOutput))
        {
            var row = Assert.Single(deploy.RootElement.GetProperty("data").EnumerateArray());
            Assert.Contains(row.GetProperty("preservedLegacyValues").EnumerateArray(), v =>
                v.GetProperty("path").GetString() == spec.Path(store) && v.GetProperty("property").GetString() == spec.Field);
        }

        var (diffExit, diffOutput) = await store.DiffAsync(json: true);
        Assert.Equal(0, diffExit);
        using (var diff = JsonDocument.Parse(diffOutput))
        {
            Assert.Equal("match", diff.RootElement.GetProperty("metadata").GetProperty("status").GetString());
            var preserved = diff.RootElement.GetProperty("data")[0].GetProperty("preservedLegacyValues").EnumerateArray().ToList();
            var item = Assert.Single(preserved);
            Assert.Equal(spec.Path(store), item.GetProperty("path").GetString());
            Assert.Equal(spec.Field, item.GetProperty("property").GetString());
        }
        var (textExit, text) = await store.DiffAsync(json: false);
        Assert.Equal(0, textExit);
        Assert.Contains("preserved, not managed, under registration version 1.0", text);
        Assert.Contains($"{spec.Path(store)}: {spec.Field} stored", text);
        Assert.DoesNotContain(InMemoryRegistrationStore.ConfigurationValue, diffOutput + text + store.LastDeployOutput);
        Assert.Equal(0, store.Deletes);
    }

    [Theory]
    [Trait("AC", "AC-134")]
    [InlineData("2.0")]
    [InlineData("1.0")]
    public async Task SecureConfiguration_SuppliedThenOmitted_IsPreservedAndNeverMakesVerificationFail(string version)
    {
        using var store = new InMemoryRegistrationStore(version);
        store.SetStep("secureConfiguration", JsonValue.Create(InMemoryRegistrationStore.SecureValue));

        // A real 2.0 deploy that supplies secure configuration completes successfully.
        var exit = await store.DeployAsync();
        Assert.True(exit == 0, store.LastDeployOutput);
        if (version == "2.0") AssertDeployVerificationMatched(store.LastDeployOutput);
        var link = store.StoredStepAttribute("sdkmessageprocessingstepsecureconfigid") as EntityReference;
        Assert.NotNull(link);
        Assert.Equal(InMemoryRegistrationStore.SecureValue, store.StoredSecureValue(link!.Id));

        store.SetStep("secureConfiguration", null);
        Assert.Equal(0, await store.DeployAsync());
        Assert.Equal(link.Id, Assert.IsType<EntityReference>(store.StoredStepAttribute("sdkmessageprocessingstepsecureconfigid")).Id);
        Assert.Equal(InMemoryRegistrationStore.SecureValue, store.StoredSecureValue(link.Id)); // omission never clears it
        Assert.DoesNotContain(InMemoryRegistrationStore.SecureValue, store.LastDeployOutput);
        var (diffExit, diffOutput) = await store.DiffAsync(json: true);
        Assert.Equal(0, diffExit);
        Assert.DoesNotContain(InMemoryRegistrationStore.SecureValue, diffOutput);
    }

    private static void AssertDeployVerificationMatched(string output)
    {
        using var deploy = JsonDocument.Parse(output);
        Assert.True(deploy.RootElement.GetProperty("success").GetBoolean(), output);
        var row = Assert.Single(deploy.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal("match", row.GetProperty("reconciliationStatus").GetString());
        Assert.False(row.TryGetProperty("preservedLegacyValues", out _));
    }

    internal sealed record PropertySpec(string Field, bool OnImage, string Attribute, object? Value, object? Version2Default,
        Func<JsonNode> ConfigValue)
    {
        public string Path(InMemoryRegistrationStore store) => OnImage
            ? $"assembly:{InMemoryRegistrationStore.AssemblyName}/type:{InMemoryRegistrationStore.TypeName}/step:{InMemoryRegistrationStore.StepName}/image:{InMemoryRegistrationStore.ImageName}"
            : $"assembly:{InMemoryRegistrationStore.AssemblyName}/type:{InMemoryRegistrationStore.TypeName}/step:{InMemoryRegistrationStore.StepName}";

        public static PropertySpec For(string property, Guid userId) => property switch
        {
            "filteringAttributes" => new("filteringAttributes", false, "filteringattributes", "name,telephone1", null, () => JsonValue.Create("telephone1, NAME")!),
            "unsecureConfiguration" => new("unsecureConfiguration", false, "configuration", InMemoryRegistrationStore.ConfigurationValue, null, () => JsonValue.Create(InMemoryRegistrationStore.ConfigurationValue)!),
            "description" => new("description", false, "description", "step description", null, () => JsonValue.Create("step description")!),
            "runAsUser" => new("runAsUser", false, "impersonatinguserid", userId, null, () => JsonValue.Create(userId.ToString("D"))!),
            "asyncAutoDelete" => new("asyncAutoDelete", false, "asyncautodelete", true, false, () => JsonValue.Create(true)!),
            "canBeBypassed" => new("canBeBypassed", false, "canbebypassed", false, true, () => JsonValue.Create(false)!),
            "canUseReadOnlyConnection" => new("canUseReadOnlyConnection", false, "canusereadonlyconnection", true, false, () => JsonValue.Create(true)!),
            "imageAttributes" => new("attributes", true, "attributes", "name", null, () => JsonValue.Create("name")!),
            "imageDescription" => new("description", true, "description", "image description", null, () => JsonValue.Create("image description")!),
            _ => throw new ArgumentOutOfRangeException(nameof(property))
        };
    }

    internal sealed class InMemoryRegistrationStore : IDisposable
    {
        public const string TypeName = "Contoso.Handler";
        public const string StepName = "Update account";
        public const string ImageName = "Before";
        public const string ConfigurationValue = "synthetic-unsecure-configuration";
        public const string SecureValue = "synthetic-secure-configuration";
        public const string AssemblyName = "RoundTripFixture";

        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ppds-roundtrip-{Guid.NewGuid():N}");
        private readonly Guid _assemblyId = Guid.NewGuid();
        private readonly Guid _typeId = Guid.NewGuid();
        private readonly Guid _messageId = Guid.NewGuid();
        private readonly Guid _filterId = Guid.NewGuid();
        private readonly Guid _stepId = Guid.NewGuid();
        private readonly Dictionary<string, Dictionary<Guid, Entity>> _tables = new(StringComparer.Ordinal)
        {
            ["sdkmessageprocessingstep"] = [], ["sdkmessageprocessingstepimage"] = [],
            ["sdkmessageprocessingstepsecureconfig"] = [], ["systemuser"] = [], ["sdkmessage"] = [], ["sdkmessagefilter"] = []
        };
        private readonly Mock<IPooledClient> _client = new();
        private readonly Mock<IPluginRegistrationService> _service = new(MockBehavior.Strict);
        private readonly JsonNode _config;

        public Guid UserId { get; } = Guid.NewGuid();
        public int Deletes { get; private set; }
        /// <summary>Creates and updates that reached the store.</summary>
        public int Writes { get; private set; }
        public string LastDeployOutput { get; private set; } = "";

        public InMemoryRegistrationStore(string version)
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllBytes(Path.Combine(_directory, "fixture.dll"), [0x4d, 0x5a]);
            _config = JsonNode.Parse($$"""
                {"version":"{{version}}","assemblies":[{"name":"{{AssemblyName}}","type":"Assembly","path":"fixture.dll",
                  "types":[{"typeName":"{{TypeName}}","steps":[{"name":"{{StepName}}","message":"Update","entity":"account",
                    "stage":"PostOperation","mode":"Asynchronous","images":[{"name":"{{ImageName}}","imageType":"PreImage"}]}]}]}]}
                """)!;
            Seed();
            ConfigureClient();
            ConfigureService();
        }

        public void SetConfigured(PropertySpec spec, bool include)
        {
            var target = spec.OnImage ? Image : Step;
            if (include) target[spec.Field] = spec.ConfigValue();
            else target.Remove(spec.Field);
        }

        public void SetExplicitNull(PropertySpec spec) => (spec.OnImage ? Image : Step)[spec.Field] = null;

        public void SetResetFields(PropertySpec spec) =>
            (spec.OnImage ? Image : Step)["resetFields"] = new JsonArray(JsonValue.Create(spec.Field));

        public void SetStep(string field, JsonNode? value)
        {
            if (value is null) Step.Remove(field); else Step[field] = value;
        }

        public object? StoredValue(PropertySpec spec)
        {
            var row = spec.OnImage ? _tables["sdkmessageprocessingstepimage"].Values.Single() : _tables["sdkmessageprocessingstep"][_stepId];
            var value = row.Contains(spec.Attribute) ? row[spec.Attribute] : null;
            return value is EntityReference reference ? reference.Id : value;
        }

        public object? StoredStepAttribute(string attribute)
        {
            var row = _tables["sdkmessageprocessingstep"][_stepId];
            return row.Contains(attribute) ? row[attribute] : null;
        }

        public string? StoredSecureValue(Guid id) => _tables["sdkmessageprocessingstepsecureconfig"][id].GetAttributeValue<string>("secureconfig");

        /// <summary>Adds another configured image (same settings as the first) to the step.</summary>
        public void AddConfiguredImage(string name) =>
            Step["images"]!.AsArray().Add(new JsonObject { ["name"] = name, ["imageType"] = "PreImage" });

        /// <summary>Renames the single stored image row.</summary>
        public void SetStoredImageName(string name) =>
            _tables["sdkmessageprocessingstepimage"].Values.Single()["name"] = name;

        /// <summary>Adds another stored image row on the same step and returns its ID.</summary>
        public Guid AddStoredImage(string name)
        {
            var image = new Entity("sdkmessageprocessingstepimage", Guid.NewGuid())
            {
                ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", _stepId),
                ["name"] = name, ["imagetype"] = new OptionSetValue(0), ["entityalias"] = ImageName,
                ["messagepropertyname"] = "Target", ["ismanaged"] = false
            };
            _tables["sdkmessageprocessingstepimage"][image.Id] = image;
            return image.Id;
        }

        public IReadOnlyList<string> StoredImageNames() =>
            _tables["sdkmessageprocessingstepimage"].Values.Select(i => i.GetAttributeValue<string>("name")).ToList();

        public async Task<int> DeployAsync(bool clean = false, bool dryRun = false)
        {
            var path = WriteConfig();
            using var output = new StringWriter();
            var exit = await DeployCommand.ExecuteAsync(new FileInfo(path), null, null, null, clean: clean, dryRun: dryRun,
                new GlobalOptionValues { OutputFormat = OutputFormat.Json }, CancellationToken.None,
                serviceProviderFactory: _ => Task.FromResult(new ServiceCollection()
                    .AddSingleton(_service.Object)
                    .AddSingleton(new Mock<ICustomApiService>(MockBehavior.Strict).Object)
                    .BuildServiceProvider()),
                outputWriter: new JsonOutputWriter(output));
            LastDeployOutput = output.ToString();
            return exit;
        }

        public async Task<(int Exit, string Output)> DiffAsync(bool json)
        {
            var path = WriteConfig();
            using var output = new StringWriter();
            using var error = new StringWriter();
            var args = new List<string> { "--config", path };
            if (json) args.AddRange(["--output-format", "json"]);
            var parsed = DiffCommand.CreateForTesting(_service.Object, output, error).Parse(args.ToArray());
            Assert.Empty(parsed.Errors);
            var exit = await parsed.InvokeAsync();
            return (exit, json ? output.ToString() : error.ToString());
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);

        private JsonObject Step => _config["assemblies"]![0]!["types"]![0]!["steps"]![0]!.AsObject();
        private JsonObject Image => Step["images"]![0]!.AsObject();

        private string WriteConfig()
        {
            var path = Path.Combine(_directory, "registrations.json");
            File.WriteAllText(path, _config.ToJsonString());
            return path;
        }

        private void Seed()
        {
            // An existing step and image already at the PPDS defaults; both deploys are updates.
            var step = new Entity("sdkmessageprocessingstep", _stepId)
            {
                ["name"] = StepName, ["eventhandler"] = new EntityReference("plugintype", _typeId),
                ["sdkmessageid"] = new EntityReference("sdkmessage", _messageId),
                ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", _filterId),
                ["stage"] = new OptionSetValue(40), ["mode"] = new OptionSetValue(1), ["rank"] = 1,
                ["statecode"] = new OptionSetValue(0), ["supporteddeployment"] = new OptionSetValue(0),
                ["invocationsource"] = new OptionSetValue(0), ["asyncautodelete"] = false, ["canbebypassed"] = true,
                ["canusereadonlyconnection"] = false, ["ishidden"] = new BooleanManagedProperty(false), ["ismanaged"] = false,
                ["iscustomizable"] = new BooleanManagedProperty(true)
            };
            _tables["sdkmessageprocessingstep"][step.Id] = step;
            var image = new Entity("sdkmessageprocessingstepimage", Guid.NewGuid())
            {
                ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", _stepId),
                ["name"] = ImageName, ["imagetype"] = new OptionSetValue(0), ["entityalias"] = ImageName,
                ["messagepropertyname"] = "Target", ["ismanaged"] = false
            };
            _tables["sdkmessageprocessingstepimage"][image.Id] = image;
            _tables["systemuser"][UserId] = new Entity("systemuser", UserId) { ["fullname"] = "Synthetic User" };
            // Joined by the step list query; identity comes from these rows, not from constants.
            _tables["sdkmessage"][_messageId] = new Entity("sdkmessage", _messageId) { ["name"] = "Update" };
            _tables["sdkmessagefilter"][_filterId] = new Entity("sdkmessagefilter", _filterId)
            {
                ["primaryobjecttypecode"] = "account", ["secondaryobjecttypecode"] = "none"
            };
        }

        private void ConfigureClient()
        {
            _client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((QueryBase query, CancellationToken _) => Query((QueryExpression)query));
            _client.Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
                .Callback<Entity, CancellationToken>((entity, _) =>
                {
                    Writes++;
                    var row = _tables[entity.LogicalName][entity.Id]; // an unknown row throws, like a missing record
                    foreach (var attribute in entity.Attributes) row[attribute.Key] = attribute.Value; // unsent attributes keep their value
                })
                .Returns(Task.CompletedTask);
            _client.Setup(c => c.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Entity entity, CancellationToken _) =>
                {
                    Writes++;
                    var row = new Entity(entity.LogicalName, Guid.NewGuid());
                    foreach (var attribute in entity.Attributes) row[attribute.Key] = attribute.Value;
                    _tables[entity.LogicalName][row.Id] = row;
                    return row.Id;
                });
            _client.Setup(c => c.DeleteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Callback(() => Deletes++).Returns(Task.CompletedTask);
            _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Unmodeled request in the round-trip store."));
            PluginFieldCapabilityTestMetadata.AllowReadOnlyConnectionWrites(_client);
        }

        private EntityCollection Query(QueryExpression query)
        {
            if (!_tables.TryGetValue(query.EntityName, out var table))
                throw new InvalidOperationException($"Unmodeled table {query.EntityName}.");
            var rows = new List<Entity>();
            foreach (var row in table.Values.Where(e => Matches(e, query.Criteria)))
            {
                // Only requested, non-null columns come back, as Dataverse returns them.
                var copy = Project(row, query.ColumnSet);
                var joined = true;
                foreach (var link in query.LinkEntities)
                    joined &= Join(row, link, copy);
                if (joined) rows.Add(copy);
            }
            IEnumerable<Entity> result = rows;
            if (query.TopCount is { } top) result = result.Take(top);
            return new EntityCollection(result.ToList()) { MoreRecords = false };
        }

        private static Entity Project(Entity row, ColumnSet columns)
        {
            var copy = new Entity(row.LogicalName, row.Id);
            foreach (var attribute in row.Attributes.Where(a => a.Value is not null &&
                         (columns.AllColumns || columns.Columns.Contains(a.Key))))
                copy[attribute.Key] = attribute.Value;
            return copy;
        }

        /// <summary>Models the single-level link entities the registration queries use.</summary>
        private bool Join(Entity row, LinkEntity link, Entity target)
        {
            if (link.LinkEntities.Count > 0 || link.LinkCriteria.Conditions.Count > 0 || link.LinkCriteria.Filters.Count > 0)
                throw new InvalidOperationException($"Unmodeled link shape to {link.LinkToEntityName}.");
            if (!_tables.TryGetValue(link.LinkToEntityName, out var linked))
                throw new InvalidOperationException($"Unmodeled linked table {link.LinkToEntityName}.");
            if (link.LinkToAttributeName != link.LinkToEntityName + "id")
                throw new InvalidOperationException($"Unmodeled link target attribute {link.LinkToAttributeName}.");
            var key = row.Contains(link.LinkFromAttributeName) ? row[link.LinkFromAttributeName] as EntityReference : null;
            if (key is null || !linked.TryGetValue(key.Id, out var match))
                return link.JoinOperator != JoinOperator.Inner;
            foreach (var attribute in Project(match, link.Columns).Attributes)
                target[$"{link.EntityAlias}.{attribute.Key}"] = new AliasedValue(link.LinkToEntityName, attribute.Key, attribute.Value);
            return true;
        }

        private static bool Matches(Entity row, FilterExpression filter)
        {
            var results = filter.Conditions.Select(c => Matches(row, c)).Concat(filter.Filters.Select(f => Matches(row, f)));
            return filter.FilterOperator == LogicalOperator.And ? results.All(r => r) : results.Any(r => r);
        }

        private static bool Matches(Entity row, ConditionExpression condition)
        {
            if (condition.Operator != ConditionOperator.Equal || condition.Values.Count != 1)
                throw new InvalidOperationException($"Unmodeled condition on {condition.AttributeName}.");
            var actual = condition.AttributeName == row.LogicalName + "id" ? row.Id
                : row.Contains(condition.AttributeName) ? row[condition.AttributeName] : null;
            actual = actual switch
            {
                EntityReference reference => reference.Id,
                OptionSetValue option => option.Value,
                BooleanManagedProperty managed => managed.Value,
                _ => actual
            };
            // Dataverse evaluates string equality under its default case-insensitive collation.
            if (actual is string text && condition.Values[0] is string value)
                return string.Equals(text, value, StringComparison.OrdinalIgnoreCase);
            return Equals(actual, condition.Values[0]);
        }

        private void ConfigureService()
        {
            var pool = new Mock<IDataverseConnectionPool>();
            pool.Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(_client.Object);
            var real = new PluginRegistrationService(pool.Object, new InactiveFakeShakedownGuard(),
                NullLogger<PluginRegistrationService>.Instance);
            var assembly = new PluginAssemblyInfo { Id = _assemblyId, Name = AssemblyName };
            _service.Setup(s => s.GetAssemblyByNameAsync(AssemblyName, It.IsAny<CancellationToken>())).ReturnsAsync(assembly);
            _service.Setup(s => s.GetAssemblyByIdAsync(_assemblyId, It.IsAny<CancellationToken>())).ReturnsAsync(assembly);
            _service.Setup(s => s.ListTypesForAssemblyAsync(_assemblyId, It.IsAny<CancellationToken>()))
                .ReturnsAsync([new PluginTypeInfo { Id = _typeId, TypeName = TypeName, AssemblyId = _assemblyId }]);
            _service.Setup(s => s.UpsertAssemblyAsync(AssemblyName, It.IsAny<byte[]>(), null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(_assemblyId);
            _service.Setup(s => s.UpsertPluginTypeAsync(_assemblyId, TypeName, null, It.IsAny<CancellationToken>())).ReturnsAsync(_typeId);
            _service.Setup(s => s.GetSdkMessageIdAsync("Update", It.IsAny<CancellationToken>())).ReturnsAsync(_messageId);
            _service.Setup(s => s.GetSdkMessageFilterIdAsync(_messageId, "account", null, It.IsAny<CancellationToken>())).ReturnsAsync(_filterId);

            // Everything that reads or writes step/image state is the actual registration service.
            _service.Setup(s => s.ListStepsForTypeAsync(It.IsAny<Guid>(), It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>()))
                .Returns((Guid id, PluginListOptions? options, CancellationToken ct) => real.ListStepsForTypeAsync(id, options, ct));
            _service.Setup(s => s.ListImagesForStepAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns((Guid id, CancellationToken ct) => real.ListImagesForStepAsync(id, ct));
            _service.Setup(s => s.ResolveRunAsUserAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Returns((string? selector, CancellationToken ct) => real.ResolveRunAsUserAsync(selector, ct));
            _service.Setup(s => s.ValidateStepFieldCapabilitiesAsync(It.IsAny<PluginStepConfig>(), It.IsAny<Guid?>(),
                    It.IsAny<CancellationToken>(), It.IsAny<IProgressReporter?>()))
                .Returns((PluginStepConfig config, Guid? id, CancellationToken ct, IProgressReporter? progress) =>
                    real.ValidateStepFieldCapabilitiesAsync(config, id, ct, progress));
            _service.Setup(s => s.UpsertStepAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<PluginStepConfig>(), It.IsAny<Guid>(),
                    It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<StepIdentityResolution?>(), It.IsAny<CancellationToken>()))
                .Returns((Guid handler, string handlerType, PluginStepConfig config, Guid message, Guid? filter, string? solution,
                    StepIdentityResolution? identity, CancellationToken ct) =>
                    real.UpsertStepAsync(handler, handlerType, config, message, filter, solution, identity, ct));
            _service.Setup(s => s.UpsertImageAsync(It.IsAny<Guid>(), It.IsAny<PluginImageConfig>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((Guid step, PluginImageConfig config, string message, CancellationToken ct) =>
                    real.UpsertImageAsync(step, config, message, ct));

            // Version 2 image cleanup (--clean): ownership reads are stubs; step/image reads and deletes are real.
            _service.Setup(s => s.ListAssembliesAsync(AssemblyName, It.IsAny<PluginListOptions?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([assembly]);
            _service.Setup(s => s.GetPluginTypeByNameOrIdAsync(_typeId.ToString("D"), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PluginTypeInfo { Id = _typeId, TypeName = TypeName, AssemblyId = _assemblyId });
            _service.Setup(s => s.GetStepByNameOrIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(async (string id, CancellationToken ct) =>
                {
                    var steps = await real.ListStepsForTypeAsync(_typeId, new PluginListOptions(IncludeHidden: true), ct);
                    var step = steps.SingleOrDefault(s => s.Id.ToString("D") == id);
                    if (step is not null) step.PluginTypeId ??= _typeId;
                    return step;
                });
            _service.Setup(s => s.GetImageByNameOrIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(async (string id, CancellationToken ct) =>
                {
                    var image = (await real.ListImagesForStepAsync(_stepId, ct)).SingleOrDefault(i => i.Id.ToString("D") == id);
                    if (image is not null) image.StepId ??= _stepId;
                    return image;
                });
            _service.Setup(s => s.DeleteImageAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns((Guid id, CancellationToken ct) => real.DeleteImageAsync(id, ct));
        }
    }
}
