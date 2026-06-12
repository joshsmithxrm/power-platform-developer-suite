using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Forms;

namespace PPDS.Cli.Commands.Forms;

/// <summary>
/// Get detailed structure of a specific Dataverse systemform.
/// </summary>
public static class GetCommand
{
    public static Command Create()
    {
        var entityOption = new Option<string>("--entity")
        {
            Description = "Logical name of the entity",
            Required = true
        };

        var formOption = new Option<string>("--form")
        {
            Description = "Name or ID of the form",
            Required = true
        };

        var unpublishedOption = new Option<bool>("--unpublished")
        {
            Description = "Show the unpublished (latest draft) form instead of the published version"
        };

        var command = new Command("get", "Get the structure of a specific Dataverse systemform")
        {
            entityOption,
            formOption,
            unpublishedOption,
            FormsCommandGroup.ProfileOption,
            FormsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var entity = parseResult.GetValue(entityOption)!;
            var form = parseResult.GetValue(formOption)!;
            var unpublished = parseResult.GetValue(unpublishedOption);
            var profile = parseResult.GetValue(FormsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(FormsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(entity, form, unpublished, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string entity,
        string form,
        bool unpublished,
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

            var formDetail = await formService.GetAsync(entity, form, unpublished, cancellationToken);

            if (formDetail == null)
            {
                var error = new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Form '{form}' not found for entity '{entity}'.",
                    null,
                    form);
                writer.WriteError(error);
                return ExitCodes.NotFoundError;
            }

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(formDetail);
            }
            else
            {
                var managedLabel = formDetail.IsManaged ? "Managed" : "Unmanaged";
                Console.WriteLine($"Form: {formDetail.Name} ({formDetail.FormTypeName}) [{formDetail.Id}]  {managedLabel}");

                foreach (var tab in formDetail.Tabs)
                {
                    Console.WriteLine($"  Tab: {tab.Label}");

                    foreach (var section in tab.Sections)
                    {
                        Console.WriteLine($"    Section: {section.Label}");

                        foreach (var field in section.Fields)
                        {
                            var label = string.IsNullOrEmpty(field.Label) ? "" : $" ({field.Label})";
                            Console.WriteLine($"      {field.LogicalName}{label}");
                        }

                        foreach (var subgrid in section.Subgrids)
                        {
                            Console.WriteLine($"      [SubGrid] {subgrid.Label} → {subgrid.TargetEntity}");
                        }
                    }
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"getting form '{form}' for entity '{entity}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models
    // FormDetail is serialized directly — no separate output model needed.
    #endregion
}
