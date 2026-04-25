using PPDS.Auth.Profiles;
using PPDS.Cli.Tui.Dialogs;
using PPDS.Cli.Tui.Testing.States;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Dialogs;

[Trait("Category", "TuiUnit")]
public sealed class ProfileCreationDialogStateTests
{
    [Fact]
    public void StateRecord_CapturesAllProperties()
    {
        var methods = new List<string> { "Device Code", "Browser", "Client Secret" };

        var state = new ProfileCreationDialogState(
            Title: "Create Profile",
            ProfileName: "my-profile",
            SelectedAuthMethod: "Browser",
            AvailableAuthMethods: methods,
            IsCreating: false,
            ValidationError: null,
            CanCreate: true);

        Assert.Equal("Create Profile", state.Title);
        Assert.Equal("my-profile", state.ProfileName);
        Assert.Equal("Browser", state.SelectedAuthMethod);
        Assert.Equal(3, state.AvailableAuthMethods.Count);
        Assert.False(state.IsCreating);
        Assert.Null(state.ValidationError);
        Assert.True(state.CanCreate);
    }

    [Fact]
    public void StateRecord_CreatingInProgress()
    {
        var state = new ProfileCreationDialogState(
            Title: "Create Profile",
            ProfileName: "test",
            SelectedAuthMethod: "Device Code",
            AvailableAuthMethods: ["Device Code"],
            IsCreating: true,
            ValidationError: null,
            CanCreate: false);

        Assert.True(state.IsCreating);
        Assert.False(state.CanCreate);
    }

    [Fact]
    public void StateRecord_WithValidationError()
    {
        var state = new ProfileCreationDialogState(
            Title: "Create Profile",
            ProfileName: "",
            SelectedAuthMethod: "Client Secret",
            AvailableAuthMethods: ["Client Secret"],
            IsCreating: false,
            ValidationError: "Application ID is required",
            CanCreate: true);

        Assert.Equal("Application ID is required", state.ValidationError);
    }

    [Fact]
    public void StateRecord_EqualityByValue()
    {
        var methods = new List<string> { "Device Code" };

        var a = new ProfileCreationDialogState(
            Title: "Create Profile", ProfileName: "p1",
            SelectedAuthMethod: "Device Code", AvailableAuthMethods: methods,
            IsCreating: false, ValidationError: null, CanCreate: true);

        var b = new ProfileCreationDialogState(
            Title: "Create Profile", ProfileName: "p1",
            SelectedAuthMethod: "Device Code", AvailableAuthMethods: methods,
            IsCreating: false, ValidationError: null, CanCreate: true);

        Assert.Equal(a, b);
    }

    // --- AuthMethodFormModel: whitespace-only values treated as missing ---

    [Fact]
    public void Validate_ClientSecret_WhitespaceAppId_ReturnsError()
    {
        var model = new AuthMethodFormModel(isWindows: true);
        var values = ValidSpnValues() with { AppId = "   " };

        var error = model.Validate(AuthMethod.ClientSecret, values);

        Assert.NotNull(error);
        Assert.Contains("Application ID", error);
    }

    [Fact]
    public void Validate_ClientSecret_WhitespaceTenantId_ReturnsError()
    {
        var model = new AuthMethodFormModel(isWindows: true);
        var values = ValidSpnValues() with { TenantId = "  \t " };

        var error = model.Validate(AuthMethod.ClientSecret, values);

        Assert.NotNull(error);
        Assert.Contains("Tenant ID", error);
    }

    [Fact]
    public void Validate_Spn_WhitespaceEnvironmentUrl_ReturnsError()
    {
        var model = new AuthMethodFormModel(isWindows: true);
        var values = ValidSpnValues() with { EnvironmentUrl = "  " };

        var error = model.Validate(AuthMethod.ClientSecret, values);

        Assert.NotNull(error);
        Assert.Contains("Environment URL", error);
    }

    [Fact]
    public void Validate_ClientSecret_AllFieldsPresent_ReturnsNull()
    {
        var model = new AuthMethodFormModel(isWindows: true);

        var error = model.Validate(AuthMethod.ClientSecret, ValidSpnValues());

        Assert.Null(error);
    }

    [Fact]
    public void Validate_UsernamePassword_AllFieldsPresent_ReturnsNull()
    {
        var model = new AuthMethodFormModel(isWindows: true);
        var values = new AuthMethodFieldValues(
            ProfileName: "test", EnvironmentUrl: null,
            AppId: null, TenantId: null, ClientSecret: null,
            CertPath: null, CertPassword: null, Thumbprint: null,
            Username: "user@org.com", Password: "pass123");

        var error = model.Validate(AuthMethod.UsernamePassword, values);

        Assert.Null(error);
    }

    [Fact]
    public void BuildRequest_TrimsWhitespace()
    {
        var model = new AuthMethodFormModel(isWindows: true);
        var values = new AuthMethodFieldValues(
            ProfileName: "  padded-name  ", EnvironmentUrl: "  https://org.crm.dynamics.com  ",
            AppId: "  app-id  ", TenantId: "  tenant  ", ClientSecret: "secret",
            CertPath: null, CertPassword: null, Thumbprint: null,
            Username: null, Password: null);

        var request = model.BuildRequest(AuthMethod.ClientSecret, values);

        Assert.Equal("padded-name", request.Name);
        Assert.Equal("https://org.crm.dynamics.com", request.Environment);
        Assert.Equal("app-id", request.ApplicationId);
        Assert.Equal("tenant", request.TenantId);
    }

    [Fact]
    public void GetAvailableMethods_Windows_Returns6Methods()
    {
        var model = new AuthMethodFormModel(isWindows: true);

        var methods = model.GetAvailableMethods();

        Assert.Equal(6, methods.Count);
    }

    [Fact]
    public void GetAvailableMethods_NonWindows_Returns5Methods()
    {
        var model = new AuthMethodFormModel(isWindows: false);

        var methods = model.GetAvailableMethods();

        Assert.Equal(5, methods.Count);
    }

    private static AuthMethodFieldValues ValidSpnValues() => new(
        ProfileName: "test", EnvironmentUrl: "https://org.crm.dynamics.com",
        AppId: "app-id", TenantId: "tenant-id", ClientSecret: "secret",
        CertPath: null, CertPassword: null, Thumbprint: null,
        Username: null, Password: null);
}
