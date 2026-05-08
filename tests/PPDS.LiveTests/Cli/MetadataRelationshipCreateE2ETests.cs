using FluentAssertions;
using PPDS.LiveTests.Infrastructure;

namespace PPDS.LiveTests.Cli;

/// <summary>
/// E2E tests for `ppds metadata relationship create --type many-to-many`.
/// Regression coverage for issue #1008: every M:N create in v1.1 failed with
/// "Required field 'IntersectEntitySchemaName' is missing for RequestName='CreateManyToMany'"
/// because the CLI never populated the field and the service set it on the wrong SDK object.
///
/// Skipped unless PPDS_TEST_SOLUTION and PPDS_TEST_PREFIX are set (alongside the standard
/// client-secret env vars), since N:N creation requires writing into a real custom solution.
/// </summary>
public class MetadataRelationshipCreateE2ETests : CliE2ETestBase
{
    private static string Solution => Environment.GetEnvironmentVariable("PPDS_TEST_SOLUTION")!;
    private static string Prefix => Environment.GetEnvironmentVariable("PPDS_TEST_PREFIX")!;

    [CliE2EWithSolution]
    public async Task CreateManyToMany_OmittedIntersect_DefaultsFromName_Succeeds()
    {
        var profileName = GenerateTestProfileName();
        await RunCliAsync(
            "auth", "create",
            "--name", profileName,
            "--applicationId", Configuration.ApplicationId!,
            "--clientSecret", Configuration.ClientSecret!,
            "--tenant", Configuration.TenantId!,
            "--environment", Configuration.DataverseUrl!);

        await RunCliAsync("auth", "select", "--name", profileName);

        // Account and contact are system entities present in every Dataverse env.
        var rawName = $"{Prefix}_acct_contact_{Guid.NewGuid():N}";
        var schemaName = rawName[..Math.Min(40, rawName.Length)];

        try
        {
            var createResult = await RunCliAsync(
                "metadata", "relationship", "create",
                "--solution", Solution,
                "--from", "account",
                "--to", "contact",
                "--type", "many-to-many",
                "--name", schemaName);

            createResult.ExitCode.Should().Be(0,
                $"creating an N:N relationship without --intersect-entity must default it from --name; "
                + $"StdOut: {createResult.StdOut}, StdErr: {createResult.StdErr}");
            (createResult.StdOut + createResult.StdErr).Should().NotContain(
                "IntersectEntitySchemaName",
                "the SDK rejection from issue #1008 must not surface anywhere in output");
        }
        finally
        {
            // Best-effort cleanup; ignore exit code so a partial success still tears down.
            await RunCliAsync(
                "metadata", "relationship", "delete",
                "--solution", Solution,
                "--name", schemaName,
                "--force");
        }
    }
}
