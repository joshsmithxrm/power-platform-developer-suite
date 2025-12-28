using System.CommandLine;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Auth.Cloud;
using PPDS.Auth.Discovery;
using PPDS.Auth.Profiles;
using PPDS.Cli.Commands;
using PPDS.Cli.Infrastructure;
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

        return command;
    }

    #region List Command

    private static Command CreateListCommand()
    {
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Output as JSON",
            DefaultValueFactory = _ => false
        };

        var command = new Command("list", "List available environments")
        {
            jsonOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            return await ExecuteListAsync(json, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteListAsync(bool json, CancellationToken cancellationToken)
    {
        try
        {
            // Load profiles to get cloud setting
            using var store = new ProfileStore();
            var collection = await store.LoadAsync(cancellationToken);

            var profile = collection.ActiveProfile;
            if (profile == null)
            {
                Console.Error.WriteLine("Error: No active profile. Use 'ppds auth create' first.");
                return ExitCodes.Failure;
            }

            Console.WriteLine("Discovering environments...");
            Console.WriteLine();

            // Use the GlobalDiscoveryService to get environments
            using var gds = GlobalDiscoveryService.FromProfile(profile);
            var environments = await gds.DiscoverEnvironmentsAsync(cancellationToken);

            if (json)
            {
                WriteEnvironmentsAsJson(environments, profile);
            }
            else
            {
                WriteEnvironmentsAsText(environments, profile);
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
        AuthProfile profile)
    {
        if (environments.Count == 0)
        {
            Console.WriteLine("No environments found.");
            Console.WriteLine();
            Console.WriteLine("This may indicate the user has no access to any environments.");
            return;
        }

        Console.WriteLine("Available Environments");
        Console.WriteLine(new string('=', 70));
        Console.WriteLine();

        // Get the currently selected environment URL if any
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

        Console.WriteLine($"Total: {environments.Count} environment(s)");
        if (selectedUrl != null)
        {
            Console.WriteLine("* = active environment");
        }
    }

    private static void WriteEnvironmentsAsJson(
        IReadOnlyList<DiscoveredEnvironment> environments,
        AuthProfile profile)
    {
        var selectedUrl = profile.Environment?.Url?.TrimEnd('/').ToLowerInvariant();

        var output = new
        {
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
        var environmentArg = new Argument<string>("environment")
        {
            Description = "Environment name, URL, or unique name"
        };

        var command = new Command("select", "Select the active environment for the current profile")
        {
            environmentArg
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var environment = parseResult.GetValue(environmentArg)!;
            return await ExecuteSelectAsync(environment, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteSelectAsync(string environmentIdentifier, CancellationToken cancellationToken)
    {
        try
        {
            using var store = new ProfileStore();
            var collection = await store.LoadAsync(cancellationToken);

            var profile = collection.ActiveProfile;
            if (profile == null)
            {
                Console.Error.WriteLine("Error: No active profile. Use 'ppds auth create' first.");
                return ExitCodes.Failure;
            }

            Console.WriteLine("Discovering environments...");

            // Use the GlobalDiscoveryService to get environments
            using var gds = GlobalDiscoveryService.FromProfile(profile);
            var environments = await gds.DiscoverEnvironmentsAsync(cancellationToken);

            // Resolve the environment
            DiscoveredEnvironment? resolved;
            try
            {
                resolved = EnvironmentResolver.Resolve(environments, environmentIdentifier);
            }
            catch (AmbiguousMatchException ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return ExitCodes.Failure;
            }

            if (resolved == null)
            {
                Console.Error.WriteLine($"Error: Environment '{environmentIdentifier}' not found.");
                Console.Error.WriteLine();
                Console.Error.WriteLine("Use 'ppds env list' to see available environments.");
                return ExitCodes.Failure;
            }

            // Update the profile with the selected environment
            profile.Environment = new EnvironmentInfo
            {
                Url = resolved.ApiUrl,
                DisplayName = resolved.FriendlyName,
                UniqueName = resolved.UniqueName,
                EnvironmentId = resolved.EnvironmentId
            };

            await store.SaveAsync(collection, cancellationToken);

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Environment selected: {resolved.FriendlyName}");
            Console.ResetColor();
            Console.WriteLine($"  URL: {resolved.ApiUrl}");
            Console.WriteLine($"  Type: {resolved.EnvironmentType}");

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
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Output as JSON",
            DefaultValueFactory = _ => false
        };

        var command = new Command("who", "Verify connection and show current user info from Dataverse")
        {
            jsonOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var json = parseResult.GetValue(jsonOption);
            return await ExecuteWhoAsync(json, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteWhoAsync(bool json, CancellationToken cancellationToken)
    {
        try
        {
            using var store = new ProfileStore();
            var collection = await store.LoadAsync(cancellationToken);

            var profile = collection.ActiveProfile;
            if (profile == null)
            {
                if (json)
                {
                    Console.WriteLine("{\"error\": \"No active profile\"}");
                }
                else
                {
                    Console.WriteLine("No active profile.");
                    Console.WriteLine();
                    Console.WriteLine("Use 'ppds auth create' to create a profile.");
                }
                return ExitCodes.Failure;
            }

            var env = profile.Environment;
            if (env == null)
            {
                if (json)
                {
                    Console.WriteLine("{\"error\": \"No environment selected\"}");
                }
                else
                {
                    Console.WriteLine($"Profile: {profile.DisplayIdentifier}");
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("No environment selected.");
                    Console.ResetColor();
                    Console.WriteLine();
                    Console.WriteLine("Use 'ppds env select <environment>' to select one.");
                }
                return ExitCodes.Failure;
            }

            if (!json)
            {
                Console.WriteLine($"Connecting to {env.DisplayName}...");
            }

            // Create connection and execute WhoAmI
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfileAsync(
                null, // Use active profile
                null, // Use profile's environment
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

            if (json)
            {
                var output = new
                {
                    userId = whoAmIResponse.UserId,
                    businessUnitId = whoAmIResponse.BusinessUnitId,
                    organizationId = orgId,
                    organizationName = orgName,
                    organizationUniqueName = orgUniqueName,
                    environmentUrl = env.Url,
                    environmentName = env.DisplayName
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
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Connected successfully!");
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine($"  Org ID:        {orgId}");
                Console.WriteLine($"  Unique Name:   {orgUniqueName}");
                Console.WriteLine($"  Friendly Name: {orgName}");
                Console.WriteLine($"  Org URL:       {env.Url}");
                Console.WriteLine($"  User ID:       {whoAmIResponse.UserId}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            if (json)
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
}
