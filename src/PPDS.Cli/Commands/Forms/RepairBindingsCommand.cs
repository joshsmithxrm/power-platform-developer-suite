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
            "Rewrite every control binding (datafieldname) on a Main form that differs from a column's logical name only by case to the " +
            "exact logical name, in tab sections, tab headers/footers and the form header/footer. Only those attribute values change: " +
            "the rest of the stored form XML text (control ids, cells, labels, whitespace) is sent back as it was, and the form is read " +
            "back to check that each rewritten control now holds the logical name. Reported and left unchanged: bindings that match no " +
            "column or several, control ids that differ by case, controls with a relationship or isunbound, controls outside the form " +
            "layout, and case-variant names in custom-control parameters, form events and hidden controls.")
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
                parseResult.GetValue(publishOption),
                parseResult.GetValue(FormsCommandGroup.ConfirmEnvironmentOption));
            return await ExecuteAsync(request,
                parseResult.GetValue(FormsCommandGroup.ProfileOption),
                parseResult.GetValue(FormsCommandGroup.EnvironmentOption),
                GlobalOptions.GetValues(parseResult), cancellationToken, providerFactory);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        RepairBindingsRequest request,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken,
        Func<string?, string?, GlobalOptionValues, CancellationToken, Task<ServiceProvider>>? providerFactory)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            // Fast path before connecting; the service enforces the same rule for every caller.
            if (request.DryRun && (request.Publish || request.SolutionUniqueName is not null))
                throw new PpdsValidationException("dry-run", "--dry-run cannot be combined with --publish or --solution.");

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
                await FormsCommandGroup.ConfirmEnvironmentAsync(serviceProvider, request.ConfirmEnvironment, cancellationToken);
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
                    readBackVerified = result.ReadBackVerified,
                    solutionAssociationRequested = result.SolutionAssociationRequested,
                    publicationRequested = result.PublicationRequested,
                    repaired = result.Repaired,
                    unchanged = result.Unchanged,
                    readBackRepairable = result.ReadBackRepairable
                });
            }
            else
            {
                Console.Error.WriteLine(message);
                foreach (var item in result.Repaired)
                    Console.Error.WriteLine($"  {(result.DryRun ? "Would repair" : "Repaired")}: control '{item.ControlId ?? "(no id)"}' ({item.Location}) " +
                        $"binding '{item.OldBinding}' -> '{item.NewBinding}'." +
                        (item.ControlIdCaseDiffers ? " Its id also differs from the logical name by case and was not changed." : ""));
                foreach (var item in result.Unchanged)
                    Console.Error.WriteLine("  " + DescribeUnchanged(request.EntityLogicalName, item));
                foreach (var item in result.ReadBackRepairable)
                    Console.Error.WriteLine($"  Read back as repairable: control '{item.ControlId ?? "(no id)"}' ({item.Location}) binding '{item.OldBinding}'; run the command again to repair it.");
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
        var remaining = result.Unchanged.Count(u => BindingRepairReasons.IsRemainingCaseVariant(u.Reason));
        var noMatch = result.Unchanged.Count - remaining;
        if (result.DryRun)
            messages.Add(result.Repaired.Count == 0
                ? $"Dry run: no control binding on form '{result.FormName}' would be repaired. Nothing was written."
                : $"Dry run: {result.Repaired.Count} control binding(s) on form '{result.FormName}' would be repaired. Nothing was written.");
        else
            messages.Add(result.Repaired.Count == 0
                ? $"No control binding on form '{result.FormName}' needed repair. The form XML was not updated."
                : $"Repaired {result.Repaired.Count} control binding(s) on form '{result.FormName}': the update was submitted and the form, read back, " +
                  "shows each rewritten control with the logical name.");
        if (result.FormXmlChanged && !result.PublicationRequested)
            messages.Add("The change is saved unpublished; publish the table (or rerun with --publish) to publish it.");
        if (remaining > 0)
            messages.Add($"{remaining} case-variant reference(s) remain on the form and were not changed (listed below).");
        if (result.ReadBackRepairable.Count > 0)
            messages.Add($"The form read back shows {result.ReadBackRepairable.Count} other binding(s) this command can repair (listed below); run it again.");
        if (noMatch > 0)
            messages.Add($"{noMatch} binding(s) match no column and were left unchanged.");
        if (result.SolutionAssociationRequested) messages.Add("The explicit solution association request completed.");
        if (result.PublicationRequested) messages.Add("The explicit publication request completed.");
        return string.Join(" ", messages);
    }

    private static string DescribeUnchanged(string entity, UnchangedBinding item)
    {
        var control = $"control '{item.ControlId ?? "(no id)"}' ({item.Location})";
        return item.Reason switch
        {
            BindingRepairReasons.AmbiguousColumn =>
                $"Left unchanged: {control} is bound to '{item.Binding}', which matches more than one column on '{entity}' ignoring case.",
            BindingRepairReasons.OutsideFormLayout =>
                $"Left unchanged: a control bound to '{item.Binding}' sits outside the form layout (for example inside custom-control parameters).",
            BindingRepairReasons.NoMatchingColumn =>
                $"Left unchanged: {control} is bound to '{item.Binding}', which matches no column on '{entity}'.",
            BindingRepairReasons.ControlIdCaseDiffers =>
                $"Left unchanged: {control} has an id that differs from the column's logical name '{item.CanonicalName}' by case; control ids are not rewritten.",
            BindingRepairReasons.RelatedOrUnboundControl =>
                $"Left unchanged: {control} is bound to '{item.Binding}' but has a relationship or isunbound, so its binding is not treated as a column of '{entity}'.",
            BindingRepairReasons.BindingNotPlainText =>
                $"Left unchanged: {control} is bound to '{item.Binding}', but the stored text spells it with character references; edit it with 'forms get --raw' and 'forms set-xml'.",
            BindingRepairReasons.CustomControlParameterCaseDiffers =>
                $"Left unchanged: a custom-control datafieldname parameter for {control} is '{item.Binding}', which differs from '{item.CanonicalName}' by case.",
            BindingRepairReasons.EventAttributeCaseDiffers =>
                $"Left unchanged: {item.Location} names attribute '{item.Binding}', which differs from '{item.CanonicalName}' by case.",
            BindingRepairReasons.HiddenDataCaseDiffers =>
                $"Left unchanged: hidden control '{item.ControlId ?? "(no id)"}' is bound to '{item.Binding}', which differs from '{item.CanonicalName}' by case.",
            _ => $"Left unchanged: {control} ({item.Reason})."
        };
    }
}
