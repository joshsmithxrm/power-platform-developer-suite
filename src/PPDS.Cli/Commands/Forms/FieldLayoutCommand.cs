using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Forms;

namespace PPDS.Cli.Commands.Forms;

internal static class FieldLayoutCommand
{
    internal static Command Create(bool move, Func<string?, string?, GlobalOptionValues, CancellationToken, Task<ServiceProvider>>? factory)
    {
        var entity = new Option<string>("--entity") { Required = true, Description = "Entity logical name" };
        var form = new Option<string>("--form") { Required = true, Description = "Form name or ID" };
        var section = new Option<string>("--section") { Required = true, Description = "Section label, name or ID" };
        var fields = new Option<string>("--fields") { Required = true, Description = "Complete comma-separated permutation of existing section fields; no additions or removals" };
        var field = new Option<string>("--field") { Required = true, Description = "Existing field logical name" };
        var from = new Option<string>("--from-section") { Required = true, Description = "Source section label, name or ID" };
        var to = new Option<string>("--to-section") { Required = true, Description = "Target section label, name or ID" };
        var before = new Option<string?>("--before-field") { Description = "Insert before this unique target field's standalone row; default appends a cross-section move" };
        var solution = new Option<string?>("--solution") { Description = "Solution unique name to associate after the layout operation" };
        var publish = new Option<bool>("--publish") { Description = "Publish all pending table customizations after the layout operation" };
        var command = new Command(move ? "move-field" : "reorder-fields-strict", move
            ? "Move an existing field cell without copying or replacing controls."
            : "Reorder all existing section fields without adding or removing cells.")
        { entity, form, solution, publish, FormsCommandGroup.ProfileOption, FormsCommandGroup.EnvironmentOption, FormsCommandGroup.ConfirmEnvironmentOption };
        if (move) { command.Options.Add(field); command.Options.Add(from); command.Options.Add(to); command.Options.Add(before); }
        else { command.Options.Add(section); command.Options.Add(fields); }
        GlobalOptions.AddToCommand(command);
        command.SetAction(async (parsed, ct) =>
        {
            var options = GlobalOptions.GetValues(parsed);
            var writer = ServiceFactory.CreateOutputWriter(options);
            try
            {
                string[] names = move ? [] : parsed.GetValue(fields)!.Split(',', StringSplitOptions.TrimEntries);
                if (!move && (names.Length == 0 || names.Any(string.IsNullOrWhiteSpace)))
                    throw new PpdsValidationException("fields", "Provide a complete nonempty field permutation.");
                await using var provider = factory is null
                    ? await ProfileServiceFactory.CreateFromProfilesAsync(parsed.GetValue(FormsCommandGroup.ProfileOption),
                        parsed.GetValue(FormsCommandGroup.EnvironmentOption), options.Verbose, options.Debug,
                        ProfileServiceFactory.DefaultDeviceCodeCallback, ct)
                    : await factory(parsed.GetValue(FormsCommandGroup.ProfileOption), parsed.GetValue(FormsCommandGroup.EnvironmentOption), options, ct);
                await FormsCommandGroup.ConfirmEnvironmentAsync(provider, parsed.GetValue(FormsCommandGroup.ConfirmEnvironmentOption), ct);
                if (!options.IsJsonMode) ConsoleHeader.WriteConnectedAs(provider.GetRequiredService<ResolvedConnectionInfo>());
                var service = provider.GetRequiredService<IFormService>();
                if (move)
                    await service.MoveFieldAsync(new(parsed.GetValue(entity)!, parsed.GetValue(form)!, parsed.GetValue(field)!,
                        parsed.GetValue(from)!, parsed.GetValue(to)!, parsed.GetValue(before), parsed.GetValue(solution), parsed.GetValue(publish)), ct: ct);
                else
                    await service.ReorderFieldsStrictAsync(new(parsed.GetValue(entity)!, parsed.GetValue(form)!, parsed.GetValue(section)!,
                        names, parsed.GetValue(solution), parsed.GetValue(publish)), ct: ct);
                // Completion does not invent a write count: already-satisfied layouts may be no-ops.
                var message = move ? "Field move request completed." : "Strict field reorder request completed.";
                if (options.IsJsonMode)
                {
                    if (move) writer.WriteSuccess(new { field = parsed.GetValue(field), fromSection = parsed.GetValue(from), toSection = parsed.GetValue(to), beforeField = parsed.GetValue(before), message });
                    else writer.WriteSuccess(new { section = parsed.GetValue(section), fields = names, message });
                }
                else Console.Error.WriteLine(message);
                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                writer.WriteError(ExceptionMapper.Map(ex, context: move ? "moving field in form" : "strictly reordering fields in form", debug: options.Debug));
                return ExceptionMapper.ToExitCode(ex);
            }
        });
        return command;
    }
}
