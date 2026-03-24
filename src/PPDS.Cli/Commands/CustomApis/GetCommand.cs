using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.CustomApis;

/// <summary>
/// Get details for a specific Custom API.
/// </summary>
public static class GetCommand
{
    public static Command Create()
    {
        var uniqueNameOrIdArgument = new Argument<string>("unique-name-or-id")
        {
            Description = "Custom API unique name or GUID"
        };

        var command = new Command("get", "Get details for a specific Custom API")
        {
            uniqueNameOrIdArgument,
            CustomApisCommandGroup.ProfileOption,
            CustomApisCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var uniqueNameOrId = parseResult.GetValue(uniqueNameOrIdArgument)!;
            var profile = parseResult.GetValue(CustomApisCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(CustomApisCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(uniqueNameOrId, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string uniqueNameOrId,
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

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var api = await customApiService.GetAsync(uniqueNameOrId, cancellationToken);

            if (api == null)
            {
                var error = new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Custom API '{uniqueNameOrId}' not found.",
                    null,
                    uniqueNameOrId);
                writer.WriteError(error);
                return ExitCodes.NotFoundError;
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new ApiDetailOutput
                {
                    Id = api.Id,
                    UniqueName = api.UniqueName,
                    DisplayName = api.DisplayName,
                    Name = api.Name,
                    Description = api.Description,
                    PluginTypeId = api.PluginTypeId,
                    PluginTypeName = api.PluginTypeName,
                    BindingType = api.BindingType,
                    BoundEntity = api.BoundEntity,
                    AllowedProcessingStepType = api.AllowedProcessingStepType,
                    IsFunction = api.IsFunction,
                    IsPrivate = api.IsPrivate,
                    ExecutePrivilegeName = api.ExecutePrivilegeName,
                    IsManaged = api.IsManaged,
                    CreatedOn = api.CreatedOn,
                    ModifiedOn = api.ModifiedOn,
                    RequestParameters = api.RequestParameters.Select(p => new ParameterOutput
                    {
                        Id = p.Id,
                        UniqueName = p.UniqueName,
                        DisplayName = p.DisplayName,
                        Type = p.Type,
                        LogicalEntityName = p.LogicalEntityName,
                        IsOptional = p.IsOptional,
                        IsManaged = p.IsManaged
                    }).ToList(),
                    ResponseProperties = api.ResponseProperties.Select(p => new ParameterOutput
                    {
                        Id = p.Id,
                        UniqueName = p.UniqueName,
                        DisplayName = p.DisplayName,
                        Type = p.Type,
                        LogicalEntityName = p.LogicalEntityName,
                        IsOptional = p.IsOptional,
                        IsManaged = p.IsManaged
                    }).ToList()
                };
                writer.WriteSuccess(output);
            }
            else
            {
                var properties = new Dictionary<string, string?>
                {
                    ["Unique Name"] = api.UniqueName,
                    ["ID"] = api.Id.ToString(),
                    ["Display Name"] = api.DisplayName,
                    ["Binding Type"] = api.BindingType,
                    ["Is Function"] = api.IsFunction ? "Yes" : "No",
                    ["Is Private"] = api.IsPrivate ? "Yes" : "No",
                    ["Step Type"] = api.AllowedProcessingStepType,
                    ["Is Managed"] = api.IsManaged ? "Yes" : "No"
                };

                if (!string.IsNullOrEmpty(api.Description))
                    properties["Description"] = api.Description;
                if (!string.IsNullOrEmpty(api.BoundEntity))
                    properties["Bound Entity"] = api.BoundEntity;
                if (!string.IsNullOrEmpty(api.PluginTypeName))
                    properties["Plugin Type"] = api.PluginTypeName;
                if (!string.IsNullOrEmpty(api.ExecutePrivilegeName))
                    properties["Execute Privilege"] = api.ExecutePrivilegeName;

                properties["Created"] = api.CreatedOn?.ToString("g") ?? "-";
                properties["Modified"] = api.ModifiedOn?.ToString("g") ?? "-";

                WritePropertyTable(properties);

                if (api.RequestParameters.Count > 0)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("  Request Parameters:");
                    foreach (var p in api.RequestParameters)
                    {
                        var optional = p.IsOptional ? " [optional]" : "";
                        Console.Error.WriteLine($"    {p.UniqueName} ({p.Type}){optional}");
                    }
                }

                if (api.ResponseProperties.Count > 0)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("  Response Properties:");
                    foreach (var p in api.ResponseProperties)
                    {
                        Console.Error.WriteLine($"    {p.UniqueName} ({p.Type})");
                    }
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"getting Custom API '{uniqueNameOrId}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static void WritePropertyTable(Dictionary<string, string?> properties)
    {
        if (properties.Count == 0) return;

        var maxKeyLength = properties.Keys.Max(k => k.Length);

        foreach (var kvp in properties)
        {
            var key = kvp.Key.PadRight(maxKeyLength);
            Console.Error.WriteLine($"  {key}  {kvp.Value}");
        }
    }

    #region Output Models

    private sealed class ApiDetailOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("uniqueName")]
        public string UniqueName { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        [JsonPropertyName("pluginTypeId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? PluginTypeId { get; set; }

        [JsonPropertyName("pluginTypeName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PluginTypeName { get; set; }

        [JsonPropertyName("bindingType")]
        public string BindingType { get; set; } = string.Empty;

        [JsonPropertyName("boundEntity")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BoundEntity { get; set; }

        [JsonPropertyName("allowedProcessingStepType")]
        public string AllowedProcessingStepType { get; set; } = string.Empty;

        [JsonPropertyName("isFunction")]
        public bool IsFunction { get; set; }

        [JsonPropertyName("isPrivate")]
        public bool IsPrivate { get; set; }

        [JsonPropertyName("executePrivilegeName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ExecutePrivilegeName { get; set; }

        [JsonPropertyName("isManaged")]
        public bool IsManaged { get; set; }

        [JsonPropertyName("createdOn")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? CreatedOn { get; set; }

        [JsonPropertyName("modifiedOn")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? ModifiedOn { get; set; }

        [JsonPropertyName("requestParameters")]
        public List<ParameterOutput> RequestParameters { get; set; } = [];

        [JsonPropertyName("responseProperties")]
        public List<ParameterOutput> ResponseProperties { get; set; } = [];
    }

    private sealed class ParameterOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("uniqueName")]
        public string UniqueName { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("logicalEntityName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LogicalEntityName { get; set; }

        [JsonPropertyName("isOptional")]
        public bool IsOptional { get; set; }

        [JsonPropertyName("isManaged")]
        public bool IsManaged { get; set; }
    }

    #endregion
}
