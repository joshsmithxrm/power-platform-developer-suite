using System.CommandLine;
using PPDS.Cli.Commands.Plugins;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Plugins;

public class PluginsCommandGroupTests
{
    private readonly Command _command;

    public PluginsCommandGroupTests()
    {
        _command = PluginsCommandGroup.Create();
    }

    #region Command Structure Tests

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("plugins", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Contains("Plugin", _command.Description);
    }

    [Fact]
    public void Create_HasExtractSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "extract");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasDeploySubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "deploy");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasRegisterSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "register");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasDiffSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "diff");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasListSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "list");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasGetSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "get");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasCleanSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "clean");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasDownloadSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "download");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasUpdateSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "update");
        Assert.NotNull(subcommand);
    }

    [Fact]
    public void Create_HasUnregisterSubcommand()
    {
        var subcommand = _command.Subcommands.FirstOrDefault(c => c.Name == "unregister");
        Assert.NotNull(subcommand);
    }

    [Fact]
    [Trait("AC", "AC-137")]
    public void OrphanedStepGuidance_NamesACommandThatParses()
    {
        // Deploy/clean help and the version 2.0 orphaned-step warning point users at this exact command.
        var parsed = _command.Parse(["unregister", "step", Guid.NewGuid().ToString("D"), "--force"]);
        Assert.Empty(parsed.Errors);
        var deployClean = _command.Subcommands.Single(c => c.Name == "deploy").Options.Single(o => o.Name == "--clean").Description!;
        Assert.Contains("ppds plugins unregister step <step-id>", deployClean);
        Assert.Contains("exit 1", deployClean);
        var clean = _command.Subcommands.Single(c => c.Name == "clean").Description!;
        Assert.Contains("ppds plugins unregister step <step-id>", clean);
        Assert.Contains("exits 1", clean);
    }

    [Fact]
    [Trait("AC", "AC-137")]
    public void DeployCleanAndExtractHelp_DescribeVersionsAndSecureConfiguration()
    {
        var deploy = _command.Subcommands.Single(c => c.Name == "deploy").Description!;
        Assert.Contains("missing or 1.0 preserves omitted optional", deploy);
        Assert.Contains("2.0 resets omitted optional properties to PPDS defaults", deploy);
        Assert.Contains("secureConfiguration is write-only", deploy);
        Assert.Contains("preserved when omitted", deploy);
        Assert.Contains("cannot be cleared by omitting it", deploy);
        var extract = _command.Subcommands.Single(c => c.Name == "extract").Options.Single(o => o.Name == "--registration-version").Description!;
        Assert.Contains("1.0 (legacy: deploy preserves omitted optional properties)", extract);
        Assert.Contains("secureConfiguration is never written to the output", extract);
        Assert.Contains("write-only", extract);
        Assert.Contains("cannot be cleared by omission", extract);
        var clean = _command.Subcommands.Single(c => c.Name == "clean").Description!;
        Assert.Contains("Secure configuration of configured steps is never read or changed", clean);
    }

    [Fact]
    [Trait("AC", "AC-138")]
    public void DiffAndDeployHelp_DescribeTheTwoPropertiesAlwaysComparedUnderVersion1()
    {
        var diff = _command.Subcommands.Single(c => c.Name == "diff").Description!;
        Assert.Contains("Under a missing or 1.0 registration version, step filteringAttributes and image attributes are always compared", diff);
        Assert.Contains("an unset value as none (as in PPDS 1.4.2)", diff);
        Assert.Contains("other omitted optional properties are not compared", diff);
        Assert.Contains("12 incomplete (the comparison could not be completed; the result is unknown)", diff);
        Assert.Contains("other failures exit with their own non-zero code (see the exit code table in the CLI README)", diff);
        var deploy = _command.Subcommands.Single(c => c.Name == "deploy").Description!;
        Assert.Contains("diff compares an omitted step filteringAttributes or image attributes against none", deploy);
        Assert.Contains("remove it from the step or image and list it in resetFields", deploy);
        Assert.Contains("verification drift exits 1; verification that could not be completed exits 12", deploy);
        Assert.Contains("a failed deploy exits with its own non-zero code, see the exit code table in the CLI README", deploy);
    }

    [Fact]
    public void Create_HasTwelveSubcommands()
    {
        Assert.Equal(12, _command.Subcommands.Count);
    }

    #endregion

    #region Shared Options Tests

    [Fact]
    public void ProfileOption_HasCorrectName()
    {
        Assert.Equal("--profile", PluginsCommandGroup.ProfileOption.Name);
    }

    [Fact]
    public void ProfileOption_HasShortAlias()
    {
        Assert.Contains("-p", PluginsCommandGroup.ProfileOption.Aliases);
    }

    [Fact]
    public void EnvironmentOption_HasCorrectName()
    {
        Assert.Equal("--environment", PluginsCommandGroup.EnvironmentOption.Name);
    }

    [Fact]
    public void EnvironmentOption_HasShortAlias()
    {
        Assert.Contains("-env", PluginsCommandGroup.EnvironmentOption.Aliases);
    }

    [Fact]
    public void SolutionOption_HasCorrectName()
    {
        Assert.Equal("--solution", PluginsCommandGroup.SolutionOption.Name);
    }

    [Fact]
    public void SolutionOption_HasShortAlias()
    {
        Assert.Contains("-s", PluginsCommandGroup.SolutionOption.Aliases);
    }

    // Note: OutputFormatOption tests removed - option is now provided by GlobalOptions

    #endregion
}
