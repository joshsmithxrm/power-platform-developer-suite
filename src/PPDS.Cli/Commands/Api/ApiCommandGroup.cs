using System.CommandLine;

namespace PPDS.Cli.Commands.Api;

public static class ApiCommandGroup
{
    public static Command Create()
    {
        var command = new Command("api", "Send raw HTTP requests to the Dataverse Web API");
        command.Subcommands.Add(ApiRequestCommand.Create());
        return command;
    }
}
