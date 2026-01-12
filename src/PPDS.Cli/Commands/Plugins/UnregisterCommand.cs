using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Plugins.Registration;

namespace PPDS.Cli.Commands.Plugins;

/// <summary>
/// Unregister plugin entities from Dataverse.
/// </summary>
public static class UnregisterCommand
{
    /// <summary>
    /// Valid entity types for unregistration.
    /// </summary>
    private static readonly string[] ValidEntityTypes = ["assembly", "package", "type", "step", "image"];

    public static Command Create()
    {
        var entityTypeArg = new Argument<string>("type")
        {
            Description = "The type of entity to unregister: assembly, package, type, step, image"
        };

        var nameOrIdArg = new Argument<string>("name-or-id")
        {
            Description = "Name or GUID of the entity to unregister"
        };

        var forceOption = new Option<bool>("--force", "-f")
        {
            Description = "Force cascade delete of all child entities",
            DefaultValueFactory = _ => false
        };

        var command = new Command("unregister", "Unregister plugin entities from Dataverse")
        {
            entityTypeArg,
            nameOrIdArg,
            forceOption,
            PluginsCommandGroup.ProfileOption,
            PluginsCommandGroup.EnvironmentOption
        };

        // Add global options including output format
        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var entityType = parseResult.GetValue(entityTypeArg)!;
            var nameOrId = parseResult.GetValue(nameOrIdArg)!;
            var force = parseResult.GetValue(forceOption);
            var profile = parseResult.GetValue(PluginsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(entityType, nameOrId, force, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string entityType,
        string nameOrId,
        bool force,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        // Validate entity type
        var normalizedType = entityType.ToLowerInvariant();
        if (!ValidEntityTypes.Contains(normalizedType))
        {
            writer.WriteError(new StructuredError(
                ErrorCodes.Validation.InvalidValue,
                $"Invalid entity type: {entityType}. Valid types are: {string.Join(", ", ValidEntityTypes)}",
                Target: "type"));
            return ExitCodes.InvalidArguments;
        }

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var registrationService = serviceProvider.GetRequiredService<IPluginRegistrationService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var result = await UnregisterEntityAsync(
                registrationService,
                normalizedType,
                nameOrId,
                force,
                cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                WriteHumanOutput(result);
            }

            return ExitCodes.Success;
        }
        catch (UnregisterException ex)
        {
            if (globalOptions.IsJsonMode)
            {
                writer.WriteError(new StructuredError(
                    ex.ErrorCode,
                    ex.Message,
                    Target: ex.EntityName));
            }
            else
            {
                WriteUnregisterError(ex);
            }

            return ex.ErrorCode switch
            {
                ErrorCodes.Plugin.NotFound => ExitCodes.NotFoundError,
                ErrorCodes.Plugin.ManagedComponent => ExitCodes.Forbidden,
                ErrorCodes.Plugin.HasChildren => ExitCodes.PreconditionFailed,
                _ => ExitCodes.Failure
            };
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "unregistering plugin", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static async Task<UnregisterResult> UnregisterEntityAsync(
        IPluginRegistrationService service,
        string entityType,
        string nameOrId,
        bool force,
        CancellationToken cancellationToken)
    {
        return entityType switch
        {
            "image" => await UnregisterImageAsync(service, nameOrId, cancellationToken),
            "step" => await UnregisterStepAsync(service, nameOrId, force, cancellationToken),
            "type" => await UnregisterTypeAsync(service, nameOrId, force, cancellationToken),
            "assembly" => await UnregisterAssemblyAsync(service, nameOrId, force, cancellationToken),
            "package" => await UnregisterPackageAsync(service, nameOrId, force, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown entity type: {entityType}")
        };
    }

    private static async Task<UnregisterResult> UnregisterImageAsync(
        IPluginRegistrationService service,
        string nameOrId,
        CancellationToken cancellationToken)
    {
        var image = await service.GetImageByNameOrIdAsync(nameOrId, cancellationToken)
            ?? throw new UnregisterException(
                $"Image not found: {nameOrId}",
                nameOrId,
                "Image",
                ErrorCodes.Plugin.NotFound);

        return await service.UnregisterImageAsync(image.Id, cancellationToken);
    }

    private static async Task<UnregisterResult> UnregisterStepAsync(
        IPluginRegistrationService service,
        string nameOrId,
        bool force,
        CancellationToken cancellationToken)
    {
        var step = await service.GetStepByNameOrIdAsync(nameOrId, cancellationToken)
            ?? throw new UnregisterException(
                $"Step not found: {nameOrId}",
                nameOrId,
                "Step",
                ErrorCodes.Plugin.NotFound);

        return await service.UnregisterStepAsync(step.Id, force, cancellationToken);
    }

    private static async Task<UnregisterResult> UnregisterTypeAsync(
        IPluginRegistrationService service,
        string nameOrId,
        bool force,
        CancellationToken cancellationToken)
    {
        var pluginType = await service.GetPluginTypeByNameOrIdAsync(nameOrId, cancellationToken)
            ?? throw new UnregisterException(
                $"Plugin type not found: {nameOrId}",
                nameOrId,
                "Type",
                ErrorCodes.Plugin.NotFound);

        return await service.UnregisterPluginTypeAsync(pluginType.Id, force, cancellationToken);
    }

    private static async Task<UnregisterResult> UnregisterAssemblyAsync(
        IPluginRegistrationService service,
        string nameOrId,
        bool force,
        CancellationToken cancellationToken)
    {
        // Try by ID first, then by name
        if (Guid.TryParse(nameOrId, out var id))
        {
            return await service.UnregisterAssemblyAsync(id, force, cancellationToken);
        }

        var assembly = await service.GetAssemblyByNameAsync(nameOrId, cancellationToken)
            ?? throw new UnregisterException(
                $"Assembly not found: {nameOrId}",
                nameOrId,
                "Assembly",
                ErrorCodes.Plugin.NotFound);

        return await service.UnregisterAssemblyAsync(assembly.Id, force, cancellationToken);
    }

    private static async Task<UnregisterResult> UnregisterPackageAsync(
        IPluginRegistrationService service,
        string nameOrId,
        bool force,
        CancellationToken cancellationToken)
    {
        // Try by ID first, then by name
        if (Guid.TryParse(nameOrId, out var id))
        {
            return await service.UnregisterPackageAsync(id, force, cancellationToken);
        }

        var package = await service.GetPackageByNameAsync(nameOrId, cancellationToken)
            ?? throw new UnregisterException(
                $"Package not found: {nameOrId}",
                nameOrId,
                "Package",
                ErrorCodes.Plugin.NotFound);

        return await service.UnregisterPackageAsync(package.Id, force, cancellationToken);
    }

    private static void WriteHumanOutput(UnregisterResult result)
    {
        Console.Error.WriteLine($"Unregistered {result.EntityType.ToLowerInvariant()}: {result.EntityName}");

        // Show cascade summary if applicable
        var cascadeParts = new List<string>();
        if (result.TypesDeleted > 0)
            cascadeParts.Add($"{result.TypesDeleted} plugin type(s)");
        if (result.StepsDeleted > 0)
            cascadeParts.Add($"{result.StepsDeleted} step(s)");
        if (result.ImagesDeleted > 0)
            cascadeParts.Add($"{result.ImagesDeleted} image(s)");

        if (cascadeParts.Count > 0)
        {
            Console.Error.WriteLine($"  Deleted: {string.Join(", ", cascadeParts)}");
        }
    }

    private static void WriteUnregisterError(UnregisterException ex)
    {
        Console.Error.WriteLine($"Cannot unregister {ex.EntityType.ToLowerInvariant()}: {ex.EntityName}");

        switch (ex.ErrorCode)
        {
            case ErrorCodes.Plugin.NotFound:
                Console.Error.WriteLine($"  {ex.EntityType} not found in the environment.");
                break;

            case ErrorCodes.Plugin.ManagedComponent:
                Console.Error.WriteLine("  Managed components cannot be deleted in this environment.");
                break;

            case ErrorCodes.Plugin.HasChildren:
                var childParts = new List<string>();
                if (ex.AssemblyCount > 0)
                    childParts.Add($"{ex.AssemblyCount} assembly(ies)");
                if (ex.TypeCount > 0)
                    childParts.Add($"{ex.TypeCount} plugin type(s)");
                if (ex.StepCount > 0)
                    childParts.Add($"{ex.StepCount} active step(s)");
                if (ex.ImageCount > 0)
                    childParts.Add($"{ex.ImageCount} image(s)");

                Console.Error.WriteLine($"  {ex.EntityType} has {string.Join(" with ", childParts)}.");
                Console.Error.WriteLine("  Use --force to cascade delete all children.");
                break;

            default:
                Console.Error.WriteLine($"  {ex.Message}");
                break;
        }
    }
}
