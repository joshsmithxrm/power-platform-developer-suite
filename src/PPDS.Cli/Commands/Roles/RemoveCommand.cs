using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Roles;
using PPDS.Cli.Services.Users;

namespace PPDS.Cli.Commands.Roles;

/// <summary>
/// Remove a role from a user.
/// </summary>
public static class RemoveCommand
{
    public static Command Create()
    {
        var roleArgument = new Argument<string>("role")
        {
            Description = "Role ID (GUID) or name"
        };

        var userOption = new Option<string>("--user", "-u")
        {
            Description = "User ID (GUID) or domain name",
            Required = true
        };

        var command = new Command("remove", "Remove a role from a user")
        {
            roleArgument,
            userOption,
            RolesCommandGroup.ProfileOption,
            RolesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var role = parseResult.GetValue(roleArgument)!;
            var user = parseResult.GetValue(userOption)!;
            var profile = parseResult.GetValue(RolesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(RolesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(role, user, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string role,
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

            var roleService = serviceProvider.GetRequiredService<IRoleService>();
            var userService = serviceProvider.GetRequiredService<IUserService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            // Resolve role
            RoleInfo? roleInfo;
            if (Guid.TryParse(role, out var roleId))
            {
                roleInfo = await roleService.GetByIdAsync(roleId, cancellationToken);
            }
            else
            {
                roleInfo = await roleService.GetByNameAsync(role, cancellationToken);
            }

            if (roleInfo == null)
            {
                var error = new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Role '{role}' not found.",
                    null,
                    role);
                writer.WriteError(error);
                return ExitCodes.NotFoundError;
            }

            // Resolve user
            UserInfo? userInfo;
            if (Guid.TryParse(user, out var userId))
            {
                userInfo = await userService.GetByIdAsync(userId, cancellationToken);
            }
            else
            {
                userInfo = await userService.GetByDomainNameAsync(user, cancellationToken);
            }

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

            // Remove the role
            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine($"Removing role '{roleInfo.Name}' from user '{userInfo.FullName ?? userInfo.DomainName}'...");
            }

            await roleService.RemoveRoleAsync(userInfo.Id, roleInfo.Id, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                var output = new RemoveOutput
                {
                    UserId = userInfo.Id,
                    UserName = userInfo.FullName ?? userInfo.DomainName,
                    RoleId = roleInfo.Id,
                    RoleName = roleInfo.Name,
                    Success = true
                };
                writer.WriteSuccess(output);
            }
            else
            {
                Console.Error.WriteLine("Role removed successfully.");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"removing role '{role}' from user '{user}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class RemoveOutput
    {
        [JsonPropertyName("userId")]
        public Guid UserId { get; set; }

        [JsonPropertyName("userName")]
        public string? UserName { get; set; }

        [JsonPropertyName("roleId")]
        public Guid RoleId { get; set; }

        [JsonPropertyName("roleName")]
        public string RoleName { get; set; } = string.Empty;

        [JsonPropertyName("success")]
        public bool Success { get; set; }
    }

    #endregion
}
