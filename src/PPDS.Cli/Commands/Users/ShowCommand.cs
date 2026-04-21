using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Users;

namespace PPDS.Cli.Commands.Users;

/// <summary>
/// Show user details.
/// </summary>
public static class ShowCommand
{
    public static Command Create()
    {
        var userArgument = new Argument<string>("user")
        {
            Description = "User ID (GUID) or domain name (e.g., user@domain.com)"
        };

        var command = new Command("show", "Show user details")
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
            var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();

            // Try to parse as GUID first, then as domain name
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

            // Get user's roles
            var roles = await userService.GetUserRolesAsync(userInfo.Id, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                var output = new UserDetails
                {
                    Id = userInfo.Id,
                    FullName = userInfo.FullName,
                    FirstName = userInfo.FirstName,
                    LastName = userInfo.LastName,
                    DomainName = userInfo.DomainName,
                    Email = userInfo.InternalEmailAddress,
                    JobTitle = userInfo.JobTitle,
                    IsDisabled = userInfo.IsDisabled,
                    IsLicensed = userInfo.IsLicensed,
                    AccessMode = userInfo.AccessMode,
                    AzureActiveDirectoryObjectId = userInfo.AzureActiveDirectoryObjectId,
                    BusinessUnitName = userInfo.BusinessUnitName,
                    CreatedOn = userInfo.CreatedOn,
                    ModifiedOn = userInfo.ModifiedOn,
                    Roles = roles.Select(r => new RoleItem { Id = r.Id, Name = r.Name }).ToList()
                };
                writer.WriteSuccess(output);
            }
            else
            {
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();

                Console.WriteLine($"Full Name:     {userInfo.FullName ?? "-"}");
                Console.WriteLine($"Domain Name:   {userInfo.DomainName ?? "-"}");
                Console.WriteLine($"Email:         {userInfo.InternalEmailAddress ?? "-"}");
                Console.WriteLine($"Job Title:     {userInfo.JobTitle ?? "-"}");
                Console.WriteLine($"Access Mode:   {userInfo.AccessMode ?? "-"}");
                Console.WriteLine($"Is Disabled:   {userInfo.IsDisabled}");
                Console.WriteLine($"Is Licensed:   {userInfo.IsLicensed?.ToString() ?? "-"}");
                Console.WriteLine($"Business Unit: {userInfo.BusinessUnitName ?? "-"}");
                Console.WriteLine($"Created:       {userInfo.CreatedOn?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"}");
                Console.WriteLine($"Modified:      {userInfo.ModifiedOn?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"}");
                Console.WriteLine();
                Console.WriteLine($"Roles ({roles.Count}):");
                if (roles.Count == 0)
                {
                    Console.WriteLine("  (none)");
                }
                else
                {
                    foreach (var role in roles)
                    {
                        Console.WriteLine($"  - {role.Name}");
                    }
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"showing user '{user}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class UserDetails
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("fullName")]
        public string? FullName { get; set; }

        [JsonPropertyName("firstName")]
        public string? FirstName { get; set; }

        [JsonPropertyName("lastName")]
        public string? LastName { get; set; }

        [JsonPropertyName("domainName")]
        public string? DomainName { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("jobTitle")]
        public string? JobTitle { get; set; }

        [JsonPropertyName("isDisabled")]
        public bool IsDisabled { get; set; }

        [JsonPropertyName("isLicensed")]
        public bool? IsLicensed { get; set; }

        [JsonPropertyName("accessMode")]
        public string? AccessMode { get; set; }

        [JsonPropertyName("azureActiveDirectoryObjectId")]
        public Guid? AzureActiveDirectoryObjectId { get; set; }

        [JsonPropertyName("businessUnitName")]
        public string? BusinessUnitName { get; set; }

        [JsonPropertyName("createdOn")]
        public DateTime? CreatedOn { get; set; }

        [JsonPropertyName("modifiedOn")]
        public DateTime? ModifiedOn { get; set; }

        [JsonPropertyName("roles")]
        public List<RoleItem> Roles { get; set; } = new();
    }

    private sealed class RoleItem
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    #endregion
}
