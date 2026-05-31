using System.CommandLine;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Views;

namespace PPDS.Cli.Commands.Views;

/// <summary>
/// Set the filter on a view from a file or inline condition.
/// </summary>
/// <remarks>
/// Expected filter fragment format:
/// <![CDATA[
/// <filter type="and">
///   <condition attribute="statecode" operator="eq" value="0" />
/// </filter>
/// ]]>
/// For compound filters, use --filter-file. For a single condition, use --condition "attr:op:value".
/// </remarks>
public static class SetFilterCommand
{
    public static Command Create()
    {
        var viewOption = new Option<string>("--view", "-v")
        {
            Description = "[Required] View name"
        };

        var filterFileOption = new Option<string?>("--filter-file")
        {
            Description = "Path to a file containing a <filter> XML fragment. Mutually exclusive with --condition."
        };

        var conditionOption = new Option<string?>("--condition")
        {
            Description = "Inline single condition in format 'attribute:operator:value' (e.g. statecode:eq:0). Mutually exclusive with --filter-file."
        };

        var command = new Command("set-filter",
            """
            Set or replace the filter on a view.

            Use --filter-file to apply a <filter> XML fragment from a file:
              <filter type="and">
                <condition attribute="statecode" operator="eq" value="0" />
              </filter>

            Use --condition for a quick single-condition filter: --condition "statecode:eq:0"
            """)
        {
            ViewsCommandGroup.ProfileOption,
            ViewsCommandGroup.EnvironmentOption,
            ViewsCommandGroup.EntityOption,
            viewOption,
            filterFileOption,
            conditionOption,
            ViewsCommandGroup.SolutionOption,
            ViewsCommandGroup.PublishOption,
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var profile = parseResult.GetValue(ViewsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ViewsCommandGroup.EnvironmentOption);
            var entity = parseResult.GetValue(ViewsCommandGroup.EntityOption)!;
            var viewName = parseResult.GetValue(viewOption)!;
            var filterFile = parseResult.GetValue(filterFileOption);
            var condition = parseResult.GetValue(conditionOption);
            var solution = parseResult.GetValue(ViewsCommandGroup.SolutionOption);
            var publish = parseResult.GetValue(ViewsCommandGroup.PublishOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            var writer = ServiceFactory.CreateOutputWriter(globalOptions);

            // Mutually exclusive (AC-12)
            if (filterFile != null && condition != null)
            {
                writer.WriteError(new StructuredError(ErrorCodes.Validation.InvalidArguments,
                    "--filter-file and --condition are mutually exclusive."));
                return ExitCodes.InvalidArguments;
            }

            if (filterFile == null && condition == null)
            {
                writer.WriteError(new StructuredError(ErrorCodes.Validation.InvalidArguments,
                    "Provide either --filter-file or --condition."));
                return ExitCodes.InvalidArguments;
            }

            string filterXmlFragment;

            if (filterFile != null)
            {
                if (!File.Exists(filterFile))
                {
                    writer.WriteError(new StructuredError(ErrorCodes.Validation.FileNotFound,
                        $"Filter file not found: {filterFile}"));
                    return ExitCodes.NotFoundError;
                }

                string fileContent;
                try
                {
                    fileContent = await File.ReadAllTextAsync(filterFile, cancellationToken);
                }
                catch (Exception ex)
                {
                    writer.WriteError(new StructuredError(ErrorCodes.Validation.FileNotFound,
                        $"Failed to read filter file: {ex.Message}"));
                    return ExitCodes.NotFoundError;
                }

                try
                {
                    var elem = XElement.Parse(fileContent);
                    if (elem.Name.LocalName != "filter")
                    {
                        writer.WriteError(new StructuredError(ErrorCodes.Validation.SchemaInvalid,
                            "Filter file must contain a <filter> root element."));
                        return ExitCodes.InvalidArguments;
                    }
                    filterXmlFragment = fileContent;
                }
                catch (Exception)
                {
                    writer.WriteError(new StructuredError(ErrorCodes.Validation.SchemaInvalid,
                        "Filter file does not contain valid XML."));
                    return ExitCodes.InvalidArguments;
                }
            }
            else
            {
                // --condition "attr:op:value"
                var parts = condition!.Split(':', 3);
                if (parts.Length != 3)
                {
                    writer.WriteError(new StructuredError(ErrorCodes.Validation.InvalidValue,
                        $"--condition must have exactly 3 colon-separated segments: 'attribute:operator:value'. Got: '{condition}'."));
                    return ExitCodes.InvalidArguments;
                }

                filterXmlFragment =
                    $"""<filter type="and"><condition attribute="{parts[0]}" operator="{parts[1]}" value="{parts[2]}" /></filter>""";
            }

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
                await service.SetFilterAsync(entity, viewName, filterXmlFragment,
                    publish, solution, cancellationToken: cancellationToken);

                if (!globalOptions.IsJsonMode)
                    Console.Error.WriteLine($"Filter set for view '{viewName}'.");
                else
                    writer.WriteSuccess(new { message = $"Filter set for view '{viewName}'." });

                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                var error = ExceptionMapper.Map(ex, context: "setting filter", debug: globalOptions.Debug);
                writer.WriteError(error);
                return ExceptionMapper.ToExitCode(ex);
            }
        });

        return command;
    }
}
