using System.Text.Json;
using FluentAssertions;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Profiles;

/// <summary>
/// The saved <c>type</c> and <c>protection</c> decide whether a write needs confirmation, so the file is read by
/// member name only. A number, even one that names a member (<c>"type": 1</c>), a numeric string or a list of
/// names fails the read: every writer then stops instead of resolving a value that names no level, which used
/// to be weaker than Production (#1829, review round 4).
/// </summary>
[Trait("Category", "Unit")]
public sealed class EnvironmentConfigEnumReadTests : IDisposable
{
    private const string Url = "https://org.crm.dynamics.com";

    private readonly string _dir;
    private readonly string _path;

    public EnvironmentConfigEnumReadTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ppds-envenum-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "environments.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void WriteFile(string field, string rawValue)
        => File.WriteAllText(_path,
            $$"""{ "version": 1, "environments": [ { "url": "{{Url}}/", "{{field}}": {{rawValue}} } ] }""");

    public static TheoryData<string, string> NotAName => new()
    {
        // an integer in range: refused (decision: names only, so "type": 1 does not mean Production)
        { "type", "1" }, { "protection", "2" }, { "type", "0" }, { "protection", "0" },
        // out of range, negative
        { "type", "42" }, { "protection", "42" }, { "type", "-1" }, { "protection", "-1" },
        // numeric strings
        { "type", "\"42\"" }, { "protection", "\"42\"" }, { "type", "\"1\"" }, { "protection", "\"-1\"" },
        // a list of names, which Enum.Parse would combine into a value that names no member
        { "type", "\"Production, Development\"" }, { "protection", "\"Production, Development\"" },
        // other text
        { "type", "\"Prod\"" }, { "protection", "true" },
    };

    [Theory]
    [MemberData(nameof(NotAName))]
    public async Task AValueThatIsNotAMemberName_FailsTheRead(string field, string rawValue)
    {
        WriteFile(field, rawValue);
        using var store = new EnvironmentConfigStore(_path);

        await FluentActions.Awaiting(() => store.GetConfigAsync(Url)).Should().ThrowAsync<JsonException>();
        await FluentActions.Awaiting(() => store.ReloadAsync()).Should().ThrowAsync<JsonException>();
    }

    [Theory]
    [InlineData("type", "\"Production\"", EnvironmentType.Production, null)]
    [InlineData("type", "\"development\"", EnvironmentType.Development, null)]
    [InlineData("protection", "\"Test\"", null, ProtectionLevel.Test)]
    [InlineData("protection", "\"production\"", null, ProtectionLevel.Production)]
    [InlineData("type", "null", null, null)]
    public async Task AMemberName_InAnyCase_IsRead(string field, string rawValue, EnvironmentType? type, ProtectionLevel? protection)
    {
        WriteFile(field, rawValue);
        using var store = new EnvironmentConfigStore(_path);

        var config = await store.GetConfigAsync(Url);

        config!.Type.Should().Be(type);
        config.Protection.Should().Be(protection);
    }

    [Fact]
    public async Task TheTypeIsWrittenByName()
    {
        using var store = new EnvironmentConfigStore(_path);
        await store.SaveConfigAsync(Url, type: EnvironmentType.Production);

        File.ReadAllText(_path).Should().Contain("\"type\": \"Production\"");
    }

    [Fact]
    public async Task AnUndefinedType_IsNotWritten()
    {
        using var store = new EnvironmentConfigStore(_path);
        await store.SaveConfigAsync(Url, type: EnvironmentType.Development);
        var before = File.ReadAllText(_path);

        await FluentActions.Awaiting(() => store.SaveConfigAsync(Url, type: (EnvironmentType)42))
            .Should().ThrowAsync<JsonException>();

        File.ReadAllText(_path).Should().Be(before);
    }
}
