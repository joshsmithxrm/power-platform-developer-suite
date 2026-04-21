using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Users;

namespace PPDS.Cli.Commands.Users;

/// <summary>
/// List users.
/// </summary>
public static class ListCommand
{
    public static Command Create()
    {
        var filterOption = new Option<string?>("--filter", "-f")
        {
            Description = "Filter by name, email, or domain"
        };

        var includeDisabledOption = new Option<bool>("--include-disabled")
        {
            Description = "Include disabled users",
            DefaultValueFactory = _ => false
        };

        var topOption = new Option<int>("--top", "-t")
        {
            Description = "Maximum number of results",
            DefaultValueFactory = _ => 100
        };

        var command = new Command("list", "List users")
        {
            filterOption,
            includeDisabledOption,
            topOption,
            UsersCommandGroup.ProfileOption,
            UsersCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var filter = parseResult.GetValue(filterOption);
            var includeDisabled = parseResult.GetValue(includeDisabledOption);
            var top = parseResult.GetValue(topOption);
            var profile = parseResult.GetValue(UsersCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(UsersCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(filter, includeDisabled, top, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? filter,
        bool includeDisabled,
        int top,
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

            var userService = serviceProvider.GetRequiredService<IUserService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var usersResult = await userService.ListAsync(filter, includeDisabled, cancellationToken);
            var users = usersResult.Items;

            if (globalOptions.IsJsonMode)
            {
                var output = users.Select(u => new UserListItem
                {
                    Id = u.Id,
                    FullName = u.FullName,
                    DomainName = u.DomainName,
                    Email = u.InternalEmailAddress,
                    IsDisabled = u.IsDisabled,
                    IsLicensed = u.IsLicensed,
                    AccessMode = u.AccessMode
                }).ToList();

                writer.WriteSuccess(output);
            }
            else
            {
                if (users.Count == 0)
                {
                    Console.Error.WriteLine("No users found.");
                }
                else
                {
                    Console.Error.WriteLine($"Found {users.Count} user(s):");
                    Console.Error.WriteLine();

                    foreach (var u in users)
                    {
                        var status = u.IsDisabled ? " [Disabled]" : "";
                        Console.WriteLine($"  {u.FullName ?? u.DomainName}{status}");
                        Console.WriteLine($"    Domain: {u.DomainName ?? "-"}");
                        Console.WriteLine($"    Email:  {u.InternalEmailAddress ?? "-"}");
                        Console.WriteLine($"    Access: {u.AccessMode ?? "-"}");
                        Console.WriteLine();
                    }
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "listing users", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class UserListItem
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("fullName")]
        public string? FullName { get; set; }

        [JsonPropertyName("domainName")]
        public string? DomainName { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("isDisabled")]
        public bool IsDisabled { get; set; }

        [JsonPropertyName("isLicensed")]
        public bool? IsLicensed { get; set; }

        [JsonPropertyName("accessMode")]
        public string? AccessMode { get; set; }
    }

    #endregion
}
