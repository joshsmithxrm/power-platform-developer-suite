using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Cli.Commands.Query;
using PPDS.Cli.Infrastructure.Errors;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Query;

/// <summary>
/// CLI unit tests for the FETCH command.
/// Tests command structure, argument parsing, input source validation,
/// and error handling. Does not execute real queries.
/// </summary>
[Trait("Category", "Unit")]
public class FetchCommandTests
{
    private readonly Command _command;

    public FetchCommandTests()
    {
        _command = FetchCommand.Create();
    }

    #region Command Structure

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("fetch", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Equal("Execute a FetchXML query against Dataverse", _command.Description);
    }

    [Fact]
    public void Create_HasFetchXmlArgument()
    {
        var argument = _command.Arguments.FirstOrDefault(a => a.Name == "fetchxml");
        Assert.NotNull(argument);
    }

    [Fact]
    public void Create_FetchXmlArgumentHasZeroOrOneArity()
    {
        var argument = _command.Arguments.First(a => a.Name == "fetchxml");
        Assert.Equal(ArgumentArity.ZeroOrOne, argument.Arity);
    }

    [Fact]
    public void Create_HasFileOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--file");
        Assert.NotNull(option);
        Assert.False(option!.Required);
    }

    [Fact]
    public void Create_HasStdinOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--stdin");
        Assert.NotNull(option);
        Assert.False(option!.Required);
    }

    [Fact]
    public void Create_HasProfileOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--profile");
        Assert.NotNull(option);
        Assert.Contains("-p", option!.Aliases);
    }

    [Fact]
    public void Create_HasEnvironmentOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--environment");
        Assert.NotNull(option);
        Assert.Contains("-env", option!.Aliases);
    }

    [Fact]
    public void Create_HasTopOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--top");
        Assert.NotNull(option);
        Assert.Contains("-t", option!.Aliases);
    }

    [Fact]
    public void Create_HasPageOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--page");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasPagingCookieOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--paging-cookie");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasCountOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--count");
        Assert.NotNull(option);
        Assert.Contains("-c", option!.Aliases);
    }

    [Fact]
    public void Create_HasOutputFormatOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--output-format");
        Assert.NotNull(option);
        Assert.False(option!.Required);
    }

    [Fact]
    public void Create_HasVerboseOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--verbose");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasDebugOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--debug");
        Assert.NotNull(option);
        Assert.False(option!.Required);
    }

    #endregion

    #region Argument Parsing

    [Fact]
    public void Parse_WithFetchXmlArgument_Succeeds()
    {
        var result = _command.Parse("\"<fetch><entity name='account'/></fetch>\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithoutAnyInputSource_HasValidationError()
    {
        var result = _command.Parse("");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithFileOption_Succeeds()
    {
        var result = _command.Parse("--file query.xml");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithStdinOption_Succeeds()
    {
        var result = _command.Parse("--stdin");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithMultipleInputSources_HasValidationError()
    {
        var result = _command.Parse("\"<fetch><entity name='account'/></fetch>\" --file query.xml");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithFetchXmlAndStdin_HasValidationError()
    {
        var result = _command.Parse("\"<fetch><entity name='account'/></fetch>\" --stdin");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithFileAndStdin_HasValidationError()
    {
        var result = _command.Parse("--file query.xml --stdin");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithProfileOption_Succeeds()
    {
        var result = _command.Parse("\"<fetch><entity name='account'/></fetch>\" --profile dev");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithEnvironmentOption_Succeeds()
    {
        var result = _command.Parse("\"<fetch><entity name='account'/></fetch>\" --environment https://org.crm.dynamics.com");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithTopOption_Succeeds()
    {
        var result = _command.Parse("\"<fetch><entity name='account'/></fetch>\" --top 10");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithPageOption_Succeeds()
    {
        var result = _command.Parse("\"<fetch><entity name='account'/></fetch>\" --page 2");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithCountFlag_Succeeds()
    {
        var result = _command.Parse("\"<fetch><entity name='account'/></fetch>\" --count");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOutputFormatJson_Succeeds()
    {
        var result = _command.Parse("\"<fetch><entity name='account'/></fetch>\" --output-format Json");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithMutuallyExclusiveVerbosity_HasError()
    {
        var result = _command.Parse("\"<fetch><entity name='account'/></fetch>\" --verbose --debug");
        Assert.NotEmpty(result.Errors);
    }

    #endregion

    #region Error Handling

    [Fact]
    public void ErrorHandling_GenericException_MapsViaExceptionMapper()
    {
        var ex = new InvalidOperationException("Something went wrong");
        var error = ExceptionMapper.Map(ex, context: "executing FetchXML query", debug: true);

        Assert.Equal(ErrorCodes.Operation.Internal, error.Code);
        Assert.Contains("Something went wrong", error.Message);
        Assert.NotNull(error.Details);
    }

    [Fact]
    public void ErrorHandling_GenericException_MapsToFailureExitCode()
    {
        var ex = new InvalidOperationException("Something went wrong");
        var exitCode = ExceptionMapper.ToExitCode(ex);

        Assert.Equal(ExitCodes.Failure, exitCode);
    }

    [Fact]
    public void ErrorHandling_FileNotFoundException_MapsToNotFoundExitCode()
    {
        var ex = new FileNotFoundException("File not found", "query.xml");
        var exitCode = ExceptionMapper.ToExitCode(ex);

        Assert.Equal(ExitCodes.NotFoundError, exitCode);
    }

    [Fact]
    public void ErrorHandling_FileNotFoundException_MapsToStructuredError()
    {
        var ex = new FileNotFoundException("File not found", "query.xml");
        var error = ExceptionMapper.Map(ex, context: "executing FetchXML query");

        Assert.Equal(ErrorCodes.Validation.FileNotFound, error.Code);
        Assert.Contains("File not found", error.Message);
    }

    [Fact]
    public void ErrorHandling_SuccessExitCode_IsZero()
    {
        Assert.Equal(0, ExitCodes.Success);
    }

    [Fact]
    public void ErrorHandling_FailureExitCode_IsTwo()
    {
        Assert.Equal(2, ExitCodes.Failure);
    }

    #endregion
}
