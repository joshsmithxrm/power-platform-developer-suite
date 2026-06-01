using System.CommandLine;
using PPDS.Cli.Commands.Api;
using PPDS.Cli.Infrastructure.Errors;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Api;

[Trait("Category", "Unit")]
public class ApiRequestCommandTests
{
    private readonly Command _command;

    public ApiRequestCommandTests()
    {
        _command = ApiRequestCommand.Create();
    }

    #region Command Structure

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("request", _command.Name);
    }

    [Fact]
    public void Create_HasPathOption_Required()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--path");
        Assert.NotNull(option);
        Assert.True(option!.Required);
    }

    [Fact]
    public void Create_HasMethodOption_WithAlias()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--method");
        Assert.NotNull(option);
        Assert.False(option!.Required);
        Assert.Contains("-X", option.Aliases);
    }

    [Fact]
    public void Create_HasBodyOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--body");
        Assert.NotNull(option);
        Assert.False(option!.Required);
    }

    [Fact]
    public void Create_HasBodyFileOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--body-file");
        Assert.NotNull(option);
        Assert.False(option!.Required);
    }

    [Fact]
    public void Create_HasHeaderOption_WithAlias()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--header");
        Assert.NotNull(option);
        Assert.Contains("-H", option!.Aliases);
    }

    [Fact]
    public void Create_HasIncludeOption_WithAlias()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--include");
        Assert.NotNull(option);
        Assert.Contains("-i", option!.Aliases);
    }

    [Fact]
    public void Create_HasEnvironmentOption_WithAlias()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--environment");
        Assert.NotNull(option);
        Assert.Contains("-env", option!.Aliases);
    }

    [Fact]
    public void Create_HasConfirmOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--confirm");
        Assert.NotNull(option);
        Assert.False(option!.Required);
    }

    [Fact]
    public void Create_HasAction()
    {
        Assert.NotNull(_command.Action);
    }

    #endregion

    #region Validation — AC-10: Path without leading slash

    [Fact]
    public void Path_NoLeadingSlash_Error()
    {
        var result = ApiRequestCommand.ValidateInputs(
            "api/data/v9.2/accounts", null, null, null, null,
            out _, out _);

        Assert.NotNull(result);
        Assert.Equal(ExitCodes.InvalidArguments, result!.Value.ExitCode);
        Assert.Contains("/", result.Value.Error);
    }

    [Fact]
    public void Path_WithLeadingSlash_Valid()
    {
        var result = ApiRequestCommand.ValidateInputs(
            "/api/data/v9.2/accounts", null, null, null, null,
            out _, out _);

        Assert.Null(result);
    }

    #endregion

    #region Validation — AC-09: --body and --body-file conflict

    [Fact]
    public void Body_And_BodyFile_Conflict()
    {
        var result = ApiRequestCommand.ValidateInputs(
            "/api/data/v9.2/accounts", null, "{}", "file.json", null,
            out _, out _);

        Assert.NotNull(result);
        Assert.Equal(ExitCodes.InvalidArguments, result!.Value.ExitCode);
        Assert.Contains("--body", result.Value.Error);
        Assert.Contains("--body-file", result.Value.Error);
    }

    [Fact]
    public void Body_Only_Valid()
    {
        var result = ApiRequestCommand.ValidateInputs(
            "/api/data/v9.2/accounts", "POST", "{}", null, null,
            out var method, out _);

        Assert.Null(result);
        Assert.Equal(HttpMethod.Post, method);
    }

    [Fact]
    public void BodyFile_Only_Valid()
    {
        var result = ApiRequestCommand.ValidateInputs(
            "/api/data/v9.2/accounts", "POST", null, "file.json", null,
            out _, out _);

        Assert.Null(result);
    }

    #endregion

    #region Validation — invalid HTTP method

    [Fact]
    public void InvalidMethod_Error()
    {
        var result = ApiRequestCommand.ValidateInputs(
            "/api/data/v9.2/accounts", "BREW", null, null, null,
            out _, out _);

        Assert.NotNull(result);
        Assert.Equal(ExitCodes.InvalidArguments, result!.Value.ExitCode);
        Assert.Contains("BREW", result.Value.Error);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void ValidMethods_NoError(string methodName)
    {
        var result = ApiRequestCommand.ValidateInputs(
            "/path", methodName, null, null, null,
            out var method, out _);

        Assert.Null(result);
        Assert.Equal(new HttpMethod(methodName), method);
    }

    #endregion

    #region Validation — invalid header format

    [Fact]
    public void InvalidHeader_Format_Error()
    {
        var result = ApiRequestCommand.ValidateInputs(
            "/path", null, null, null, new[] { "BadHeader" },
            out _, out _);

        Assert.NotNull(result);
        Assert.Equal(ExitCodes.InvalidArguments, result!.Value.ExitCode);
        Assert.Contains("BadHeader", result.Value.Error);
    }

    [Fact]
    public void ValidHeader_Parsed()
    {
        var result = ApiRequestCommand.ValidateInputs(
            "/path", null, null, null, new[] { "Accept: text/plain" },
            out _, out var headers);

        Assert.Null(result);
        Assert.NotNull(headers);
        Assert.Equal("text/plain", headers!["Accept"]);
    }

    #endregion
}
