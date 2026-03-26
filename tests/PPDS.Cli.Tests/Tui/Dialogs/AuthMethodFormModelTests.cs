using PPDS.Auth.Profiles;
using PPDS.Cli.Tui.Dialogs;
using Xunit;

namespace PPDS.Cli.Tests.Tui.Dialogs;

[Trait("Category", "TuiUnit")]
public class AuthMethodFormModelTests
{
    // ---- AC-01: No Terminal.Gui dependency ----

    [Fact]
    public void NoTerminalGuiDependency()
    {
        // The source file must not contain "using Terminal.Gui"
        var sourceFile = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "PPDS.Cli", "Tui", "Dialogs", "AuthMethodFormModel.cs");

        // Fall back to reflection if source file is not accessible
        if (File.Exists(sourceFile))
        {
            var content = File.ReadAllText(sourceFile);
            Assert.DoesNotContain("using Terminal.Gui", content);
        }
        else
        {
            // Verify via reflection that the type's assembly doesn't reference Terminal.Gui
            var assembly = typeof(AuthMethodFormModel).Assembly;
            var references = assembly.GetReferencedAssemblies();
            Assert.DoesNotContain(references, r => r.Name == "Terminal.Gui");
        }
    }

    // ---- AC-05: Platform-aware method list ----

    [Fact]
    public void GetAvailableMethods_Windows_IncludesCertificateStore()
    {
        var model = new AuthMethodFormModel(isWindows: true);
        var methods = model.GetAvailableMethods();

        Assert.Contains(methods, m => m.Method == AuthMethod.CertificateStore);
    }

    [Fact]
    public void GetAvailableMethods_NonWindows_ExcludesCertificateStore()
    {
        var model = new AuthMethodFormModel(isWindows: false);
        var methods = model.GetAvailableMethods();

        Assert.DoesNotContain(methods, m => m.Method == AuthMethod.CertificateStore);
    }

    // ---- AC-02: Validate returns errors for missing required fields ----

    private static AuthMethodFieldValues EmptyValues() => new(
        ProfileName: null, EnvironmentUrl: null,
        AppId: null, TenantId: null, ClientSecret: null,
        CertPath: null, CertPassword: null, Thumbprint: null,
        Username: null, Password: null);

    private static AuthMethodFieldValues SpnBaseValues() => new(
        ProfileName: "test", EnvironmentUrl: "https://org.crm.dynamics.com",
        AppId: "app-id", TenantId: "tenant-id", ClientSecret: "secret",
        CertPath: "/path/to/cert.pfx", CertPassword: "pwd", Thumbprint: "AABB",
        Username: null, Password: null);

    [Fact]
    public void Validate_ClientSecret_MissingAppId_ReturnsError()
    {
        var model = new AuthMethodFormModel(isWindows: true);
        var values = SpnBaseValues() with { AppId = null };

        var error = model.Validate(AuthMethod.ClientSecret, values);

        Assert.NotNull(error);
        Assert.Contains("Application ID", error);
    }

    [Fact]
    public void Validate_ClientSecret_MissingClientSecret_ReturnsError()
    {
        var model = new AuthMethodFormModel(isWindows: true);
        var values = SpnBaseValues() with { ClientSecret = null };

        var error = model.Validate(AuthMethod.ClientSecret, values);

        Assert.NotNull(error);
        Assert.Contains("Client Secret", error);
    }

    [Fact]
    public void Validate_CertificateFile_MissingCertPath_ReturnsError()
    {
        var model = new AuthMethodFormModel(isWindows: true);
        var values = SpnBaseValues() with { CertPath = null };

        var error = model.Validate(AuthMethod.CertificateFile, values);

        Assert.NotNull(error);
        Assert.Contains("Certificate path", error);
    }

    [Fact]
    public void Validate_CertificateStore_MissingThumbprint_ReturnsError()
    {
        var model = new AuthMethodFormModel(isWindows: true);
        var values = SpnBaseValues() with { Thumbprint = null };

        var error = model.Validate(AuthMethod.CertificateStore, values);

        Assert.NotNull(error);
        Assert.Contains("thumbprint", error);
    }

    [Fact]
    public void Validate_UsernamePassword_MissingUsername_ReturnsError()
    {
        var model = new AuthMethodFormModel(isWindows: true);
        var values = EmptyValues() with { Password = "pass" };

        var error = model.Validate(AuthMethod.UsernamePassword, values);

        Assert.NotNull(error);
        Assert.Contains("Username", error);
    }

    [Fact]
    public void Validate_UsernamePassword_MissingPassword_ReturnsError()
    {
        var model = new AuthMethodFormModel(isWindows: true);
        var values = EmptyValues() with { Username = "user@test.com" };

        var error = model.Validate(AuthMethod.UsernamePassword, values);

        Assert.NotNull(error);
        Assert.Contains("Password", error);
    }

    [Fact]
    public void Validate_DeviceCode_NoFieldsRequired_ReturnsNull()
    {
        var model = new AuthMethodFormModel(isWindows: true);

        var error = model.Validate(AuthMethod.DeviceCode, EmptyValues());

        Assert.Null(error);
    }

    [Fact]
    public void Validate_InteractiveBrowser_NoFieldsRequired_ReturnsNull()
    {
        var model = new AuthMethodFormModel(isWindows: true);

        var error = model.Validate(AuthMethod.InteractiveBrowser, EmptyValues());

        Assert.Null(error);
    }

    // ---- AC-03: BuildRequest produces correct ProfileCreateRequest ----

    [Fact]
    public void BuildRequest_ClientSecret_CorrectRequest()
    {
        var model = new AuthMethodFormModel(isWindows: true);
        var values = new AuthMethodFieldValues(
            ProfileName: "my-profile", EnvironmentUrl: "https://org.crm.dynamics.com",
            AppId: "app-id", TenantId: "tenant-id", ClientSecret: "my-secret",
            CertPath: null, CertPassword: null, Thumbprint: null,
            Username: null, Password: null);

        var request = model.BuildRequest(AuthMethod.ClientSecret, values);

        Assert.Equal("my-profile", request.Name);
        Assert.Equal("https://org.crm.dynamics.com", request.Environment);
        Assert.Equal(AuthMethod.ClientSecret, request.AuthMethod);
        Assert.False(request.UseDeviceCode);
        Assert.Equal("app-id", request.ApplicationId);
        Assert.Equal("tenant-id", request.TenantId);
        Assert.Equal("my-secret", request.ClientSecret);
        Assert.Null(request.CertificatePath);
        Assert.Null(request.CertificatePassword);
        Assert.Null(request.CertificateThumbprint);
        Assert.Null(request.Username);
        Assert.Null(request.Password);
    }

    [Fact]
    public void BuildRequest_CertificateFile_CorrectRequest()
    {
        var model = new AuthMethodFormModel(isWindows: true);
        var values = new AuthMethodFieldValues(
            ProfileName: "cert-profile", EnvironmentUrl: "https://org.crm.dynamics.com",
            AppId: "app-id", TenantId: "tenant-id", ClientSecret: null,
            CertPath: "/path/to/cert.pfx", CertPassword: "cert-pwd", Thumbprint: null,
            Username: null, Password: null);

        var request = model.BuildRequest(AuthMethod.CertificateFile, values);

        Assert.Equal(AuthMethod.CertificateFile, request.AuthMethod);
        Assert.Equal("/path/to/cert.pfx", request.CertificatePath);
        Assert.Equal("cert-pwd", request.CertificatePassword);
        Assert.Null(request.ClientSecret);
        Assert.Null(request.CertificateThumbprint);
    }

    [Fact]
    public void BuildRequest_DeviceCode_CorrectRequest()
    {
        var model = new AuthMethodFormModel(isWindows: true);
        var values = new AuthMethodFieldValues(
            ProfileName: "dc-profile", EnvironmentUrl: null,
            AppId: null, TenantId: null, ClientSecret: null,
            CertPath: null, CertPassword: null, Thumbprint: null,
            Username: null, Password: null);

        var request = model.BuildRequest(AuthMethod.DeviceCode, values);

        Assert.Equal(AuthMethod.DeviceCode, request.AuthMethod);
        Assert.True(request.UseDeviceCode);
        Assert.Equal("dc-profile", request.Name);
        Assert.Null(request.Environment);
    }

    // ---- AC-04: GetVisibleFields returns correct visibility ----

    [Fact]
    public void GetVisibleFields_ClientSecret_ShowsSpnFrame()
    {
        var model = new AuthMethodFormModel(isWindows: true);

        var visibility = model.GetVisibleFields(AuthMethod.ClientSecret);

        Assert.True(visibility.ShowSpnFrame);
        Assert.False(visibility.ShowCredFrame);
        Assert.True(visibility.ShowClientSecret);
        Assert.False(visibility.ShowCertPath);
        Assert.False(visibility.ShowCertPassword);
        Assert.False(visibility.ShowThumbprint);
    }

    [Fact]
    public void GetVisibleFields_UsernamePassword_ShowsCredFrame()
    {
        var model = new AuthMethodFormModel(isWindows: true);

        var visibility = model.GetVisibleFields(AuthMethod.UsernamePassword);

        Assert.False(visibility.ShowSpnFrame);
        Assert.True(visibility.ShowCredFrame);
        Assert.False(visibility.ShowClientSecret);
        Assert.False(visibility.ShowCertPath);
        Assert.False(visibility.ShowCertPassword);
        Assert.False(visibility.ShowThumbprint);
    }

    [Fact]
    public void GetVisibleFields_DeviceCode_HidesAllFrames()
    {
        var model = new AuthMethodFormModel(isWindows: true);

        var visibility = model.GetVisibleFields(AuthMethod.DeviceCode);

        Assert.False(visibility.ShowSpnFrame);
        Assert.False(visibility.ShowCredFrame);
        Assert.False(visibility.ShowClientSecret);
        Assert.False(visibility.ShowCertPath);
        Assert.False(visibility.ShowCertPassword);
        Assert.False(visibility.ShowThumbprint);
    }

    // ---- GetStatusText ----

    [Theory]
    [InlineData(AuthMethod.DeviceCode, "A code will be shown to enter at microsoft.com/devicelogin")]
    [InlineData(AuthMethod.InteractiveBrowser, "Your default browser will open for sign-in")]
    [InlineData(AuthMethod.ClientSecret, "Requires App ID, Tenant ID, Client Secret, and Environment URL")]
    [InlineData(AuthMethod.CertificateFile, "Requires App ID, Tenant ID, Certificate, and Environment URL")]
    [InlineData(AuthMethod.CertificateStore, "Requires App ID, Tenant ID, Thumbprint, and Environment URL")]
    [InlineData(AuthMethod.UsernamePassword, "Username and password \u2014 may require disabling MFA")]
    public void GetStatusText_ReturnsExpectedText(AuthMethod method, string expected)
    {
        var model = new AuthMethodFormModel(isWindows: true);

        var text = model.GetStatusText(method);

        Assert.Equal(expected, text);
    }
}
