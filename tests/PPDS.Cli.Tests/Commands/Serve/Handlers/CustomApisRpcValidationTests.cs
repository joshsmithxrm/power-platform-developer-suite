using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Auth.DependencyInjection;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Serve.Handlers;

/// <summary>
/// Validation-parity tests for the customApis RPC handlers.
///
/// Issue #1228: customApis/register mapped nested parameters with `?? ""`, silently
/// coercing a null/whitespace uniqueName/displayName/type to an empty string, while
/// customApis/addParameter validated the same fields and threw RequiredField. These
/// tests pin the two entry points to the same validation rules.
/// </summary>
[Trait("Category", "Unit")]
public class CustomApisRpcValidationTests
{
    private const string ValidPluginTypeId = "11111111-1111-1111-1111-111111111111";
    private const string ValidApiId = "22222222-2222-2222-2222-222222222222";

    private static IServiceProvider CreateAuthServices() =>
        new ServiceCollection().AddAuthServices().BuildServiceProvider();

    private static RpcMethodHandler CreateHandler() =>
        new(new Mock<IDaemonConnectionPoolManager>().Object, CreateAuthServices());

    private static List<CustomApiParameterDto> SingleParameter(
        string? uniqueName = "in_param",
        string? displayName = "In Param",
        string? type = "String") =>
        new()
        {
            new CustomApiParameterDto
            {
                UniqueName = uniqueName,
                DisplayName = displayName,
                Type = type,
                Direction = "Request",
            }
        };

    #region register: nested parameter validation

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Register_ParameterEmptyUniqueName_ThrowsRequiredField(string? uniqueName)
    {
        var handler = CreateHandler();

        var act = () => handler.CustomApisRegisterAsync(
            "ppds_MyApi",
            "My API",
            ValidPluginTypeId,
            parameters: SingleParameter(uniqueName: uniqueName));

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StructuredErrorCode.Should().Be(ErrorCodes.Validation.RequiredField);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Register_ParameterEmptyDisplayName_ThrowsRequiredField(string? displayName)
    {
        var handler = CreateHandler();

        var act = () => handler.CustomApisRegisterAsync(
            "ppds_MyApi",
            "My API",
            ValidPluginTypeId,
            parameters: SingleParameter(displayName: displayName));

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StructuredErrorCode.Should().Be(ErrorCodes.Validation.RequiredField);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Register_ParameterEmptyType_ThrowsRequiredField(string? type)
    {
        var handler = CreateHandler();

        var act = () => handler.CustomApisRegisterAsync(
            "ppds_MyApi",
            "My API",
            ValidPluginTypeId,
            parameters: SingleParameter(type: type));

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StructuredErrorCode.Should().Be(ErrorCodes.Validation.RequiredField);
    }

    [Fact]
    public async Task Register_NullParameterElement_ThrowsRequiredField()
    {
        var handler = CreateHandler();

        // A malformed payload can deserialize to a list containing a null element;
        // it must produce a structured RequiredField error, not a NullReferenceException.
        var act = () => handler.CustomApisRegisterAsync(
            "ppds_MyApi",
            "My API",
            ValidPluginTypeId,
            parameters: new List<CustomApiParameterDto> { null! });

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StructuredErrorCode.Should().Be(ErrorCodes.Validation.RequiredField);
    }

    [Fact]
    public async Task Register_NullParameters_DoesNotThrowValidation()
    {
        var handler = CreateHandler();

        // No parameters supplied: must pass the nested-parameter gate. It still fails
        // later (no real connection), but never with a parameter RequiredField.
        var act = () => handler.CustomApisRegisterAsync(
            "ppds_MyApi",
            "My API",
            ValidPluginTypeId,
            parameters: null);

        var ex = await Record.ExceptionAsync(act);
        (ex as RpcException)?.StructuredErrorCode.Should().NotBe(ErrorCodes.Validation.RequiredField);
    }

    #endregion

    #region addParameter: parity reference

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddParameter_EmptyUniqueName_ThrowsRequiredField(string? uniqueName)
    {
        var handler = CreateHandler();

        var act = () => handler.CustomApisAddParameterAsync(
            ValidApiId,
            uniqueName!,
            "In Param",
            "String",
            "Request");

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StructuredErrorCode.Should().Be(ErrorCodes.Validation.RequiredField);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddParameter_EmptyDisplayName_ThrowsRequiredField(string? displayName)
    {
        var handler = CreateHandler();

        var act = () => handler.CustomApisAddParameterAsync(
            ValidApiId,
            "in_param",
            displayName!,
            "String",
            "Request");

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StructuredErrorCode.Should().Be(ErrorCodes.Validation.RequiredField);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AddParameter_EmptyType_ThrowsRequiredField(string? type)
    {
        var handler = CreateHandler();

        var act = () => handler.CustomApisAddParameterAsync(
            ValidApiId,
            "in_param",
            "In Param",
            type!,
            "Request");

        (await act.Should().ThrowAsync<RpcException>())
            .Which.StructuredErrorCode.Should().Be(ErrorCodes.Validation.RequiredField);
    }

    #endregion
}
