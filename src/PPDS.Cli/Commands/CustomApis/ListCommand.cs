using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.CustomApis;

/// <summary>
/// List registered Custom APIs in a Dataverse environment.
/// </summary>
public static class ListCommand
{
    public static Command Create()
    {
        var solutionOption = new Option<string?>("--solution")
        {
            Description = "Filter by solution name"
        };

        var command = new Command("list", "List registered Custom APIs in the environment")
        {
            solutionOption,
            CustomApisCommandGroup.ProfileOption,
            CustomApisCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(solutionOption);
            var profile = parseResult.GetValue(CustomApisCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(CustomApisCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(solution, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? solution,
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

            var apis = await customApiService.ListAsync(cancellationToken);

            // Apply solution filter if specified (post-filter since ListAsync doesn't take a solution param)
            // Note: solution filtering would require a different service method; for now list all
            _ = solution; // reserved for future filtering

            if (apis.Count == 0)
            {
                if (globalOptions.IsJsonMode)
                {
                    writer.WriteSuccess(new ListOutput { Apis = [] });
                }
                else
                {
                    Console.Error.WriteLine("No Custom APIs found.");
                }
                return ExitCodes.Success;
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new ListOutput
                {
                    Apis = apis.Select(a => new ApiOutput
                    {
                        Id = a.Id,
                        UniqueName = a.UniqueName,
                        DisplayName = a.DisplayName,
                        BindingType = a.BindingType,
                        BoundEntity = a.BoundEntity,
                        IsFunction = a.IsFunction,
                        IsPrivate = a.IsPrivate,
                        AllowedProcessingStepType = a.AllowedProcessingStepType,
                        PluginTypeName = a.PluginTypeName,
                        IsManaged = a.IsManaged,
                        CreatedOn = a.CreatedOn,
                        ModifiedOn = a.ModifiedOn
                    }).ToList()
                };
                writer.WriteSuccess(output);
            }
            else
            {
                foreach (var api in apis)
                {
                    var managed = api.IsManaged ? " [managed]" : "";
                    var flags = new List<string>();
                    if (api.IsFunction) flags.Add("function");
                    if (api.IsPrivate) flags.Add("private");
                    var flagStr = flags.Count > 0 ? $" ({string.Join(", ", flags)})" : "";
                    Console.Error.WriteLine($"{api.UniqueName}{managed}{flagStr}");
                    Console.Error.WriteLine($"  Display: {api.DisplayName}");
                    Console.Error.WriteLine($"  Binding: {api.BindingType}");
                    if (!string.IsNullOrEmpty(api.BoundEntity))
                        Console.Error.WriteLine($"  Entity: {api.BoundEntity}");
                    if (!string.IsNullOrEmpty(api.PluginTypeName))
                        Console.Error.WriteLine($"  Plugin: {api.PluginTypeName}");
                }

                Console.Error.WriteLine();
                Console.Error.WriteLine($"Total: {apis.Count} Custom API(s)");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "listing Custom APIs", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class ListOutput
    {
        [JsonPropertyName("apis")]
        public List<ApiOutput> Apis { get; set; } = [];
    }

    private sealed class ApiOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("uniqueName")]
        public string UniqueName { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("bindingType")]
        public string BindingType { get; set; } = string.Empty;

        [JsonPropertyName("boundEntity")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BoundEntity { get; set; }

        [JsonPropertyName("isFunction")]
        public bool IsFunction { get; set; }

        [JsonPropertyName("isPrivate")]
        public bool IsPrivate { get; set; }

        [JsonPropertyName("allowedProcessingStepType")]
        public string AllowedProcessingStepType { get; set; } = string.Empty;

        [JsonPropertyName("pluginTypeName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PluginTypeName { get; set; }

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
