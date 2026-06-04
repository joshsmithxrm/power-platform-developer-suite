using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Views;

namespace PPDS.Cli.Commands.Views;

/// <summary>
/// List all views for an entity.
/// </summary>
public static class ListCommand
{
    public static Command Create()
    {
        var command = new Command("list", "List all views for an entity")
        {
            ViewsCommandGroup.ProfileOption,
            ViewsCommandGroup.EnvironmentOption,
            ViewsCommandGroup.EntityOption,
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var profile = parseResult.GetValue(ViewsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ViewsCommandGroup.EnvironmentOption);
            var entity = parseResult.GetValue(ViewsCommandGroup.EntityOption)!;
            var globalOptions = GlobalOptions.GetValues(parseResult);
            var writer = ServiceFactory.CreateOutputWriter(globalOptions);

            try
            {
                await using var sp = await ProfileServiceFactory.CreateFromProfilesAsync(
                    profile, environment,
                    globalOptions.Verbose, globalOptions.Debug,
                    ProfileServiceFactory.DefaultDeviceCodeCallback,
                    cancellationToken);

                if (!globalOptions.IsJsonMode)
                {
                    ConsoleHeader.WriteConnectedAs(sp.GetRequiredService<ResolvedConnectionInfo>());
                    Console.Error.WriteLine();
                }

                var service = sp.GetRequiredService<IViewService>();
                var result = await service.ListAsync(entity, cancellationToken: cancellationToken);

                if (globalOptions.IsJsonMode)
                {
                    writer.WriteSuccess(new ListOutput
                    {
                        Views = result.Items.Select(v => new ViewInfoOutput
                        {
                            Id = v.Id,
                            Name = v.Name,
                            QueryType = v.QueryType,
                            QueryTypeLabel = v.QueryTypeLabel,
                            IsManaged = v.IsManaged,
                            ModifiedOn = v.ModifiedOn
                        }).ToList()
                    });
                }
                else
                {
                    if (result.Items.Count == 0)
                    {
                        Console.Error.WriteLine($"0 views found for '{entity}'.");
                    }
                    else
                    {
                        Console.WriteLine($"{"Name",-50} {"Type",-25} {"Managed",-8} {"Modified",-20}");
                        Console.WriteLine(new string('-', 110));
                        foreach (var v in result.Items)
                        {
                            var modified = v.ModifiedOn?.ToString("g") ?? "-";
                            Console.WriteLine($"{Truncate(v.Name, 50),-50} {v.QueryTypeLabel,-25} {v.IsManaged,-8} {modified,-20}");
                        }
                        Console.Error.WriteLine();
                        Console.Error.WriteLine($"Total: {result.TotalCount} view(s)");
                    }
                }

                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                var error = ExceptionMapper.Map(ex, context: "listing views", debug: globalOptions.Debug);
                writer.WriteError(error);
                return ExceptionMapper.ToExitCode(ex);
            }
        });

        return command;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 3)] + "...";

    private sealed class ListOutput
    {
        [JsonPropertyName("views")]
        public List<ViewInfoOutput> Views { get; set; } = [];
    }

    private sealed class ViewInfoOutput
    {
        [JsonPropertyName("id")] public Guid Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("queryType")] public int QueryType { get; set; }
        [JsonPropertyName("queryTypeLabel")] public string QueryTypeLabel { get; set; } = "";
        [JsonPropertyName("isManaged")] public bool IsManaged { get; set; }
        [JsonPropertyName("modifiedOn")] public DateTime? ModifiedOn { get; set; }
    }
}
