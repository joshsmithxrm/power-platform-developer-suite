using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Cli.Commands.Metadata.Attribute;
using PPDS.Cli.Commands.Metadata.Column;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;
using PPDS.Dataverse.Security;
using Xunit;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

[Trait("Category", "Unit")]
public sealed class GlobalChoiceReferenceTests : IDisposable
{
    private const string Stored = "new_ServiceType";
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Mock<IDataverseConnectionPool> _pool = new(MockBehavior.Strict);
    private readonly Mock<ICachedMetadataProvider> _cache = new();
    private readonly Mock<IMetadataAuthoringProgressReporter> _progress = new();
    private readonly List<OrganizationRequest> _requests = [];
    private readonly List<string> _queries = [];
    private readonly DataverseMetadataAuthoringService _service;
    private OptionSetMetadataBase[] _catalog = [Choice(Stored)];
    private readonly List<string> _leaseEvents = [];
    private int _leaseCount;
    private Exception? _checkoutFailure;
    private Exception? _lookupFailure;
    private OrganizationResponse? _invalidResponse;
    private bool _returnNullResponse;

    public GlobalChoiceReferenceTests()
    {
        _pool.Setup(p => p.GetClientAsync(null, null, _cancellation.Token)).ReturnsAsync(CheckOutClient);
        _service = new DataverseMetadataAuthoringService(_pool.Object, new SchemaValidator(),
            new InactiveFakeShakedownGuard(), cacheProvider: _cache.Object);
    }

    private IPooledClient CheckOutClient()
    {
        var lease = ++_leaseCount;
        _leaseEvents.Add($"checkout {lease}");
        if (_checkoutFailure != null && _queries.Contains("publisher"))
            throw _checkoutFailure;

        var client = new Mock<IPooledClient>();
        client.Setup(c => c.DisposeAsync()).Callback(() => _leaseEvents.Add($"dispose {lease}"))
            .Returns(ValueTask.CompletedTask);
        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), _cancellation.Token))
            .Returns<QueryBase, CancellationToken>((query, _) =>
            {
                var table = Assert.IsType<QueryExpression>(query).EntityName;
                _queries.Add(table);
                var row = new Entity(table);
                if (table == "solution") row["publisherid"] = new EntityReference("publisher", Guid.NewGuid());
                else if (table == "publisher") row["customizationprefix"] = "new";
                else throw new InvalidOperationException("Unexpected metadata setup query.");
                return Task.FromResult(new EntityCollection(new List<Entity> { row }));
            });
        client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), _cancellation.Token))
            .Returns<OrganizationRequest, CancellationToken>((request, _) =>
            {
                _requests.Add(request);
                _leaseEvents.Add($"{request.RequestName} {lease}");
                OrganizationResponse response = request switch
                {
                    RetrieveAllOptionSetsRequest when _lookupFailure != null => throw _lookupFailure,
                    RetrieveAllOptionSetsRequest when _invalidResponse != null => _invalidResponse,
                    RetrieveAllOptionSetsRequest when _returnNullResponse => null!,
                    RetrieveAllOptionSetsRequest lookup => new RetrieveAllOptionSetsResponse
                    {
                        Results = new ParameterCollection { ["OptionSetMetadata"] = lookup.RetrieveAsIfPublished ? _catalog : Array.Empty<OptionSetMetadataBase>() }
                    },
                    CreateAttributeRequest => new CreateAttributeResponse
                    {
                        Results = new ParameterCollection { ["AttributeId"] = Guid.NewGuid() }
                    },
                    PublishXmlRequest => new PublishXmlResponse(),
                    _ => throw new InvalidOperationException("Unexpected SDK request.")
                };
                return Task.FromResult(response);
            });
        return client.Object;
    }

    [Theory, Trait("AC", "AC-78"), Trait("AC", "AC-81")]
    [InlineData(SchemaColumnType.Choice, "NEW_SERVICETYPE", Stored, true)]
    [InlineData(SchemaColumnType.Choices, "new_servicetype", Stored, false)]
    [InlineData(SchemaColumnType.Choice, "new_ServiceType", "new_servicetype", false)]
    [InlineData(SchemaColumnType.Choices, Stored, Stored, true)]
    public async Task Attach_UsesCanonicalName(SchemaColumnType type, string input, string canonical, bool publish)
    {
        var definition = Choice(canonical);
        definition.DisplayName = new Label("Localized display", 1033);
        definition.Options.Add(new OptionMetadata(new Label("One", 1033), 1));
        _catalog = [definition];
        var dto = Request(type, input);
        dto.Publish = publish;
        dto.DefaultValue = 1;
        var result = await CreateAsync(dto);
        Assert.False(result.WasDryRun);
        Assert.Equal(input, dto.OptionSetName);
        var lookup = Assert.IsType<RetrieveAllOptionSetsRequest>(_requests[0]);
        Assert.True(lookup.RetrieveAsIfPublished);
        _progress.Verify(p => p.ReportPhase("Resolving global choice", input), Times.Once);
        var create = Assert.IsType<CreateAttributeRequest>(_requests[1]);
        Assert.Equal("account", create.EntityName);
        Assert.Equal("TestSolution", create.SolutionUniqueName);
        Assert.Equal("new_TestField", create.Attribute.SchemaName);
        EnumAttributeMetadata column;
        if (type == SchemaColumnType.Choice) column = Assert.IsType<PicklistAttributeMetadata>(create.Attribute);
        else column = Assert.IsType<MultiSelectPicklistAttributeMetadata>(create.Attribute);
        Assert.Equal(1, column.DefaultFormValue);
        Assert.True(column.OptionSet.IsGlobal);
        Assert.Equal(canonical, column.OptionSet.Name);
        Assert.NotSame(definition, column.OptionSet);
        Assert.Empty(column.OptionSet.Options);
        Assert.Null(column.OptionSet.DisplayName);
        Assert.Equal(publish ? 3 : 2, _requests.Count);
        if (publish) Assert.Contains("<entity>account</entity>", Assert.IsType<PublishXmlRequest>(_requests[2]).ParameterXml);
        _cache.Verify(c => c.InvalidateEntity("account"), Times.Once);
    }

    [Fact, Trait("AC", "AC-78")]
    public async Task ExactName_WinsOverCaseVariant()
    {
        // Include an unrelated Boolean, and an earlier case variant, ahead of the exact name.
        _catalog = [new BooleanOptionSetMetadata { Name = "new_other" }, Choice("new_servicetype"), Choice(Stored, type: null)];
        await CreateAsync(Request(SchemaColumnType.Choice, Stored));
        Assert.True(Assert.Single(_requests.OfType<RetrieveAllOptionSetsRequest>()).RetrieveAsIfPublished);
        var create = Assert.Single(_requests.OfType<CreateAttributeRequest>());
        Assert.Equal(Stored, Assert.IsType<PicklistAttributeMetadata>(create.Attribute).OptionSet.Name);
    }

    [Theory, Trait("AC", "AC-79")]
    [InlineData("absent", MetadataErrorCodes.OptionNotFound)]
    [InlineData("ambiguous", MetadataErrorCodes.InvalidConstraint)]
    [InlineData("duplicate-exact", MetadataErrorCodes.InvalidConstraint)]
    [InlineData("mixed-types", MetadataErrorCodes.InvalidConstraint)]
    public async Task UnresolvableName_StopsBeforeCreate(string situation, string code)
    {
        _catalog = situation switch
        {
            "absent" => [Choice("new_Unrelated")],
            "ambiguous" => [Choice("NEW_SERVICETYPE"), Choice("new_servicetype")],
            "duplicate-exact" => [Choice(Stored), Choice(Stored)],
            "mixed-types" => [Choice("NEW_SERVICETYPE"), new BooleanOptionSetMetadata { Name = "new_servicetype" }],
            _ => throw new ArgumentOutOfRangeException(nameof(situation))
        };
        var failure = await Assert.ThrowsAsync<MetadataValidationException>(() => CreateAsync(Request(SchemaColumnType.Choices, Stored, publish: true)));
        Assert.Equal(code, failure.ErrorCode);
        Assert.Equal("OptionSetName", failure.Field);
        Assert.Contains(Stored, failure.Message);
        AssertLookupOnly();
    }

    [Theory, Trait("AC", "AC-79")]
    [InlineData("boolean")]
    [InlineData("state")]
    [InlineData("status")]
    [InlineData("local")]
    public async Task IncompatibleChoice_StopsBeforeCreate(string kind)
    {
        // Exact-name selection must not fall through to the compatible folded match.
        OptionSetMetadataBase selected = kind switch
        {
            "boolean" => new BooleanOptionSetMetadata { Name = Stored, IsGlobal = true },
            "state" => Choice(Stored, OptionSetType.State),
            "status" => Choice(Stored, OptionSetType.Status),
            "local" => new OptionSetMetadata { Name = Stored, IsGlobal = false },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        _catalog = [Choice("new_servicetype"), selected];
        var failure = await Assert.ThrowsAsync<MetadataValidationException>(() => CreateAsync(Request(SchemaColumnType.Choice, Stored, publish: true)));
        Assert.Equal(MetadataErrorCodes.InvalidConstraint, failure.ErrorCode);
        Assert.Equal("OptionSetName", failure.Field);
        AssertLookupOnly();
    }

    [Fact, Trait("AC", "AC-80")]
    public async Task ConsecutiveCreates_ReadCurrentCatalog()
    {
        var dto = Request(SchemaColumnType.Choices, "NEW_SERVICETYPE");
        await CreateAsync(dto);
        _catalog = [Choice("new_serviceTYPE")];
        dto.SchemaName = "new_NextField";
        await CreateAsync(dto);
        Assert.Equal(new[] { Stored, "new_serviceTYPE" }, _requests.OfType<CreateAttributeRequest>()
            .Select(c => Assert.IsType<MultiSelectPicklistAttributeMetadata>(c.Attribute).OptionSet.Name));
        Assert.Equal(2, _requests.OfType<RetrieveAllOptionSetsRequest>().Count());
    }

    [Fact, Trait("AC", "AC-80")]
    public async Task CatalogLease_IsReturnedBeforeCreateCheckout()
    {
        await CreateAsync(Request(SchemaColumnType.Choice, Stored));
        Assert.Equal(new[]
        {
            "checkout 1", "dispose 1", // existing solution and publisher queries
            "checkout 2", "RetrieveAllOptionSets 2", "dispose 2",
            "checkout 3", "CreateAttribute 3", "dispose 3"
        }, _leaseEvents);
    }

    [Fact, Trait("AC", "AC-81")]
    public async Task Publish_ObtainsLeaseAfterCreateLeaseIsReturned()
    {
        _cache.Setup(c => c.InvalidateEntity("account")).Callback(() => _leaseEvents.Add("invalidate"));
        await CreateAsync(Request(SchemaColumnType.Choice, Stored, publish: true));
        Assert.Equal(new[]
        {
            "checkout 1", "dispose 1",
            "checkout 2", "RetrieveAllOptionSets 2", "dispose 2",
            "checkout 3", "CreateAttribute 3", "dispose 3",
            "invalidate", "checkout 4", "PublishXml 4", "dispose 4"
        }, _leaseEvents);
    }

    [Theory, Trait("AC", "AC-80")]
    [InlineData("timeout", ErrorCodes.Connection.Timeout)]
    [InlineData("configuration", ErrorCodes.Validation.InvalidValue)]
    [InlineData("missing-argument", ErrorCodes.Validation.RequiredField)]
    public async Task CatalogCheckoutFailure_IsCodedAndSkipsCreate(string kind, string expectedCode)
    {
        _checkoutFailure = kind switch
        {
            "timeout" => new TimeoutException("Synthetic checkout timeout."),
            "configuration" => new ConfigurationException("Synthetic invalid pool configuration."),
            "missing-argument" => new ArgumentNullException("syntheticArgument"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var wrapped = await Assert.ThrowsAsync<PpdsException>(() => CreateAsync(Request(SchemaColumnType.Choices, Stored, publish: true)));
        Assert.Equal(expectedCode, wrapped.ErrorCode);
        Assert.Same(_checkoutFailure, wrapped.InnerException);
        Assert.Equal(ExceptionMapper.ToExitCode(_checkoutFailure), ExceptionMapper.ToExitCode(wrapped));
        Assert.Equal(new[] { "checkout 1", "dispose 1", "checkout 2" }, _leaseEvents);
        Assert.Empty(_requests);
        _cache.VerifyNoOtherCalls();
    }

    [Theory, Trait("AC", "AC-80")]
    [InlineData("sdk")]
    [InlineData("auth")]
    [InlineData("custom-auth-code")]
    [InlineData("expired")]
    [InlineData("permission")]
    [InlineData("connection")]
    [InlineData("throttle")]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("coded")]
    [InlineData("validation")]
    public async Task LookupFailure_StopsBeforeCreate(string kind)
    {
        _lookupFailure = kind switch
        {
            "auth" => new AuthenticationException("Synthetic credential fault"),
            "custom-auth-code" => new AuthenticationException("Synthetic credential fault", "Synthetic.ProviderCode"),
            "expired" => new DataverseAuthenticationException("Synthetic token expiry", true),
            "permission" => new DataverseAuthenticationException("Synthetic access fault", false),
            "connection" => new DataverseConnectionException("Synthetic connection fault"),
            "throttle" => new ServiceProtectionException("Synthetic throttling"),
            "timeout" => new TimeoutException("Synthetic timeout"),
            "cancel" => new OperationCanceledException(_cancellation.Token),
            "validation" => new MetadataValidationException(MetadataErrorCodes.InvalidConstraint, "Synthetic metadata constraint"),
            "coded" => new PpdsException("Synthetic.Code", "Synthetic coded fault"),
            _ => new InvalidOperationException("Synthetic SDK fault")
        };
        var failure = await Record.ExceptionAsync(() => CreateAsync(Request(SchemaColumnType.Choice, Stored, publish: true)));
        if (kind is "cancel" or "coded" or "validation")
        {
            Assert.Same(_lookupFailure, failure);
        }
        else
        {
            var wrapped = Assert.IsAssignableFrom<PpdsException>(failure);
            var expectedCode = kind == "sdk" ? MetadataErrorCodes.SdkOperationFailed : ExceptionMapper.Map(_lookupFailure).Code;
            Assert.Equal(expectedCode, wrapped.ErrorCode);
            if (_lookupFailure is DataverseAuthenticationException auth)
                Assert.Equal(auth.RequiresReauthentication, Assert.IsType<PpdsAuthException>(wrapped).RequiresReauthentication);
            Assert.Same(_lookupFailure, wrapped.InnerException);
            Assert.Equal(ExceptionMapper.ToExitCode(_lookupFailure), ExceptionMapper.ToExitCode(wrapped));
        }
        AssertLookupOnly();
    }

    [Theory, Trait("AC", "AC-80")]
    [InlineData("wrong-type")]
    [InlineData("no-definitions")]
    [InlineData("null-response")]
    public async Task InvalidCatalog_StopsBeforeCreate(string kind)
    {
        _returnNullResponse = kind == "null-response";
        _invalidResponse = kind switch
        {
            "wrong-type" => new OrganizationResponse(),
            "no-definitions" => new RetrieveAllOptionSetsResponse(),
            _ => null
        };
        var failure = await Assert.ThrowsAsync<PpdsException>(() => CreateAsync(Request(SchemaColumnType.Choices, Stored, publish: true)));
        Assert.Equal(MetadataErrorCodes.SdkOperationFailed, failure.ErrorCode);
        AssertLookupOnly();
    }

    [Theory, Trait("AC", "AC-81")]
    [InlineData(SchemaColumnType.Choice)]
    [InlineData(SchemaColumnType.Choices)]
    [InlineData(SchemaColumnType.Boolean)]
    [InlineData(SchemaColumnType.String)]
    public async Task UnrelatedPath_SkipsCatalog(SchemaColumnType type)
    {
        var local = type is SchemaColumnType.Choice or SchemaColumnType.Choices;
        var dto = Request(type, local ? null : Stored);
        if (local) dto.Options = [new OptionDefinition { Label = "Local", Value = 7 }];
        await CreateAsync(dto);
        var create = Assert.IsType<CreateAttributeRequest>(Assert.Single(_requests));
        if (local)
        {
            var options = Assert.IsAssignableFrom<EnumAttributeMetadata>(create.Attribute).OptionSet;
            Assert.False(options.IsGlobal);
            Assert.Equal(7, Assert.Single(options.Options).Value);
        }
        else if (type == SchemaColumnType.Boolean) Assert.IsType<BooleanAttributeMetadata>(create.Attribute);
        else Assert.IsType<StringAttributeMetadata>(create.Attribute);
    }

    [Fact, Trait("AC", "AC-81")]
    public async Task DryRunAndInputErrors_SkipCatalog()
    {
        var dto = Request(SchemaColumnType.Choice, Stored);
        dto.DryRun = true;
        dto.Publish = true;
        Assert.True((await CreateAsync(dto)).WasDryRun);
        Assert.Equal(new[] { "solution", "publisher" }, _queries);
        dto.DryRun = false;
        dto.SchemaName = "wrong_Prefix";
        await Assert.ThrowsAsync<MetadataValidationException>(() => CreateAsync(dto));
        Assert.Empty(_requests);
        _cache.VerifyNoOtherCalls();
    }

    [Fact, Trait("AC", "AC-81")]
    public async Task Guard_PrecedesAllLookups()
    {
        var blocked = new DataverseMetadataAuthoringService(_pool.Object, new SchemaValidator(), new ActiveFakeShakedownGuard());
        var failure = await Assert.ThrowsAsync<PpdsException>(() => blocked.CreateColumnAsync(Request(SchemaColumnType.Choice, Stored), ct: _cancellation.Token));
        Assert.Equal(ErrorCodes.Safety.ShakedownActive, failure.ErrorCode);
        _pool.VerifyNoOtherCalls();
    }

    [Theory, Trait("AC", "AC-78")]
    [InlineData(false, "--choice")]
    [InlineData(false, "--option-set-name")]
    [InlineData(true, "--option-set-name")]
    public async Task CliActions_PreserveSpellingAndAttachStoredName(bool deprecatedColumn, string flag)
    {
        const string input = "New_sErViCeType";
        var adapter = new Mock<IMetadataAuthoringService>(MockBehavior.Strict);
        CreateColumnRequest? captured = null;
        adapter.Setup(s => s.CreateColumnAsync(It.IsAny<CreateColumnRequest>(), null, _cancellation.Token))
            .Returns((CreateColumnRequest dto, IMetadataAuthoringProgressReporter? reporter, CancellationToken ct) =>
            {
                captured = dto;
                return _service.CreateColumnAsync(dto, reporter, ct);
            });

        Task<ServiceProvider> Provider(CancellationToken _) =>
            Task.FromResult(new ServiceCollection().AddSingleton(adapter.Object).BuildServiceProvider());
        var command = deprecatedColumn
            ? ColumnCommandGroup.CreateCreateCommand(Provider)
            : AttributeCommandGroup.CreateCreateCommand(Provider);
        var parsed = command.Parse(["--solution", "TestSolution", "--entity", "account", "--name", "new_TestField",
            "--display-name", "Test", "--type", "Choice", flag, input, "--output-format", "Json"]);
        Assert.Empty(parsed.Errors);
        var action = Assert.IsAssignableFrom<AsynchronousCommandLineAction>(parsed.Action);
        Assert.Equal(ExitCodes.Success, await action.InvokeAsync(parsed, _cancellation.Token));
        Assert.Equal(input, captured!.OptionSetName);
        var create = Assert.Single(_requests.OfType<CreateAttributeRequest>());
        Assert.Equal(Stored, Assert.IsType<PicklistAttributeMetadata>(create.Attribute).OptionSet.Name);
    }

    private void AssertLookupOnly()
    {
        Assert.True(Assert.IsType<RetrieveAllOptionSetsRequest>(Assert.Single(_requests)).RetrieveAsIfPublished);
        Assert.Equal(new[] { "checkout 1", "dispose 1", "checkout 2", "RetrieveAllOptionSets 2", "dispose 2" }, _leaseEvents);
        _cache.VerifyNoOtherCalls();
    }

    private Task<CreateColumnResult> CreateAsync(CreateColumnRequest dto) => _service.CreateColumnAsync(dto, _progress.Object, _cancellation.Token);

    private static CreateColumnRequest Request(SchemaColumnType type, string? name, bool publish = false) => new()
    {
        EntityLogicalName = "account",
        SchemaName = "new_TestField",
        DisplayName = "Test",
        SolutionUniqueName = "TestSolution",
        ColumnType = type,
        OptionSetName = name,
        Publish = publish
    };

    private static OptionSetMetadata Choice(string name, OptionSetType? type = OptionSetType.Picklist) => new()
    {
        Name = name,
        IsGlobal = true,
        OptionSetType = type
    };

    public void Dispose() => _cancellation.Dispose();
}
