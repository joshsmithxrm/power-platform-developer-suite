using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Profiles;

/// <summary>
/// A file PPDS cannot read fails every read with <see cref="EnvironmentConfigFileException"/>, whose message names
/// the file by its full path, the entry and the field, lists the accepted values and says to correct the field by
/// hand (#1829, review round 5). The enum settings under <c>safety_settings</c> are read by name only too.
/// </summary>
[Trait("Category", "Unit")]
public sealed class EnvironmentConfigFileErrorTests : IDisposable
{
    private const string Url = "https://org.crm.dynamics.com";
    private const string OtherUrl = "https://other.crm.dynamics.com";

    private readonly string _dir;
    private readonly string _path;

    public EnvironmentConfigFileErrorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ppds-envfile-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "environments.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<EnvironmentConfigFileException> ReadFailureAsync()
    {
        using var store = new EnvironmentConfigStore(_path);
        return (await FluentActions.Awaiting(() => store.ReloadAsync()).Should().ThrowAsync<EnvironmentConfigFileException>()).Which;
    }

    [Fact]
    public async Task ATypeThatIsNotAName_NamesTheFileTheEntryAndTheField_AndSaysHowToRepairIt()
    {
        File.WriteAllText(_path, $$"""{ "version": 1, "environments": [ { "url": "{{Url}}/", "type": 1 } ] }""");

        var failure = await ReadFailureAsync();

        failure.ErrorCode.Should().Be("Validation.EnvironmentConfigInvalid");
        failure.FilePath.Should().Be(Path.GetFullPath(_path));
        failure.Entry.Should().Contain(Url);
        failure.Field.Should().Be("type");
        failure.AcceptedValues.Should().Equal(Enum.GetNames<EnvironmentType>());
        failure.Message.Should().Contain($"'{Path.GetFullPath(_path)}'")
            .And.Contain(Url)
            .And.Contain("the field 'type'")
            .And.Contain("Unknown, Production, Sandbox, Development, Test, Trial")
            .And.Contain("Correct that field in the file by hand");
    }

    [Fact]
    public async Task TheEntryNamed_IsTheOneThatHoldsTheValue_NotTheFirst()
    {
        File.WriteAllText(_path,
            $$"""{ "environments": [ { "url": "{{Url}}", "type": "Production" }, { "url": "{{OtherUrl}}", "protection": "Production, Development" } ] }""");

        var failure = await ReadFailureAsync();

        failure.Entry.Should().Contain(OtherUrl).And.NotContain(Url);
        failure.Field.Should().Be("protection");
        failure.Message.Should().Contain(OtherUrl).And.Contain("Development, Test, Production");
    }

    [Fact]
    public async Task AnEntryWithoutAUrl_IsNamedByItsLabel_ElseByItsPosition()
    {
        File.WriteAllText(_path, """{ "environments": [ { "label": "Prod-EU", "type": "3" } ] }""");
        (await ReadFailureAsync()).Message.Should().Contain("the label 'Prod-EU'");

        File.WriteAllText(_path, $$"""{ "environments": [ { "url": "{{Url}}" }, { "type": -1 } ] }""");
        (await ReadFailureAsync()).Message.Should().Contain("environment number 2");
    }

    public static TheoryData<string, string, string> SafetySettingNotAName => new()
    {
        { "cross_env_dml_policy", "2", "ReadOnly, Prompt, Allow" },
        { "cross_env_dml_policy", "42", "ReadOnly, Prompt, Allow" },
        { "cross_env_dml_policy", "\"ReadOnly, Allow\"", "ReadOnly, Prompt, Allow" },
        { "cross_env_dml_policy", "\"2\"", "ReadOnly, Prompt, Allow" },
        { "bypass_custom_plugins", "3", "None, Synchronous, Asynchronous, All" },
        { "bypass_custom_plugins", "\"Synchronous, Asynchronous\"", "None, Synchronous, Asynchronous, All" },
        { "datetime_mode", "1", "Utc, Local, EnvironmentTimezone" },
        { "datetime_mode", "\"Utc,Local\"", "Utc, Local, EnvironmentTimezone" },
    };

    [Theory]
    [MemberData(nameof(SafetySettingNotAName))]
    public async Task ASafetySettingThatIsNotAName_FailsTheRead_AndIsNamed(string setting, string rawValue, string accepted)
    {
        File.WriteAllText(_path,
            $$"""{ "environments": [ { "url": "{{Url}}", "type": "Development", "safety_settings": { "{{setting}}": {{rawValue}} } } ] }""");

        var failure = await ReadFailureAsync();

        failure.Field.Should().Be($"safety_settings.{setting}");
        failure.Message.Should().Contain(Url).And.Contain($"'safety_settings.{setting}'").And.Contain(accepted);
        using var store = new EnvironmentConfigStore(_path);
        await FluentActions.Awaiting(() => store.GetConfigAsync(Url)).Should().ThrowAsync<EnvironmentConfigFileException>();
    }

    [Theory]
    [InlineData("\"prompt\"", CrossEnvironmentDmlPolicy.Prompt)]
    [InlineData("\"ALLOW\"", CrossEnvironmentDmlPolicy.Allow)]
    [InlineData("\"ReadOnly\"", CrossEnvironmentDmlPolicy.ReadOnly)]
    public async Task ASafetySettingName_InAnyCase_IsRead(string rawValue, CrossEnvironmentDmlPolicy expected)
    {
        File.WriteAllText(_path,
            $$"""{ "environments": [ { "url": "{{Url}}", "safety_settings": { "cross_env_dml_policy": {{rawValue}} } } ] }""");
        using var store = new EnvironmentConfigStore(_path);

        (await store.GetConfigAsync(Url))!.SafetySettings!.CrossEnvironmentDmlPolicy.Should().Be(expected);
    }

    [Fact]
    public async Task ASafetySetting_IsWrittenByName_AndReadBack()
    {
        using (var store = new EnvironmentConfigStore(_path))
        {
            await store.UpdateAsync(collection =>
            {
                collection.Environments.Add(new EnvironmentConfig
                {
                    Url = Url,
                    SafetySettings = new QuerySafetySettings { CrossEnvironmentDmlPolicy = CrossEnvironmentDmlPolicy.Prompt }
                });
                return true;
            });
        }

        File.ReadAllText(_path).Should().Contain("\"cross_env_dml_policy\": \"Prompt\"");
        using var reread = new EnvironmentConfigStore(_path);
        (await reread.GetConfigAsync(Url))!.SafetySettings!.CrossEnvironmentDmlPolicy.Should().Be(CrossEnvironmentDmlPolicy.Prompt);
    }

    [Fact]
    public async Task TextThatIsNotJson_NamesTheFileAndTheLine()
    {
        File.WriteAllText(_path, "{\n  \"environments\": [\n    { \"url\": \"x\" \n  ]\n}");

        var failure = await ReadFailureAsync();

        failure.ErrorCode.Should().Be(EnvironmentConfigFileException.Code);
        failure.AcceptedValues.Should().BeNull();
        failure.Message.Should().Contain(Path.GetFullPath(_path)).And.Contain("not valid JSON at line").And.Contain("by hand");
    }

    [Fact]
    public async Task TheMessage_HasNoStackTraceOrGuid_AndTheFileIsNotChanged()
    {
        var content = $$"""{ "environments": [ { "url": "{{Url}}", "type": 42 } ] }""";
        File.WriteAllText(_path, content);

        var failure = await ReadFailureAsync();

        failure.Message.Should().NotContain(" at PPDS.").And.NotContain("System.Text.Json");
        Regex.IsMatch(failure.Message, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Should().BeFalse();
        File.ReadAllText(_path).Should().Be(content);
    }

    [Fact]
    public async Task TheFailure_IsStillAJsonException_SoCallersThatHandledAFailedReadStillDo()
        => (await ReadFailureAsyncFor("\"type\": 7")).Should().BeAssignableTo<JsonException>();

    private async Task<EnvironmentConfigFileException> ReadFailureAsyncFor(string member)
    {
        File.WriteAllText(_path, $$"""{ "environments": [ { "url": "{{Url}}", {{member}} } ] }""");
        return await ReadFailureAsync();
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("production", true)]
    [InlineData(" Production", false)]
    [InlineData("1", false)]
    [InlineData("Production, Test", false)]
    [InlineData("Production,Test", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void EnumMemberName_AcceptsOnlyAMemberName(string? text, bool accepted)
        => EnumMemberName.TryParse<EnvironmentType>(text, out _).Should().Be(accepted);
}
