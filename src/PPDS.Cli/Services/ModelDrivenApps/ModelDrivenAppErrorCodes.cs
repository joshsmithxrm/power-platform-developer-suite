namespace PPDS.Cli.Services.ModelDrivenApps;

/// <summary>
/// Error codes for model-driven app operations.
/// </summary>
public static class ModelDrivenAppErrorCodes
{
    /// <summary>The specified model-driven app was not found.</summary>
    public const string AppNotFound = "ModelDrivenApp.AppNotFound";

    /// <summary>The specified entity (table) was not found in the environment.</summary>
    public const string EntityNotFound = "ModelDrivenApp.EntityNotFound";

    /// <summary>The entity is not present in the app's sitemap navigation.</summary>
    public const string EntityNotInApp = "ModelDrivenApp.EntityNotInApp";

    /// <summary>The entity is already present in the app's sitemap navigation.</summary>
    public const string EntityAlreadyInApp = "ModelDrivenApp.EntityAlreadyInApp";

    /// <summary>A form, view, or chart component was not found for the specified entity.</summary>
    public const string ComponentNotFound = "ModelDrivenApp.ComponentNotFound";

    /// <summary>The specified solution was not found.</summary>
    public const string SolutionNotFound = "ModelDrivenApp.SolutionNotFound";

    /// <summary>The sitemap XML failed XSD validation.</summary>
    public const string InvalidSitemapXml = "ModelDrivenApp.InvalidSitemapXml";

    /// <summary>Mutually exclusive arguments were both provided (e.g., --all and --form).</summary>
    public const string InvalidArguments = "ModelDrivenApp.InvalidArguments";

    /// <summary>Failed to list model-driven apps from Dataverse.</summary>
    public const string ListFailed = "ModelDrivenApp.ListFailed";

    /// <summary>Failed to retrieve model-driven app details from Dataverse.</summary>
    public const string GetFailed = "ModelDrivenApp.GetFailed";

    /// <summary>Failed to update the sitemap record in Dataverse.</summary>
    public const string UpdateFailed = "ModelDrivenApp.UpdateFailed";
}
