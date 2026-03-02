using System.CommandLine;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Auth.Cloud;
using PPDS.Auth.Credentials;
using PPDS.Auth.Discovery;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Environment;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Commands.Env;

/// <summary>
/// Environment management commands.
/// </summary>
public static class EnvCommandGroup
{
    /// <summary>
    /// Creates the 'env' command group with all subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("env", "Manage environment selection");

        command.Subcommands.Add(CreateListCommand());
        command.Subcommands.Add(CreateSelectCommand());
        command.Subcommands.Add(CreateWhoCommand());
        command.Subcommands.Add(CreateConfigCommand());
        command.Subcommands.Add(CreateTypeCommand());

        return command;
    }

    /// <summary>
    /// Creates the 'org' command group as an alias for 'env'.
    /// </summary>
    public static Command CreateOrgAlias()
    {
        var command = new Command("org", "Manage environment selection (alias for 'env')");

        command.Subcommands.Add(CreateListCommand());
        command.Subcommands.Add(CreateSelectCommand());
        command.Subcommands.Add(CreateWhoCommand());
        command.Subcommands.Add(CreateConfigCommand());
        command.Subcommands.Add(CreateTypeCommand());

        return command;
    }

    #region List Command

    private static Command CreateListCommand()
    {
        var outputFormatOption = new Option<OutputFormat>("--output-format", "-f")
        {
            Description = "Output format",
            DefaultValueFactory = _ => OutputFormat.Text
        };

        var filterOption = new Option<string?>("--filter", "-fl")
        {
            Description = "Filter environments by name, URL, or ID (case-insensitive)"
        };

        var command = new Command("list", "List available environments")
        {
            outputFormatOption,
            filterOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputFormat = parseResult.GetValue(outputFormatOption);
            var filter = parseResult.GetValue(filterOption);
            return await ExecuteListAsync(outputFormat, filter, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteListAsync(
        OutputFormat outputFormat,
        string? filter,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var localProvider = ProfileServiceFactory.CreateLocalProvider();
            var store = localProvider.GetRequiredService<ProfileStore>();
            var collection = await store.LoadAsync(cancellationToken);

            var profile = collection.ActiveProfile;
            if (profile == null)
            {
                Console.Error.WriteLine("Error: No active profile. Use 'ppds auth create' first.");
                return ExitCodes.Failure;
            }

            ConsoleHeader.WriteConnectedAs(profile);
            Console.Error.WriteLine("Discovering environments...");
            Console.Error.WriteLine();

            using var gds = GlobalDiscoveryService.FromProfile(profile);
            var environments = await gds.DiscoverEnvironmentsAsync(cancellationToken);

            // Apply filter if provided
            IReadOnlyList<DiscoveredEnvironment> filtered = environments;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                filtered = environments.Where(e =>
                    e.FriendlyName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    e.UniqueName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    e.ApiUrl.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    (e.EnvironmentId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                ).ToList();
            }

            if (outputFormat == OutputFormat.Json)
            {
                WriteEnvironmentsAsJson(filtered, profile, filter);
            }
            else
            {
                WriteEnvironmentsAsText(filtered, profile, filter);
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Failure;
        }
    }

    private static void WriteEnvironmentsAsText(
        IReadOnlyList<DiscoveredEnvironment> environments,
        AuthProfile profile,
        string? filter)
    {
        if (environments.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(filter))
            {
                Console.Error.WriteLine($"No environments matching '{filter}'.");
            }
            else
            {
                Console.Error.WriteLine("No environments found.");
                Console.Error.WriteLine();
                Console.Error.WriteLine("This may indicate the user has no access to any environments.");
            }
            return;
        }

        Console.WriteLine("[Environments]");
        Console.WriteLine();

        var selectedUrl = profile.Environment?.Url?.TrimEnd('/').ToLowerInvariant();

        foreach (var env in environments)
        {
            var isActive = selectedUrl != null &&
                env.ApiUrl.TrimEnd('/').ToLowerInvariant() == selectedUrl;
            var activeMarker = isActive ? " *" : "";

            Console.ForegroundColor = isActive ? ConsoleColor.Green : ConsoleColor.Gray;
            Console.Write($"  {env.FriendlyName}");
            Console.ResetColor();
            Console.WriteLine(activeMarker);

            Console.WriteLine($"      Type: {env.EnvironmentType}");
            Console.WriteLine($"      URL: {env.ApiUrl}");
            Console.WriteLine($"      Unique Name: {env.UniqueName}");
            if (!string.IsNullOrEmpty(env.Region))
            {
                Console.WriteLine($"      Region: {env.Region}");
            }
            Console.WriteLine();
        }

        var filterNote = !string.IsNullOrWhiteSpace(filter) ? $" matching '{filter}'" : "";
        Console.WriteLine($"Total: {environments.Count} environment(s){filterNote}");
        if (selectedUrl != null)
        {
            Console.WriteLine("* = active environment");
        }
    }

    private static void WriteEnvironmentsAsJson(
        IReadOnlyList<DiscoveredEnvironment> environments,
        AuthProfile profile,
        string? filter)
    {
        var selectedUrl = profile.Environment?.Url?.TrimEnd('/').ToLowerInvariant();

        var output = new
        {
            filter,
            environments = environments.Select(e => new
            {
                id = e.Id,
                environmentId = e.EnvironmentId,
                friendlyName = e.FriendlyName,
                uniqueName = e.UniqueName,
                apiUrl = e.ApiUrl,
                url = e.Url,
                type = e.EnvironmentType,
                state = e.IsEnabled ? "Enabled" : "Disabled",
                region = e.Region,
                version = e.Version,
                isActive = selectedUrl != null &&
                    e.ApiUrl.TrimEnd('/').ToLowerInvariant() == selectedUrl
            })
        };

        var jsonOutput = System.Text.Json.JsonSerializer.Serialize(output, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
        Console.WriteLine(jsonOutput);
    }

    #endregion

    #region Select Command

    private static Command CreateSelectCommand()
    {
        var envOption = new Option<string>("--environment", "-env")
        {
            Description = "Default environment (ID, url, unique name, or partial name)",
            Required = true
        };

        var command = new Command("select", "Select the active environment for the current profile")
        {
            envOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var environment = parseResult.GetValue(envOption)!;
            return await ExecuteSelectAsync(environment, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteSelectAsync(string environmentIdentifier, CancellationToken cancellationToken)
    {
        try
        {
            await using var localProvider = ProfileServiceFactory.CreateLocalProvider();
            var store = localProvider.GetRequiredService<ProfileStore>();
            var collection = await store.LoadAsync(cancellationToken);

            var profile = collection.ActiveProfile;
            if (profile == null)
            {
                Console.Error.WriteLine("Error: No active profile. Use 'ppds auth create' first.");
                return ExitCodes.Failure;
            }

            ConsoleHeader.WriteConnectedAs(profile);
            Console.Error.WriteLine($"Resolving environment '{environmentIdentifier}'...");

            // Use multi-layer resolution: direct connection first for URLs, Global Discovery for names
            var credentialStore = localProvider.GetRequiredService<ISecureCredentialStore>();
            using var resolver = new EnvironmentResolutionService(profile, credentialStore: credentialStore);
            var result = await resolver.ResolveAsync(environmentIdentifier, cancellationToken);

            if (!result.Success)
            {
                Console.Error.WriteLine($"Error: {result.ErrorMessage}");
                return ExitCodes.Failure;
            }

            var resolved = result.Environment!;

            // Validate connection before saving - ensures user has actual access to the environment
            Console.Error.WriteLine("Validating connection...");

            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfileAsync(
                profile.Name, resolved.Url,
                deviceCodeCallback: ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken: cancellationToken);

            var pool = serviceProvider.GetRequiredService<IDataverseConnectionPool>();
            await using var client = await pool.GetClientAsync(cancellationToken: cancellationToken);
            await client.ExecuteAsync(new WhoAmIRequest(), cancellationToken);

            // Connection validated - now safe to save
            profile.Environment = resolved;
            await store.SaveAsync(collection, cancellationToken);

            var methodNote = result.Method == ResolutionMethod.DirectConnection
                ? " (via direct connection)"
                : " (via Global Discovery)";

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Error.WriteLine($"Connected to {resolved.DisplayName}{methodNote}");
            Console.ResetColor();

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Failure;
        }
    }

    #endregion

    #region Who Command

    private static Command CreateWhoCommand()
    {
        var outputFormatOption = new Option<OutputFormat>("--output-format", "-f")
        {
            Description = "Output format",
            DefaultValueFactory = _ => OutputFormat.Text
        };

        var envOption = new Option<string?>("--environment", "-env")
        {
            Description = "Environment to query (ID, URL, unique name, or partial name). Uses profile default if not specified."
        };

        var command = new Command("who", "Verify connection and show current user info from Dataverse")
        {
            outputFormatOption,
            envOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outputFormat = parseResult.GetValue(outputFormatOption);
            var environmentOverride = parseResult.GetValue(envOption);
            return await ExecuteWhoAsync(outputFormat, environmentOverride, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteWhoAsync(
        OutputFormat outputFormat,
        string? environmentOverride,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var localProvider = ProfileServiceFactory.CreateLocalProvider();
            var store = localProvider.GetRequiredService<ProfileStore>();
            var collection = await store.LoadAsync(cancellationToken);

            var profile = collection.ActiveProfile;
            if (profile == null)
            {
                if (outputFormat == OutputFormat.Json)
                {
                    Console.WriteLine("{\"error\": \"No active profile\"}");
                }
                else
                {
                    Console.Error.WriteLine("No active profile.");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Use 'ppds auth create' to create a profile.");
                }
                return ExitCodes.Failure;
            }

            // Resolve environment: use override if provided, otherwise profile default
            string environmentUrl;
            string environmentName;
            string? environmentId = null;

            if (!string.IsNullOrWhiteSpace(environmentOverride))
            {
                // Resolve the override environment using multi-layer resolution
                if (outputFormat != OutputFormat.Json)
                {
                    ConsoleHeader.WriteConnectedAs(profile);
                    Console.Error.WriteLine($"Resolving environment '{environmentOverride}'...");
                }

                var credentialStore = localProvider.GetRequiredService<ISecureCredentialStore>();
                using var resolver = new EnvironmentResolutionService(profile, credentialStore: credentialStore);
                var result = await resolver.ResolveAsync(environmentOverride, cancellationToken);

                if (!result.Success)
                {
                    if (outputFormat == OutputFormat.Json)
                    {
                        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { error = result.ErrorMessage }));
                    }
                    else
                    {
                        Console.Error.WriteLine($"Error: {result.ErrorMessage}");
                    }
                    return ExitCodes.Failure;
                }

                var resolved = result.Environment!;
                environmentUrl = resolved.Url;
                environmentName = resolved.DisplayName;
                environmentId = resolved.EnvironmentId;
            }
            else
            {
                // Use profile's default environment
                var env = profile.Environment;
                if (env == null)
                {
                    if (outputFormat == OutputFormat.Json)
                    {
                        Console.WriteLine("{\"error\": \"No environment selected\"}");
                    }
                    else
                    {
                        Console.Error.WriteLine($"Profile: {profile.DisplayIdentifier}");
                        Console.Error.WriteLine();
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.Error.WriteLine("No environment selected.");
                        Console.ResetColor();
                        Console.Error.WriteLine();
                        Console.Error.WriteLine("Use 'ppds env select <environment>' to select one.");
                    }
                    return ExitCodes.Failure;
                }

                environmentUrl = env.Url;
                environmentName = env.DisplayName;
                environmentId = env.EnvironmentId;

                if (outputFormat != OutputFormat.Json)
                {
                    ConsoleHeader.WriteConnectedAs(profile, environmentName);
                }
            }

            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfileAsync(
                null, // Use active profile
                environmentUrl, // Use resolved environment URL
                deviceCodeCallback: ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken: cancellationToken);

            var pool = serviceProvider.GetRequiredService<IDataverseConnectionPool>();
            await using var client = await pool.GetClientAsync(cancellationToken: cancellationToken);

            // WhoAmI verifies the connection and returns user/org IDs
            var whoAmIResponse = (WhoAmIResponse)await client.ExecuteAsync(
                new WhoAmIRequest(), cancellationToken);

            // Org info is available directly on the client - no extra query needed
            var orgName = client.ConnectedOrgFriendlyName;
            var orgUniqueName = client.ConnectedOrgUniqueName;
            var orgId = client.ConnectedOrgId;

            if (outputFormat == OutputFormat.Json)
            {
                var output = new
                {
                    userId = whoAmIResponse.UserId,
                    userEmail = profile.Username,
                    businessUnitId = whoAmIResponse.BusinessUnitId,
                    organizationId = orgId,
                    organizationName = orgName,
                    organizationUniqueName = orgUniqueName,
                    environmentId,
                    environmentUrl,
                    environmentName
                };

                var jsonOutput = System.Text.Json.JsonSerializer.Serialize(output, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });
                Console.WriteLine(jsonOutput);
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("Organization Information");
                Console.WriteLine($"  Org ID:         {orgId}");
                Console.WriteLine($"  Unique Name:    {orgUniqueName}");
                Console.WriteLine($"  Friendly Name:  {orgName}");
                Console.WriteLine($"  Org URL:        {environmentUrl}");
                if (!string.IsNullOrEmpty(profile.Username))
                {
                    Console.WriteLine($"  User Email:     {profile.Username}");
                }
                Console.WriteLine($"  User ID:        {whoAmIResponse.UserId}");
                if (!string.IsNullOrEmpty(environmentId))
                {
                    Console.WriteLine($"  Environment ID: {environmentId}");
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            if (outputFormat == OutputFormat.Json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message }));
            }
            else
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
            }
            return ExitCodes.Failure;
        }
    }

    #endregion

    #region Config Command

    private static Command CreateConfigCommand()
    {
        var urlArgument = new Argument<string?>("url")
        {
            Description = "Environment URL to configure"
        };
        urlArgument.Arity = ArgumentArity.ZeroOrOne;

        var labelOption = new Option<string?>("--label", "-l")
        {
            Description = "Short display label for status bar and tabs"
        };

        var typeOption = new Option<EnvironmentType?>("--type", "-t")
        {
            Description = "Environment type (Production, Sandbox, Development, Test, Trial)"
        };

        var colorOption = new Option<EnvironmentColor?>("--color", "-c")
        {
            Description = "Status bar color. Valid values: Red, Green, Yellow, Cyan, Blue, Gray, Brown, BrightRed, BrightGreen, BrightYellow, BrightCyan, BrightBlue, White"
        };

        var showOption = new Option<bool>("--show", "-s")
        {
            Description = "Show current configuration for the environment"
        };

        var listOption = new Option<bool>("--list")
        {
            Description = "List all configured environments"
        };

        var removeOption = new Option<bool>("--remove")
        {
            Description = "Remove configuration for the environment"
        };

        var command = new Command("config", "Configure environment display settings (label, type, color)")
        {
            urlArgument, labelOption, typeOption, colorOption, showOption, listOption, removeOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var url = parseResult.GetValue(urlArgument);
            var label = parseResult.GetValue(labelOption);
            var type = parseResult.GetValue(typeOption);
            var color = parseResult.GetValue(colorOption);
            var show = parseResult.GetValue(showOption);
            var list = parseResult.GetValue(listOption);
            var remove = parseResult.GetValue(removeOption);

            await using var localProvider = ProfileServiceFactory.CreateLocalProvider();
            var service = localProvider.GetRequiredService<IEnvironmentConfigService>();

            if (list)
                return await ExecuteConfigListAsync(service, cancellationToken);

            if (string.IsNullOrWhiteSpace(url))
            {
                Console.Error.WriteLine("Error: Environment URL is required. Use --list to see all configs.");
                return ExitCodes.Failure;
            }

            if (show)
                return await ExecuteConfigShowAsync(service, url, cancellationToken);

            if (remove)
                return await ExecuteConfigRemoveAsync(service, url, cancellationToken);

            if (label == null && type == null && color == null)
                return await ExecuteConfigShowAsync(service, url, cancellationToken);

            return await ExecuteConfigSetAsync(service, url, label, type, color, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteConfigSetAsync(
        IEnvironmentConfigService service, string url,
        string? label, EnvironmentType? type, EnvironmentColor? color,
        CancellationToken ct)
    {
        var config = await service.SaveConfigAsync(url, label, type, color, ct: ct);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.Error.WriteLine("Environment configuration saved.");
        Console.ResetColor();

        WriteConfigDetails(config);
        return ExitCodes.Success;
    }

    private static async Task<int> ExecuteConfigShowAsync(
        IEnvironmentConfigService service, string url, CancellationToken ct)
    {
        var config = await service.GetConfigAsync(url, ct);
        if (config == null)
        {
            Console.Error.WriteLine($"No configuration found for: {url}");
            Console.Error.WriteLine("Use 'ppds env config <url> --label <label> --type <type> --color <color>' to configure.");
            return ExitCodes.Success;
        }

        WriteConfigDetails(config);
        return ExitCodes.Success;
    }

    private static async Task<int> ExecuteConfigRemoveAsync(
        IEnvironmentConfigService service, string url, CancellationToken ct)
    {
        var removed = await service.RemoveConfigAsync(url, ct);
        if (removed)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Error.WriteLine($"Configuration removed for: {url}");
            Console.ResetColor();
        }
        else
        {
            Console.Error.WriteLine($"No configuration found for: {url}");
        }
        return ExitCodes.Success;
    }

    private static async Task<int> ExecuteConfigListAsync(
        IEnvironmentConfigService service, CancellationToken ct)
    {
        var configs = await service.GetAllConfigsAsync(ct);
        if (configs.Count == 0)
        {
            Console.Error.WriteLine("No environments configured.");
            Console.Error.WriteLine("Use 'ppds env config <url> --label <label> --type <type> --color <color>' to add one.");
            return ExitCodes.Success;
        }

        Console.WriteLine("[Configured Environments]");
        Console.WriteLine();
        foreach (var config in configs)
        {
            WriteConfigDetails(config);
            Console.WriteLine();
        }
        Console.WriteLine($"Total: {configs.Count} environment(s)");
        return ExitCodes.Success;
    }

    private static void WriteConfigDetails(EnvironmentConfig config)
    {
        Console.WriteLine($"  URL:   {config.Url}");
        if (config.Label != null)
            Console.WriteLine($"  Label: {config.Label}");
        if (config.Type != null)
            Console.WriteLine($"  Type:  {config.Type}");
        if (config.Color != null)
            Console.WriteLine($"  Color: {config.Color}");
    }

    #endregion

    #region Type Command

    private static Command CreateTypeCommand()
    {
        var nameArgument = new Argument<EnvironmentType?>("name")
        {
            Description = "Type name (Production, Sandbox, Development, Test, Trial)"
        };
        nameArgument.Arity = ArgumentArity.ZeroOrOne;

        var colorOption = new Option<EnvironmentColor?>("--color", "-c")
        {
            Description = "Default color for this type"
        };

        var removeOption = new Option<bool>("--remove")
        {
            Description = "Remove this custom type definition"
        };

        var listOption = new Option<bool>("--list")
        {
            Description = "List all type definitions (built-in + custom)"
        };

        var command = new Command("type", "Manage custom environment type definitions")
        {
            nameArgument, colorOption, removeOption, listOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArgument);
            var color = parseResult.GetValue(colorOption);
            var remove = parseResult.GetValue(removeOption);
            var list = parseResult.GetValue(listOption);

            await using var localProvider = ProfileServiceFactory.CreateLocalProvider();
            var service = localProvider.GetRequiredService<IEnvironmentConfigService>();

            if (list)
            {
                var defaults = await service.GetAllTypeDefaultsAsync(cancellationToken);
                Console.WriteLine("[Environment Types]");
                Console.WriteLine();
                foreach (var (typeName, typeColor) in defaults.OrderBy(d => d.Key))
                {
                    Console.WriteLine($"  {typeName,-15} {typeColor}");
                }
                return ExitCodes.Success;
            }

            if (name == null)
            {
                Console.Error.WriteLine("Error: Type name is required. Use --list to see all types.");
                return ExitCodes.Failure;
            }

            if (remove)
            {
                var removed = await service.RemoveTypeDefaultAsync(name.Value, cancellationToken);
                Console.Error.WriteLine(removed
                    ? $"Removed type override '{name}'."
                    : $"'{name}' is not a custom override (may be built-in).");
                return ExitCodes.Success;
            }

            if (color == null)
            {
                Console.Error.WriteLine("Error: --color is required when defining a type.");
                return ExitCodes.Failure;
            }

            await service.SaveTypeDefaultAsync(name.Value, color.Value, cancellationToken);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Error.WriteLine($"Type '{name}' set to {color.Value}.");
            Console.ResetColor();
            return ExitCodes.Success;
        });

        return command;
    }

    #endregion
}
