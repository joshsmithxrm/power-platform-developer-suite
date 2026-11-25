using System.IO;
using System.Text.Json;
using FluentAssertions;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Services.WebApi;
using PPDS.Query.Parsing;
using Xunit;

namespace PPDS.Cli.Tests.Services.Environment;

/// <summary>
/// A saved <c>type</c> or <c>protection</c> that is not a defined member never resolves weaker than Production
/// (#1829, review round 4). Two layers: the file read refuses anything but a member name, so every writer stops;
/// and the resolver, the confirmation gate and the DML and Web API guards treat an undefined value that reaches
/// them some other way (a cast) as Production.
/// </summary>
[Trait("Category", "Unit")]
public sealed class UndefinedProtectionValueTests : IDisposable
{
    private const string Url = "https://org.crm.dynamics.com";

    private readonly string _dir;
    private readonly string _path;

    public UndefinedProtectionValueTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ppds-undefprot-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "environments.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ---- layer 1: the read -----------------------------------------------------------------------------

    public static TheoryData<string, string> NotAName => new()
    {
        { "type", "1" }, { "protection", "1" },          // in range: refused (names only)
        { "type", "42" }, { "protection", "42" },        // out of range
        { "type", "-1" }, { "protection", "-1" },        // negative
        { "type", "\"42\"" }, { "protection", "\"42\"" } // numeric string
    };

    /// <summary>A type field replaces the type; a protection field sits beside type Development.</summary>
    private void WriteFile(string field, string rawValue)
    {
        var fields = field == "type" ? $"\"type\": {rawValue}" : $"\"type\": \"Development\", \"protection\": {rawValue}";
        File.WriteAllText(_path, $$"""{ "version": 1, "environments": [ { "url": "{{Url}}", {{fields}} } ] }""");
    }

    /// <summary>The config as a writer reads it, resolved the way SQL DML and the confirmation gate resolve it.</summary>
    private async Task<(ProtectionLevel Confirmation, ProtectionLevel SqlDml)> ResolveFromFileAsync()
    {
        using var store = new EnvironmentConfigStore(_path);
        var config = await new EnvironmentConfigService(store).GetConfigAsync(Url);
        return (WriteProtectionResolver.ResolveForConfirmation(config), WriteProtectionResolver.ResolveForSqlDml(config));
    }

    [Theory]
    [MemberData(nameof(NotAName))]
    public async Task AValueThatIsNotAMemberName_StopsResolution_ForSqlDmlAndForConfirmation(string field, string rawValue)
    {
        WriteFile(field, rawValue);

        await FluentActions.Awaiting(ResolveFromFileAsync).Should().ThrowAsync<JsonException>(
            "the read fails, so no writer gets a level from it");
    }

    [Theory]
    [MemberData(nameof(NotAName))]
    public async Task AValueThatIsNotAMemberName_IsPolicyUnavailable_AtTheConfirmationGate(string field, string rawValue)
    {
        WriteFile(field, rawValue);
        using var store = new EnvironmentConfigStore(_path);
        var gate = new EnvironmentConfirmationService(
            new EnvironmentConfirmationContext("session", "identity", Url, false), new EnvironmentConfigService(store),
            new EnvironmentAcknowledgementStore());

        var refusal = await FluentActions.Awaiting(() => gate.EnsureConfirmedAsync("write")).Should().ThrowAsync<PpdsException>();

        refusal.Which.ErrorCode.Should().Be(EnvironmentConfirmationErrorCodes.PolicyUnavailable);
    }

    [Fact]
    public async Task ANumberIsRefusedAtSave_WithAValidationCode()
    {
        using var store = new EnvironmentConfigStore(_path);

        var refusal = await FluentActions.Awaiting(() => new EnvironmentConfigService(store).SaveConfigAsync(Url, type: (EnvironmentType)42))
            .Should().ThrowAsync<PpdsException>();

        refusal.Which.ErrorCode.Should().Be(ErrorCodes.Validation.InvalidArguments);
        File.Exists(_path).Should().BeFalse("nothing was written");
    }

    // ---- layer 2: a value cast in ----------------------------------------------------------------------

    [Theory]
    [InlineData(42)]
    [InlineData(-1)]
    [InlineData(6)]
    public void AnUndefinedType_ResolvesToProduction(int value)
    {
        var type = (EnvironmentType)value;

        WriteProtectionResolver.Resolve(type, null).Should().Be(ProtectionLevel.Production);
        DmlSafetyGuard.DetectProtectionLevel(type).Should().Be(ProtectionLevel.Production);
        var config = new EnvironmentConfig { Url = Url, Type = type };
        WriteProtectionResolver.ResolveForConfirmation(config).Should().Be(ProtectionLevel.Production);
        WriteProtectionResolver.ResolveForSqlDml(config).Should().Be(ProtectionLevel.Production);
        WriteProtectionResolver.ResolveForCrossEnvironmentTarget(config).Should().Be(ProtectionLevel.Production);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(-1)]
    [InlineData(3)]
    public void AnUndefinedProtection_ResolvesToProduction_WhateverTheType(int value)
    {
        var protection = (ProtectionLevel)value;

        WriteProtectionResolver.Resolve(EnvironmentType.Development, protection).Should().Be(ProtectionLevel.Production);
        var config = new EnvironmentConfig { Url = Url, Type = EnvironmentType.Development, Protection = protection };
        WriteProtectionResolver.ResolveForConfirmation(config).Should().Be(ProtectionLevel.Production);
        WriteProtectionResolver.ResolveForSqlDml(config).Should().Be(ProtectionLevel.Production);
        WriteProtectionResolver.ResolveForCrossEnvironmentTarget(config).Should().Be(ProtectionLevel.Production);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(-1)]
    public async Task TheConfirmationGate_AsksForAnUndefinedProtection(int value)
    {
        var configuration = new Mock<IEnvironmentConfigService>();
        configuration.Setup(x => x.GetConfigAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnvironmentConfig { Url = Url, Type = EnvironmentType.Development, Protection = (ProtectionLevel)value });
        var gate = new EnvironmentConfirmationService(
            new EnvironmentConfirmationContext("session", "identity", Url, false), configuration.Object,
            new EnvironmentAcknowledgementStore());

        var refusal = await FluentActions.Awaiting(() => gate.EnsureConfirmedAsync("write")).Should().ThrowAsync<PpdsException>();

        refusal.Which.ErrorCode.Should().Be(EnvironmentConfirmationErrorCodes.Required);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(-1)]
    public void TheSqlDmlGuard_TreatsAnUndefinedLevelAsProduction(int value)
    {
        var level = (ProtectionLevel)value;
        var statement = new QueryParser().ParseStatement("DELETE FROM account WHERE name = 'x'");

        var result = new DmlSafetyGuard().Check(statement, new DmlSafetyOptions(), settings: null, protectionLevel: level);
        var production = new DmlSafetyGuard().Check(statement, new DmlSafetyOptions(), settings: null, protectionLevel: ProtectionLevel.Production);

        result.RequiresConfirmation.Should().BeTrue();
        result.RequiresPreview.Should().Be(production.RequiresPreview).And.BeTrue();

        var crossEnvironment = new DmlSafetyGuard().CheckCrossEnvironmentDml(statement,
            new QuerySafetySettings { CrossEnvironmentDmlPolicy = CrossEnvironmentDmlPolicy.Allow }, "local", "B", level);
        crossEnvironment.RequiresConfirmation.Should().BeTrue();
    }

    [Theory]
    [InlineData(42)]
    [InlineData(-1)]
    public void TheWebApiGuard_BlocksAnUndefinedLevelUnconfirmed(int value)
    {
        var level = (ProtectionLevel)value;

        WebApiWriteGuard.IsBlocked(level, isConfirmed: false).Should().BeTrue();
        WebApiWriteGuard.IsBlocked(HttpMethod.Patch, level, isConfirmed: false).Should().BeTrue();
        WebApiWriteGuard.IsBlocked(level, isConfirmed: true).Should().BeFalse();
    }
}
