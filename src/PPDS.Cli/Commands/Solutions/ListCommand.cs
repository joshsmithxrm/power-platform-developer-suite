using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Solutions;

namespace PPDS.Cli.Commands.Solutions;

/// <summary>
/// List solutions in a Dataverse environment.
/// </summary>
public static class ListCommand
{
    public static Command Create()
    {
        var filterOption = new Option<string?>("--filter")
        {
            Description = "Filter by solution unique name or friendly name"
        };

        var includeManagedOption = new Option<bool>("--include-managed", "-m")
        {
            Description = "Include managed solutions in the list"
        };

        var command = new Command("list", "List solutions in the environment")
        {
            SolutionsCommandGroup.ProfileOption,
            SolutionsCommandGroup.EnvironmentOption,
            filterOption,
            includeManagedOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var profile = parseResult.GetValue(SolutionsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(SolutionsCommandGroup.EnvironmentOption);
            var filter = parseResult.GetValue(filterOption);
            var includeManaged = parseResult.GetValue(includeManagedOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(profile, environment, filter, includeManaged, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? profile,
        string? environment,
        string? filter,
        bool includeManaged,
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

            var solutionService = serviceProvider.GetRequiredService<ISolutionService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var result = await solutionService.ListAsync(filter, includeManaged, cancellationToken: cancellationToken);
            var solutions = result.Items;

            if (solutions.Count == 0)
            {
                if (globalOptions.IsJsonMode)
                {
                    writer.WriteSuccess(new ListOutput { Solutions = [] });
                }
                else
                {
                    Console.Error.WriteLine("No solutions found.");
                }
                return ExitCodes.Success;
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new ListOutput
                {
                    Solutions = solutions.Select(s => new SolutionOutput
                    {
                        Id = s.Id,
                        UniqueName = s.UniqueName,
                        FriendlyName = s.FriendlyName,
                        Version = s.Version,
                        IsManaged = s.IsManaged,
                        Publisher = s.PublisherName,
                        ModifiedOn = s.ModifiedOn
                    }).ToList()
                };
                writer.WriteSuccess(output);
            }
            else
            {
                // Table output
                Console.Error.WriteLine($"{"Name",-40} {"Unique Name",-30} {"Version",-12} {"Publisher",-20} {"Managed"}");
                Console.Error.WriteLine(new string('-', 120));

                foreach (var solution in solutions)
                {
                    var name = Truncate(solution.FriendlyName, 40);
                    var uniqueName = Truncate(solution.UniqueName, 30);
                    var version = Truncate(solution.Version ?? "-", 12);
                    var publisher = Truncate(solution.PublisherName ?? "-", 20);
                    var managed = solution.IsManaged ? "Yes" : "No";

                    Console.Error.WriteLine($"{name,-40} {uniqueName,-30} {version,-12} {publisher,-20} {managed}");
                }

                Console.Error.WriteLine();
                Console.Error.WriteLine($"Total: {solutions.Count} solution(s)");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "listing solutions", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }

    #region Output Models

    private sealed class ListOutput
    {
        [JsonPropertyName("solutions")]
        public List<SolutionOutput> Solutions { get; set; } = [];
    }

    private sealed class SolutionOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("uniqueName")]
        public string UniqueName { get; set; } = string.Empty;

        [JsonPropertyName("friendlyName")]
        public string FriendlyName { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("isManaged")]
        public bool IsManaged { get; set; }

        [JsonPropertyName("publisher")]
        public string? Publisher { get; set; }

        [JsonPropertyName("modifiedOn")]
        public DateTime? ModifiedOn { get; set; }
    }

    #endregion
}
