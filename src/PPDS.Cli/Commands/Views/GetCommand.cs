using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Views;

namespace PPDS.Cli.Commands.Views;

/// <summary>
/// Get detailed view configuration including columns, sort, and filter.
/// </summary>
public static class GetCommand
{
    public static Command Create()
    {
        var viewOption = new Option<string>("--view", "-v")
        {
            Description = "[Required] View name or ID",
            Required = true
        };

        var unpublishedOption = new Option<bool>("--unpublished")
        {
            Description = "Show the unpublished (latest draft) view instead of the published version"
        };

        var command = new Command("get", "Get detailed view configuration including columns, sort, and filter")
        {
            ViewsCommandGroup.ProfileOption,
            ViewsCommandGroup.EnvironmentOption,
            ViewsCommandGroup.EntityOption,
            viewOption,
            unpublishedOption,
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var profile = parseResult.GetValue(ViewsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ViewsCommandGroup.EnvironmentOption);
            var entity = parseResult.GetValue(ViewsCommandGroup.EntityOption)!;
            var viewName = parseResult.GetValue(viewOption)!;
            var unpublished = parseResult.GetValue(unpublishedOption);
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
                var detail = await service.GetAsync(entity, viewName, unpublished, cancellationToken: cancellationToken);

                if (globalOptions.IsJsonMode)
                {
                    writer.WriteSuccess(new ViewDetailOutput
                    {
                        Id = detail.Id,
                        Name = detail.Name,
                        QueryType = detail.QueryType,
                        QueryTypeLabel = detail.QueryTypeLabel,
                        EntityLogicalName = detail.EntityLogicalName,
                        Columns = detail.Columns.Select(c => new ColumnOutput
                        {
                            AttributeName = c.AttributeName,
                            Width = c.Width,
                            IsRelated = c.IsRelated,
                            RelationshipAttribute = c.RelationshipAttribute,
                            RelatedEntityName = c.RelatedEntityName,
                            RelatedEntityPrimaryKeyName = c.RelatedEntityPrimaryKeyName
                        }).ToList(),
                        SortOrders = detail.SortOrders.Select(s => new SortOutput
                        {
                            AttributeName = s.AttributeName,
                            Descending = s.Descending
                        }).ToList(),
                        ActiveFilter = detail.ActiveFilter?.FetchXmlFragment
                    });
                }
                else
                {
                    Console.WriteLine($"View: {detail.Name}");
                    Console.WriteLine($"  ID:    {detail.Id}");
                    Console.WriteLine($"  Type:  {detail.QueryTypeLabel}");
                    Console.WriteLine();
                    Console.WriteLine($"Columns ({detail.Columns.Count}):");
                    foreach (var col in detail.Columns)
                    {
                        var relNote = col.IsRelated ? $" [via {col.RelationshipAttribute}]" : "";
                        Console.WriteLine($"  {col.AttributeName,-40} width={col.Width}{relNote}");
                    }
                    Console.WriteLine();
                    Console.WriteLine($"Sort ({detail.SortOrders.Count}):");
                    foreach (var s in detail.SortOrders)
                        Console.WriteLine($"  {s.AttributeName} {(s.Descending ? "desc" : "asc")}");
                    Console.WriteLine();
                    if (detail.ActiveFilter != null)
                    {
                        Console.WriteLine("Filter:");
                        Console.WriteLine($"  {detail.ActiveFilter.FetchXmlFragment}");
                    }
                    else
                    {
                        Console.WriteLine("Filter: (none)");
                    }
                }

                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                var error = ExceptionMapper.Map(ex, context: "getting view", debug: globalOptions.Debug);
                writer.WriteError(error);
                return ExceptionMapper.ToExitCode(ex);
            }
        });

        return command;
    }

    private sealed class ViewDetailOutput
    {
        [JsonPropertyName("id")] public Guid Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("queryType")] public int QueryType { get; set; }
        [JsonPropertyName("queryTypeLabel")] public string QueryTypeLabel { get; set; } = "";
        [JsonPropertyName("entityLogicalName")] public string EntityLogicalName { get; set; } = "";
        [JsonPropertyName("columns")] public List<ColumnOutput> Columns { get; set; } = [];
        [JsonPropertyName("sortOrders")] public List<SortOutput> SortOrders { get; set; } = [];
        [JsonPropertyName("activeFilter")] public string? ActiveFilter { get; set; }
    }

    private sealed class ColumnOutput
    {
        [JsonPropertyName("attributeName")] public string AttributeName { get; set; } = "";
        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("isRelated")] public bool IsRelated { get; set; }
        [JsonPropertyName("relationshipAttribute")] public string? RelationshipAttribute { get; set; }
        [JsonPropertyName("relatedEntityName")] public string? RelatedEntityName { get; set; }
        [JsonPropertyName("relatedEntityPrimaryKeyName")] public string? RelatedEntityPrimaryKeyName { get; set; }
    }

    private sealed class SortOutput
    {
        [JsonPropertyName("attributeName")] public string AttributeName { get; set; } = "";
        [JsonPropertyName("descending")] public bool Descending { get; set; }
    }
}
