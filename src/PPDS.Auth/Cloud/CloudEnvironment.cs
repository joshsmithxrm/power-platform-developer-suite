namespace PPDS.Auth.Cloud;

/// <summary>
/// Azure cloud environments for Dataverse authentication.
/// </summary>
public enum CloudEnvironment
{
    /// <summary>
    /// Azure Public Cloud (default).
    /// Authority: https://login.microsoftonline.com
    /// </summary>
    Public,

    /// <summary>
    /// Azure US Government Cloud.
    /// Authority: https://login.microsoftonline.us
    /// </summary>
    UsGov,

    /// <summary>
    /// Azure US Government High Cloud.
    /// Authority: https://login.microsoftonline.us
    /// </summary>
    UsGovHigh,

    /// <summary>
    /// Azure US Government DoD Cloud.
    /// Authority: https://login.microsoftonline.us
    /// </summary>
    UsGovDod,

    /// <summary>
    /// Azure China Cloud (21Vianet).
    /// Authority: https://login.chinacloudapi.cn
    /// </summary>
    China
}
