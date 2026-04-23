using System.CommandLine;
using PPDS.Cli.Commands;
using Xunit;

namespace PPDS.Cli.Tests.Commands;

public class DocsCommandTests
{
    private readonly Command _command;

    public DocsCommandTests()
    {
        _command = DocsCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("docs", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("documentation", _command.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_HasNoSubcommands()
    {
        // docs is a standalone command, not a command group
        Assert.Empty(_command.Subcommands);
    }

    [Fact]
    public void Create_HasNoOptions()
    {
        // docs has no options - it just opens the browser
        Assert.Empty(_command.Options);
    }

    [Fact]
    public void Create_HasAction()
    {
        // The command should have a handler set
        Assert.NotNull(_command.Action);
    }

    [Fact]
    public void DocsUrl_IsValidHttpsUrl()
    {
        // Verify the URL is well-formed
        Assert.StartsWith("https://", DocsCommand.DocsUrl);
        Assert.True(Uri.TryCreate(DocsCommand.DocsUrl, UriKind.Absolute, out var uri));
        Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
    }

    [Fact]
    public void DocsUrl_PointsToGitHubPagesSite()
    {
        Assert.Contains("github.io", DocsCommand.DocsUrl);
        Assert.Contains("ppds-docs", DocsCommand.DocsUrl);
    }
}
