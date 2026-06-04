using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Forms;

namespace PPDS.Cli.Commands.Forms;

/// <summary>
/// Add a sub-grid control to a section on a Main form.
/// </summary>
public static class AddSubgridCommand
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

        var sectionOption = new Option<string>("--section")
        {
            Description = "Section label to add the sub-grid to",
            Required = true
        };

        var labelOption = new Option<string>("--label")
        {
            Description = "Display label for the sub-grid",
            Required = true
        };

        var targetEntityOption = new Option<string>("--target-entity")
        {
            Description = "Logical name of the entity displayed in the sub-grid",
            Required = true
        };

        var defaultViewOption = new Option<Guid>("--default-view")
        {
            Description = "GUID of the default view (savedqueries record) to display in the sub-grid",
            Required = true
        };

        var relationshipOption = new Option<string?>("--relationship")
        {
            Description = "Relationship schema name used to filter the sub-grid records"
        };

        var hideLabelOption = new Option<bool?>("--hide-label")
        {
            Description = "Whether to hide the sub-grid label"
        };

        var hideOnPhoneOption = new Option<bool?>("--hide-on-phone")
        {
            Description = "Whether to hide the sub-grid on phone"
        };

        var maxRowsOption = new Option<int?>("--max-rows")
        {
            Description = "Maximum number of rows to display in the sub-grid"
        };

        var hideSearchBoxOption = new Option<bool?>("--hide-search-box")
        {
            Description = "Whether to hide the search box in the sub-grid"
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

        var command = new Command("add-subgrid", "Add a sub-grid control to a section on a Main form")
        {
            entityOption,
            formOption,
            sectionOption,
            labelOption,
            targetEntityOption,
            defaultViewOption,
            relationshipOption,
            hideLabelOption,
            hideOnPhoneOption,
            maxRowsOption,
            hideSearchBoxOption,
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
            var section = parseResult.GetValue(sectionOption)!;
            var label = parseResult.GetValue(labelOption)!;
            var targetEntity = parseResult.GetValue(targetEntityOption)!;
            var defaultView = parseResult.GetValue(defaultViewOption);
            var relationship = parseResult.GetValue(relationshipOption);
            var hideLabel = parseResult.GetValue(hideLabelOption);
            var hideOnPhone = parseResult.GetValue(hideOnPhoneOption);
            var maxRows = parseResult.GetValue(maxRowsOption);
            var hideSearchBox = parseResult.GetValue(hideSearchBoxOption);
            var publish = parseResult.GetValue(publishOption);
            var solution = parseResult.GetValue(solutionOption);
            var profile = parseResult.GetValue(FormsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(FormsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(entity, form, section, label, targetEntity, defaultView, relationship, hideLabel, hideOnPhone, maxRows, hideSearchBox, publish, solution, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string entity,
        string form,
        string section,
        string label,
        string targetEntity,
        Guid defaultView,
        string? relationship,
        bool? hideLabel,
        bool? hideOnPhone,
        int? maxRows,
        bool? hideSearchBox,
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

            var request = new AddSubgridRequest(
                entity,
                form,
                section,
                label,
                targetEntity,
                defaultView,
                relationship,
                hideLabel ?? false,
                hideOnPhone ?? false,
                maxRows ?? 5,
                hideSearchBox ?? false,
                solution,
                publish);

            await formService.AddSubgridAsync(request, null, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new { section, label, targetEntity, message = "Sub-grid added." });
            }
            else
            {
                Console.Error.WriteLine($"Sub-grid '{label}' added to section '{section}'.");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "adding sub-grid to form", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
