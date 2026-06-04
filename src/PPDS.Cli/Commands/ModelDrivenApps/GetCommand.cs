using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.ModelDrivenApps;

namespace PPDS.Cli.Commands.ModelDrivenApps;

/// <summary>
/// Shows detailed metadata and component counts for a model-driven app.
/// </summary>
public static class GetCommand
{
    public static Command Create()
    {
        var command = new Command("get", "Show app metadata and component counts")
        {
            ModelDrivenAppCommandGroup.AppOption,
            ModelDrivenAppCommandGroup.ProfileOption,
            ModelDrivenAppCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, ct) =>
        {
            var app = parseResult.GetValue(ModelDrivenAppCommandGroup.AppOption);
            var profile = parseResult.GetValue(ModelDrivenAppCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ModelDrivenAppCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            return await ExecuteAsync(app, profile, environment, globalOptions, ct);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? appName,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken ct)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        if (string.IsNullOrWhiteSpace(appName))
        {
            writer.WriteError(new StructuredError(ErrorCodes.Validation.RequiredField, "--app is required."));
            return ExitCodes.InvalidArguments;
        }

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile, environment, globalOptions.Verbose, globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback, ct);

            var service = serviceProvider.GetRequiredService<IModelDrivenAppService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var details = await service.GetAppAsync(appName, ct);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new AppDetailsOutput(details));
            }
            else
            {
                Console.WriteLine($"Name:           {details.DisplayName}");
                Console.WriteLine($"Unique Name:    {details.UniqueName}");
                Console.WriteLine($"App ID:         {details.AppModuleId}");
                Console.WriteLine($"Description:    {details.Description ?? "(none)"}");
                Console.WriteLine($"Publisher:      {details.PublisherName ?? "(unknown)"}");
                Console.WriteLine();
                Console.WriteLine("Components:");
                Console.WriteLine($"  Entities:     {details.EntityCount}");
                Console.WriteLine($"  Forms:        {details.ExplicitFormCount} (explicit)");
                Console.WriteLine($"  Views:        {details.ExplicitViewCount} (explicit)");
                Console.WriteLine($"  Charts:       {details.ExplicitChartCount} (explicit)");
                Console.WriteLine($"  Sitemap:      {details.SitemapCount}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"getting app '{appName}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private sealed record AppDetailsOutput(ModelDrivenAppDetails Details)
    {
        [JsonPropertyName("appModuleId")]
        public Guid AppModuleId => Details.AppModuleId;

        [JsonPropertyName("displayName")]
        public string DisplayName => Details.DisplayName;

        [JsonPropertyName("uniqueName")]
        public string UniqueName => Details.UniqueName;

        [JsonPropertyName("description")]
        public string? Description => Details.Description;

        [JsonPropertyName("publisherName")]
        public string? PublisherName => Details.PublisherName;

        [JsonPropertyName("entityCount")]
        public int EntityCount => Details.EntityCount;

        [JsonPropertyName("explicitFormCount")]
        public int ExplicitFormCount => Details.ExplicitFormCount;

        [JsonPropertyName("explicitViewCount")]
        public int ExplicitViewCount => Details.ExplicitViewCount;

        [JsonPropertyName("explicitChartCount")]
        public int ExplicitChartCount => Details.ExplicitChartCount;

        [JsonPropertyName("sitemapCount")]
        public int SitemapCount => Details.SitemapCount;
    }
}
