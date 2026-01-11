namespace PPDS.Auth.Credentials;

/// <summary>
/// Result of the pre-authentication dialog shown before browser auth.
/// </summary>
public enum PreAuthDialogResult
{
    /// <summary>
    /// Proceed with browser authentication.
    /// </summary>
    OpenBrowser,

    /// <summary>
    /// Use device code authentication for this session instead.
    /// </summary>
    UseDeviceCode,

    /// <summary>
    /// Cancel authentication - return to TUI in limited mode.
    /// </summary>
    Cancel
}
