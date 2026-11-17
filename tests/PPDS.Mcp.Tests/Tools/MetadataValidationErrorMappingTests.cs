using System.Reflection;
using System.ServiceModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using ModelContextProtocol;
using Moq;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.Mcp.Infrastructure;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// Metadata validation failures from the shared authoring service reach MCP clients with their real error code,
/// message and field, like the CLI, instead of a generic internal error; server diagnostic text stays withheld.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MetadataValidationErrorMappingTests
{
    private const string Secret = "ClientSecret=synthetic-secret; Bearer synthetic-token";

    [Fact]
    [Trait("AC", "AC-144")]
    public void Helper_ReportsCodeMessageAndField_WithoutTheInnerFault()
    {
        var inner = new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { Message = Secret }, Secret);
        var validation = new MetadataValidationException(MetadataErrorCodes.InvalidConstraint,
            "Global choice 'mayo_X' matched multiple definitions. Check the exact stored name.", "Name", inner);

        var ex = Assert.Throws<McpException>(() => McpToolErrorHelper.ThrowStructuredError((Exception)validation));

        using var payload = JsonDocument.Parse(ex.Message);
        Assert.Equal("INVALID_CONSTRAINT", payload.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(validation.Message, payload.RootElement.GetProperty("userMessage").GetString());
        Assert.Equal("Name", payload.RootElement.GetProperty("context").GetProperty("field").GetString());
        Assert.DoesNotContain("synthetic-secret", ex.Message);
        Assert.DoesNotContain("synthetic-token", ex.Message);
    }

    [Fact]
    [Trait("AC", "AC-144")]
    public void Helper_WithoutField_OmitsContext()
    {
        var validation = new MetadataValidationException(MetadataErrorCodes.InvalidConstraint, "A column label update requires a display name or description.");
        var ex = Assert.Throws<McpException>(() => McpToolErrorHelper.ThrowStructuredError((Exception)validation));
        using var payload = JsonDocument.Parse(ex.Message);
        Assert.Equal("INVALID_CONSTRAINT", payload.RootElement.GetProperty("errorCode").GetString());
        Assert.False(payload.RootElement.TryGetProperty("context", out _));
    }

    public static TheoryData<string> AuthoringTools => new()
    {
        "add-column", "add-option-value", "create-choice", "create-key", "create-relationship",
        "create-table", "update-choice", "update-column", "update-relationship", "update-table"
    };

    [Theory]
    [MemberData(nameof(AuthoringTools))]
    [Trait("AC", "AC-144")]
    public async Task EveryAuthoringTool_ReportsTheValidationFailure(string tool)
    {
        var validation = new MetadataValidationException(MetadataErrorCodes.MissingRequiredField, "SolutionUniqueName is required.", "SolutionUniqueName");
        using var fixture = await ToolFixture.CreateAsync(ThrowingAuthoringService.Create(validation));

        var ex = await Assert.ThrowsAsync<McpException>(() => fixture.InvokeAsync(tool));

        using var payload = JsonDocument.Parse(ex.Message);
        Assert.Equal(MetadataErrorCodes.MissingRequiredField, payload.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("SolutionUniqueName is required.", payload.RootElement.GetProperty("userMessage").GetString());
        Assert.Equal("SolutionUniqueName", payload.RootElement.GetProperty("context").GetProperty("field").GetString());
    }

    [Fact]
    [Trait("AC", "AC-144")]
    public async Task ActualService_AmbiguousChoiceName_IsInvalidConstraintAtMcpBoundary()
    {
        // Two stored names match the requested spelling by case: the CLI reports INVALID_CONSTRAINT; so must MCP.
        var notFound = new FaultException<OrganizationServiceFault>(
            new OrganizationServiceFault { ErrorCode = unchecked((int)0x80040217), Message = "Could not find an optionset with name Mayo_X. " + Secret },
            "Could not find an optionset with name Mayo_X. " + Secret);
        var pool = new Mock<IDataverseConnectionPool>(MockBehavior.Strict);
        pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            var client = new Mock<IPooledClient>(MockBehavior.Loose);
            client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
                .Returns<OrganizationRequest, CancellationToken>((request, _) => request switch
                {
                    RetrieveOptionSetRequest => Task.FromException<OrganizationResponse>(notFound),
                    RetrieveAllOptionSetsRequest => Task.FromResult<OrganizationResponse>(new RetrieveAllOptionSetsResponse
                    {
                        Results = new ParameterCollection
                        {
                            ["OptionSetMetadata"] = new OptionSetMetadataBase[]
                            {
                                new OptionSetMetadata { Name = "MAYO_X", IsGlobal = true },
                                new OptionSetMetadata { Name = "mayo_x", IsGlobal = true }
                            }
                        }
                    }),
                    _ => throw new InvalidOperationException("Unexpected SDK request: " + request.RequestName)
                });
            return client.Object;
        });
        var service = new DataverseMetadataAuthoringService(pool.Object, new SchemaValidator(),
            new Mock<PPDS.Cli.Infrastructure.Safety.IShakedownGuard>().Object);
        using var fixture = await ToolFixture.CreateAsync(service);

        var ex = await Assert.ThrowsAsync<McpException>(() => fixture.InvokeAsync("update-choice", choiceName: "Mayo_X"));

        using var payload = JsonDocument.Parse(ex.Message);
        Assert.Equal("INVALID_CONSTRAINT", payload.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("Global choice 'Mayo_X' matched multiple definitions. Check the exact stored name.",
            payload.RootElement.GetProperty("userMessage").GetString());
        Assert.Equal("Name", payload.RootElement.GetProperty("context").GetProperty("field").GetString());
        Assert.DoesNotContain("synthetic-secret", ex.Message);
        Assert.DoesNotContain("Could not find", ex.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("AC", "AC-143")]
    [Trait("AC", "AC-145")]
    public async Task UpdateChoiceTool_ReportsTheUncheckedNameOnDryRun_AndLabelNotices(bool dryRun)
    {
        var notice = new ValidationMessage { Field = "DisplayName", Rule = MetadataLabelWriteResult.EnglishLabelOnlyRule, Message = "Only the English (LCID 1033) display name is written." };
        var service = new Mock<IMetadataAuthoringService>(MockBehavior.Strict);
        service.Setup(s => s.UpdateGlobalChoiceAsync(It.IsAny<UpdateGlobalChoiceRequest>(), It.IsAny<IMetadataAuthoringProgressReporter?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataLabelWriteResult { ValidationMessages = [notice] });
        using var fixture = await ToolFixture.CreateAsync(service.Object);

        var result = await new MetadataUpdateChoiceTool(fixture.Context).ExecuteAsync("Sol", "mayo_Typo", displayName: "Renamed", dryRun: dryRun);

        var json = JsonSerializer.SerializeToElement(result);
        if (dryRun) Assert.False(json.GetProperty("globalChoiceNameChecked").GetBoolean());
        else Assert.False(json.TryGetProperty("globalChoiceNameChecked", out _));
        Assert.Equal(MetadataLabelWriteResult.EnglishLabelOnlyRule, Assert.Single(json.GetProperty("validationMessages").EnumerateArray()).GetProperty("rule").GetString());
    }

    /// <summary>An authoring service whose every method fails with the given exception.</summary>
    public class ThrowingAuthoringService : DispatchProxy
    {
        private Exception _failure = null!;

        internal static IMetadataAuthoringService Create(Exception failure)
        {
            var proxy = Create<IMetadataAuthoringService, ThrowingAuthoringService>();
            ((ThrowingAuthoringService)(object)proxy)._failure = failure;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var returnType = targetMethod!.ReturnType;
            if (returnType == typeof(Task))
                return Task.FromException(_failure);
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
                return typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!
                    .MakeGenericMethod(returnType.GetGenericArguments()[0]).Invoke(null, [_failure]);
            throw _failure;
        }
    }

    private sealed class ToolFixture : IDisposable
    {
        private readonly string _tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        private readonly string _directory;
        private readonly ProfileStore _store;
        private readonly McpToolContext _context;
        private bool _disposed;
        public McpToolContext Context => _context;

        private ToolFixture(IMetadataAuthoringService service)
        {
            _directory = Path.Combine(_tempRoot, "ppds-validation-mapping-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            _store = new ProfileStore(Path.Combine(_directory, "profiles.json"));
            _context = new McpToolContext(new Mock<IMcpConnectionPoolManager>(MockBehavior.Strict).Object, _store,
                new Mock<ISecureCredentialStore>(MockBehavior.Strict).Object, new McpSessionOptions())
            {
                ServiceProviderFactory = (_, _, _, _) => new ServiceCollection().AddSingleton(service).BuildServiceProvider()
            };
        }

        public static async Task<ToolFixture> CreateAsync(IMetadataAuthoringService service)
        {
            var fixture = new ToolFixture(service);
            try
            {
                await fixture._store.SaveAsync(new ProfileCollection
                {
                    ActiveProfileIndex = 1,
                    Profiles = [new AuthProfile { Index = 1, Name = "fixture", Environment = new() { Url = "https://fixture.example.test" } }]
                });
                await fixture._context.InitializeAsync();
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        public Task InvokeAsync(string tool, string choiceName = "new_choice") => tool switch
        {
            "add-column" => new MetadataAddColumnTool(_context).ExecuteAsync("Sol", "new_table", "new_col", "Col", "String"),
            "add-option-value" => new MetadataAddOptionValueTool(_context).ExecuteAsync("Sol", choiceName, "New"),
            "create-choice" => new MetadataCreateChoiceTool(_context).ExecuteAsync("Sol", "new_choice", "Choice", "Description", "[{\"label\":\"A\",\"value\":1}]"),
            "create-key" => new MetadataCreateKeyTool(_context).ExecuteAsync("Sol", "new_table", "new_key", "Key", "new_code"),
            "create-relationship" => new MetadataCreateRelationshipTool(_context).ExecuteAsync("Sol", "account", "contact", "new_rel", "manyToMany"),
            "create-table" => new MetadataCreateTableTool(_context).ExecuteAsync("Sol", "new_table", "Table", "Tables", "Description", "UserOwned"),
            "update-choice" => new MetadataUpdateChoiceTool(_context).ExecuteAsync("Sol", choiceName, displayName: "Renamed"),
            "update-column" => new MetadataUpdateColumnTool(_context).ExecuteAsync("Sol", "new_table", "new_col", displayName: "Renamed"),
            "update-relationship" => new MetadataUpdateRelationshipTool(_context).ExecuteAsync("Sol", "new_rel", cascadeDelete: "Cascade"),
            "update-table" => new MetadataUpdateTableTool(_context).ExecuteAsync("Sol", "new_table", displayName: "Renamed"),
            _ => throw new ArgumentOutOfRangeException(nameof(tool))
        };

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _context.Dispose();
            _store.Dispose();
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(_directory)), _tempRoot, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
    }
}
