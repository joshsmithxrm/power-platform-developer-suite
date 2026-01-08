using PPDS.Cli.Plugins.Models;

namespace PPDS.Cli.Plugins.Registration;

/// <summary>
/// Service for managing plugin registrations in Dataverse.
/// </summary>
/// <remarks>
/// <para>
/// This service provides operations for querying, creating, updating, and deleting
/// plugin assemblies, packages, types, steps, and images in Dataverse.
/// </para>
/// <para>
/// See ADR-0002 and ADR-0005 for pool architecture details.
/// See ADR-0015 for application service layer pattern.
/// </para>
/// </remarks>
public interface IPluginRegistrationService
{
    #region Query Operations

    /// <summary>
    /// Lists all plugin assemblies in the environment.
    /// </summary>
    /// <param name="assemblyNameFilter">Optional filter by assembly name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<List<PluginAssemblyInfo>> ListAssembliesAsync(
        string? assemblyNameFilter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all plugin packages in the environment.
    /// </summary>
    /// <param name="packageNameFilter">Optional filter by package name or unique name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<List<PluginPackageInfo>> ListPackagesAsync(
        string? packageNameFilter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all assemblies contained in a plugin package.
    /// </summary>
    /// <param name="packageId">The package ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<List<PluginAssemblyInfo>> ListAssembliesForPackageAsync(
        Guid packageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all plugin types for a package by querying through the package's assemblies.
    /// </summary>
    /// <param name="packageId">The package ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<List<PluginTypeInfo>> ListTypesForPackageAsync(
        Guid packageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all plugin types for an assembly.
    /// </summary>
    /// <param name="assemblyId">The assembly ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<List<PluginTypeInfo>> ListTypesForAssemblyAsync(
        Guid assemblyId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all processing steps for a plugin type.
    /// </summary>
    /// <param name="pluginTypeId">The plugin type ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<List<PluginStepInfo>> ListStepsForTypeAsync(
        Guid pluginTypeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all images for a processing step.
    /// </summary>
    /// <param name="stepId">The step ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<List<PluginImageInfo>> ListImagesForStepAsync(
        Guid stepId,
        CancellationToken cancellationToken = default);

    #endregion

    #region Lookup Operations

    /// <summary>
    /// Gets an assembly by name.
    /// </summary>
    /// <param name="name">The assembly name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PluginAssemblyInfo?> GetAssemblyByNameAsync(
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a plugin package by name or unique name.
    /// </summary>
    /// <param name="name">The package name or unique name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PluginPackageInfo?> GetPackageByNameAsync(
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the SDK message ID for a message name.
    /// </summary>
    /// <param name="messageName">The message name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Guid?> GetSdkMessageIdAsync(
        string messageName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the SDK message filter ID for a message and entity combination.
    /// </summary>
    /// <param name="messageId">The message ID.</param>
    /// <param name="primaryEntity">The primary entity logical name.</param>
    /// <param name="secondaryEntity">Optional secondary entity logical name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Guid?> GetSdkMessageFilterIdAsync(
        Guid messageId,
        string primaryEntity,
        string? secondaryEntity = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the assembly ID for an assembly that is part of a plugin package.
    /// </summary>
    /// <param name="packageId">The package ID.</param>
    /// <param name="assemblyName">The assembly name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Guid?> GetAssemblyIdForPackageAsync(
        Guid packageId,
        string assemblyName,
        CancellationToken cancellationToken = default);

    #endregion

    #region Create/Update Operations

    /// <summary>
    /// Creates or updates a plugin assembly (for classic DLL assemblies only).
    /// For NuGet packages, use <see cref="UpsertPackageAsync"/> instead.
    /// </summary>
    /// <param name="name">The assembly name.</param>
    /// <param name="content">The assembly DLL content.</param>
    /// <param name="solutionName">Optional solution to add the assembly to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Guid> UpsertAssemblyAsync(
        string name,
        byte[] content,
        string? solutionName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates a plugin package (for NuGet packages).
    /// </summary>
    /// <param name="packageName">The package name from .nuspec.</param>
    /// <param name="nupkgContent">The raw .nupkg file content.</param>
    /// <param name="solutionName">Solution to add the package to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The package ID.</returns>
    Task<Guid> UpsertPackageAsync(
        string packageName,
        byte[] nupkgContent,
        string? solutionName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates a plugin type.
    /// </summary>
    /// <param name="assemblyId">The assembly ID.</param>
    /// <param name="typeName">The type name.</param>
    /// <param name="solutionName">Optional solution name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Guid> UpsertPluginTypeAsync(
        Guid assemblyId,
        string typeName,
        string? solutionName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates a processing step.
    /// </summary>
    /// <param name="pluginTypeId">The plugin type ID.</param>
    /// <param name="stepConfig">The step configuration.</param>
    /// <param name="messageId">The SDK message ID.</param>
    /// <param name="filterId">Optional SDK message filter ID.</param>
    /// <param name="solutionName">Optional solution name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Guid> UpsertStepAsync(
        Guid pluginTypeId,
        PluginStepConfig stepConfig,
        Guid messageId,
        Guid? filterId,
        string? solutionName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates a step image.
    /// </summary>
    /// <param name="stepId">The step ID.</param>
    /// <param name="imageConfig">The image configuration.</param>
    /// <param name="messageName">The SDK message name (e.g., "Create", "Update", "SetState").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when the message does not support images.</exception>
    Task<Guid> UpsertImageAsync(
        Guid stepId,
        PluginImageConfig imageConfig,
        string messageName,
        CancellationToken cancellationToken = default);

    #endregion

    #region Delete Operations

    /// <summary>
    /// Deletes a step image.
    /// </summary>
    /// <param name="imageId">The image ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteImageAsync(Guid imageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a processing step (also deletes child images in parallel).
    /// </summary>
    /// <param name="stepId">The step ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteStepAsync(Guid stepId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a plugin type (only if it has no steps).
    /// </summary>
    /// <param name="pluginTypeId">The plugin type ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeletePluginTypeAsync(Guid pluginTypeId, CancellationToken cancellationToken = default);

    #endregion

    #region Solution Operations

    /// <summary>
    /// Adds a component to a solution.
    /// </summary>
    /// <param name="componentId">The component ID.</param>
    /// <param name="componentType">The component type code.</param>
    /// <param name="solutionName">The solution unique name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddToSolutionAsync(
        Guid componentId,
        int componentType,
        string solutionName,
        CancellationToken cancellationToken = default);

    #endregion
}
