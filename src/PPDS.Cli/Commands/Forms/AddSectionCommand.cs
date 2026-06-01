using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Forms;

namespace PPDS.Cli.Commands.Forms;

/// <summary>
/// Add a section to a tab on a Main form.
/// </summary>
public static class AddSectionCommand
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
            Description = "Form name",
            Required = true
        };

        var tabOption = new Option<string>("--tab")
        {
            Description = "Parent tab label",
            Required = true
        };

        var labelOption = new Option<string>("--label")
        {
            Description = "Section label",
            Required = true
        };

        var showLabelOption = new Option<bool?>("--show-label")
        {
            Description = "Show the section label"
        };

        var columnsOption = new Option<int?>("--columns")
        {
            Description = "Number of columns (1 or 2)"
        };

        var visibleOption = new Option<bool?>("--visible")
        {
            Description = "Whether the section is visible"
        };

        var availableOnPhoneOption = new Option<bool?>("--available-on-phone")
        {
            Description = "Whether the section is available on phone"
        };

        var publishOption = new Option<bool>("--publish")
        {
            Description = "Publish customizations after adding the section",
            DefaultValueFactory = _ => false
        };

        var solutionOption = new Option<string?>("--solution")
        {
            Description = "Solution unique name to associate the change with"
        };

        var command = new Command("add-section", "Add a section to a tab on a Main form")
        {
            entityOption,
            formOption,
            tabOption,
            labelOption,
            showLabelOption,
            columnsOption,
            visibleOption,
            availableOnPhoneOption,
            publishOption,
            solutionOption,
            FormsCommandGroup.ProfileOption,
            FormsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var entity = parseResult.GetValue(entityOption)!;
            var form = parseResult.GetValue(formOption)!;
            var tab = parseResult.GetValue(tabOption)!;
            var label = parseResult.GetValue(labelOption)!;
            var showLabel = parseResult.GetValue(showLabelOption);
            var columns = parseResult.GetValue(columnsOption);
            var visible = parseResult.GetValue(visibleOption);
            var availableOnPhone = parseResult.GetValue(availableOnPhoneOption);
            var publish = parseResult.GetValue(publishOption);
            var solution = parseResult.GetValue(solutionOption);
            var profile = parseResult.GetValue(FormsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(FormsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(entity, form, tab, label, showLabel, columns, visible,
                availableOnPhone, publish, solution, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string entity,
        string form,
        string tab,
        string label,
        bool? showLabel,
        int? columns,
        bool? visible,
        bool? availableOnPhone,
        bool publish,
        string? solution,
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

            var request = new AddSectionRequest(
                entity,
                form,
                TabLabel: tab,
                Label: label,
                ShowLabel: showLabel ?? true,
                Columns: columns ?? 1,
                Visible: visible ?? true,
                AvailableOnPhone: availableOnPhone ?? true,
                SolutionUniqueName: solution,
                Publish: publish);

            var result = await formService.AddSectionAsync(request, null, ct);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new AddSectionResult
                {
                    SectionId = result.SectionId,
                    SectionLabel = result.SectionLabel,
                    TabLabel = result.TabLabel
                });
            }
            else
            {
                Console.Error.WriteLine($"Section '{result.SectionLabel}' added to tab '{result.TabLabel}' (ID: {result.SectionId})");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "adding section", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class AddSectionResult
    {
        [JsonPropertyName("sectionId")]
        public Guid SectionId { get; set; }

        [JsonPropertyName("sectionLabel")]
        public string SectionLabel { get; set; } = string.Empty;

        [JsonPropertyName("tabLabel")]
        public string TabLabel { get; set; } = string.Empty;
    }

    #endregion
}
