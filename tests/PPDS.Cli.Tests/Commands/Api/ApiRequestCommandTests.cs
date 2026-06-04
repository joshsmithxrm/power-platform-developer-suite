using System.CommandLine;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Api;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.WebApi;
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

    #region AC-05: --include outputs status line and headers

    [Fact]
    public void Include_OutputsHeaders()
    {
        var response = new RawWebApiResponse
        {
            StatusCode = 200,
            ReasonPhrase = "OK",
            Headers = new Dictionary<string, string> { ["OData-Version"] = "4.0", ["Content-Type"] = "application/json" },
            Body = "{\"value\":[]}"
        };

        var lines = ApiRequestCommand.FormatResponsePreamble(response, include: true).ToList();

        Assert.Contains("HTTP/1.1 200 OK", lines);
        Assert.Contains("OData-Version: 4.0", lines);
        Assert.Contains("Content-Type: application/json", lines);
        Assert.Contains(string.Empty, lines); // blank separator before body
    }

    [Fact]
    public void Include_False_OutputsNoPreamble()
    {
        var response = new RawWebApiResponse { StatusCode = 200, ReasonPhrase = "OK" };
        var lines = ApiRequestCommand.FormatResponsePreamble(response, include: false).ToList();
        Assert.Empty(lines);
    }

    #endregion

    #region AC-02: write-blocked returns exit code 2

    [Fact]
    public async Task WriteBlocked_ReturnsExitCode2()
    {
        // Drive the real command path: a production mutating request whose service raises
        // the write-guard PpdsException must map to exit code 2 (distinct from non-2xx = 1).
        var service = new Mock<IRawWebApiService>();
        service
            .Setup(s => s.SendAsync(It.IsAny<RawWebApiRequest>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PpdsException(
                "Api.WriteBlocked",
                "Mutating request blocked on Production environment. Add --confirm to proceed."));

        var request = new RawWebApiRequest
        {
            EnvironmentUrl = "https://org.crm.dynamics.com",
            Path = "/api/data/v9.2/accounts",
            Method = HttpMethod.Post,
            Body = "{}",
            IsConfirmed = false,
            ProtectionLevel = ProtectionLevel.Production
        };

        var exitCode = await ApiRequestCommand.RunRequestAsync(
            service.Object, request, include: false, CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Equal(2, ExitCodes.Failure); // contract: write-blocked exit code is exactly 2
    }

    [Fact]
    public async Task Non2xxResponse_ReturnsExitCode1()
    {
        // A non-2xx response (not a write block) routes to stderr and maps to exit 1,
        // keeping it distinguishable from the write-blocked exit code (2).
        var service = new Mock<IRawWebApiService>();
        service
            .Setup(s => s.SendAsync(It.IsAny<RawWebApiRequest>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RawWebApiResponse
            {
                StatusCode = 404,
                ReasonPhrase = "Not Found",
                Body = "{\"error\":{}}"
            });

        var request = new RawWebApiRequest
        {
            EnvironmentUrl = "https://org.crm.dynamics.com",
            Path = "/api/data/v9.2/nope",
            Method = HttpMethod.Get
        };

        var exitCode = await ApiRequestCommand.RunRequestAsync(
            service.Object, request, include: false, CancellationToken.None);

        Assert.Equal(1, exitCode);
    }

    #endregion

    #region AC-02/write-guard fail-safe: unknown environment blocks mutating requests

    [Fact]
    public void ResolveProtectionLevel_UnknownEnvironment_IsProduction()
    {
        // Fail-safe: an undetectable environment must resolve to Production so the write
        // guard is conservative (unlike DmlSafetyGuard.DetectProtectionLevel which maps
        // Unknown -> Development for SQL DML).
        var level = ApiRequestCommand.ResolveProtectionLevel(EnvironmentType.Unknown, configuredProtection: null);
        Assert.Equal(ProtectionLevel.Production, level);
    }

    [Fact]
    public void UnknownEnvironment_BlocksMutatingRequestWithoutConfirm()
    {
        // End-to-end through the real guard: Unknown env -> Production -> POST without
        // --confirm is blocked.
        var level = ApiRequestCommand.ResolveProtectionLevel(EnvironmentType.Unknown, configuredProtection: null);

        var blocked = WebApiWriteGuard.IsBlocked(HttpMethod.Post, level, isConfirmed: false);

        Assert.True(blocked, "Unknown environment must block a mutating request without --confirm");
    }

    [Fact]
    public void UnknownEnvironment_AllowsMutatingRequestWithConfirm()
    {
        var level = ApiRequestCommand.ResolveProtectionLevel(EnvironmentType.Unknown, configuredProtection: null);

        var blocked = WebApiWriteGuard.IsBlocked(HttpMethod.Post, level, isConfirmed: true);

        Assert.False(blocked, "--confirm must bypass the write guard even on an unknown environment");
    }

    [Fact]
    public void ConfiguredProtection_OverridesUnknownFailSafe()
    {
        // An explicit per-environment protection override always wins over the Unknown fail-safe.
        var level = ApiRequestCommand.ResolveProtectionLevel(
            EnvironmentType.Unknown, configuredProtection: ProtectionLevel.Development);
        Assert.Equal(ProtectionLevel.Development, level);
    }

    #endregion

    #region AC-08: --body-file reads body from file

    [Fact]
    public async Task BodyFile_ReadsFromDisk()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            const string expectedBody = "{\"name\":\"test\"}";
            await File.WriteAllTextAsync(tempFile, expectedBody);

            // Drive the command's actual file-read branch (ResolveBodyAsync), not a
            // re-implementation: this exercises the --body-file reading path the handler uses.
            var (resolvedBody, error, exitCode) = await ApiRequestCommand.ResolveBodyAsync(
                body: null, bodyFile: tempFile, CancellationToken.None);

            Assert.Null(error);
            Assert.Equal(ExitCodes.Success, exitCode);
            Assert.Equal(expectedBody, resolvedBody);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task BodyFile_Missing_ReturnsNotFound()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"ppds-missing-{Guid.NewGuid():N}.json");

        var (resolvedBody, error, exitCode) = await ApiRequestCommand.ResolveBodyAsync(
            body: null, bodyFile: missing, CancellationToken.None);

        Assert.Null(resolvedBody);
        Assert.NotNull(error);
        Assert.Contains(missing, error);
        Assert.Equal(ExitCodes.NotFoundError, exitCode);
    }

    [Fact]
    public async Task ResolveBody_NoFile_ReturnsInlineBody()
    {
        var (resolvedBody, error, _) = await ApiRequestCommand.ResolveBodyAsync(
            body: "{\"inline\":true}", bodyFile: null, CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("{\"inline\":true}", resolvedBody);
    }

    #endregion

    #region POST-on-body default (issue #1164)

    [Fact]
    public void NoMethod_WithInlineBody_DefaultsToPost()
    {
        var result = ApiRequestCommand.ValidateInputs(
            "/api/data/v9.2/accounts", method: null, body: "{}", bodyFile: null, headers: null,
            out var method, out _);

        Assert.Null(result);
        Assert.Equal(HttpMethod.Post, method);
    }

    [Fact]
    public void NoMethod_WithBodyFile_DefaultsToPost()
    {
        var result = ApiRequestCommand.ValidateInputs(
            "/api/data/v9.2/accounts", method: null, body: null, bodyFile: "file.json", headers: null,
            out var method, out _);

        Assert.Null(result);
        Assert.Equal(HttpMethod.Post, method);
    }

    [Fact]
    public void NoMethod_NoBody_DefaultsToGet()
    {
        var result = ApiRequestCommand.ValidateInputs(
            "/api/data/v9.2/accounts", method: null, body: null, bodyFile: null, headers: null,
            out var method, out _);

        Assert.Null(result);
        Assert.Equal(HttpMethod.Get, method);
    }

    [Fact]
    public void ExplicitMethod_WithBody_WinsOverPostDefault()
    {
        // An explicit --method always wins, even when a body is present.
        var result = ApiRequestCommand.ValidateInputs(
            "/api/data/v9.2/accounts", method: "PATCH", body: "{}", bodyFile: null, headers: null,
            out var method, out _);

        Assert.Null(result);
        Assert.Equal(HttpMethod.Patch, method);
    }

    #endregion
}
