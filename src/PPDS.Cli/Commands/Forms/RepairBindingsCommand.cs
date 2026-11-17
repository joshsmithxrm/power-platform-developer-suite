using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Forms;

namespace PPDS.Cli.Commands.Forms;

/// <summary>
/// Rewrite control bindings that differ from a column's logical name only by case (#1527).
/// </summary>
public static class RepairBindingsCommand
{
    /// <summary>Creates the 'repair-bindings' command.</summary>
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
            Description = "Name or ID of the Main form",
            Required = true
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Report the bindings that would be repaired without writing, acknowledging the environment, associating or publishing",
            DefaultValueFactory = _ => false
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

        var command = new Command("repair-bindings",
            "Rewrite each control binding (datafieldname) that differs from a column's logical name only by case to the exact logical name, " +
            "in tab sections, header and footer. Only the binding changes: control ids, cells and labels are kept. " +
            "Bindings that match no column, and control ids that differ by case, are reported and left unchanged.")
        {
            entityOption,
            formOption,
            dryRunOption,
            publishOption,
            solutionOption,
            FormsCommandGroup.ProfileOption,
            FormsCommandGroup.EnvironmentOption,
            FormsCommandGroup.ConfirmEnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var request = new RepairBindingsRequest(
                parseResult.GetValue(entityOption)!,
                parseResult.GetValue(formOption)!,
                parseResult.GetValue(dryRunOption),
                parseResult.GetValue(solutionOption),
                parseResult.GetValue(publishOption));
            return await ExecuteAsync(request,
                parseResult.GetValue(FormsCommandGroup.ProfileOption),
                parseResult.GetValue(FormsCommandGroup.EnvironmentOption),
                parseResult.GetValue(FormsCommandGroup.ConfirmEnvironmentOption),
                GlobalOptions.GetValues(parseResult), cancellationToken, providerFactory);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        RepairBindingsRequest request,
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
            if (request.DryRun && (request.Publish || request.SolutionUniqueName is not null))
            {
                Console.Error.WriteLine("--dry-run cannot be combined with --publish or --solution.");
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

            if (!request.DryRun)
                await FormsCommandGroup.ConfirmEnvironmentAsync(serviceProvider, confirmEnvironment, cancellationToken);
            var formService = serviceProvider.GetRequiredService<IFormService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var result = await formService.RepairBindingsAsync(request, null, cancellationToken);
            var message = DescribeResult(result);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new
                {
                    entity = request.EntityLogicalName,
                    form = result.FormName,
                    formId = result.FormId,
                    dryRun = result.DryRun,
                    message,
                    repairedCount = result.Repaired.Count,
                    unchangedCount = result.Unchanged.Count,
                    formXmlChanged = result.FormXmlChanged,
                    solutionAssociationRequested = result.SolutionAssociationRequested,
                    publicationRequested = result.PublicationRequested,
                    repaired = result.Repaired,
                    unchanged = result.Unchanged
                });
            }
            else
            {
                Console.Error.WriteLine(message);
                foreach (var item in result.Repaired)
                    Console.Error.WriteLine($"  {(result.DryRun ? "Would repair" : "Repaired")}: control '{item.ControlId ?? "(no id)"}' ({item.Location}) " +
                        $"binding '{item.OldBinding}' -> '{item.NewBinding}'.");
                foreach (var item in result.Unchanged)
                    Console.Error.WriteLine("  " + DescribeUnchanged(request.EntityLogicalName, item));
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "repairing form bindings", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static string DescribeResult(RepairBindingsResult result)
    {
        var messages = new List<string>();
        if (result.DryRun)
            messages.Add(result.Repaired.Count == 0
                ? $"Dry run: no binding on form '{result.FormName}' differs from its column's logical name only by case. Nothing was written."
                : $"Dry run: {result.Repaired.Count} binding(s) on form '{result.FormName}' would be repaired. Nothing was written.");
        else
            messages.Add(result.Repaired.Count == 0
                ? $"No binding on form '{result.FormName}' differs from its column's logical name only by case. The form XML was not updated."
                : $"Repaired {result.Repaired.Count} binding(s) on form '{result.FormName}'.");
        if (result.FormXmlChanged && !result.PublicationRequested)
            messages.Add("The change is saved unpublished; publish the table (or rerun with --publish) to publish it.");
        if (result.Unchanged.Count > 0)
            messages.Add($"{result.Unchanged.Count} control(s) reported and left unchanged.");
        if (result.SolutionAssociationRequested) messages.Add("The explicit solution association request completed.");
        if (result.PublicationRequested) messages.Add("The explicit publication request completed.");
        return string.Join(" ", messages);
    }

    private static string DescribeUnchanged(string entity, UnchangedBinding item)
    {
        var control = $"control '{item.ControlId ?? "(no id)"}' ({item.Location})";
        return item.Reason == BindingRepairReasons.NoMatchingColumn
            ? $"Left unchanged: {control} is bound to '{item.Binding}', which matches no column on '{entity}'."
            : $"Left unchanged: {control} has an id that differs from the column's logical name '{item.CanonicalName}' by case; control ids are not rewritten.";
    }
}
