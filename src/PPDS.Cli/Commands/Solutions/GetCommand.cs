using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.Solutions;

/// <summary>
/// Get details for a specific solution.
/// </summary>
public static class GetCommand
{
    public static Command Create()
    {
        var uniqueNameArgument = new Argument<string>("unique-name")
        {
            Description = "The solution unique name"
        };

        var command = new Command("get", "Get details for a specific solution")
        {
            uniqueNameArgument,
            SolutionsCommandGroup.ProfileOption,
            SolutionsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var uniqueName = parseResult.GetValue(uniqueNameArgument)!;
            var profile = parseResult.GetValue(SolutionsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(SolutionsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(uniqueName, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string uniqueName,
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

            var solutionService = serviceProvider.GetRequiredService<ISolutionService>();
            var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();

            if (!globalOptions.IsJsonMode)
            {
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var solution = await solutionService.GetAsync(uniqueName, cancellationToken);

            if (solution == null)
            {
                var error = new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Solution '{uniqueName}' not found.",
                    null,
                    uniqueName);
                writer.WriteError(error);
                return ExitCodes.NotFoundError;
            }

            var makerUrl = DataverseUrlBuilder.BuildSolutionMakerUrl(connectionInfo.EnvironmentUrl, solution.Id);

            if (globalOptions.IsJsonMode)
            {
                var output = new SolutionDetailOutput
                {
                    Id = solution.Id,
                    UniqueName = solution.UniqueName,
                    FriendlyName = solution.FriendlyName,
                    Version = solution.Version,
                    IsManaged = solution.IsManaged,
                    Publisher = solution.PublisherName,
                    Description = solution.Description,
                    CreatedOn = solution.CreatedOn,
                    ModifiedOn = solution.ModifiedOn,
                    InstalledOn = solution.InstalledOn,
                    MakerUrl = makerUrl
                };
                writer.WriteSuccess(output);
            }
            else
            {
                Console.Error.WriteLine($"Solution: {solution.FriendlyName}");
                Console.Error.WriteLine($"  Unique Name: {solution.UniqueName}");
                Console.Error.WriteLine($"  Version:     {solution.Version ?? "-"}");
                Console.Error.WriteLine($"  Managed:     {(solution.IsManaged ? "Yes" : "No")}");
                Console.Error.WriteLine($"  Publisher:   {solution.PublisherName ?? "-"}");
                if (!string.IsNullOrEmpty(solution.Description))
                {
                    Console.Error.WriteLine($"  Description: {solution.Description}");
                }
                Console.Error.WriteLine($"  Created:     {solution.CreatedOn?.ToString("g") ?? "-"}");
                Console.Error.WriteLine($"  Modified:    {solution.ModifiedOn?.ToString("g") ?? "-"}");
                if (solution.InstalledOn.HasValue)
                {
                    Console.Error.WriteLine($"  Installed:   {solution.InstalledOn.Value:g}");
                }
                Console.Error.WriteLine($"  ID:          {solution.Id}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"getting solution '{uniqueName}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class SolutionDetailOutput
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

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("createdOn")]
        public DateTime? CreatedOn { get; set; }

        [JsonPropertyName("modifiedOn")]
        public DateTime? ModifiedOn { get; set; }

        [JsonPropertyName("installedOn")]
        public DateTime? InstalledOn { get; set; }

        [JsonPropertyName("makerUrl")]
        public string MakerUrl { get; set; } = string.Empty;
    }

    #endregion
}
