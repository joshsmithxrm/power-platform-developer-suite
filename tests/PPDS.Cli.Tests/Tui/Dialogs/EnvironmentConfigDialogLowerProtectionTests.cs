using FluentAssertions;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Tui.Dialogs;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Dialogs;

/// <summary>
/// The TUI Environment Config dialog lowers write protection only after the user says yes (#1829).
/// Exercises the dialog's save step without a running Terminal.Gui application.
/// </summary>
[Trait("Category", "TuiUnit")]
public sealed class EnvironmentConfigDialogLowerProtectionTests : IDisposable
{
    private const string Url = "https://org.crm.dynamics.com/";

    private readonly string _tempDir;
    private readonly EnvironmentConfigStore _store;
    private readonly EnvironmentConfigService _service;

    public EnvironmentConfigDialogLowerProtectionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ppds-tuicfg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new EnvironmentConfigStore(Path.Combine(_tempDir, "environments.json"));
        _service = new EnvironmentConfigService(_store);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Lowering_UserSaysYes_IsSaved()
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);
        ProtectionChangeAssessment? asked = null;

        var saved = await EnvironmentConfigDialog.SaveAsync(
            _service, Url, label: null, EnvironmentType.Sandbox, color: null, clearColor: false,
            a => { asked = a; return true; });

        saved.Should().BeTrue();
        asked!.LowersProtection.Should().BeTrue();
        (await _store.GetConfigAsync(Url))!.Type.Should().Be(EnvironmentType.Sandbox);
    }

    [Fact]
    public async Task Lowering_UserSaysNo_SavesNothing()
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);

        var saved = await EnvironmentConfigDialog.SaveAsync(
            _service, Url, label: "Changed", EnvironmentType.Sandbox, color: null, clearColor: false, _ => false);

        saved.Should().BeFalse();
        var config = await _store.GetConfigAsync(Url);
        config!.Type.Should().Be(EnvironmentType.Production);
        config.Label.Should().BeNull();
    }

    [Fact]
    public async Task NotLowering_DoesNotAsk()
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Development);

        var saved = await EnvironmentConfigDialog.SaveAsync(
            _service, Url, label: "Dev", EnvironmentType.Production, color: null, clearColor: false,
            _ => throw new InvalidOperationException("must not ask"));

        saved.Should().BeTrue();
    }

    [Fact]
    public async Task ServiceStillRefuses_WhenTheDialogIsBypassed()
    {
        await _store.SaveConfigAsync(Url, type: EnvironmentType.Production);

        var direct = () => _service.SaveConfigAsync(Url, type: EnvironmentType.Sandbox);

        (await direct.Should().ThrowAsync<PpdsException>()).Which.ErrorCode
            .Should().Be(EnvironmentConfirmationErrorCodes.ProtectionLoweringConfirmationRequired);
    }
}
