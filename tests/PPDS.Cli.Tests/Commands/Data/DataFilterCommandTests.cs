using FluentAssertions;
using PPDS.Cli.Commands.Data;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Tests.TestHelpers;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Data;

/// <summary>
/// Issue 1705: <c>data delete --filter</c> and <c>data update --filter</c> refuse a filter FetchXML cannot express exactly
/// before they sign in or read anything (the refusal is raised before the connection is made, so these tests need no
/// profile and no Dataverse).
/// </summary>
[Trait("Category", "Unit")]
[Collection(nameof(ConsoleCaptureCollection))]
public class DataFilterCommandTests
{
    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(System.CommandLine.Command command, params string[] args)
    {
        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exit = await command.Parse(args).InvokeAsync();
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
        }
    }

    [Theory]
    [InlineData("statecode = 0 AND UPPER(name) = 'X'", "a function")]
    [InlineData("name = accountnumber", "a column compared with a column")]
    [InlineData("NOT (statecode = 0)", "NOT (...)")]
    public async Task Delete_UnsupportedFilter_IsRefusedBeforeConnecting(string filter, string kind)
    {
        var (exit, _, stderr) = await RunAsync(DeleteCommand.Create(), "--entity", "account", "--filter", filter, "--force");

        exit.Should().Be(ExitCodes.InvalidArguments);
        stderr.Should().Contain(kind).And.Contain("Nothing was read or changed");
        stderr.Should().NotContain("Connecting to Dataverse");
    }

    [Theory]
    [InlineData("revenue * 2 > 1500", "arithmetic")]
    [InlineData("name = NULL", "a comparison with NULL")]
    public async Task Update_UnsupportedFilter_IsRefusedBeforeConnecting(string filter, string kind)
    {
        var (exit, _, stderr) = await RunAsync(UpdateCommand.Create(),
            "--entity", "account", "--filter", filter, "--set", "name=x", "--force");

        exit.Should().Be(ExitCodes.InvalidArguments);
        stderr.Should().Contain(kind);
        stderr.Should().NotContain("Connecting to Dataverse");
    }

    [Fact]
    public async Task Delete_WhiteSpaceFilter_IsRefusedBeforeConnecting_AndPointsToTruncate()
    {
        var (exit, _, stderr) = await RunAsync(DeleteCommand.Create(), "--entity", "account", "--filter", "   ", "--force");

        exit.Should().Be(ExitCodes.InvalidArguments);
        stderr.Should().Contain("data truncate");
        stderr.Should().NotContain("Connecting to Dataverse");
    }

    [Fact]
    public async Task Delete_UnsupportedFilter_JsonOutput_CarriesTheCode()
    {
        var (exit, stdout, _) = await RunAsync(DeleteCommand.Create(),
            "--entity", "account", "--filter", "LEN(name) = 3", "--force", "--output-format", "json");

        exit.Should().Be(ExitCodes.InvalidArguments);
        stdout.Should().Contain("Validation.FilterNotSupported");
    }
}
