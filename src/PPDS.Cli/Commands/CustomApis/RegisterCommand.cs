using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.CustomApis;

/// <summary>
/// Register a new Custom API in Dataverse.
/// </summary>
public static class RegisterCommand
{
    public static Command Create()
    {
        var uniqueNameArgument = new Argument<string>("unique-name")
        {
            Description = "Unique message name for the Custom API"
        };

        var pluginOption = new Option<string>("--plugin")
        {
            Description = "Plugin type name (e.g. MyAssembly.MyPlugin)",
            Required = true
        };

        var assemblyOption = new Option<string>("--assembly")
        {
            Description = "Plugin assembly name",
            Required = true
        };

        var displayNameOption = new Option<string?>("--display-name")
        {
            Description = "Display name for the Custom API (defaults to unique name)"
        };

        var descriptionOption = new Option<string?>("--description")
        {
            Description = "Optional description"
        };

        var bindingTypeOption = new Option<string?>("--binding-type")
        {
            Description = "Binding type: Global, Entity, or EntityCollection (default: Global)"
        };

        var boundEntityOption = new Option<string?>("--bound-entity")
        {
            Description = "Bound entity logical name (required when binding-type is Entity or EntityCollection)"
        };

        var isFunctionOption = new Option<bool>("--is-function")
        {
            Description = "Mark this API as a function (returns a value)",
            DefaultValueFactory = _ => false
        };

        var isPrivateOption = new Option<bool>("--is-private")
        {
            Description = "Mark this API as private",
            DefaultValueFactory = _ => false
        };

        var executePrivilegeOption = new Option<string?>("--execute-privilege")
        {
            Description = "Privilege name required to execute the API"
        };

        var allowedStepTypeOption = new Option<string?>("--allowed-step-type")
        {
            Description = "Allowed processing step type: None, AsyncOnly, or SyncAndAsync (default: None)"
        };

        var command = new Command("register", "Register a new Custom API in Dataverse")
        {
            uniqueNameArgument,
            pluginOption,
            assemblyOption,
            displayNameOption,
            descriptionOption,
            bindingTypeOption,
            boundEntityOption,
            isFunctionOption,
            isPrivateOption,
            executePrivilegeOption,
            allowedStepTypeOption,
            CustomApisCommandGroup.ProfileOption,
            CustomApisCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var uniqueName = parseResult.GetValue(uniqueNameArgument)!;
            var plugin = parseResult.GetValue(pluginOption)!;
            var assembly = parseResult.GetValue(assemblyOption)!;
            var displayName = parseResult.GetValue(displayNameOption);
            var description = parseResult.GetValue(descriptionOption);
            var bindingType = parseResult.GetValue(bindingTypeOption);
            var boundEntity = parseResult.GetValue(boundEntityOption);
            var isFunction = parseResult.GetValue(isFunctionOption);
            var isPrivate = parseResult.GetValue(isPrivateOption);
            var executePrivilege = parseResult.GetValue(executePrivilegeOption);
            var allowedStepType = parseResult.GetValue(allowedStepTypeOption);
            var profile = parseResult.GetValue(CustomApisCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(CustomApisCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(
                uniqueName, plugin, assembly, displayName, description,
                bindingType, boundEntity, isFunction, isPrivate, executePrivilege, allowedStepType,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string uniqueName,
        string pluginTypeName,
        string assemblyName,
        string? displayName,
        string? description,
        string? bindingType,
        string? boundEntity,
        bool isFunction,
        bool isPrivate,
        string? executePrivilege,
        string? allowedStepType,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var customApiService = serviceProvider.GetRequiredService<ICustomApiService>();
            var registrationService = serviceProvider.GetRequiredService<IPluginRegistrationService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Registering Custom API: {uniqueName}");
            }

            // Resolve plugin type via assembly
            var assemblyInfo = await registrationService.GetAssemblyByNameAsync(assemblyName, cancellationToken);
            if (assemblyInfo == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Plugin assembly '{assemblyName}' not found.",
                    Target: assemblyName));
                return ExitCodes.NotFoundError;
            }

            var types = await registrationService.ListTypesForAssemblyAsync(assemblyInfo.Id, cancellationToken);
            var pluginType = types.FirstOrDefault(t =>
                string.Equals(t.TypeName, pluginTypeName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.FriendlyName, pluginTypeName, StringComparison.OrdinalIgnoreCase));

            if (pluginType == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Plugin type '{pluginTypeName}' not found in assembly '{assemblyName}'.",
                    Target: pluginTypeName));
                return ExitCodes.NotFoundError;
            }

            var registration = new CustomApiRegistration(
                UniqueName: uniqueName,
                DisplayName: displayName ?? uniqueName,
                Name: null,
                Description: description,
                PluginTypeId: pluginType.Id,
                BindingType: bindingType,
                BoundEntity: boundEntity,
                IsFunction: isFunction,
                IsPrivate: isPrivate,
                ExecutePrivilegeName: executePrivilege,
                AllowedProcessingStepType: allowedStepType);

            var apiId = await customApiService.RegisterAsync(registration, cancellationToken: cancellationToken);

            var result = new RegisterResult
            {
                Success = true,
                Operation = "register-custom-api",
                Id = apiId,
                UniqueName = uniqueName,
                PluginTypeName = pluginType.TypeName
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"  Custom API registered: {apiId}");
                Console.Error.WriteLine($"  Unique Name: {uniqueName}");
                Console.Error.WriteLine($"  Plugin Type: {pluginType.TypeName}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"registering Custom API '{uniqueName}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Result Models

    private sealed class RegisterResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("operation")]
        public string Operation { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("uniqueName")]
        public string UniqueName { get; set; } = string.Empty;

        [JsonPropertyName("pluginTypeName")]
        public string PluginTypeName { get; set; } = string.Empty;
    }

    #endregion
}
