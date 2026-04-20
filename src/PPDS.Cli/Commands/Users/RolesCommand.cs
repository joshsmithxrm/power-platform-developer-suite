using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Users;

namespace PPDS.Cli.Commands.Users;

/// <summary>
/// List roles for a user.
/// </summary>
public static class RolesCommand
{
    public static Command Create()
    {
        var userArgument = new Argument<string>("user")
        {
            Description = "User ID (GUID) or domain name (e.g., user@domain.com)"
        };

        var command = new Command("roles", "List roles for a user")
        {
            userArgument,
            UsersCommandGroup.ProfileOption,
            UsersCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var user = parseResult.GetValue(userArgument)!;
            var profile = parseResult.GetValue(UsersCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(UsersCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(user, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string user,
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

            // Get user ID
            Guid userId;
            string userName;
            if (Guid.TryParse(user, out userId))
            {
                var userInfo = await userService.GetByIdAsync(userId, cancellationToken);
                if (userInfo == null)
                {
                    var error = new StructuredError(
                        ErrorCodes.Operation.NotFound,
                        $"User '{user}' not found.",
                        null,
                        user);
                    writer.WriteError(error);
                    return ExitCodes.NotFoundError;
                }
                userName = userInfo.FullName ?? userInfo.DomainName ?? user;
            }
            else
            {
                var userInfo = await userService.GetByDomainNameAsync(user, cancellationToken);
                if (userInfo == null)
                {
                    var error = new StructuredError(
                        ErrorCodes.Operation.NotFound,
                        $"User '{user}' not found.",
                        null,
                        user);
                    writer.WriteError(error);
                    return ExitCodes.NotFoundError;
                }
                userId = userInfo.Id;
                userName = userInfo.FullName ?? userInfo.DomainName ?? user;
            }

            var roles = await userService.GetUserRolesAsync(userId, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                var output = roles.Select(r => new RoleListItem
                {
                    Id = r.Id,
                    Name = r.Name,
                    Description = r.Description,
                    IsManaged = r.IsManaged
                }).ToList();

                writer.WriteSuccess(output);
            }
            else
            {
                Console.Error.WriteLine($"Roles for '{userName}':");
                Console.Error.WriteLine();

                if (roles.Count == 0)
                {
                    Console.Error.WriteLine("  (no roles assigned)");
                }
                else
                {
                    foreach (var role in roles)
                    {
                        Console.WriteLine($"  {role.Name}");
                        if (!string.IsNullOrEmpty(role.Description))
                        {
                            Console.WriteLine($"    {role.Description}");
                        }
                    }
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"getting roles for user '{user}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class RoleListItem
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("isManaged")]
        public bool IsManaged { get; set; }
    }

    #endregion
}
