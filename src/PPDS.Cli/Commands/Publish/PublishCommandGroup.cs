using System.CommandLine;
using System.Security;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Commands.WebResources;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.Publish;

/// <summary>
/// Top-level publish command for Dataverse customizations.
/// Supports --all (PublishAllXml), --type with specific resources, and --solution scoping.
/// </summary>
public static class PublishCommandGroup
{
    private static readonly Option<bool> AllOption = new("--all")
    {
        Description = "Publish all customizations (PublishAllXml). Cannot combine with other flags."
    };

    private static readonly Option<string?> TypeOption = new("--type", "-t")
    {
        Description = "Component type to publish. Required when specifying resources or --solution. Supported: webresource, entity"
    };

    private static readonly Option<string?> SolutionOption = new("--solution", "-s")
    {
        Description = "Publish all components of the specified type in this solution"
    };

    private static readonly Option<string?> ProfileOption = new("--profile", "-p")
    {
        Description = "Authentication profile name"
    };

    private static readonly Option<string?> EnvironmentOption = new("--environment", "-e")
    {
        Description = "Override the environment URL. Takes precedence over profile's bound environment."
    };

    /// <summary>
    /// Creates the top-level 'publish' command. Also used by domain aliases.
    /// </summary>
    public static Command Create()
    {
        var namesArgument = new Argument<string[]>("names")
        {
            Description = "Web resource names, partial names, or GUIDs to publish",
            Arity = ArgumentArity.ZeroOrMore
        };

        var command = new Command("publish", "Publish Dataverse customizations")
        {
            namesArgument,
            AllOption,
            TypeOption,
            SolutionOption,
            ProfileOption,
            EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        // Flag combination validation
        command.Validators.Add(result =>
        {
            var all = result.GetValue(AllOption);
            var type = result.GetValue(TypeOption);
            var solution = result.GetValue(SolutionOption);
            var names = result.GetValue(namesArgument) ?? [];

            if (all)
            {
                if (type != null)
                    result.AddError("--all publishes all customizations and cannot be combined with --type. Use --type without --all to scope by component type.");
                if (solution != null)
                    result.AddError("--all publishes all customizations and cannot be combined with --solution. Use --solution without --all to scope by solution.");
                if (names.Length > 0)
                    result.AddError("--all publishes all customizations and cannot be combined with resource names. Remove --all to publish specific resources.");
            }
            else if (names.Length > 0 && type == null)
            {
                result.AddError("--type is required when specifying resources. Example: ppds publish --type webresource app.js");
            }
            else if (solution != null && type == null)
            {
                result.AddError("--type is required with --solution. Supported types: webresource, entity");
            }
            else if (names.Length == 0 && solution == null)
            {
                // Bare "ppds publish" with no flags — we let it through and show help in execute
            }
        });

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var names = parseResult.GetValue(namesArgument) ?? [];
            var all = parseResult.GetValue(AllOption);
            var type = parseResult.GetValue(TypeOption);
            var solution = parseResult.GetValue(SolutionOption);
            var profile = parseResult.GetValue(ProfileOption);
            var environment = parseResult.GetValue(EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(names, all, type, solution, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    /// <summary>
    /// Core execution — also called by alias commands that pre-set type/all.
    /// </summary>
    internal static async Task<int> ExecuteAsync(
        string[] names,
        bool all,
        string? type,
        string? solution,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        // Bare command with no actionable input
        if (!all && names.Length == 0 && solution == null)
        {
            Console.Error.WriteLine("Usage: ppds publish --all | --type <type> <names...> | --type <type> --solution <name>");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Options:");
            Console.Error.WriteLine("  --all                Publish all customizations (PublishAllXml)");
            Console.Error.WriteLine("  --type <type>        Component type (supported: webresource, entity)");
            Console.Error.WriteLine("  --solution <name>    Scope to components in a solution (requires --type)");
            return ExitCodes.InvalidArguments;
        }

        // Validate type if provided
        var supportedTypes = new[] { "webresource", "entity" };
        if (type != null && !supportedTypes.Any(t => t.Equals(type, StringComparison.OrdinalIgnoreCase)))
        {
            var error = new StructuredError(
                ErrorCodes.Validation.InvalidValue,
                $"Unsupported type '{type}'. Supported types: webresource, entity",
                null,
                type);
            writer.WriteError(error);
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

            var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();

            if (!globalOptions.IsJsonMode)
            {
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            if (all)
            {
                return await PublishAllAsync(serviceProvider, writer, globalOptions, cancellationToken);
            }
            else if (type != null && type.Equals("entity", StringComparison.OrdinalIgnoreCase))
            {
                return await PublishEntitiesAsync(
                    serviceProvider, names, solution, writer, globalOptions, cancellationToken);
            }
            else
            {
                return await PublishWebResourcesAsync(
                    serviceProvider, names, solution, writer, globalOptions, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "publishing customizations", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static async Task<int> PublishAllAsync(
        ServiceProvider serviceProvider,
        IOutputWriter writer,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        // Use ISolutionService for PublishAllXml — this is a platform-level operation,
        // not web-resource-specific. Matches original ppds solutions publish behavior.
        var solutionService = serviceProvider.GetRequiredService<ISolutionService>();

        if (!globalOptions.IsJsonMode)
        {
            Console.Error.WriteLine("Publishing all customizations...");
        }

        var startTime = DateTime.UtcNow;
        await solutionService.PublishAllAsync(cancellationToken);
        var duration = DateTime.UtcNow - startTime;

        if (globalOptions.IsJsonMode)
        {
            writer.WriteSuccess(new PublishAllOutput
            {
                Success = true,
                DurationSeconds = duration.TotalSeconds
            });
        }
        else
        {
            Console.Error.WriteLine($"Published successfully in {duration.TotalSeconds:F1} seconds.");
        }

        return ExitCodes.Success;
    }

    private static async Task<int> PublishWebResourcesAsync(
        ServiceProvider serviceProvider,
        string[] names,
        string? solution,
        IOutputWriter writer,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var webResourceService = serviceProvider.GetRequiredService<IWebResourceService>();

        // Resolve solution if provided
        Guid? solutionId = null;
        if (solution != null)
        {
            var solutionService = serviceProvider.GetRequiredService<ISolutionService>();
            var solutionInfo = await solutionService.GetAsync(solution, cancellationToken);
            if (solutionInfo == null)
            {
                var error = new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Solution '{solution}' not found.",
                    null,
                    solution);
                writer.WriteError(error);
                return ExitCodes.NotFoundError;
            }
            solutionId = solutionInfo.Id;
        }

        // Get all resources (filtered by solution if provided) for name resolution
        var resources = (await webResourceService.ListAsync(
            solutionId: solutionId,
            cancellationToken: cancellationToken)).Items;

        List<Guid> idsToPublish;

        if (names.Length > 0)
        {
            // Resolve each name
            idsToPublish = [];
            foreach (var name in names)
            {
                var resolveResult = WebResourceNameResolver.Resolve(name, resources);
                if (!resolveResult.IsSuccess)
                {
                    if (resolveResult.Matches.Count == 0)
                    {
                        var error = new StructuredError(
                            ErrorCodes.WebResource.NotFound,
                            $"Web resource '{name}' not found.",
                            null,
                            name);
                        writer.WriteError(error);
                        return ExitCodes.NotFoundError;
                    }
                    else
                    {
                        var matchNames = string.Join("\n  ", resolveResult.Matches.Select(m => m.Name));
                        var error = new StructuredError(
                            ErrorCodes.WebResource.Ambiguous,
                            $"Multiple web resources match '{name}':\n  {matchNames}\n\nSpecify a more complete name to narrow the match.",
                            null,
                            name);
                        writer.WriteError(error);
                        return ExitCodes.InvalidArguments;
                    }
                }
                idsToPublish.Add(resolveResult.Matches[0].Id);
            }
        }
        else
        {
            // --solution without names: publish all web resources in solution
            idsToPublish = resources.Select(r => r.Id).ToList();
        }

        if (idsToPublish.Count == 0)
        {
            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine("No web resources to publish.");
            }
            else
            {
                writer.WriteSuccess(new PublishResourcesOutput
                {
                    PublishedCount = 0,
                    DurationSeconds = 0
                });
            }
            return ExitCodes.Success;
        }

        if (!globalOptions.IsJsonMode)
        {
            Console.Error.WriteLine($"Publishing {idsToPublish.Count} web resource(s)...");
        }

        var startTime = DateTime.UtcNow;
        var publishedCount = await webResourceService.PublishAsync(idsToPublish, cancellationToken);
        var duration = DateTime.UtcNow - startTime;

        if (globalOptions.IsJsonMode)
        {
            writer.WriteSuccess(new PublishResourcesOutput
            {
                PublishedCount = publishedCount,
                DurationSeconds = duration.TotalSeconds
            });
        }
        else
        {
            Console.Error.WriteLine($"Published {publishedCount} web resource(s) in {duration.TotalSeconds:F1} seconds.");
        }

        return ExitCodes.Success;
    }

    private static async Task<int> PublishEntitiesAsync(
        ServiceProvider serviceProvider,
        string[] names,
        string? solution,
        IOutputWriter writer,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        if (names.Length == 0)
        {
            Console.Error.WriteLine("Specify entity logical names to publish. Example: ppds publish --type entity account contact");
            return ExitCodes.InvalidArguments;
        }

        var entityNames = names.ToList();

        var pool = serviceProvider.GetRequiredService<IDataverseConnectionPool>();
        await using var client = await pool.GetClientAsync(cancellationToken: cancellationToken);

        if (!globalOptions.IsJsonMode)
        {
            Console.Error.WriteLine($"Publishing {entityNames.Count} entity metadata...");
        }

        var entityXml = string.Join("", entityNames.Select(n =>
            $"<entity>{SecurityElement.Escape(n)}</entity>"));
        var parameterXml = $"<importexportxml><entities>{entityXml}</entities></importexportxml>";

        var startTime = DateTime.UtcNow;
        var environmentKey = client.ConnectedOrgUniqueName ?? "default";
        await client.PublishXmlAsync(parameterXml, environmentKey, cancellationToken);
        var duration = DateTime.UtcNow - startTime;

        if (globalOptions.IsJsonMode)
        {
            writer.WriteSuccess(new PublishEntitiesOutput
            {
                PublishedCount = entityNames.Count,
                EntityNames = entityNames,
                DurationSeconds = duration.TotalSeconds
            });
        }
        else
        {
            Console.Error.WriteLine($"Published {entityNames.Count} entity metadata in {duration.TotalSeconds:F1} seconds.");
        }

        return ExitCodes.Success;
    }

    #region Output Models

    private sealed class PublishAllOutput
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("durationSeconds")]
        public double DurationSeconds { get; set; }
    }

    private sealed class PublishResourcesOutput
    {
        [JsonPropertyName("publishedCount")]
        public int PublishedCount { get; set; }

        [JsonPropertyName("durationSeconds")]
        public double DurationSeconds { get; set; }
    }

    private sealed class PublishEntitiesOutput
    {
        [JsonPropertyName("publishedCount")]
        public int PublishedCount { get; set; }

        [JsonPropertyName("entityNames")]
        public List<string> EntityNames { get; set; } = [];

        [JsonPropertyName("durationSeconds")]
        public double DurationSeconds { get; set; }
    }

    #endregion
}
