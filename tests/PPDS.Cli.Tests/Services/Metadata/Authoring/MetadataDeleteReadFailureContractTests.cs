using System.Text.Json;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Commands.Metadata;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.TestHelpers;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

/// <summary>Raw SDK read and acquisition classifications, independently of already-wrapped auth errors.</summary>
[Trait("Category", "Unit")]
[Collection(nameof(ConsoleCaptureCollection))]
public sealed class MetadataDeleteReadFailureContractTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> ReadCases()
    {
        foreach (var stage in new[] { "identity", "dependency", "metadata-name", "record-name" })
        {
            foreach (var failure in new[] { "auth", "timeout", "generic" })
            {
                yield return [stage, failure];
            }
        }
    }

    public static IEnumerable<object[]> AcquisitionCases()
    {
        foreach (var stage in new[] { "identity", "dependency", "metadata-name", "record-name" })
        {
            foreach (var failure in new[] { "auth", "timeout" })
            {
                yield return [stage, failure];
            }
        }
    }

    [Theory]
    [MemberData(nameof(ReadCases))]
    [Trait("AC", "AC-118")]
    public Task RawReadFailures_PreserveClassificationCauseAndPrivateOutput(string stage, string failure)
        => Check(stage, failure, acquisition: false);

    [Theory]
    [MemberData(nameof(AcquisitionCases))]
    [Trait("AC", "AC-118")]
    public Task RawAcquisitionFailures_RetainExistingFriendlyMapping(string stage, string failure)
        => Check(stage, failure, acquisition: true);

    private async Task Check(string stage, string failure, bool acquisition)
    {
        using var f = new Fixture(stage, failure, acquisition);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var oldOut = Console.Out;
        var oldError = Console.Error;
        var observations = new Dictionary<string, object?>();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var caught = await Record.ExceptionAsync(() => f.Preview());
            f.Verify(); // Prove the precise raw throw site before classification assertions.
            observations["reachedRequestedFailure"] = true;
            var expectedCode = failure switch
            {
                "auth" => ErrorCodes.Auth.Expired,
                "timeout" => ErrorCodes.Connection.Timeout,
                _ => MetadataErrorCodes.SdkOperationFailed
            };
            if (stage is "identity" or "dependency")
            {
                var error = Assert.IsAssignableFrom<PpdsException>(caught);
                Assert.Same(f.Failure, error.InnerException);
                var writer = new JsonOutputWriter(stdout);
                writer.WriteError(ExceptionMapper.Map(error, context: "inspecting deletion dependencies"));
                new TextOutputWriter().WriteError(ExceptionMapper.Map(error, context: "inspecting deletion dependencies"));
                AssertPrivate(stdout.ToString(), stderr.ToString(), error.UserMessage);
                observations["errorCode"] = error.ErrorCode;
                observations["exitCode"] = ExceptionMapper.ToExitCode(error);
                Assert.Equal(expectedCode, error.ErrorCode);
                Assert.Equal(failure switch { "auth" => ExitCodes.AuthError, "timeout" => ExitCodes.ConnectionError, _ => ExitCodes.Failure }, ExceptionMapper.ToExitCode(error));
                if (failure == "auth")
                {
                    Assert.True(Assert.IsType<PpdsAuthException>(error).RequiresReauthentication);
                }
            }
            else
            {
                Assert.Null(caught);
                var preview = Assert.IsType<MetadataDeletePreviewResult>(f.Result);
                Assert.Equal("blocked", preview.Status);
                Assert.True(preview.TargetExistenceChecked);
                Assert.True(preview.DependenciesChecked);
                Assert.False(preview.DeletionFeasibilityChecked);
                Assert.Equal(2, preview.BlockingComponents.Length);
                Assert.Contains(preview.BlockingComponents, row => row.ComponentType == 90000 && row.ObjectId == Fixture.UnknownId);
                var known = Assert.Single(preview.BlockingComponents.Where(row => row.ObjectId == Fixture.DependentId));
                Assert.Null(known.Name);
                Assert.Equal("unavailable", known.NameResolutionStatus);
                var issue = Assert.Single(preview.NameResolutionIssues);
                Assert.Equal(Fixture.DependentId, issue.ObjectId);
                Assert.Equal(stage == "metadata-name" ? 1 : 26, issue.ComponentType);
                Assert.NotNull(preview.FailureCause);
                Assert.True(ReferenceEquals(f.Failure, preview.FailureCause) || ReferenceEquals(f.Failure, preview.FailureCause.InnerException));
                Assert.NotEmpty(preview.Warnings);
                var jsonExit = MetadataDeletePreview.WriteDependencies(new JsonOutputWriter(stdout), new { dryRun = true, preview }, preview);
                var textExit = MetadataDeletePreview.WriteDependencies(new TextOutputWriter(), new { dryRun = true, preview }, preview);
                Assert.Equal(ExitCodes.PreconditionFailed, jsonExit);
                Assert.Equal(jsonExit, textExit);
                AssertPrivate(stdout.ToString(), stderr.ToString(), JsonSerializer.Serialize(preview));
                observations["errorCode"] = issue.ErrorCode;
                observations["requiresReauthentication"] = issue.RequiresReauthentication;
                Assert.Equal(expectedCode, issue.ErrorCode);
                Assert.Equal(failure == "auth", issue.RequiresReauthentication);
            }
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
            f.Verify();
            output.WriteLine("PPDS_MD_READ_FAILURE_PROOF=" + JsonSerializer.Serialize(new
            {
                stage, failure, acquisition, f.Attempts, f.Acquired, f.Returned, f.Operations,
                failureTriggered = f.FailureTriggered, originalFailureType = f.Failure.GetType().FullName,
                exactTokenAndIdentityAssertionsPassed = true, observations,
                stdout = stdout.ToString(), stderr = stderr.ToString()
            }));
        }
    }

    private static void AssertPrivate(params string[] values)
    {
        foreach (var value in values)
        {
            Assert.DoesNotContain("SyntheticReadSecret", value);
            Assert.DoesNotContain("ClientSecret=", value);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly Guid TargetId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        internal static readonly Guid DependentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        internal static readonly Guid UnknownId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        private readonly string _stage;
        private readonly bool _acquisition;
        private readonly Mock<IDataverseConnectionPool> _pool = new(MockBehavior.Strict);
        private readonly Mock<ICachedMetadataProvider> _cache = new(MockBehavior.Strict);
        private readonly Mock<IShakedownGuard> _guard = new(MockBehavior.Strict);
        private readonly CancellationTokenSource _cts = new();
        private readonly List<int> _uses = [], _returned = [];
        private int _active;
        internal int Attempts { get; private set; }
        internal int Acquired { get; private set; }
        internal int Returned => _returned.Count;
        internal bool FailureTriggered { get; private set; }
        internal List<string> Operations { get; } = [];
        internal Exception Failure { get; }
        internal CancellationToken Token => _cts.Token;
        internal MetadataDeletePreviewResult? Result { get; private set; }
        internal DataverseMetadataAuthoringService Service { get; }

        internal Fixture(string stage, string failure, bool acquisition)
        {
            _stage = stage;
            _acquisition = acquisition;
            Failure = failure switch
            {
                "auth" => new DataverseAuthenticationException("ClientSecret=SyntheticReadSecret", requiresReauthentication: true),
                "timeout" => new TimeoutException("ClientSecret=SyntheticReadSecret"),
                _ => new InvalidOperationException("ClientSecret=SyntheticReadSecret")
            };
            _guard.Setup(g => g.EnsureCanMutate("metadata.table.delete"));
            _pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
                .Returns<DataverseClientOptions?, string?, CancellationToken>((_, _, token) =>
                {
                    Assert.Equal(Token, token);
                    Assert.Equal(0, _active);
                    Attempts++;
                    if (_acquisition && Attempts == ExpectedStageCount)
                    {
                        Assert.False(FailureTriggered);
                        FailureTriggered = true;
                        throw Failure;
                    }
                    _active++;
                    var id = ++Acquired;
                    var client = new Mock<IPooledClient>(MockBehavior.Strict);
                    client.Setup(c => c.DisposeAsync()).Callback(() => { _active--; _returned.Add(id); }).Returns(ValueTask.CompletedTask);
                    client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                        .Returns<OrganizationRequest, CancellationToken>((request, ct) => Task.FromResult(Execute(id, request, ct)));
                    client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
                        .Returns<QueryBase, CancellationToken>((query, ct) =>
                        {
                            Assert.Equal("record-name", _stage);
                            var q = Assert.IsType<QueryExpression>(query);
                            Assert.Equal("savedquery", q.EntityName);
                            Assert.False(q.ColumnSet.AllColumns);
                            Assert.Equal("name", Assert.Single(q.ColumnSet.Columns));
                            Assert.Empty(q.LinkEntities);
                            Assert.Empty(q.Criteria.Filters);
                            var condition = Assert.Single(q.Criteria.Conditions);
                            Assert.Equal("savedqueryid", condition.AttributeName);
                            Assert.Equal(ConditionOperator.Equal, condition.Operator);
                            Assert.Equal(DependentId, Assert.Single(condition.Values));
                            Begin(id, "record-name", ct);
                            throw new InvalidOperationException("The designated failure did not throw");
                        });
                    return Task.FromResult(client.Object);
                });
            Service = new DataverseMetadataAuthoringService(_pool.Object, new SchemaValidator(), _guard.Object, cacheProvider: _cache.Object);
        }

        private int ExpectedStageCount => _stage == "identity" ? 1 : _stage == "dependency" ? 2 : 3;

        private void Begin(int id, string stage, CancellationToken token)
        {
            Assert.Equal(Token, token);
            Assert.Equal(1, _active);
            Assert.DoesNotContain(id, _uses);
            _uses.Add(id);
            Operations.Add(stage);
            if (!_acquisition && stage == _stage)
            {
                Assert.False(FailureTriggered);
                FailureTriggered = true;
                throw Failure;
            }
        }

        private OrganizationResponse Execute(int id, OrganizationRequest request, CancellationToken token)
        {
            if (request is RetrieveDependenciesForDeleteRequest dependencies)
            {
                Assert.Equal(TargetId, dependencies.ObjectId);
                Assert.Equal(1, dependencies.ComponentType);
                Begin(id, "dependency", token);
                var type = _stage == "metadata-name" ? 1 : 26;
                var rows = new EntityCollection([
                    new Entity("dependency") { ["dependentcomponenttype"] = new OptionSetValue(type), ["dependentcomponentobjectid"] = DependentId },
                    new Entity("dependency") { ["dependentcomponenttype"] = new OptionSetValue(90000), ["dependentcomponentobjectid"] = UnknownId }
                ]);
                return new RetrieveDependenciesForDeleteResponse { Results = new ParameterCollection { ["EntityCollection"] = rows } };
            }
            var entity = Assert.IsType<RetrieveEntityRequest>(request);
            Assert.True(entity.RetrieveAsIfPublished);
            Assert.Equal(EntityFilters.Entity, entity.EntityFilters);
            if (entity.MetadataId == DependentId)
            {
                Assert.Equal("metadata-name", _stage);
                Assert.True(string.IsNullOrEmpty(entity.LogicalName));
                Begin(id, "metadata-name", token);
                throw new InvalidOperationException("The designated failure did not throw");
            }
            Assert.Equal("new_table", entity.LogicalName);
            Assert.True(entity.MetadataId == Guid.Empty);
            Begin(id, "identity", token);
            return new RetrieveEntityResponse { Results = new ParameterCollection { ["EntityMetadata"] = new EntityMetadata { LogicalName = "new_table", MetadataId = TargetId } } };
        }

        internal async Task<MetadataDeletePreviewResult> Preview()
        {
            Result = await Service.PreviewDeleteAsync(new MetadataDeleteTarget(MetadataDeleteKind.Table, "SyntheticSolution", "new_table"), Token);
            return Result;
        }

        internal void Verify()
        {
            Assert.True(FailureTriggered);
            Assert.Equal(ExpectedStageCount, Attempts);
            Assert.Equal(ExpectedStageCount - (_acquisition ? 1 : 0), Acquired);
            Assert.Equal(0, _active);
            Assert.Equal(Enumerable.Range(1, Acquired), _returned);
            Assert.Equal(Acquired, _uses.Count);
            Assert.All(_uses.GroupBy(i => i), group => Assert.Single(group));
            var expected = new[] { "identity", "dependency", _stage }.Take(Acquired);
            Assert.Equal(expected, Operations);
            _cache.VerifyNoOtherCalls();
            _guard.Verify(g => g.EnsureCanMutate("metadata.table.delete"), Times.Once);
            _guard.VerifyNoOtherCalls();
        }

        public void Dispose() => _cts.Dispose();
    }
}
