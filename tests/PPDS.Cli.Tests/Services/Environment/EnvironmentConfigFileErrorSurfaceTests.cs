using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Query;
using PPDS.Query.Parsing;
using Xunit;

namespace PPDS.Cli.Tests.Services.Environment;

/// <summary>
/// A file PPDS cannot read is reported with <see cref="ErrorCodes.Validation.EnvironmentConfigInvalid"/> and a message
/// that names the file, the entry and the field, on the CLI, at the confirmation gate and in the daemon; every reader
/// still fails closed (#1829, review round 5). And a cross-environment policy that is not a defined member is the
/// strictest, ReadOnly.
/// </summary>
[Trait("Category", "Unit")]
public sealed class EnvironmentConfigFileErrorSurfaceTests : IDisposable
{
    private const string Url = "https://org.crm.dynamics.com";

    private readonly string _dir;
    private readonly string _path;

    public EnvironmentConfigFileErrorSurfaceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ppds-envfile-cli-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "environments.json");
        File.WriteAllText(_path, $$"""{ "environments": [ { "url": "{{Url}}", "type": 3 } ] }""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void ShouldNameFileEntryAndField(string message, string field = "type")
        => message.Should().Contain(Path.GetFullPath(_path)).And.Contain(Url).And.Contain($"the field '{field}'")
            .And.Contain("by hand");

    [Fact]
    public void TheCode_IsTheOneTheAuthLibraryCarries()
        => ErrorCodes.Validation.EnvironmentConfigInvalid.Should().Be(EnvironmentConfigFileException.Code)
            .And.Be("Validation.EnvironmentConfigInvalid");

    [Fact]
    public async Task TheCli_ReportsTheCode_AndTheMessage_NotOperationInternal()
    {
        using var store = new EnvironmentConfigStore(_path);
        var failure = (await FluentActions.Awaiting(() => new EnvironmentConfigService(store).GetConfigAsync(Url))
            .Should().ThrowAsync<EnvironmentConfigFileException>()).Which;

        var (error, exitCode) = ExceptionMapper.MapWithExitCode(failure);

        error.Code.Should().Be(ErrorCodes.Validation.EnvironmentConfigInvalid);
        exitCode.Should().Be(ExitCodes.InvalidArguments);
        ShouldNameFileEntryAndField(error.Message);
    }

    [Fact]
    public async Task TheConfirmationGate_StaysPolicyUnavailable_AndCarriesTheDetail()
    {
        using var store = new EnvironmentConfigStore(_path);
        var gate = new EnvironmentConfirmationService(
            new EnvironmentConfirmationContext("session", "identity", Url, false), new EnvironmentConfigService(store),
            new EnvironmentAcknowledgementStore());

        var refusal = (await FluentActions.Awaiting(() => gate.EnsureConfirmedAsync("update", Url))
            .Should().ThrowAsync<PpdsException>()).Which;

        refusal.ErrorCode.Should().Be(EnvironmentConfirmationErrorCodes.PolicyUnavailable);
        refusal.UserMessage.Should().Contain("No write was performed");
        ShouldNameFileEntryAndField(refusal.UserMessage);
    }

    [Fact]
    public async Task TheDaemon_EnvironmentMethods_ReportTheCode_AndTheMessage()
    {
        using var store = new EnvironmentConfigStore(_path);
        using var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton<IEnvironmentConfigService>(sp => new EnvironmentConfigService(sp.GetRequiredService<EnvironmentConfigStore>()))
            .BuildServiceProvider();
        var handler = new RpcMethodHandler(new Mock<IDaemonConnectionPoolManager>().Object, services);

        foreach (var call in new Func<Task>[]
        {
            () => handler.EnvConfigGetAsync(Url),
            () => handler.EnvConfigSetAsync(Url, type: "Production"),
            () => handler.EnvConfigRemoveAsync(Url),
        })
        {
            var failure = (await FluentActions.Awaiting(call).Should().ThrowAsync<RpcException>()).Which;
            failure.StructuredErrorCode.Should().Be(ErrorCodes.Validation.EnvironmentConfigInvalid);
            ShouldNameFileEntryAndField(failure.Message);
        }
    }

    [Fact]
    public async Task TheDaemon_SafeExecute_PassesTheMessageOn_InsteadOfRedactingIt()
    {
        using var store = new EnvironmentConfigStore(_path);
        using var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        var handler = new RpcMethodHandler(new Mock<IDaemonConnectionPoolManager>().Object, services);

        var failure = (await FluentActions.Awaiting(() => handler.SafeExecuteAsync<int>("query/sql", async () =>
        {
            await store.ReloadAsync();
            return 0;
        })).Should().ThrowAsync<RpcException>()).Which;

        failure.StructuredErrorCode.Should().Be(ErrorCodes.Validation.EnvironmentConfigInvalid);
        ShouldNameFileEntryAndField(failure.Message);
    }

    // ---- cross_env_dml_policy that is not a defined member ------------------------------------------------------

    [Theory]
    [InlineData(42, ProtectionLevel.Development)]
    [InlineData(-1, ProtectionLevel.Development)]
    [InlineData(3, ProtectionLevel.Test)]
    public void AnUndefinedCrossEnvironmentPolicy_IsReadOnly_TheStrictest(int value, ProtectionLevel target)
    {
        var statement = new QueryParser().ParseStatement("DELETE FROM account WHERE name = 'x'");
        var guard = new DmlSafetyGuard();

        var undefined = guard.CheckCrossEnvironmentDml(statement,
            new QuerySafetySettings { CrossEnvironmentDmlPolicy = (CrossEnvironmentDmlPolicy)value }, "local", "B", target);
        var readOnly = guard.CheckCrossEnvironmentDml(statement,
            new QuerySafetySettings { CrossEnvironmentDmlPolicy = CrossEnvironmentDmlPolicy.ReadOnly }, "local", "B", target);

        undefined.IsBlocked.Should().BeTrue("an undefined policy must not behave like Allow");
        undefined.ErrorCode.Should().Be(readOnly.ErrorCode);
        undefined.BlockReason.Should().Be(readOnly.BlockReason);
    }

    [Fact]
    public void AnUndefinedCrossEnvironmentPolicy_StillLetsReadOnlyStatementsThrough()
    {
        var statement = new QueryParser().ParseStatement("SELECT name FROM account");

        new DmlSafetyGuard().CheckCrossEnvironmentDml(statement,
            new QuerySafetySettings { CrossEnvironmentDmlPolicy = (CrossEnvironmentDmlPolicy)42 }, "local", "B", ProtectionLevel.Development)
            .IsBlocked.Should().BeFalse();
    }
}
