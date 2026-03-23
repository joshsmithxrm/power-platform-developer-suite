using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.Roles;

/// <summary>
/// List security roles.
/// </summary>
public static class ListCommand
{
    public static Command Create()
    {
        var filterOption = new Option<string?>("--filter", "-f")
        {
            Description = "Filter by role name"
        };

        var command = new Command("list", "List security roles")
        {
            filterOption,
            RolesCommandGroup.ProfileOption,
            RolesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var filter = parseResult.GetValue(filterOption);
            var profile = parseResult.GetValue(RolesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(RolesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(filter, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? filter,
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

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var rolesResult = await roleService.ListAsync(filter, cancellationToken);
            var roles = rolesResult.Items;

            if (globalOptions.IsJsonMode)
            {
                var output = roles.Select(r => new RoleListItem
                {
                    Id = r.Id,
                    Name = r.Name,
                    Description = r.Description,
                    BusinessUnitName = r.BusinessUnitName,
                    IsManaged = r.IsManaged,
                    IsCustomizable = r.IsCustomizable
                }).ToList();

                writer.WriteSuccess(output);
            }
            else
            {
                if (roles.Count == 0)
                {
                    Console.Error.WriteLine("No roles found.");
                }
                else
                {
                    Console.Error.WriteLine($"Found {roles.Count} role(s):");
                    Console.Error.WriteLine();

                    foreach (var role in roles)
                    {
                        var managed = role.IsManaged ? " [Managed]" : "";
                        Console.WriteLine($"  {role.Name}{managed}");
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
            var error = ExceptionMapper.Map(ex, context: "listing roles", debug: globalOptions.Debug);
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

        [JsonPropertyName("businessUnitName")]
        public string? BusinessUnitName { get; set; }

        [JsonPropertyName("isManaged")]
        public bool IsManaged { get; set; }

        [JsonPropertyName("isCustomizable")]
        public bool? IsCustomizable { get; set; }
    }

    #endregion
}
