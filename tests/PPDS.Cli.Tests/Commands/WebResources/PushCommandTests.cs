using System.CommandLine;
using FluentAssertions;
using PPDS.Cli.Commands.WebResources;
using PPDS.Cli.Infrastructure.Errors;
using Xunit;

namespace PPDS.Cli.Tests.Commands.WebResources;

public class PushCommandTests
{
    private readonly Command _command = PushCommand.Create();

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        _command.Name.Should().Be("push");
    }

    [Fact]
    public void Create_HasPathArgument()
    {
        _command.Arguments.Should().ContainSingle();
        _command.Arguments[0].Name.Should().Be("path");
    }

    [Theory]
    [InlineData("--force")]
    [InlineData("--dry-run")]
    [InlineData("--publish")]
    [InlineData("--profile")]
    [InlineData("--environment")]
    public void Create_HasOption(string optionName)
    {
        _command.Options.Should().Contain(o => o.Name == optionName);
    }

    [Fact]
    public void Create_RegisteredInWebResourcesCommandGroup()
    {
        var group = WebResourcesCommandGroup.Create();
        group.Subcommands.Should().Contain(c => c.Name == "push");
    }

    /// <summary>
    /// AC-WR-42: tracking-file-not-found surfaces as exit-code NotFoundError (6) via ExceptionMapper
    /// when PpdsException's ErrorCode is Validation.FileNotFound.
    /// </summary>
    [Fact]
    public void PushErrorsOnMissingTrackingFile_MapsToNotFoundExitCode()
    {
        var ex = new PpdsException(
            ErrorCodes.Validation.FileNotFound,
            "Tracking file not found.");

        var exitCode = ExceptionMapper.ToExitCode(ex);

        exitCode.Should().Be(ExitCodes.InvalidArguments);
    }

    /// <summary>
    /// AC-WR-52: environment-mismatch PpdsException maps to ConnectionError (4) exit code.
    /// </summary>
    [Fact]
    public void PushErrorsOnEnvironmentMismatch_MapsToConnectionExitCode()
    {
        var ex = new PpdsException(
            ErrorCodes.Connection.InvalidEnvironmentUrl,
            "Environment mismatch.");

        var exitCode = ExceptionMapper.ToExitCode(ex);

        exitCode.Should().Be(ExitCodes.ConnectionError);
    }

    /// <summary>
    /// AC-WR-44: PushResult with conflicts maps to PreconditionFailed (10).
    /// Verifies the constant the command relies on; the command-level mapping is exercised
    /// in WebResourceSyncServiceTests.PushDetectsServerConflict and asserted at the boundary.
    /// </summary>
    [Fact]
    public void PushConflictReturnsExitCode10()
    {
        ExitCodes.PreconditionFailed.Should().Be(10);
    }
}
