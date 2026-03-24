using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.ServiceEndpoints;

/// <summary>
/// Get details for a specific service endpoint.
/// </summary>
public static class GetCommand
{
    public static Command Create()
    {
        var nameOrIdArgument = new Argument<string>("name-or-id")
        {
            Description = "Service endpoint name or GUID"
        };

        var command = new Command("get", "Get details for a specific service endpoint")
        {
            nameOrIdArgument,
            ServiceEndpointsCommandGroup.ProfileOption,
            ServiceEndpointsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var nameOrId = parseResult.GetValue(nameOrIdArgument)!;
            var profile = parseResult.GetValue(ServiceEndpointsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ServiceEndpointsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(nameOrId, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string nameOrId,
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

            var endpointService = serviceProvider.GetRequiredService<IServiceEndpointService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            ServiceEndpointInfo? endpoint;
            if (Guid.TryParse(nameOrId, out var id))
            {
                endpoint = await endpointService.GetByIdAsync(id, cancellationToken);
            }
            else
            {
                endpoint = await endpointService.GetByNameAsync(nameOrId, cancellationToken);
            }

            if (endpoint == null)
            {
                var error = new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Service endpoint '{nameOrId}' not found.",
                    null,
                    nameOrId);
                writer.WriteError(error);
                return ExitCodes.NotFoundError;
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new EndpointDetailOutput
                {
                    Id = endpoint.Id,
                    Name = endpoint.Name,
                    Description = endpoint.Description,
                    ContractType = endpoint.ContractType,
                    IsWebhook = endpoint.IsWebhook,
                    Url = endpoint.Url,
                    NamespaceAddress = endpoint.NamespaceAddress,
                    Path = endpoint.Path,
                    AuthType = endpoint.AuthType,
                    MessageFormat = endpoint.MessageFormat,
                    UserClaim = endpoint.UserClaim,
                    IsManaged = endpoint.IsManaged,
                    CreatedOn = endpoint.CreatedOn,
                    ModifiedOn = endpoint.ModifiedOn
                };
                writer.WriteSuccess(output);
            }
            else
            {
                var properties = new Dictionary<string, string?>
                {
                    ["Name"] = endpoint.Name,
                    ["ID"] = endpoint.Id.ToString(),
                    ["Type"] = endpoint.IsWebhook ? "Webhook" : $"Service Bus ({endpoint.ContractType})",
                    ["Auth Type"] = endpoint.AuthType,
                    ["Is Managed"] = endpoint.IsManaged ? "Yes" : "No"
                };

                if (!string.IsNullOrEmpty(endpoint.Description))
                    properties["Description"] = endpoint.Description;

                if (endpoint.IsWebhook && !string.IsNullOrEmpty(endpoint.Url))
                    properties["URL"] = endpoint.Url;

                if (!endpoint.IsWebhook)
                {
                    if (!string.IsNullOrEmpty(endpoint.NamespaceAddress))
                        properties["Namespace"] = endpoint.NamespaceAddress;
                    if (!string.IsNullOrEmpty(endpoint.Path))
                        properties["Path"] = endpoint.Path;
                    if (!string.IsNullOrEmpty(endpoint.MessageFormat))
                        properties["Message Format"] = endpoint.MessageFormat;
                    if (!string.IsNullOrEmpty(endpoint.UserClaim))
                        properties["User Claim"] = endpoint.UserClaim;
                }

                properties["Created"] = endpoint.CreatedOn?.ToString("g") ?? "-";
                properties["Modified"] = endpoint.ModifiedOn?.ToString("g") ?? "-";

                WritePropertyTable(properties);
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"getting service endpoint '{nameOrId}'", debug: globalOptions.Debug);
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

    private sealed class EndpointDetailOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        [JsonPropertyName("contractType")]
        public string ContractType { get; set; } = string.Empty;

        [JsonPropertyName("isWebhook")]
        public bool IsWebhook { get; set; }

        [JsonPropertyName("url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Url { get; set; }

        [JsonPropertyName("namespaceAddress")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NamespaceAddress { get; set; }

        [JsonPropertyName("path")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Path { get; set; }

        [JsonPropertyName("authType")]
        public string AuthType { get; set; } = string.Empty;

        [JsonPropertyName("messageFormat")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? MessageFormat { get; set; }

        [JsonPropertyName("userClaim")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? UserClaim { get; set; }

        [JsonPropertyName("isManaged")]
        public bool IsManaged { get; set; }

        [JsonPropertyName("createdOn")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? CreatedOn { get; set; }

        [JsonPropertyName("modifiedOn")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? ModifiedOn { get; set; }
    }

    #endregion
}
