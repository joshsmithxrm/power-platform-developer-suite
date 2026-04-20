using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Solutions;
using PPDS.Cli.Services.WebResources;

namespace PPDS.Cli.Commands.WebResources;

/// <summary>
/// List web resources with optional filters.
/// </summary>
public static class ListCommand
{
    /// <summary>
    /// Type shortcut mappings. "text" and "image" expand to multiple type codes.
    /// Individual types map to their type code.
    /// </summary>
    private static readonly Dictionary<string, int[]> TypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text"] = [1, 2, 3, 4, 9, 11, 12],     // HTML, CSS, JS, XML, XSL, SVG, RESX
        ["image"] = [5, 6, 7, 10, 11],            // PNG, JPG, GIF, ICO, SVG
        ["data"] = [4, 12],                        // XML, RESX
        ["html"] = [1],
        ["css"] = [2],
        ["js"] = [3], ["javascript"] = [3],
        ["xml"] = [4],
        ["png"] = [5],
        ["jpg"] = [6], ["jpeg"] = [6],
        ["gif"] = [7],
        ["xap"] = [8],
        ["xsl"] = [9], ["xslt"] = [9],
        ["ico"] = [10],
        ["svg"] = [11],
        ["resx"] = [12],
    };

    public static Command Create()
    {
        var namePatternArgument = new Argument<string?>("name-pattern")
        {
            Description = "Filter by partial name match (e.g., 'app.js', 'new_/scripts/')",
            Arity = ArgumentArity.ZeroOrOne
        };

        var solutionOption = new Option<string?>("--solution", "-s")
        {
            Description = "Filter by solution unique name"
        };

        var typeOption = new Option<string?>("--type", "-t")
        {
            Description = "Filter by type: text, image, data, or specific type (js, css, html, xml, png, etc.)"
        };

        var topOption = new Option<int?>("--top")
        {
            Description = "Maximum number of results (default: 5000)"
        };

        var command = new Command("list", "List web resources in the environment")
        {
            namePatternArgument,
            WebResourcesCommandGroup.ProfileOption,
            WebResourcesCommandGroup.EnvironmentOption,
            solutionOption,
            typeOption,
            topOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var namePattern = parseResult.GetValue(namePatternArgument);
            var profile = parseResult.GetValue(WebResourcesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(WebResourcesCommandGroup.EnvironmentOption);
            var solution = parseResult.GetValue(solutionOption);
            var type = parseResult.GetValue(typeOption);
            var top = parseResult.GetValue(topOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(namePattern, profile, environment, solution, type, top, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? namePattern,
        string? profile,
        string? environment,
        string? solution,
        string? type,
        int? top,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        // Validate --type if provided
        int[]? typeCodes = null;
        if (type != null)
        {
            if (!TypeMap.TryGetValue(type, out typeCodes))
            {
                var error = new StructuredError(
                    ErrorCodes.Validation.InvalidValue,
                    $"Unknown type '{type}'. Supported: text, image, data, js, css, html, xml, png, jpg, gif, svg, ico, xsl, resx",
                    null,
                    type);
                writer.WriteError(error);
                return ExitCodes.InvalidArguments;
            }
        }

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var webResourceService = serviceProvider.GetRequiredService<IWebResourceService>();
            var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();

            if (!globalOptions.IsJsonMode)
            {
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            // Resolve solution name to ID if provided
            Guid? solutionId = null;
            if (solution != null)
            {
                var solutionService = serviceProvider.GetRequiredService<ISolutionService>();
                var solutionInfo = await solutionService.GetAsync(solution, cancellationToken);
                if (solutionInfo == null)
                {
                    var error = new StructuredError(
                        ErrorCodes.Operation.NotFound,
                        $"Solution '{solution}' not found.",
                        null,
                        solution);
                    writer.WriteError(error);
                    return ExitCodes.NotFoundError;
                }
                solutionId = solutionInfo.Id;
            }

            // Use textOnly if type filter is specifically "text"
            var textOnly = type != null && type.Equals("text", StringComparison.OrdinalIgnoreCase);

            var listResult = await webResourceService.ListAsync(
                solutionId: solutionId,
                textOnly: textOnly,
                cancellationToken: cancellationToken);
            var resources = listResult.Items.ToList();

            // Apply client-side top truncation if requested
            if (top.HasValue)
            {
                resources = resources.Take(top.Value).ToList();
            }

            // Apply type filter (for specific types, not "text" shortcut which is handled server-side)
            if (typeCodes != null && !textOnly)
            {
                resources = resources.Where(r => typeCodes.Contains(r.WebResourceType)).ToList();
            }

            // Apply name pattern filter
            if (!string.IsNullOrEmpty(namePattern))
            {
                resources = WebResourceNameResolver.Filter(namePattern, resources).ToList();
            }

            if (resources.Count == 0)
            {
                if (globalOptions.IsJsonMode)
                {
                    writer.WriteSuccess(new ListOutput { Resources = [] });
                }
                else
                {
                    Console.Error.WriteLine("No web resources found.");
                }
                return ExitCodes.Success;
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new ListOutput
                {
                    Resources = resources.Select(r => new WebResourceOutput
                    {
                        Id = r.Id,
                        Name = r.Name,
                        DisplayName = r.DisplayName,
                        Type = r.TypeName,
                        WebResourceType = r.WebResourceType,
                        IsManaged = r.IsManaged,
                        IsTextType = r.IsTextType,
                        CreatedBy = r.CreatedByName,
                        CreatedOn = r.CreatedOn,
                        ModifiedBy = r.ModifiedByName,
                        ModifiedOn = r.ModifiedOn
                    }).ToList()
                };
                writer.WriteSuccess(output);
            }
            else
            {
                Console.Error.WriteLine($"{"Name",-50} {"Type",-12} {"Managed",-10} {"Modified On",-20} {"Modified By"}");
                Console.Error.WriteLine(new string('-', 112));

                foreach (var r in resources)
                {
                    var name = Truncate(r.Name, 50);
                    var type_ = Truncate(r.TypeName, 12);
                    var managed = r.IsManaged ? "Managed" : "Unmanaged";
                    var modified = r.ModifiedOn?.ToString("yyyy-MM-dd HH:mm") ?? "-";
                    var modifiedBy = Truncate(r.ModifiedByName ?? "-", 20);

                    Console.Error.WriteLine($"{name,-50} {type_,-12} {managed,-10} {modified,-20} {modifiedBy}");
                }

                Console.Error.WriteLine();
                Console.Error.WriteLine($"Total: {resources.Count} web resource(s)");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "listing web resources", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        return value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }

    #region Output Models

    private sealed class ListOutput
    {
        [JsonPropertyName("resources")]
        public List<WebResourceOutput> Resources { get; set; } = [];
    }

    private sealed class WebResourceOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("webResourceType")]
        public int WebResourceType { get; set; }

        [JsonPropertyName("isManaged")]
        public bool IsManaged { get; set; }

        [JsonPropertyName("isTextType")]
        public bool IsTextType { get; set; }

        [JsonPropertyName("createdBy")]
        public string? CreatedBy { get; set; }

        [JsonPropertyName("createdOn")]
        public DateTime? CreatedOn { get; set; }

        [JsonPropertyName("modifiedBy")]
        public string? ModifiedBy { get; set; }

        [JsonPropertyName("modifiedOn")]
        public DateTime? ModifiedOn { get; set; }
    }

    #endregion
}
