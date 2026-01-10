using FluentAssertions;
using PPDS.LiveTests.Infrastructure;

namespace PPDS.LiveTests.Cli;

/// <summary>
/// E2E tests for ppds data schema command.
/// Generates CMT-format schema files from Dataverse metadata.
/// Tests only run on .NET 8.0 since CLI is spawned with --framework net8.0.
/// </summary>
public class DataSchemaCommandE2ETests : CliE2ETestBase
{
    #region data schema

    [CliE2EWithCredentials]
    public async Task DataSchema_SingleEntity_GeneratesValidSchema()
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

        var outputPath = GenerateTempFilePath(".xml");

        var result = await RunCliAsync(
            "data", "schema",
            "--entities", "account",
            "--output", outputPath);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        File.Exists(outputPath).Should().BeTrue("Schema file should be created");

        var content = await File.ReadAllTextAsync(outputPath);
        content.Should().Contain("account");
        content.Should().Contain("<entity");
    }

    [CliE2EWithCredentials]
    public async Task DataSchema_MultipleEntities_GeneratesSchemaWithAll()
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

        var outputPath = GenerateTempFilePath(".xml");

        var result = await RunCliAsync(
            "data", "schema",
            "--entities", "account,contact",
            "--output", outputPath);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");

        var content = await File.ReadAllTextAsync(outputPath);
        content.Should().Contain("account");
        content.Should().Contain("contact");
    }

    [CliE2EWithCredentials]
    public async Task DataSchema_WithDisablePlugins_SetsFlag()
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

        var outputPath = GenerateTempFilePath(".xml");

        var result = await RunCliAsync(
            "data", "schema",
            "--entities", "account",
            "--output", outputPath,
            "--disable-plugins");

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");

        var content = await File.ReadAllTextAsync(outputPath);
        content.Should().Contain("disableplugins");
    }

    [CliE2EWithCredentials]
    public async Task DataSchema_WithProfileOption_UsesSpecifiedProfile()
    {
        var profileName = GenerateTestProfileName();
        await RunCliAsync(
            "auth", "create",
            "--name", profileName,
            "--applicationId", Configuration.ApplicationId!,
            "--clientSecret", Configuration.ClientSecret!,
            "--tenant", Configuration.TenantId!,
            "--environment", Configuration.DataverseUrl!);

        var outputPath = GenerateTempFilePath(".xml");

        // Use --profile instead of selecting the profile
        var result = await RunCliAsync(
            "data", "schema",
            "--entities", "account",
            "--output", outputPath,
            "--profile", profileName);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        File.Exists(outputPath).Should().BeTrue();
    }

    [CliE2EWithCredentials]
    public async Task DataSchema_WithEnvironmentOverride_UsesSpecifiedEnvironment()
    {
        var profileName = GenerateTestProfileName();
        await RunCliAsync(
            "auth", "create",
            "--name", profileName,
            "--applicationId", Configuration.ApplicationId!,
            "--clientSecret", Configuration.ClientSecret!,
            "--tenant", Configuration.TenantId!,
            "--environment", Configuration.DataverseUrl!);

        var outputPath = GenerateTempFilePath(".xml");

        var result = await RunCliAsync(
            "data", "schema",
            "--entities", "account",
            "--output", outputPath,
            "--profile", profileName,
            "--environment", Configuration.DataverseUrl!);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
    }

    #endregion

    #region Validation errors

    [CliE2EFact]
    public async Task DataSchema_MissingEntities_Fails()
    {
        var outputPath = GenerateTempFilePath(".xml");

        var result = await RunCliAsync(
            "data", "schema",
            "--output", outputPath);

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("--entities", "required");
    }

    [CliE2EFact]
    public async Task DataSchema_MissingOutput_Fails()
    {
        var result = await RunCliAsync(
            "data", "schema",
            "--entities", "account");

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("--output", "required");
    }

    [CliE2EWithCredentials]
    public async Task DataSchema_InvalidEntity_Fails()
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

        var outputPath = GenerateTempFilePath(".xml");

        var result = await RunCliAsync(
            "data", "schema",
            "--entities", "nonexistent_entity_xyz123",
            "--output", outputPath);

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("not found", "does not exist", "Error", "failed");
    }

    [CliE2EFact]
    public async Task DataSchema_InvalidOutputDirectory_Fails()
    {
        // Use a guaranteed-nonexistent path in temp directory (cross-platform)
        var nonexistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "schema.xml");

        var result = await RunCliAsync(
            "data", "schema",
            "--entities", "account",
            "--output", nonexistentPath);

        result.ExitCode.Should().NotBe(0);
        (result.StdOut + result.StdErr).Should().ContainAny("directory", "does not exist", "Error");
    }

    #endregion

    #region JSON output

    [CliE2EWithCredentials]
    public async Task DataSchema_JsonFormat_OutputsProgress()
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

        var outputPath = GenerateTempFilePath(".xml");

        var result = await RunCliAsync(
            "data", "schema",
            "--entities", "account",
            "--output", outputPath,
            "--output-format", "json");

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        // JSON format outputs progress to stderr per output conventions
        result.StdErr.Should().Contain("{");
    }

    #endregion

    #region Environment variable bypass

    [CliE2EWithCredentials]
    public async Task DataSchema_WithSpnSecretEnvVar_UsesEnvVarForAuth()
    {
        // Create profile WITHOUT --clientSecret (uses interactive auth method placeholder)
        // Then provide secret via PPDS_SPN_SECRET env var
        var profileName = GenerateTestProfileName();

        // First create with clientSecret to establish the profile with correct auth method
        await RunCliAsync(
            "auth", "create",
            "--name", profileName,
            "--applicationId", Configuration.ApplicationId!,
            "--clientSecret", Configuration.ClientSecret!,
            "--tenant", Configuration.TenantId!,
            "--environment", Configuration.DataverseUrl!);

        await RunCliAsync("auth", "select", "--name", profileName);

        var outputPath = GenerateTempFilePath(".xml");

        // Run with PPDS_SPN_SECRET - this tests the env var bypass path
        // The env var takes priority over secure store lookup
        var envVars = new Dictionary<string, string>
        {
            ["PPDS_SPN_SECRET"] = Configuration.ClientSecret!
        };

        var result = await RunCliWithEnvAsync(
            envVars,
            "data", "schema",
            "--entities", "account",
            "--output", outputPath);

        result.ExitCode.Should().Be(0, $"StdErr: {result.StdErr}");
        File.Exists(outputPath).Should().BeTrue("Schema file should be created");

        var content = await File.ReadAllTextAsync(outputPath);
        content.Should().Contain("account");
    }

    #endregion
}
