using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Forms;

namespace PPDS.Cli.Commands.Forms;

/// <summary>
/// Reorder fields in a section. Provide the authoritative ordered list; fields not in the list are removed.
/// </summary>
public static class ReorderFieldsCommand
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
            Description = "Section label containing the fields to reorder",
            Required = true
        };

        var fieldsOption = new Option<string>("--fields")
        {
            Description = "Comma-separated ordered list of field logical names. Fields not in the list are removed.",
            Required = true
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

        var command = new Command("reorder-fields",
            "Reorder fields in a section. Provide the authoritative ordered list; fields not in the list are removed.")
        {
            entityOption,
            formOption,
            sectionOption,
            fieldsOption,
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
            var fields = parseResult.GetValue(fieldsOption)!;
            var publish = parseResult.GetValue(publishOption);
            var solution = parseResult.GetValue(solutionOption);
            var profile = parseResult.GetValue(FormsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(FormsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(entity, form, section, fields, publish, solution, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string entity,
        string form,
        string section,
        string fields,
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
            var fieldNames = fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (fieldNames.Length == 0)
            {
                Console.Error.WriteLine("At least one field is required.");
                return ExitCodes.InvalidArguments;
            }

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

            var request = new ReorderFieldsRequest(entity, form, section, fieldNames, solution, publish);
            await formService.ReorderFieldsAsync(request, null, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new { section, fields = fieldNames, message = "Fields reordered." });
            }
            else
            {
                Console.Error.WriteLine($"Fields in section '{section}' reordered.");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "reordering fields in form", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
