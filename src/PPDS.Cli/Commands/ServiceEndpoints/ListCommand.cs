using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.ServiceEndpoints;

/// <summary>
/// List registered service endpoints in a Dataverse environment.
/// </summary>
public static class ListCommand
{
    public static Command Create()
    {
        var command = new Command("list", "List registered service endpoints in the environment")
        {
            ServiceEndpointsCommandGroup.ProfileOption,
            ServiceEndpointsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var profile = parseResult.GetValue(ServiceEndpointsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ServiceEndpointsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
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

            var endpoints = await endpointService.ListAsync(cancellationToken);

            if (endpoints.Count == 0)
            {
                if (globalOptions.IsJsonMode)
                {
                    writer.WriteSuccess(new ListOutput { Endpoints = [] });
                }
                else
                {
                    Console.Error.WriteLine("No service endpoints found.");
                }
                return ExitCodes.Success;
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new ListOutput
                {
                    Endpoints = endpoints.Select(e => new EndpointOutput
                    {
                        Id = e.Id,
                        Name = e.Name,
                        Description = e.Description,
                        ContractType = e.ContractType,
                        IsWebhook = e.IsWebhook,
                        Url = e.Url,
                        NamespaceAddress = e.NamespaceAddress,
                        Path = e.Path,
                        AuthType = e.AuthType,
                        MessageFormat = e.MessageFormat,
                        UserClaim = e.UserClaim,
                        IsManaged = e.IsManaged,
                        CreatedOn = e.CreatedOn,
                        ModifiedOn = e.ModifiedOn
                    }).ToList()
                };
                writer.WriteSuccess(output);
            }
            else
            {
                foreach (var endpoint in endpoints)
                {
                    var managed = endpoint.IsManaged ? " [managed]" : "";
                    if (endpoint.IsWebhook)
                    {
                        Console.Error.WriteLine($"Webhook: {endpoint.Name}{managed}");
                        if (!string.IsNullOrEmpty(endpoint.Url))
                            Console.Error.WriteLine($"  URL: {endpoint.Url}");
                    }
                    else
                    {
                        Console.Error.WriteLine($"Service Bus ({endpoint.ContractType}): {endpoint.Name}{managed}");
                        if (!string.IsNullOrEmpty(endpoint.NamespaceAddress))
                            Console.Error.WriteLine($"  Namespace: {endpoint.NamespaceAddress}");
                        if (!string.IsNullOrEmpty(endpoint.Path))
                            Console.Error.WriteLine($"  Path: {endpoint.Path}");
                    }
                    Console.Error.WriteLine($"  Auth: {endpoint.AuthType}");
                }

                var webhookCount = endpoints.Count(e => e.IsWebhook);
                var serviceBusCount = endpoints.Count - webhookCount;
                var parts = new List<string>();
                if (webhookCount > 0) parts.Add($"{webhookCount} webhook(s)");
                if (serviceBusCount > 0) parts.Add($"{serviceBusCount} service bus endpoint(s)");
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Total: {string.Join(", ", parts)}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "listing service endpoints", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class ListOutput
    {
        [JsonPropertyName("endpoints")]
        public List<EndpointOutput> Endpoints { get; set; } = [];
    }

    private sealed class EndpointOutput
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
