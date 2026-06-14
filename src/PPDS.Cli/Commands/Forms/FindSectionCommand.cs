using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Forms;

namespace PPDS.Cli.Commands.Forms;

/// <summary>
/// Find a section in a form by label and return its ID and parent tab.
/// </summary>
public static class FindSectionCommand
{
    public static Command Create()
    {
        var entityOption = new Option<string>("--entity")
        {
            Description = "Entity logical name",
            Required = true
        };

        var formOption = new Option<string>("--form")
        {
            Description = "Name or ID of the form",
            Required = true
        };

        var labelOption = new Option<string>("--label")
        {
            Description = "Label or name of the section to find",
            Required = true
        };

        var command = new Command("find-section", "Find a section in a form by label and return its ID and parent tab")
        {
            entityOption,
            formOption,
            labelOption,
            FormsCommandGroup.ProfileOption,
            FormsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var entity = parseResult.GetValue(entityOption)!;
            var form = parseResult.GetValue(formOption)!;
            var label = parseResult.GetValue(labelOption)!;
            var profile = parseResult.GetValue(FormsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(FormsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(entity, form, label, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string entity,
        string form,
        string label,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken ct)
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
                ct);

            var formService = serviceProvider.GetRequiredService<IFormService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var request = new FindSectionRequest(entity, form, SectionLabel: label);
            var result = await formService.FindSectionAsync(request, ct);

            if (result is null)
            {
                Console.Error.WriteLine($"Section '{label}' not found.");
                return ExitCodes.Failure;
            }

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new FindSectionResult
                {
                    SectionId = result.SectionId,
                    SectionLabel = result.SectionLabel,
                    TabLabel = result.TabLabel,
                    TabId = result.TabId
                });
            }
            else
            {
                Console.Error.WriteLine($"Section '{result.SectionLabel}' found in tab '{result.TabLabel}' (ID: {result.SectionId})");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "finding section", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class FindSectionResult
    {
        [JsonPropertyName("sectionId")]
        public Guid SectionId { get; set; }

        [JsonPropertyName("sectionLabel")]
        public string SectionLabel { get; set; } = string.Empty;

        [JsonPropertyName("tabLabel")]
        public string TabLabel { get; set; } = string.Empty;

        [JsonPropertyName("tabId")]
        public Guid TabId { get; set; }
    }

    #endregion
}
