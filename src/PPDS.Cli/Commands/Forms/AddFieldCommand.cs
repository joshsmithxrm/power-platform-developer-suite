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
    public static Command Create() => Create(null);

    internal static Command Create(
        Func<string?, string?, GlobalOptionValues, CancellationToken, Task<ServiceProvider>>? providerFactory)
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
            Description = "Label or name of the section to add the field(s) to",
            Required = true
        };

        var fieldOption = new Option<string[]>("--field")
        {
            Description = "Field logical name to add. Repeat for multiple fields; existing bindings in this section are kept.",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = false
        };

        var publishOption = new Option<bool>("--publish")
        {
            Description = "Publish all pending customizations for this table after writing",
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
            FormsCommandGroup.EnvironmentOption,
            FormsCommandGroup.ConfirmEnvironmentOption
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
            var confirmEnvironment = parseResult.GetValue(FormsCommandGroup.ConfirmEnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(entity, form, section, fields, publish, solution, profile, environment, confirmEnvironment, globalOptions, cancellationToken, providerFactory);
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
        string? confirmEnvironment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken,
        Func<string?, string?, GlobalOptionValues, CancellationToken, Task<ServiceProvider>>? providerFactory)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            if (fields.Length == 0)
            {
                Console.Error.WriteLine("At least one --field is required.");
                return ExitCodes.InvalidArguments;
            }

            await using var serviceProvider = providerFactory is null
                ? await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken)
                : await providerFactory(profile, environment, globalOptions, cancellationToken);

            await FormsCommandGroup.ConfirmEnvironmentAsync(serviceProvider, confirmEnvironment, cancellationToken);
            var formService = serviceProvider.GetRequiredService<IFormService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var request = new AddFieldRequest(entity, form, section, fields, solution, publish);
            var result = await formService.AddFieldsWithResultAsync(request, null, cancellationToken);
            var message = DescribeResult(section, result);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new { section, fields, message, outcomeKnown = result.CountsKnown,
                    addedCount = result.CountsKnown ? result.AddedFields!.Count : (int?)null,
                    alreadyPresentCount = result.CountsKnown ? result.AlreadyPresentFields!.Count : (int?)null,
                    duplicateRequestCount = result.CountsKnown ? result.DuplicateRequests!.Count : (int?)null,
                    addedFields = result.AddedFields, alreadyPresentFields = result.AlreadyPresentFields,
                    duplicateRequests = result.DuplicateRequests, formXmlChanged = result.FormXmlChanged,
                    solutionAssociationRequested = result.SolutionAssociationRequested, publicationRequested = result.PublicationRequested });
            }
            else
            {
                Console.Error.WriteLine(message);
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

    private static string DescribeResult(string section, AddFieldsResult result)
    {
        if (!result.CountsKnown) return "Field request completed; this service did not report addition counts.";
        var messages = new List<string>();
        if (result.AddedFields!.Count > 0)
            messages.Add($"Added {result.AddedFields.Count} field(s) to section '{section}'.");
        if (result.AlreadyPresentFields!.Count > 0)
            messages.Add($"Already present in section '{section}': {string.Join(", ", result.AlreadyPresentFields)}.");
        if (result.DuplicateRequests!.Count > 0)
            messages.Add($"Ignored {result.DuplicateRequests.Count} duplicate field request(s): {string.Join(", ", result.DuplicateRequests)}.");
        if (result.SolutionAssociationRequested) messages.Add("The explicit solution association request completed.");
        if (result.PublicationRequested) messages.Add("The explicit publication request completed.");
        return messages.Count == 0 ? "Field request completed." : string.Join(" ", messages);
    }
}
