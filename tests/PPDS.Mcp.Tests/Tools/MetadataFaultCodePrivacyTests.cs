using System.ServiceModel;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using ModelContextProtocol;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.Mcp.Infrastructure;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

[Trait("Category", "Unit")]
public sealed class MetadataFaultCodePrivacyTests
{
    private const string PrivateText = "ClientSecret=fixture-secret; Bearer fixture-token; private-server-text";

    public static IEnumerable<object[]> TypedFaultCases()
    {
        foreach (var kind in new[] { "status", "choice", "choices" })
        foreach (var wrapped in new[] { false, true })
        foreach (var (code, hex) in new[] { (0, "00000000"), (int.MinValue, "80000000"), (-2147204316, "80044324"), (int.MaxValue, "7fffffff") })
            yield return [kind, wrapped, code, hex];
    }

    [Theory, MemberData(nameof(TypedFaultCases)), Trait("AC", "AC-131")]
    public async Task TypedFault_ExposesOnlyNumericCodeAcrossErrorBoundaries(string kind, bool wrapped, int code, string hex)
    {
        Exception original = Fault(code);
        if (wrapped) original = new InvalidOperationException(PrivateText, original);
        using var fixture = new Fixture(kind, original);
        var actual = await fixture.InvokeAsync();
        AssertErrorBoundaries(actual, original, fixture.FixedMessage + $" Dataverse fault 0x{hex}. Server diagnostic text is withheld.");
        fixture.AssertComplete();
    }

    [Theory, Trait("AC", "AC-132")]
    [InlineData("status", false)]
    [InlineData("status", true)]
    [InlineData("choice", false)]
    [InlineData("choice", true)]
    [InlineData("choices", false)]
    [InlineData("choices", true)]
    public async Task UnknownOrDeepFault_RetainsFixedMessage(string kind, bool deepFault)
    {
        Exception original = deepFault ? Fault(int.MinValue) : new InvalidOperationException(PrivateText);
        if (deepFault)
            for (var i = 0; i < 8; i++) original = new InvalidOperationException(PrivateText, original);
        using var fixture = new Fixture(kind, original);
        var actual = await fixture.InvokeAsync();
        AssertErrorBoundaries(actual, original, fixture.FixedMessage);
        fixture.AssertComplete();
    }

    [Theory, Trait("AC", "AC-132")]
    [InlineData("status")]
    [InlineData("choice")]
    [InlineData("choices")]
    public async Task ClosestTypedFault_DoesNotCopyInnerFault(string kind)
    {
        var original = Fault(7);
        original.Detail.InnerFault = new OrganizationServiceFault { ErrorCode = 99, Message = PrivateText, TraceText = PrivateText };
        using var fixture = new Fixture(kind, original);
        var actual = await fixture.InvokeAsync();
        AssertErrorBoundaries(actual, original, fixture.FixedMessage + " Dataverse fault 0x00000007. Server diagnostic text is withheld.");
        fixture.AssertComplete();
    }

    private static FaultException<OrganizationServiceFault> Fault(int code)
    {
        var detail = new OrganizationServiceFault { ErrorCode = code, Message = PrivateText, TraceText = PrivateText };
        detail.ErrorDetails["private-diagnostic"] = PrivateText;
        return new FaultException<OrganizationServiceFault>(detail, new FaultReason(PrivateText));
    }

    private static void AssertErrorBoundaries(PpdsException actual, Exception original, string expected)
    {
        Assert.Same(original, actual.InnerException);
        Assert.Equal(MetadataErrorCodes.SdkOperationFailed, actual.ErrorCode);
        Assert.Equal(expected, actual.UserMessage);
        Assert.Null(actual.Context);
        var mapped = ExceptionMapper.MapWithExitCode(actual, context: "inserting fixture option");
        Assert.Equal(ExitCodes.Failure, mapped.ExitCode);
        using var output = new StringWriter();
        new JsonOutputWriter(output).WriteError(mapped.Error);
        using var cli = JsonDocument.Parse(output.ToString());
        Assert.False(cli.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(expected, cli.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(MetadataErrorCodes.SdkOperationFailed, cli.RootElement.GetProperty("error").GetProperty("code").GetString());

        // This is the existing structured helper, not a live transport or a new MCP tool.
        var mcpError = Assert.Throws<McpException>(() => McpToolErrorHelper.ThrowStructuredError(actual));
        using var mcp = JsonDocument.Parse(mcpError.Message);
        Assert.Equal(expected, mcp.RootElement.GetProperty("userMessage").GetString());
        Assert.Equal(MetadataErrorCodes.SdkOperationFailed, mcp.RootElement.GetProperty("errorCode").GetString());
        foreach (var secret in new[] { "fixture-secret", "fixture-token", "private-server-text", "private-diagnostic" })
        {
            Assert.DoesNotContain(secret, actual.UserMessage);
            Assert.DoesNotContain(secret, output.ToString());
            Assert.DoesNotContain(secret, mcpError.Message);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _kind;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Mock<IDataverseConnectionPool> _pool = new(MockBehavior.Strict);
        private readonly Mock<IShakedownGuard> _guard = new(MockBehavior.Strict);
        private readonly List<Mock<IPooledClient>> _leases = [];
        private readonly List<OrganizationRequest> _requests = [];
        private readonly DataverseMetadataAuthoringService _service;
        private int _returned;
        private string Attribute => _kind == "status" ? "statuscode" : "new_choice";
        public string FixedMessage => _kind == "status"
            ? "Failed to add status reason 'New' to 'new_table'."
            : "Failed to add option 'New' to 'new_table.new_choice'.";

        public Fixture(string kind, Exception failure)
        {
            _kind = kind;
            _guard.Setup(guard => guard.EnsureCanMutate(kind == "status" ? "metadata.statusReason.add" : "metadata.columnOption.add"));
            _pool.Setup(pool => pool.GetClientAsync(null, null, _cancellation.Token)).ReturnsAsync(() =>
            {
                var lease = new Mock<IPooledClient>(MockBehavior.Strict);
                _leases.Add(lease);
                lease.Setup(client => client.DisposeAsync()).Returns(() => { _returned++; return ValueTask.CompletedTask; });
                lease.Setup(client => client.ExecuteAsync(It.IsAny<OrganizationRequest>(), _cancellation.Token))
                    .Returns<OrganizationRequest, CancellationToken>((request, _) =>
                    {
                        _requests.Add(request);
                        if (request is RetrieveAttributeRequest read)
                        {
                            Assert.True(read.RetrieveAsIfPublished);
                            Assert.Equal("new_table", read.EntityLogicalName);
                            Assert.Equal(Attribute, read.LogicalName);
                            EnumAttributeMetadata attribute = kind switch
                            {
                                "status" => new StatusAttributeMetadata(),
                                "choices" => new MultiSelectPicklistAttributeMetadata(),
                                _ => new PicklistAttributeMetadata()
                            };
                            attribute.OptionSet = new OptionSetMetadata();
                            return Task.FromResult<OrganizationResponse>(new RetrieveAttributeResponse
                                { Results = new ParameterCollection { ["AttributeMetadata"] = attribute } });
                        }
                        Assert.Equal(kind == "status" ? "InsertStatusValue" : "InsertOptionValue", request.RequestName);
                        Assert.Equal("new_table", request["EntityLogicalName"]);
                        Assert.Equal(Attribute, request["AttributeLogicalName"]);
                        Assert.Equal(77, request["Value"]);
                        return Task.FromException<OrganizationResponse>(failure);
                    });
                return lease.Object;
            });
            _service = new DataverseMetadataAuthoringService(_pool.Object, new SchemaValidator(), _guard.Object);
        }

        public Task<PpdsException> InvokeAsync() => Assert.ThrowsAsync<PpdsException>(() => _kind == "status"
            ? _service.AddStatusReasonAsync(new AddStatusReasonRequest
                { EntityLogicalName = "new_table", Label = "New", StateCode = 0, Value = 77 }, ct: _cancellation.Token)
            : _service.AddColumnOptionAsync(new AddColumnOptionRequest
                { EntityLogicalName = "new_table", ColumnLogicalName = Attribute, Label = "New", Value = 77 }, _cancellation.Token));

        public void AssertComplete()
        {
            Assert.Equal(2, _requests.Count);
            Assert.IsType<RetrieveAttributeRequest>(_requests[0]);
            Assert.Equal(2, _leases.Count);
            Assert.Equal(2, _returned);
            _pool.Verify(pool => pool.GetClientAsync(null, null, _cancellation.Token), Times.Exactly(2));
            _pool.VerifyNoOtherCalls();
            _guard.Verify(guard => guard.EnsureCanMutate(_kind == "status" ? "metadata.statusReason.add" : "metadata.columnOption.add"), Times.Once);
            _guard.VerifyNoOtherCalls();
            foreach (var lease in _leases)
            {
                lease.Verify(client => client.ExecuteAsync(It.IsAny<OrganizationRequest>(), _cancellation.Token), Times.Once);
                lease.Verify(client => client.DisposeAsync(), Times.Once);
                lease.VerifyNoOtherCalls();
            }
        }

        public void Dispose() => _cancellation.Dispose();
    }
}
