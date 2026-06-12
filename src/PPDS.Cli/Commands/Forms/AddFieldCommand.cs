using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Forms;

namespace PPDS.Cli.Commands.Forms;

/// <summary>
/// Add one or more fields to a section on a Main form.
/// </summary>
public static class AddFieldCommand
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

        var sectionOption = new Option<string>("--section")
        {
            Description = "Label or ID of the section to add the field(s) to",
            Required = true
        };

        var fieldOption = new Option<string[]>("--field")
        {
            Description = "Field logical name to add. Repeat for multiple fields.",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = false
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

        var command = new Command("add-field",
            "Add one or more fields to a section on a Main form. classid is resolved automatically from column metadata — never specify it directly.")
        {
            entityOption,
            formOption,
            sectionOption,
            fieldOption,
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
            var fields = parseResult.GetValue(fieldOption) ?? [];
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
        string[] fields,
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
            if (fields.Length == 0)
            {
                Console.Error.WriteLine("At least one --field is required.");
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

            var request = new AddFieldRequest(entity, form, section, fields, solution, publish);
            await formService.AddFieldAsync(request, null, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new { section, fields, message = $"Added {fields.Length} field(s)." });
            }
            else
            {
                Console.Error.WriteLine($"Added {fields.Length} field(s) to section '{section}'.");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "adding field(s) to form", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
