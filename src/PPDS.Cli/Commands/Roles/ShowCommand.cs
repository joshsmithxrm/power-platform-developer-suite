using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Roles;

namespace PPDS.Cli.Commands.Roles;

/// <summary>
/// Show role details.
/// </summary>
public static class ShowCommand
{
    public static Command Create()
    {
        var roleArgument = new Argument<string>("role")
        {
            Description = "Role ID (GUID) or name"
        };

        var command = new Command("show", "Show role details and assigned users")
        {
            roleArgument,
            RolesCommandGroup.ProfileOption,
            RolesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var role = parseResult.GetValue(roleArgument)!;
            var profile = parseResult.GetValue(RolesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(RolesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(role, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string role,
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
            var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();

            // Try to parse as GUID first, then as name
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

            // Get users assigned to this role
            var users = await roleService.GetRoleUsersAsync(roleInfo.Id, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                var output = new RoleDetails
                {
                    Id = roleInfo.Id,
                    Name = roleInfo.Name,
                    Description = roleInfo.Description,
                    BusinessUnitName = roleInfo.BusinessUnitName,
                    IsManaged = roleInfo.IsManaged,
                    IsCustomizable = roleInfo.IsCustomizable,
                    CreatedOn = roleInfo.CreatedOn,
                    ModifiedOn = roleInfo.ModifiedOn,
                    Users = users.Select(u => new UserItem
                    {
                        Id = u.Id,
                        FullName = u.FullName,
                        DomainName = u.DomainName
                    }).ToList()
                };
                writer.WriteSuccess(output);
            }
            else
            {
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();

                Console.WriteLine($"Name:           {roleInfo.Name}");
                Console.WriteLine($"Description:    {roleInfo.Description ?? "-"}");
                Console.WriteLine($"Business Unit:  {roleInfo.BusinessUnitName ?? "-"}");
                Console.WriteLine($"Is Managed:     {roleInfo.IsManaged}");
                Console.WriteLine($"Is Customizable:{roleInfo.IsCustomizable?.ToString() ?? "-"}");
                Console.WriteLine($"Created:        {roleInfo.CreatedOn?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"}");
                Console.WriteLine($"Modified:       {roleInfo.ModifiedOn?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"}");
                Console.WriteLine();
                Console.WriteLine($"Assigned Users ({users.Count}):");
                if (users.Count == 0)
                {
                    Console.WriteLine("  (none)");
                }
                else
                {
                    foreach (var user in users)
                    {
                        var disabled = user.IsDisabled ? " [Disabled]" : "";
                        Console.WriteLine($"  - {user.FullName ?? user.DomainName}{disabled}");
                    }
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"showing role '{role}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class RoleDetails
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("businessUnitName")]
        public string? BusinessUnitName { get; set; }

        [JsonPropertyName("isManaged")]
        public bool IsManaged { get; set; }

        [JsonPropertyName("isCustomizable")]
        public bool? IsCustomizable { get; set; }

        [JsonPropertyName("createdOn")]
        public DateTime? CreatedOn { get; set; }

        [JsonPropertyName("modifiedOn")]
        public DateTime? ModifiedOn { get; set; }

        [JsonPropertyName("users")]
        public List<UserItem> Users { get; set; } = new();
    }

    private sealed class UserItem
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("fullName")]
        public string? FullName { get; set; }

        [JsonPropertyName("domainName")]
        public string? DomainName { get; set; }
    }

    #endregion
}
