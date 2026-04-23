using System.CommandLine;
using PPDS.Cli.Commands.DeploymentSettings;
using Xunit;

namespace PPDS.Cli.Tests.Commands.DeploymentSettings;

public class ValidateCommandTests
{
    private readonly Command _command;

    public ValidateCommandTests()
    {
        _command = ValidateCommand.Create();
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("validate", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("deployment settings", _command.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_HasNoArguments()
    {
        Assert.Empty(_command.Arguments);
    }

    [Fact]
    public void Create_HasSolutionOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--solution");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasFileOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--file");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_FileOptionHasNoShortAlias_FReservedForOutputFormat()
    {
        // L1: -f is reserved exclusively for --output-format; --file uses only its long form.
        var fileOption = _command.Options.First(o => o.Name == "--file");
        Assert.DoesNotContain("-f", fileOption.Aliases);
    }

    [Fact]
    public void Create_OutputFormatOption_HasFShortAlias()
    {
        // -f belongs to --output-format — consistent across the CLI.
        var formatOption = _command.Options.FirstOrDefault(o => o.Name == "--output-format");
        Assert.NotNull(formatOption);
        Assert.Contains("-f", formatOption.Aliases);
    }

    [Fact]
    public void Create_NoTwoOptions_ShareAShortAlias()
    {
        // Regression: duplicate short aliases cause silent parse collisions.
        var aliases = _command.Options
            .SelectMany(o => o.Aliases)
            .Where(a => a.StartsWith("-") && !a.StartsWith("--"))
            .ToList();
        var duplicates = aliases.GroupBy(a => a).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicates);
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
}
