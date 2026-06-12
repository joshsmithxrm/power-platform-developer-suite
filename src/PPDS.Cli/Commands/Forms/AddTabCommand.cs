using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Forms;

namespace PPDS.Cli.Commands.Forms;

/// <summary>
/// Add a tab to a Main form.
/// </summary>
public static class AddTabCommand
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
            Description = "Label for the new tab",
            Required = true
        };

        var showLabelOption = new Option<bool?>("--show-label")
        {
            Description = "Whether to show the tab label"
        };

        var expandedOption = new Option<bool?>("--expanded")
        {
            Description = "Whether the tab is expanded by default"
        };

        var visibleOption = new Option<bool?>("--visible")
        {
            Description = "Whether the tab is visible"
        };

        var availableOnPhoneOption = new Option<bool?>("--available-on-phone")
        {
            Description = "Whether the tab is available on phone"
        };

        var columnsOption = new Option<int?>("--columns")
        {
            Description = "Number of columns in the tab (1, 2, or 3)"
        };

        var publishOption = new Option<bool>("--publish")
        {
            Description = "Publish customizations after writing",
            DefaultValueFactory = _ => false
        };

        var solutionOption = new Option<string?>("--solution")
        {
            Description = "Solution unique name to associate the change with"
        };

        var command = new Command("add-tab", "Add a tab to a Main form")
        {
            entityOption,
            formOption,
            labelOption,
            showLabelOption,
            expandedOption,
            visibleOption,
            availableOnPhoneOption,
            columnsOption,
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
            var label = parseResult.GetValue(labelOption)!;
            var showLabel = parseResult.GetValue(showLabelOption);
            var expanded = parseResult.GetValue(expandedOption);
            var visible = parseResult.GetValue(visibleOption);
            var availableOnPhone = parseResult.GetValue(availableOnPhoneOption);
            var columns = parseResult.GetValue(columnsOption);
            var publish = parseResult.GetValue(publishOption);
            var solution = parseResult.GetValue(solutionOption);
            var profile = parseResult.GetValue(FormsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(FormsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(entity, form, label, showLabel, expanded, visible, availableOnPhone, columns, publish, solution, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string entity,
        string form,
        string label,
        bool? showLabel,
        bool? expanded,
        bool? visible,
        bool? availableOnPhone,
        int? columns,
        bool publish,
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

            var formService = serviceProvider.GetRequiredService<IFormService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var request = new AddTabRequest(
                entity,
                form,
                label,
                showLabel ?? true,
                expanded ?? true,
                visible ?? true,
                availableOnPhone ?? true,
                columns ?? 1,
                solution,
                publish);

            var result = await formService.AddTabAsync(request, null, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new { tabId = result.TabId, tabLabel = result.TabLabel });
            }
            else
            {
                Console.Error.WriteLine($"Tab '{result.TabLabel}' added (ID: {result.TabId})");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "adding tab", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
