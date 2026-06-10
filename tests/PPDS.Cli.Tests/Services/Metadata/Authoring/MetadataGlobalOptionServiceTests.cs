using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Moq;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

/// <summary>
/// Covers global (option-set-scoped) option management on the authoring service.
/// </summary>
[Trait("Category", "Unit")]
public class MetadataGlobalOptionServiceTests
{
    private readonly Mock<IDataverseConnectionPool> _pool = new();
    private readonly Mock<IPooledClient> _client = new();
    private readonly DataverseMetadataAuthoringService _service;
    private OrganizationRequest? _captured;

    public MetadataGlobalOptionServiceTests()
    {
        _pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);
        _service = new DataverseMetadataAuthoringService(_pool.Object, new SchemaValidator(), new InactiveFakeShakedownGuard());

        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Returns<OrganizationRequest, CancellationToken>((req, _) =>
            {
                switch (req)
                {
                    case InsertOptionValueRequest insert:
                        _captured = req;
                        var ins = new InsertOptionValueResponse();
                        ins.Results["NewOptionValue"] = insert.Value ?? 0;
                        return Task.FromResult<OrganizationResponse>(ins);
                    default:
                        return Task.FromResult(new OrganizationResponse());
                }
            });
    }

    [Fact]
    public async Task AddOptionValue_WithColor_ForwardsColorToInsertRequest()
    {
        var assigned = await _service.AddOptionValueAsync(new AddOptionValueRequest
        {
            OptionSetName = "hsl_severity",
            SolutionUniqueName = "MySolution",
            Label = "Severe",
            Value = 864630001,
            Color = "#FF0000"
        });

        assigned.Should().Be(864630001);
        var insert = _captured.Should().BeOfType<InsertOptionValueRequest>().Subject;
        insert.Value.Should().Be(864630001);
        insert["Color"].Should().Be("#FF0000");
    }

    [Fact]
    public async Task AddOptionValue_WithoutColor_DoesNotSetColorParameter()
    {
        await _service.AddOptionValueAsync(new AddOptionValueRequest
        {
            OptionSetName = "hsl_severity",
            SolutionUniqueName = "MySolution",
            Label = "Mild",
            Value = 864630000
        });

        var insert = _captured.Should().BeOfType<InsertOptionValueRequest>().Subject;
        insert.Parameters.ContainsKey("Color").Should().BeFalse();
    }
}
