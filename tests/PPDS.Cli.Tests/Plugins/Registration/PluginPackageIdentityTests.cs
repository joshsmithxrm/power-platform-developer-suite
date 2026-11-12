using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Registration;

[Trait("Category", "Unit")]
public sealed class PluginPackageIdentityTests
{
    private readonly Mock<IPooledClient> _client = new(MockBehavior.Strict);
    private readonly Mock<IDataverseConnectionPool> _pool = new(MockBehavior.Strict);
    private readonly List<Entity> _packages = [];
    private readonly Dictionary<Guid, byte[]> _stored = [];
    private readonly List<OrganizationRequest> _writes = [];
    private readonly List<QueryExpression> _queries = [];
    private readonly List<Entity> _assemblies = [];
    private readonly Guid _solutionId = Guid.NewGuid();
    private readonly Guid _publisherId = Guid.NewGuid();
    private readonly PluginRegistrationService _service;
    private string? _prefix = "ppds";
    private bool _solutionPresent = true;
    private bool _solutionManaged;
    private bool _membership = true;
    private bool _applyWrites = true;
    private int _downloads;

    public PluginPackageIdentityTests()
    {
        _client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _pool.Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>())).ReturnsAsync(_client.Object);
        _client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((QueryBase query, CancellationToken _) => RetrieveRows((QueryExpression)query));
        _client.Setup(c => c.RetrieveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<ColumnSet>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string table, Guid id, ColumnSet columns, CancellationToken _) =>
            {
                if (table == Publisher.EntityLogicalName)
                    return new Entity(table, id) { [Publisher.Fields.CustomizationPrefix] = _prefix };
                Assert.Equal(PluginPackage.EntityLogicalName, table);
                _downloads++;
                var package = _packages.Single(p => p.Id == id);
                return new Entity(table, id)
                {
                    [PluginPackage.Fields.Name] = package.GetAttributeValue<string>(PluginPackage.Fields.Name),
                    [PluginPackage.Fields.Version] = package.GetAttributeValue<string>(PluginPackage.Fields.Version),
                    [PluginPackage.Fields.Content] = Convert.ToBase64String(_stored[id])
                };
            });
        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrganizationRequest request, CancellationToken _) =>
            {
                _writes.Add(request);
                if (request is CreateRequest create)
                {
                    var id = Guid.NewGuid();
                    if (_applyWrites)
                    {
                        create.Target.Id = id;
                        create.Target[PluginPackage.Fields.IsManaged] = false;
                        _packages.Add(create.Target);
                        _stored[id] = Convert.FromBase64String(create.Target.GetAttributeValue<string>(PluginPackage.Fields.Content));
                    }
                    var response = new CreateResponse();
                    response.Results["id"] = id;
                    return response;
                }
                var update = Assert.IsType<UpdateRequest>(request);
                if (_applyWrites) _stored[update.Target.Id] = Convert.FromBase64String(update.Target.GetAttributeValue<string>(PluginPackage.Fields.Content));
                return new UpdateResponse();
            });
        _service = new PluginRegistrationService(_pool.Object, new InactiveFakeShakedownGuard(),
            Mock.Of<ILogger<PluginRegistrationService>>());
    }

    private EntityCollection RetrieveRows(QueryExpression query)
    {
        _queries.Add(query);
        IEnumerable<Entity> rows = query.EntityName switch
        {
            "pluginpackage" => _packages,
            "pluginassembly" => _assemblies,
            "solution" => _solutionPresent ? [new Entity("solution", _solutionId)
            {
                ["uniquename"] = "TestSolution", ["ismanaged"] = _solutionManaged,
                ["publisherid"] = new EntityReference("publisher", _publisherId)
            }] : [],
            "solutioncomponent" => _membership ? [new Entity("solutioncomponent", Guid.NewGuid())
            {
                ["solutionid"] = new EntityReference("solution", _solutionId),
                ["objectid"] = (Guid)query.Criteria.Conditions.Single(c => c.AttributeName == "objectid").Values[0]
            }] : [],
            _ => throw new InvalidOperationException("Unexpected entity query: " + query.EntityName)
        };
        return new EntityCollection(rows.Where(e => Matches(e, query.Criteria)).ToList());
    }

    private static bool Matches(Entity entity, FilterExpression filter)
    {
        bool Condition(ConditionExpression c)
        {
            Assert.Equal(ConditionOperator.Equal, c.Operator);
            object? actual = c.AttributeName == entity.LogicalName + "id" ? entity.Id :
                entity.Attributes.TryGetValue(c.AttributeName, out var attribute) ? attribute : null;
            if (actual is EntityReference reference) actual = reference.Id;
            return actual is string text && c.Values[0] is string value
                ? string.Equals(text, value, StringComparison.OrdinalIgnoreCase) : Equals(actual, c.Values[0]);
        }
        return filter.Conditions.Count == 0 || (filter.FilterOperator == LogicalOperator.Or
            ? filter.Conditions.Any(Condition) : filter.Conditions.All(Condition));
    }

    private Guid AddPackage(string name, string uniqueName, byte[] content, bool managed = false)
    {
        var id = Guid.NewGuid();
        _packages.Add(new Entity("pluginpackage", id)
        {
            ["name"] = name, ["uniquename"] = uniqueName, ["version"] = "1.0.0", ["ismanaged"] = managed
        });
        _stored[id] = content;
        return id;
    }

    private static byte[] Package(string id = "Example.Plugin", string version = "1.0.0", string? extraNuspec = null)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("package.nuspec").Open()))
                writer.Write($"<package><metadata><id>{id}</id><version>{version}</version></metadata></package>");
            if (extraNuspec is not null)
            {
                using var duplicate = new StreamWriter(zip.CreateEntry(extraNuspec).Open());
                duplicate.Write("<package><metadata><id>Other.Artifact</id><version>1.0.0</version></metadata></package>");
            }
            using var dll = zip.CreateEntry("lib/net48/Plugin.dll").Open();
            dll.Write([1, 2, 3]);
        }
        return memory.ToArray();
    }

    [Theory]
    [InlineData("Example.Plugin", "ppds_Example.Plugin")]
    [InlineData("ppds_Example.Plugin", "ppds_ppds_Example.Plugin")]
    public async Task CanonicalCreate_BindsVerifiedPublisherAndLiteralArtifactId(string artifact, string canonical)
    {
        var bytes = Package(artifact);
        var plan = await _service.ResolvePackageAsync(bytes, "TestSolution");
        Assert.True(plan.IsCreate);
        Assert.Equal(canonical, plan.RegisteredName);
        Assert.Equal(canonical, plan.RegisteredUniqueName);
        Assert.Equal(_solutionId, plan.SolutionId);
        Assert.Equal(_publisherId, plan.PublisherId);
        Assert.Empty(_writes);
        var id = await _service.UpsertResolvedPackageAsync(plan, bytes);
        var write = Assert.IsType<CreateRequest>(Assert.Single(_writes));
        Assert.Equal(canonical, write.Target.GetAttributeValue<string>("name"));
        Assert.Equal(canonical, write.Target.GetAttributeValue<string>("uniquename"));
        Assert.Equal("TestSolution", write.Parameters["SolutionUniqueName"]);
        var verified = await _service.VerifyUploadedPackageAsync(plan, id);
        Assert.Equal(id, verified.ExistingPackageId);
        Assert.Equal(plan.ArtifactSha256, verified.ExistingArtifactSha256);
        Assert.True(plan.IsCreate); // Immutable historical pre-upload observation.
    }

    [Fact]
    public async Task CanonicalAndLegacyMatches_RefuseCompetingIdsBeforeDownload()
    {
        var bytes = Package();
        AddPackage("A", "ppds_Example.Plugin", bytes);
        AddPackage("B", "Example.Plugin", bytes);
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.ResolvePackageAsync(bytes, "TestSolution"));
        Assert.Equal(ErrorCodes.Operation.Duplicate, error.ErrorCode);
        Assert.Equal(0, _downloads);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task UniqueName_OutranksDisplayNameCollision()
    {
        var bytes = Package();
        AddPackage("ppds_Example.Plugin", "different", Package("Other"));
        var expected = AddPackage("Friendly name", "ppds_Example.Plugin", bytes);
        var plan = await _service.ResolvePackageAsync(bytes, "TestSolution");
        Assert.Equal(expected, plan.ExistingPackageId);
        Assert.Equal("Friendly name", plan.RegisteredName);
        Assert.Equal(expected, (await _service.GetPackageByNameAsync("ppds_Example.Plugin"))!.Id);
    }

    [Theory]
    [InlineData("Example.Plugin")]
    [InlineData("ppds_Example.Plugin")]
    public async Task ExistingPackage_VerifiesStoredArtifactAndPreservesNames(string uniqueName)
    {
        var stored = Package();
        var incoming = Package(version: "2.0.0");
        var id = AddPackage("Friendly name", uniqueName, stored);
        var plan = await _service.ResolvePackageAsync(incoming, "TestSolution");
        Assert.Equal(id, plan.ExistingPackageId);
        var uploaded = await _service.UpsertResolvedPackageAsync(plan, incoming);
        var request = Assert.IsType<UpdateRequest>(Assert.Single(_writes));
        Assert.Equal(id, request.Target.Id);
        // The generated Id setter also writes the primary-key attribute.
        Assert.Equal(new[] { "content", "pluginpackageid" }, request.Target.Attributes.Keys.Order());
        Assert.Equal(id, request.Target.GetAttributeValue<Guid>(PluginPackage.Fields.PluginPackageId));
        Assert.DoesNotContain(PluginPackage.Fields.Name, request.Target.Attributes.Keys);
        Assert.DoesNotContain(PluginPackage.Fields.UniqueName, request.Target.Attributes.Keys);
        Assert.DoesNotContain(PluginPackage.Fields.Version, request.Target.Attributes.Keys);
        var successor = await _service.VerifyUploadedPackageAsync(plan, uploaded);
        Assert.NotEqual(plan.ExistingArtifactSha256, successor.ExistingArtifactSha256);
        await _service.ValidatePackageResolutionAsync(successor);
    }

    [Theory]
    [InlineData("guid")]
    [InlineData("unique")]
    [InlineData("display")]
    public async Task ExplicitUpdate_PinsIdAndNeverCreates(string selectorKind)
    {
        var bytes = Package();
        var id = AddPackage("Friendly name", "otherprefix_Example.Plugin", bytes);
        var selector = selectorKind == "guid" ? id.ToString() : selectorKind == "unique" ? "otherprefix_Example.Plugin" : "Friendly name";
        var plan = await _service.ResolvePackageAsync(bytes, existingSelector: selector);
        Assert.Equal(id, plan.ExistingPackageId);
        _packages.Clear();
        await Assert.ThrowsAsync<PpdsException>(() => _service.UpsertResolvedPackageAsync(plan, bytes));
        Assert.Empty(_writes);
    }

    [Theory]
    [InlineData("replaced")]
    [InlineData("content")]
    [InlineData("publisher")]
    [InlineData("new-collision")]
    [InlineData("managed")]
    public async Task StaleResolution_RefusesBeforeWrites(string change)
    {
        var bytes = Package();
        var id = AddPackage("Friendly", "ppds_Example.Plugin", bytes);
        var plan = await _service.ResolvePackageAsync(bytes, "TestSolution");
        switch (change)
        {
            case "replaced": _packages.Clear(); AddPackage("Friendly", "ppds_Example.Plugin", bytes); break;
            case "content": _stored[id] = Package(version: "1.0.1"); break;
            case "publisher": _prefix = "other"; break;
            case "new-collision": AddPackage("Legacy", "Example.Plugin", bytes); break;
            case "managed": _packages[0]["ismanaged"] = true; break;
        }
        await Assert.ThrowsAsync<PpdsException>(() => _service.UpsertResolvedPackageAsync(plan, bytes));
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task ChangedArtifactBytes_RefusesBeforeRequests()
    {
        var plan = await _service.ResolvePackageAsync(Package(), "TestSolution");
        var reads = _queries.Count;
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.UpsertResolvedPackageAsync(plan, Package(version: "2.0.0")));
        Assert.Equal(ErrorCodes.Plugin.PackageIdentityMismatch, error.ErrorCode);
        Assert.Equal(reads, _queries.Count);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task NoSolution_UpdatesExactLegacyButRefusesCreate()
    {
        var bytes = Package();
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.ResolvePackageAsync(bytes));
        Assert.Equal(ErrorCodes.Validation.RequiredField, error.ErrorCode);
        var id = AddPackage("Friendly", "Example.Plugin", bytes);
        Assert.Equal(id, (await _service.ResolvePackageAsync(bytes)).ExistingPackageId);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task DisplayFallback_RequiresSelectedSolutionMembership()
    {
        var bytes = Package();
        var id = AddPackage("ppds_Example.Plugin", "CustomRegisteredIdentity", bytes);
        _membership = false;
        await Assert.ThrowsAsync<PpdsException>(() => _service.ResolvePackageAsync(bytes, "TestSolution"));
        Assert.Equal(0, _downloads);
        _membership = true;
        Assert.Equal(id, (await _service.ResolvePackageAsync(bytes, "TestSolution")).ExistingPackageId);
        Assert.Empty(_writes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("managed")]
    [InlineData("prefix")]
    public async Task UnknownSolutionOrPublisher_RefusesBeforeWrites(string problem)
    {
        _solutionPresent = problem != "missing";
        _solutionManaged = problem == "managed";
        if (problem == "prefix") _prefix = null;
        await Assert.ThrowsAsync<PpdsException>(() => _service.ResolvePackageAsync(Package(), "TestSolution"));
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task StoredArtifactMismatch_RefusesEverySelectedTarget()
    {
        AddPackage("Canonical", "ppds_Example.Plugin", Package("Other.Artifact"));
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.ResolvePackageAsync(Package(), "TestSolution"));
        Assert.Equal(ErrorCodes.Plugin.PackageIdentityMismatch, error.ErrorCode);
        Assert.Equal(1, _downloads);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task DuplicateAssemblyName_RefusesAmbiguousBinding()
    {
        var packageId = Guid.NewGuid();
        foreach (var _ in Enumerable.Range(0, 2))
            _assemblies.Add(new Entity("pluginassembly", Guid.NewGuid())
            {
                ["name"] = "Plugin", ["packageid"] = new EntityReference("pluginpackage", packageId)
            });
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.GetAssemblyIdForPackageAsync(packageId, "Plugin"));
        Assert.Equal(ErrorCodes.Operation.Duplicate, error.ErrorCode);
    }

    [Fact]
    public async Task AcknowledgedButUnchangedUpload_DoesNotAdvanceObservation()
    {
        var incoming = Package(version: "2.0.0");
        var id = AddPackage("Friendly", "ppds_Example.Plugin", Package());
        var plan = await _service.ResolvePackageAsync(incoming, "TestSolution");
        _applyWrites = false;
        Assert.Equal(id, await _service.UpsertResolvedPackageAsync(plan, incoming));
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.VerifyUploadedPackageAsync(plan, id));
        Assert.Equal(ErrorCodes.Plugin.PackageIdentityChanged, error.ErrorCode);
        Assert.Single(_writes); // No retry or recovery mutation.
    }

    [Theory]
    [InlineData(95, true)]
    [InlineData(96, false)]
    public async Task CanonicalLength_IsCheckedBeforeAnyWrite(int artifactLength, bool valid)
    {
        var bytes = Package(new string('a', artifactLength));
        if (valid) Assert.Equal(100, (await _service.ResolvePackageAsync(bytes, "TestSolution")).RegisteredName.Length);
        else await Assert.ThrowsAsync<PpdsException>(() => _service.ResolvePackageAsync(bytes, "TestSolution"));
        Assert.Empty(_writes);
    }

    [Theory]
    [InlineData(true, "other.nuspec")]
    [InlineData(true, "package.nuspec")]
    [InlineData(false, "other.nuspec")]
    [InlineData(false, "package.nuspec")]
    public async Task MultipleRootNuspecs_RejectIncomingAndStoredAmbiguity(bool incoming, string extraEntry)
    {
        var ambiguous = Package(extraNuspec: extraEntry);
        var bytes = incoming ? ambiguous : Package();
        if (!incoming) AddPackage("Friendly", "ppds_Example.Plugin", ambiguous);
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.ResolvePackageAsync(bytes, "TestSolution"));
        Assert.Equal(ErrorCodes.Validation.InvalidValue, error.ErrorCode);
        Assert.Contains("multiple root-level", error.Message);
        if (incoming) Assert.Empty(_queries);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task VerifiedAutomaticSuccessor_StillRejectsNewIdentityCollision()
    {
        var incoming = Package(version: "2.0.0");
        AddPackage("Friendly", "ppds_Example.Plugin", Package());
        var plan = await _service.ResolvePackageAsync(incoming, "TestSolution");
        var id = await _service.UpsertResolvedPackageAsync(plan, incoming);
        var successor = await _service.VerifyUploadedPackageAsync(plan, id);
        Assert.Null(successor.ExplicitSelector);
        Assert.Equal("UniqueName", successor.MatchKind);
        AddPackage("Legacy contender", "Example.Plugin", incoming);
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.ValidatePackageResolutionAsync(successor));
        Assert.Equal(ErrorCodes.Operation.Duplicate, error.ErrorCode);
        Assert.Single(_writes);
    }

    [Fact]
    public async Task ExplicitDisplaySelector_RetainsMembershipRequirementAfterUpload()
    {
        var bytes = Package();
        AddPackage("Friendly", "CustomIdentity", bytes);
        _membership = false;
        await Assert.ThrowsAsync<PpdsException>(() => _service.ResolvePackageAsync(bytes, "TestSolution", "Friendly"));
        Assert.Equal(0, _downloads);
        _membership = true;
        var plan = await _service.ResolvePackageAsync(bytes, "TestSolution", "Friendly");
        var id = await _service.UpsertResolvedPackageAsync(plan, bytes);
        var successor = await _service.VerifyUploadedPackageAsync(plan, id);
        Assert.Equal("Friendly", successor.ExplicitSelector);
        Assert.Equal("ExplicitName", successor.MatchKind);
        _membership = false;
        await Assert.ThrowsAsync<PpdsException>(() => _service.ValidatePackageResolutionAsync(successor));
        Assert.Single(_writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SequentialAliasCreation_InvalidatesOtherAutomaticResolution(bool secondAlreadyExists)
    {
        var firstBytes = Package("Thing");
        var secondBytes = Package("ppds_Thing");
        if (secondAlreadyExists)
            AddPackage("Existing second package", "ppds_ppds_Thing", secondBytes);

        // Both independent resolutions are valid before either artifact is uploaded.
        var first = await _service.ResolvePackageAsync(firstBytes, "TestSolution");
        var second = await _service.ResolvePackageAsync(secondBytes, "TestSolution");
        Assert.True(first.IsCreate);
        Assert.Equal(!secondAlreadyExists, second.IsCreate);
        Assert.Equal("ppds_Thing", first.RegisteredUniqueName);
        Assert.Equal("ppds_ppds_Thing", second.RegisteredUniqueName);
        Assert.Empty(_writes);

        var firstId = await _service.UpsertResolvedPackageAsync(first, firstBytes);
        var verified = await _service.VerifyUploadedPackageAsync(first, firstId);
        Assert.Equal(firstId, verified.ExistingPackageId);
        Assert.IsType<CreateRequest>(Assert.Single(_writes));

        // The first canonical identity is now the second artifact's legacy candidate.
        // Without a batch-level guard this creates a partial deployment: one write
        // succeeds, then the second plan can no longer safely select its original target.
        var error = await Assert.ThrowsAsync<PpdsException>(() =>
            _service.UpsertResolvedPackageAsync(second, secondBytes));
        Assert.Equal(secondAlreadyExists ? ErrorCodes.Operation.Duplicate :
            ErrorCodes.Plugin.PackageIdentityMismatch, error.ErrorCode);
        if (!secondAlreadyExists)
            Assert.Contains("contains artifact 'Thing', not 'ppds_Thing'", error.Message);
        Assert.IsType<CreateRequest>(Assert.Single(_writes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupLookup_AbsenceIsNotCreationAndRevalidatesWithoutPublisherInference(bool withSolution)
    {
        var bytes = Package();
        AddPackage("Unrelated sibling", "different_Example.Plugin", Package("Different.Artifact"));
        var plan = await _service.ResolvePackageForCleanupAsync(bytes, withSolution ? "TestSolution" : null);
        Assert.True(plan.IsCleanupLookup);
        Assert.False(plan.IsCreate);
        Assert.Null(plan.ExistingPackageId);
        Assert.Equal("Absent", plan.MatchKind);
        Assert.Equal("Example.Plugin", plan.RegisteredName);
        Assert.Null(plan.RegisteredUniqueName);
        Assert.Equal(withSolution ? _solutionId : (Guid?)null, plan.SolutionId);
        await _service.ValidatePackageResolutionAsync(plan);
        if (!withSolution)
        {
            Assert.Null(plan.PublisherPrefix);
            Assert.Null(plan.PublisherId);
            Assert.All(_queries, query => Assert.Equal("pluginpackage", query.EntityName));
        }
        Assert.Equal(0, _downloads);
        Assert.Empty(_writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupLookup_PresenceChangesInvalidateTheOriginalObservation(bool initiallyExists)
    {
        var bytes = Package();
        if (initiallyExists) AddPackage("Friendly", "Example.Plugin", bytes);
        var plan = await _service.ResolvePackageForCleanupAsync(bytes);
        Assert.True(plan.IsCleanupLookup);
        if (initiallyExists) _packages.Clear();
        else AddPackage("New package", "Example.Plugin", bytes);
        var error = await Assert.ThrowsAsync<PpdsException>(() => _service.ValidatePackageResolutionAsync(plan));
        Assert.Equal(ErrorCodes.Plugin.PackageIdentityChanged, error.ErrorCode);
        Assert.Empty(_writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupLookup_CannotAuthorizeUploadOrAcknowledgeUpload(bool existing)
    {
        var bytes = Package();
        if (existing) AddPackage("Friendly", "Example.Plugin", bytes);
        var plan = await _service.ResolvePackageForCleanupAsync(bytes);
        var clientCalls = _client.Invocations.Count;
        var poolCalls = _pool.Invocations.Count;
        // Invalid bytes would otherwise reach metadata validation. Intent refuses first.
        var upload = await Assert.ThrowsAsync<PpdsException>(() => _service.UpsertResolvedPackageAsync(plan, []));
        var verify = await Assert.ThrowsAsync<PpdsException>(() =>
            _service.VerifyUploadedPackageAsync(plan, plan.ExistingPackageId ?? Guid.NewGuid()));
        Assert.Equal(ErrorCodes.Plugin.PackageIdentityMismatch, upload.ErrorCode);
        Assert.Equal(ErrorCodes.Plugin.PackageIdentityMismatch, verify.ErrorCode);
        Assert.Equal(clientCalls, _client.Invocations.Count);
        Assert.Equal(poolCalls, _pool.Invocations.Count);
        Assert.Empty(_writes);
    }

    [Theory]
    [InlineData("collision", ErrorCodes.Operation.Duplicate)]
    [InlineData("stored-id", ErrorCodes.Plugin.PackageIdentityMismatch)]
    [InlineData("unknown-solution", ErrorCodes.Validation.InvalidValue)]
    [InlineData("managed", ErrorCodes.Validation.InvalidValue)]
    [InlineData("missing-selector", ErrorCodes.Plugin.NotFound)]
    public async Task CleanupLookup_PreservesTargetAndSolutionErrors(string scenario, string expectedCode)
    {
        var bytes = Package();
        string? solution = null;
        string? selector = null;
        switch (scenario)
        {
            case "collision":
                AddPackage("First", "Example.Plugin", bytes);
                AddPackage("Second", "Example.Plugin", bytes);
                break;
            case "stored-id": AddPackage("Wrong artifact", "Example.Plugin", Package("Different.Artifact")); break;
            case "unknown-solution": _solutionPresent = false; solution = "TestSolution"; break;
            case "managed": AddPackage("Managed", "Example.Plugin", bytes, managed: true); break;
            case "missing-selector": selector = "Missing explicit package"; break;
        }
        var error = await Assert.ThrowsAsync<PpdsException>(() =>
            _service.ResolvePackageForCleanupAsync(bytes, solution, selector));
        Assert.Equal(expectedCode, error.ErrorCode);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task InterfaceDefaults_PreserveImplementorCompatibilityAndFailClosed()
    {
        var mock = new Mock<IPluginRegistrationService> { CallBase = true };
        var plan = new PluginPackageResolution
        {
            ArtifactId = "Example", ArtifactVersion = "1.0.0", ArtifactSha256 = "unused", RegisteredName = "ppds_Example"
        };
        async Task Unsupported(Func<Task> call)
        {
            var error = await Assert.ThrowsAsync<PpdsException>(call);
            Assert.Equal(ErrorCodes.Operation.NotSupported, error.ErrorCode);
        }
        await Unsupported(() => mock.Object.ResolvePackageAsync([]));
        await Unsupported(() => mock.Object.ResolvePackageForCleanupAsync([]));
        await Unsupported(() => mock.Object.ValidatePackageResolutionAsync(plan));
        await Unsupported(() => mock.Object.UpsertResolvedPackageAsync(plan, []));
        await Unsupported(() => mock.Object.VerifyUploadedPackageAsync(plan, Guid.NewGuid()));
        foreach (var name in new[] { "ResolvePackageAsync", "ResolvePackageForCleanupAsync", "ValidatePackageResolutionAsync", "UpsertResolvedPackageAsync", "VerifyUploadedPackageAsync" })
            Assert.False(typeof(IPluginRegistrationService).GetMethod(name)!.IsAbstract);
        mock.Verify(s => s.UpsertPackageAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
