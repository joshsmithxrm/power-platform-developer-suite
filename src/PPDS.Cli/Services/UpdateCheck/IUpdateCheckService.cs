namespace PPDS.Cli.Services.UpdateCheck;

/// <summary>
/// Service for checking available updates to the PPDS CLI tool.
/// </summary>
public interface IUpdateCheckService
{
    /// <summary>
    /// Asynchronously checks for available updates to the PPDS CLI.
    /// </summary>
    /// <param name="currentVersion">The currently installed version.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the update check result, or null if the check could not be completed.</returns>
    Task<UpdateCheckResult?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously retrieves a previously cached update check result.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the cached result, or null if no cached result is available.</returns>
    Task<UpdateCheckResult?> GetCachedResultAsync(CancellationToken cancellationToken = default);
}
