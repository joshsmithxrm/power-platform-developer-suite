using System.CommandLine;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using PPDS.Cli.Commands.Flows;
using PPDS.Cli.Services.Flows;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Flows;

public class UrlCommandTests
{
    private readonly Command _command;

    public UrlCommandTests()
    {
        _command = UrlCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("url", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("url", _command.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_HasNameArgument()
    {
        Assert.Single(_command.Arguments);
        Assert.Equal("name", _command.Arguments[0].Name);
    }

    [Fact]
    public void Create_NameArgumentHasDescription()
    {
        var arg = _command.Arguments[0];
        Assert.NotNull(arg.Description);
        Assert.Contains("unique name", arg.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Create_NameArgumentDescription_MentionsGuidOrUniqueName()
    {
        // Argument accepts either a GUID (WorkflowId) or a unique name —
        // description must communicate both to avoid "not found" confusion
        // when users pipe IDs from 'flows list'.
        var arg = _command.Arguments[0];
        Assert.Contains("GUID", arg.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unique name", arg.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_HasProfileOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--profile");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasEnvironmentOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--environment");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasGlobalOptions()
    {
        var formatOption = _command.Options.FirstOrDefault(o => o.Name == "--output-format");
        Assert.NotNull(formatOption);
    }

    #region GUID routing tests

    private static FlowInfo MakeFlow(Guid id, string uniqueName) => new()
    {
        Id = id,
        UniqueName = uniqueName,
        State = FlowState.Activated,
        Category = FlowCategory.ModernFlow,
        ConnectionReferenceLogicalNames = new System.Collections.Generic.List<string>()
    };

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveFlowAsync_GuidInput_CallsGetByIdAsync()
    {
        // When the caller passes a valid GUID string (e.g. piped from 'flows list'),
        // the command must resolve via GetByIdAsync — not GetAsync — so the lookup
        // succeeds even when uniqueName is empty.
        var flowId = Guid.NewGuid();
        var expected = MakeFlow(flowId, string.Empty);

        var mockService = new Mock<IFlowService>();
        mockService
            .Setup(s => s.GetByIdAsync(flowId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await UrlCommand.ResolveFlowAsync(
            mockService.Object, flowId.ToString(), CancellationToken.None);

        result.Should().BeSameAs(expected);
        mockService.Verify(s => s.GetByIdAsync(flowId, It.IsAny<CancellationToken>()), Times.Once);
        mockService.Verify(s => s.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveFlowAsync_NonGuidInput_CallsGetAsync()
    {
        // When the caller passes a unique name (non-GUID string), the command must
        // resolve via GetAsync (unique-name lookup).
        const string uniqueName = "cat_myflow_12345";
        var expected = MakeFlow(Guid.NewGuid(), uniqueName);

        var mockService = new Mock<IFlowService>();
        mockService
            .Setup(s => s.GetAsync(uniqueName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await UrlCommand.ResolveFlowAsync(
            mockService.Object, uniqueName, CancellationToken.None);

        result.Should().BeSameAs(expected);
        mockService.Verify(s => s.GetAsync(uniqueName, It.IsAny<CancellationToken>()), Times.Once);
        mockService.Verify(s => s.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion
}
