using System.CommandLine;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Views;

namespace PPDS.Cli.Commands.Views;

/// <summary>
/// Apply a complete FetchXML document to a view wholesale.
/// </summary>
/// <remarks>
/// Expected FetchXML format:
/// <![CDATA[
/// <fetch version="1.0" output-format="xml-platform" mapping="logical" no-lock="false">
///   <entity name="account">
///     <attribute name="name" />
///     <order attribute="name" descending="false" />
///     <filter type="and">
///       <condition attribute="statecode" operator="eq" value="0" />
///     </filter>
///   </entity>
/// </fetch>
/// ]]>
/// </remarks>
public static class SetFetchXmlCommand
{
    public static Command Create()
    {
        var viewOption = new Option<string>("--view", "-v")
        {
            Description = "[Required] View name or ID",
            Required = true
        };

        var fetchXmlOption = new Option<string>("--fetchxml")
        {
            Description = "[Required] Path to a file containing a complete FetchXML document with <fetch> root element.",
            Required = true
        };

        var command = new Command("set-fetchxml",
            """
            Apply a complete FetchXML document to a view, replacing the existing fetchxml.

            Expected format (file must have <fetch> as root element):
              <fetch version="1.0" output-format="xml-platform" mapping="logical" no-lock="false">
                <entity name="account">
                  <attribute name="name" />
                  <order attribute="name" descending="false" />
                  <filter type="and">
                    <condition attribute="statecode" operator="eq" value="0" />
                  </filter>
                </entity>
              </fetch>
            """)
        {
            ViewsCommandGroup.ProfileOption,
            ViewsCommandGroup.EnvironmentOption,
            ViewsCommandGroup.EntityOption,
            viewOption,
            fetchXmlOption,
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
            var fetchXmlPath = parseResult.GetValue(fetchXmlOption)!;
            var solution = parseResult.GetValue(ViewsCommandGroup.SolutionOption);
            var publish = parseResult.GetValue(ViewsCommandGroup.PublishOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            var writer = ServiceFactory.CreateOutputWriter(globalOptions);

            if (!File.Exists(fetchXmlPath))
            {
                writer.WriteError(new StructuredError(ErrorCodes.Validation.FileNotFound,
                    $"FetchXML file not found: {fetchXmlPath}"));
                return ExitCodes.NotFoundError;
            }

            string fetchXmlContent;
            try
            {
                fetchXmlContent = await File.ReadAllTextAsync(fetchXmlPath, cancellationToken);
            }
            catch (Exception ex)
            {
                writer.WriteError(new StructuredError(ErrorCodes.Validation.FileNotFound,
                    $"Failed to read FetchXML file: {ex.Message}"));
                return ExitCodes.NotFoundError;
            }

            try
            {
                var doc = XDocument.Parse(fetchXmlContent);
                if (doc.Root?.Name.LocalName != "fetch")
                {
                    writer.WriteError(new StructuredError(ErrorCodes.Validation.SchemaInvalid,
                        "FetchXML file must contain a <fetch> root element."));
                    return ExitCodes.InvalidArguments;
                }
            }
            catch (Exception)
            {
                writer.WriteError(new StructuredError(ErrorCodes.Validation.SchemaInvalid,
                    "FetchXML file does not contain valid XML."));
                return ExitCodes.InvalidArguments;
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
                await service.SetFetchXmlAsync(entity, viewName, fetchXmlContent,
                    publish, solution, cancellationToken: cancellationToken);

                if (!globalOptions.IsJsonMode)
                    Console.Error.WriteLine($"FetchXML applied to view '{viewName}'.");
                else
                    writer.WriteSuccess(new { message = $"FetchXML applied to view '{viewName}'." });

                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                var error = ExceptionMapper.Map(ex, context: ex is PpdsException ? null : "setting fetchxml", debug: globalOptions.Debug);
                writer.WriteError(error);
                return ExceptionMapper.ToExitCode(ex);
            }
        });

        return command;
    }
}
