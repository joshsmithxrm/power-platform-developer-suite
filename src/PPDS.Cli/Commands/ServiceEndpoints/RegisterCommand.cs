using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.ServiceEndpoints;

/// <summary>
/// Register service endpoints (webhooks, queues, topics, event hubs) in Dataverse.
/// </summary>
public static class RegisterCommand
{
    /// <summary>
    /// Creates the 'register' command with all subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("register", "Register a service endpoint: webhook, queue, topic, eventhub");

        command.Subcommands.Add(CreateWebhookCommand());
        command.Subcommands.Add(CreateQueueCommand());
        command.Subcommands.Add(CreateTopicCommand());
        command.Subcommands.Add(CreateEventHubCommand());

        return command;
    }

    #region Webhook Subcommand

    private static Command CreateWebhookCommand()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Unique display name for the webhook endpoint"
        };

        var urlOption = new Option<string>("--url")
        {
            Description = "Absolute HTTPS/HTTP URL of the webhook receiver",
            Required = true
        };

        var authTypeOption = new Option<string>("--auth-type")
        {
            Description = "Authentication type: WebhookKey, HttpHeader, or HttpQueryString",
            Required = true
        };

        var authKeyOption = new Option<string?>("--auth-key")
        {
            Description = "Auth key name (for HttpHeader or HttpQueryString auth types)"
        };

        var authValueOption = new Option<string?>("--auth-value")
        {
            Description = "Auth secret value. For WebhookKey: plain string. For HttpHeader/HttpQueryString: name=value pairs."
        };

        var command = new Command("webhook", "Register an HTTP webhook endpoint")
        {
            nameArgument,
            urlOption,
            authTypeOption,
            authKeyOption,
            authValueOption,
            ServiceEndpointsCommandGroup.ProfileOption,
            ServiceEndpointsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArgument)!;
            var url = parseResult.GetValue(urlOption)!;
            var authType = parseResult.GetValue(authTypeOption)!;
            var authKey = parseResult.GetValue(authKeyOption);
            var authValue = parseResult.GetValue(authValueOption);
            var profile = parseResult.GetValue(ServiceEndpointsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ServiceEndpointsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteWebhookAsync(name, url, authType, authKey, authValue, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteWebhookAsync(
        string name,
        string url,
        string authType,
        string? authKey,
        string? authValue,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            // Build auth value - for HttpHeader/HttpQueryString with key+value, format as XML
            var resolvedAuthValue = authValue;
            if (!string.IsNullOrEmpty(authKey) && !string.IsNullOrEmpty(authValue))
            {
                resolvedAuthValue = $"<settings><setting name=\"{authKey}\" value=\"{authValue}\" /></settings>";
            }

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
                Console.Error.WriteLine($"Registering webhook: {name}");
            }

            var registration = new WebhookRegistration(name, url, authType, resolvedAuthValue);
            var endpointId = await endpointService.RegisterWebhookAsync(registration, cancellationToken);

            var result = new RegisterResult
            {
                Success = true,
                Operation = "register-webhook",
                Id = endpointId,
                Name = name,
                ContractType = "Webhook"
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"  Webhook registered: {endpointId}");
                Console.Error.WriteLine($"  URL: {url}");
                Console.Error.WriteLine($"  Auth: {authType}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "registering webhook", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #endregion

    #region Service Bus Subcommands (Queue, Topic, EventHub)

    private static Command CreateQueueCommand() =>
        CreateServiceBusCommand("queue", "Queue", "Register an Azure Service Bus queue endpoint");

    private static Command CreateTopicCommand() =>
        CreateServiceBusCommand("topic", "Topic", "Register an Azure Service Bus topic endpoint");

    private static Command CreateEventHubCommand() =>
        CreateServiceBusCommand("eventhub", "EventHub", "Register an Azure Event Hub endpoint");

    private static Command CreateServiceBusCommand(string commandName, string contractType, string description)
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Unique display name for the endpoint"
        };

        var namespaceOption = new Option<string>("--namespace")
        {
            Description = "Service Bus namespace address (e.g. sb://myns.servicebus.windows.net)",
            Required = true
        };

        var pathOption = new Option<string>("--path")
        {
            Description = $"{contractType} name",
            Required = true
        };

        var sasKeyNameOption = new Option<string>("--sas-key-name")
        {
            Description = "SAS key name",
            Required = true
        };

        var sasKeyOption = new Option<string>("--sas-key")
        {
            Description = "SAS key value (exactly 44 characters)",
            Required = true
        };

        var command = new Command(commandName, description)
        {
            nameArgument,
            namespaceOption,
            pathOption,
            sasKeyNameOption,
            sasKeyOption,
            ServiceEndpointsCommandGroup.ProfileOption,
            ServiceEndpointsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArgument)!;
            var ns = parseResult.GetValue(namespaceOption)!;
            var path = parseResult.GetValue(pathOption)!;
            var sasKeyName = parseResult.GetValue(sasKeyNameOption)!;
            var sasKey = parseResult.GetValue(sasKeyOption)!;
            var profile = parseResult.GetValue(ServiceEndpointsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ServiceEndpointsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteServiceBusAsync(name, ns, path, contractType, sasKeyName, sasKey, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteServiceBusAsync(
        string name,
        string namespaceAddress,
        string path,
        string contractType,
        string sasKeyName,
        string sasKey,
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
                Console.Error.WriteLine($"Registering {contractType.ToLowerInvariant()}: {name}");
            }

            var registration = new ServiceBusRegistration(
                Name: name,
                NamespaceAddress: namespaceAddress,
                Path: path,
                Contract: contractType,
                AuthType: "SASKey",
                SasKeyName: sasKeyName,
                SasKey: sasKey);

            var endpointId = await endpointService.RegisterServiceBusAsync(registration, cancellationToken);

            var result = new RegisterResult
            {
                Success = true,
                Operation = $"register-{contractType.ToLowerInvariant()}",
                Id = endpointId,
                Name = name,
                ContractType = contractType
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"  Endpoint registered: {endpointId}");
                Console.Error.WriteLine($"  Namespace: {namespaceAddress}");
                Console.Error.WriteLine($"  Path: {path}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"registering {contractType.ToLowerInvariant()}", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #endregion

    #region Result Models

    private sealed class RegisterResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("operation")]
        public string Operation { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("contractType")]
        public string ContractType { get; set; } = string.Empty;
    }

    #endregion
}
