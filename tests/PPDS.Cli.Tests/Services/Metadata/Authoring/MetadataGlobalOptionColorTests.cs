using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;
using SdkUpdateOptionValueRequest = Microsoft.Xrm.Sdk.Messages.UpdateOptionValueRequest;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

/// <summary>
/// Covers option color on GLOBAL choices. The Insert/Update OptionValue messages do not define a Color
/// parameter — the platform silently drops request["Color"] (Gemini #review). The service must instead
/// retrieve the global option set, set OptionMetadata.Color, and re-send it via UpdateOptionSet.
/// </summary>
[Trait("Category", "Unit")]
public class MetadataGlobalOptionColorTests
{
    private readonly Mock<IDataverseConnectionPool> _pool = new();
    private readonly Mock<IPooledClient> _client = new();
    private readonly DataverseMetadataAuthoringService _service;

    private readonly List<OptionMetadata> _options = new();
    private OrganizationRequest? _captured;
    private UpdateOptionSetRequest? _capturedColorUpdate;

    public MetadataGlobalOptionColorTests()
    {
        _pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);
        _service = new DataverseMetadataAuthoringService(_pool.Object, new SchemaValidator(), new InactiveFakeShakedownGuard());
    }

    private void SetupGlobalOptions(params (int value, string label)[] options)
    {
        foreach (var (value, label) in options)
            _options.Add(new OptionMetadata(new Label(label, 1033), value));

        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Returns<OrganizationRequest, CancellationToken>((req, _) =>
            {
                switch (req)
                {
                    case RetrieveOptionSetRequest:
                    {
                        var optionSet = new OptionSetMetadata { IsGlobal = true };
                        foreach (var o in _options)
                            optionSet.Options.Add(o);
                        var response = new RetrieveOptionSetResponse();
                        response.Results["OptionSetMetadata"] = optionSet;
                        return Task.FromResult<OrganizationResponse>(response);
                    }
                    case InsertOptionValueRequest ins:
                        _captured = req;
                        var insertedValue = ins.Value ?? 0;
                        _options.Add(new OptionMetadata(ins.Label, insertedValue));
                        var insResponse = new InsertOptionValueResponse();
                        insResponse.Results["NewOptionValue"] = insertedValue;
                        return Task.FromResult<OrganizationResponse>(insResponse);
                    case SdkUpdateOptionValueRequest:
                        _captured = req;
                        return Task.FromResult<OrganizationResponse>(new UpdateOptionValueResponse());
                    case UpdateOptionSetRequest upd:
                        _capturedColorUpdate = upd;
                        return Task.FromResult<OrganizationResponse>(new UpdateOptionSetResponse());
                    default:
                        return Task.FromResult(new OrganizationResponse());
                }
            });
    }

    private string? CapturedColorFor(int value)
        => ((_capturedColorUpdate?.OptionSet as OptionSetMetadata)?.Options)
            ?.FirstOrDefault(o => o.Value == value)?.Color;

    [Fact]
    public async Task AddOptionValue_WithColor_AppliesColorViaUpdateOptionSet()
    {
        SetupGlobalOptions((100, "Existing"));

        await _service.AddOptionValueAsync(new AddOptionValueRequest
        {
            OptionSetName = "samples_status",
            Label = "Pending",
            Value = 101,
            Color = "#AABBCC"
        });

        _captured.Should().BeOfType<InsertOptionValueRequest>();
        _capturedColorUpdate.Should().NotBeNull();
        CapturedColorFor(101).Should().Be("#AABBCC");
    }

    [Fact]
    public async Task AddOptionValue_WithoutColor_DoesNotUpdateOptionSet()
    {
        SetupGlobalOptions((100, "Existing"));

        await _service.AddOptionValueAsync(new AddOptionValueRequest
        {
            OptionSetName = "samples_status",
            Label = "Pending",
            Value = 101
        });

        _capturedColorUpdate.Should().BeNull();
    }

    [Fact]
    public async Task UpdateOptionValue_WithColor_AppliesColorViaUpdateOptionSet()
    {
        SetupGlobalOptions((100, "Existing"));

        await _service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest
        {
            OptionSetName = "samples_status",
            Value = 100,
            Label = "Renamed",
            Color = "#112233"
        });

        _captured.Should().BeOfType<SdkUpdateOptionValueRequest>();
        _capturedColorUpdate.Should().NotBeNull();
        CapturedColorFor(100).Should().Be("#112233");
    }
}
