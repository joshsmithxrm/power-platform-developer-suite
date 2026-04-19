using System.CommandLine;

namespace PPDS.DocsGen.Cli.Tests.FixtureCli.Commands.Auth;

public static class AuthCommandGroup
{
    public static Command Create()
    {
        var root = new Command("auth", "Authentication commands.");
        root.Subcommands.Add(CreateLogin());
        root.Subcommands.Add(CreateLogout());
        return root;
    }

    private static Command CreateLogin()
    {
        var username = new Argument<string>("username")
        {
            Description = "Username to sign in as."
        };

        var tenant = new Option<string?>("--tenant", "-t")
        {
            Description = "Tenant identifier, if your account belongs to multiple tenants."
        };

        var force = new Option<bool>("--force")
        {
            Description = "Skip the confirmation prompt."
        };

        // Hidden option declared with a long name that sorts between --force
        // and --tenant alphabetically. It must be filtered out by the generator
        // (and the filter must run BEFORE the sort, see filter-before-sort
        // optimization in CliReferenceGenerator).
        var secret = new Option<string?>("--secret")
        {
            Description = "Internal flag — must never appear in generated docs.",
            Hidden = true,
        };

        return new Command("login", "Sign in to the fixture service.")
        {
            username,
            tenant,
            force,
            secret,
        };
    }

    private static Command CreateLogout() =>
        new Command("logout", "Sign out of the fixture service.");
}
