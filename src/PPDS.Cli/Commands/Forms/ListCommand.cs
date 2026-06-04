using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Forms;

namespace PPDS.Cli.Commands.Forms;

/// <summary>
/// List Dataverse systemforms for an entity.
/// </summary>
public static class ListCommand
{
    public static Command Create()
    {
        var entityOption = new Option<string>("--entity")
        {
            Description = "Logical name of the entity",
            Required = true
        };

        var command = new Command("list", "List systemforms for a Dataverse entity")
        {
            entityOption,
            FormsCommandGroup.ProfileOption,
            FormsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var entity = parseResult.GetValue(entityOption)!;
            var profile = parseResult.GetValue(FormsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(FormsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(entity, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string entity,
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

            var formsResult = await formService.ListAsync(entity, cancellationToken);
            var forms = formsResult.Items;

            if (globalOptions.IsJsonMode)
            {
                var output = forms.Select(f => new FormListItem
                {
                    Id = f.Id,
                    Name = f.Name,
                    FormType = f.FormType,
                    FormTypeName = f.FormTypeName,
                    IsManaged = f.IsManaged,
                    Description = f.Description
                }).ToList();

                writer.WriteSuccess(output);
            }
            else
            {
                if (forms.Count == 0)
                {
                    Console.WriteLine($"No forms found for entity '{entity}'.");
                }
                else
                {
                    Console.WriteLine($"Found {forms.Count} form(s) for entity '{entity}':");
                    Console.WriteLine();

                    foreach (var form in forms)
                    {
                        var managedLabel = form.IsManaged ? "Managed" : "Unmanaged";
                        Console.WriteLine($"  {form.Name,-40} {form.FormTypeName,-20} {form.Id}  {managedLabel}");
                    }
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"listing forms for entity '{entity}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class FormListItem
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("formType")]
        public int FormType { get; set; }

        [JsonPropertyName("formTypeName")]
        public string FormTypeName { get; set; } = string.Empty;

        [JsonPropertyName("isManaged")]
        public bool IsManaged { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }

    #endregion
}
