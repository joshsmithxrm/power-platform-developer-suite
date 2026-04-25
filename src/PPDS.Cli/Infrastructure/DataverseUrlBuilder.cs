using PPDS.Auth.Cloud;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Centralized URL construction for Dynamics 365 and Power Platform Maker portal URLs.
/// All URL patterns should be built through this class — no inline URL construction in production code.
/// </summary>
public static class DataverseUrlBuilder
{
    /// <summary>
    /// Builds the Dynamics 365 record URL for a given entity and record ID.
    /// Returns null if any parameter is null or empty.
    /// </summary>
    public static string? BuildRecordUrl(string? environmentUrl, string? entityLogicalName, string? recordId)
    {
        if (string.IsNullOrEmpty(environmentUrl) ||
            string.IsNullOrEmpty(entityLogicalName) ||
            string.IsNullOrEmpty(recordId))
        {
            return null;
        }

        var baseUrl = environmentUrl.TrimEnd('/');
        return $"{baseUrl}/main.aspx?etn={entityLogicalName}&id={recordId}&pagetype=entityrecord";
    }

    /// <summary>
    /// Builds the Dynamics 365 entity list URL for a given entity logical name.
    /// </summary>
    public static string BuildEntityListUrl(string environmentUrl, string entityLogicalName)
    {
        var baseUrl = environmentUrl.TrimEnd('/');
        return $"{baseUrl}/main.aspx?pagetype=entitylist&etn={entityLogicalName}";
    }

    /// <summary>
    /// Builds a Power Apps Maker portal URL using a Power Platform environment ID directly.
    /// Used when the environment ID is available but the environment URL may not contain org name.
    /// </summary>
    public static string BuildMakerPortalUrl(string environmentId, string? path = "/solutions", CloudEnvironment cloud = CloudEnvironment.Public)
    {
        var makerBase = CloudEndpoints.GetMakerPortalUrl(cloud);
        var trimmedPath = (path ?? "/solutions").TrimStart('/');
        return $"{makerBase}/environments/{environmentId}/{trimmedPath}";
    }

    /// <summary>
    /// Builds the Power Apps Maker portal URL for a specific solution.
    /// </summary>
    public static string BuildSolutionMakerUrl(string environmentUrl, Guid solutionId, CloudEnvironment cloud = CloudEnvironment.Public)
    {
        var makerBase = CloudEndpoints.GetMakerPortalUrl(cloud);
        var uri = new Uri(environmentUrl);
        var orgName = uri.Host.Split('.')[0];
        return $"{makerBase}/environments/Default-{orgName}/solutions/{solutionId}";
    }

    /// <summary>
    /// Builds the Power Apps Maker portal URL for an environment variable definition.
    /// </summary>
    public static string BuildEnvironmentVariableMakerUrl(string environmentUrl, Guid definitionId, CloudEnvironment cloud = CloudEnvironment.Public)
    {
        var makerBase = CloudEndpoints.GetMakerPortalUrl(cloud);
        var uri = new Uri(environmentUrl);
        var orgName = uri.Host.Split('.')[0];
        return $"{makerBase}/environments/Default-{orgName}/solutions/environmentvariables/{definitionId}";
    }

    /// <summary>
    /// Builds the Power Apps Maker portal URL for an import job.
    /// </summary>
    public static string BuildImportJobMakerUrl(string environmentUrl, Guid importJobId, CloudEnvironment cloud = CloudEnvironment.Public)
    {
        var makerBase = CloudEndpoints.GetMakerPortalUrl(cloud);
        var uri = new Uri(environmentUrl);
        var orgName = uri.Host.Split('.')[0];
        return $"{makerBase}/environments/Default-{orgName}/solutions/importjob/{importJobId}";
    }

    /// <summary>
    /// Builds the Dynamics 365 web resource editor URL.
    /// </summary>
    public static string BuildWebResourceEditorUrl(string environmentUrl, Guid webResourceId)
    {
        var uri = new Uri(environmentUrl);
        return $"{uri.Scheme}://{uri.Host}/main.aspx?appid=&pagetype=webresourceedit&id={{{webResourceId}}}";
    }

    /// <summary>
    /// Builds the Power Automate flow details URL.
    /// Note: This takes a Power Platform environment ID (GUID string), not an environment URL.
    /// </summary>
    public static string BuildFlowUrl(string environmentId, Guid flowId, CloudEnvironment cloud = CloudEnvironment.Public)
    {
        var flowBase = CloudEndpoints.GetFlowPortalUrl(cloud);
        return $"{flowBase}/environments/{environmentId}/flows/{flowId}/details";
    }
}
