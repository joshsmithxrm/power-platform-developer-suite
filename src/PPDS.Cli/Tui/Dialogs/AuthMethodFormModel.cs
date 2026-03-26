using System.Runtime.InteropServices;
using PPDS.Auth.Profiles;
using PPDS.Cli.Services.Profile;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Field values collected from the auth method form. Terminal.Gui-free.
/// </summary>
public record AuthMethodFieldValues(
    string? ProfileName, string? EnvironmentUrl,
    string? AppId, string? TenantId, string? ClientSecret,
    string? CertPath, string? CertPassword, string? Thumbprint,
    string? Username, string? Password);

/// <summary>
/// Describes which field groups and individual fields should be visible for a given auth method.
/// </summary>
public record FieldVisibility(
    bool ShowSpnFrame, bool ShowCredFrame,
    bool ShowClientSecret, bool ShowCertPath, bool ShowCertPassword, bool ShowThumbprint);

/// <summary>
/// Pure model for the auth method creation form. Contains all validation, visibility,
/// status text, and request-building logic extracted from ProfileCreationDialog.
/// </summary>
/// <remarks>
/// This class has ZERO Terminal.Gui dependencies — it can be unit tested without a GUI runtime.
/// </remarks>
public class AuthMethodFormModel
{
    private readonly bool _isWindows;

    /// <summary>
    /// Creates a new form model.
    /// </summary>
    /// <param name="isWindows">Override for testability. Defaults to runtime detection.</param>
    public AuthMethodFormModel(bool? isWindows = null)
    {
        _isWindows = isWindows ?? RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }

    /// <summary>
    /// Returns the platform-aware list of available auth methods in display order.
    /// CertificateStore is only included on Windows.
    /// </summary>
    public IReadOnlyList<(string Label, AuthMethod Method)> GetAvailableMethods()
    {
        var methods = new List<(string Label, AuthMethod Method)>
        {
            ("Device Code (Interactive)", AuthMethod.DeviceCode),
            ("Browser (Interactive)", AuthMethod.InteractiveBrowser),
            ("Client Secret (Service Principal)", AuthMethod.ClientSecret),
            ("Certificate File (Service Principal)", AuthMethod.CertificateFile),
        };

        if (_isWindows)
        {
            methods.Add(("Certificate Store (Service Principal)", AuthMethod.CertificateStore));
        }

        methods.Add(("Username & Password", AuthMethod.UsernamePassword));

        return methods;
    }

    /// <summary>
    /// Returns which field groups and individual fields should be visible for the given auth method.
    /// </summary>
    public FieldVisibility GetVisibleFields(AuthMethod method)
    {
        var isSpn = method is AuthMethod.ClientSecret or AuthMethod.CertificateFile or AuthMethod.CertificateStore;

        return new FieldVisibility(
            ShowSpnFrame: isSpn,
            ShowCredFrame: method == AuthMethod.UsernamePassword,
            ShowClientSecret: method == AuthMethod.ClientSecret,
            ShowCertPath: method == AuthMethod.CertificateFile,
            ShowCertPassword: method == AuthMethod.CertificateFile,
            ShowThumbprint: method == AuthMethod.CertificateStore);
    }

    /// <summary>
    /// Returns status/help text for the given auth method.
    /// </summary>
    public string GetStatusText(AuthMethod method) => method switch
    {
        AuthMethod.DeviceCode => "A code will be shown to enter at microsoft.com/devicelogin",
        AuthMethod.InteractiveBrowser => "Your default browser will open for sign-in",
        AuthMethod.ClientSecret => "Requires App ID, Tenant ID, Client Secret, and Environment URL",
        AuthMethod.CertificateFile => "Requires App ID, Tenant ID, Certificate, and Environment URL",
        AuthMethod.CertificateStore => "Requires App ID, Tenant ID, Thumbprint, and Environment URL",
        AuthMethod.UsernamePassword => "Username and password \u2014 may require disabling MFA",
        _ => "Select an authentication method"
    };

    /// <summary>
    /// Validates field values for the given auth method.
    /// Returns an error message string, or null if valid.
    /// </summary>
    public string? Validate(AuthMethod method, AuthMethodFieldValues values)
    {
        var isSpn = method is AuthMethod.ClientSecret or AuthMethod.CertificateFile or AuthMethod.CertificateStore;

        if (isSpn)
        {
            if (string.IsNullOrWhiteSpace(values.AppId))
                return "Application ID is required";
            if (string.IsNullOrWhiteSpace(values.TenantId))
                return "Tenant ID is required";
            if (string.IsNullOrWhiteSpace(values.EnvironmentUrl))
                return "Environment URL is required for service principals";
            if (method == AuthMethod.ClientSecret && string.IsNullOrWhiteSpace(values.ClientSecret))
                return "Client Secret is required";
            if (method == AuthMethod.CertificateFile && string.IsNullOrWhiteSpace(values.CertPath))
                return "Certificate path is required";
            if (method == AuthMethod.CertificateStore && string.IsNullOrWhiteSpace(values.Thumbprint))
                return "Certificate thumbprint is required";
        }
        else if (method == AuthMethod.UsernamePassword)
        {
            if (string.IsNullOrWhiteSpace(values.Username))
                return "Username is required";
            if (string.IsNullOrWhiteSpace(values.Password))
                return "Password is required";
        }

        return null;
    }

    /// <summary>
    /// Builds a <see cref="ProfileCreateRequest"/> from the field values and selected auth method.
    /// </summary>
    public ProfileCreateRequest BuildRequest(AuthMethod method, AuthMethodFieldValues values)
    {
        return new ProfileCreateRequest
        {
            Name = string.IsNullOrWhiteSpace(values.ProfileName) ? null : values.ProfileName.Trim(),
            Environment = string.IsNullOrWhiteSpace(values.EnvironmentUrl) ? null : values.EnvironmentUrl.Trim(),
            AuthMethod = method,
            UseDeviceCode = method == AuthMethod.DeviceCode,
            // SPN fields
            ApplicationId = values.AppId?.Trim(),
            TenantId = values.TenantId?.Trim(),
            ClientSecret = method == AuthMethod.ClientSecret ? values.ClientSecret : null,
            CertificatePath = method == AuthMethod.CertificateFile ? values.CertPath?.Trim() : null,
            CertificatePassword = method == AuthMethod.CertificateFile ? values.CertPassword : null,
            CertificateThumbprint = method == AuthMethod.CertificateStore ? values.Thumbprint?.Trim() : null,
            // Username/Password fields
            Username = method == AuthMethod.UsernamePassword ? values.Username?.Trim() : null,
            Password = method == AuthMethod.UsernamePassword ? values.Password : null,
        };
    }
}
