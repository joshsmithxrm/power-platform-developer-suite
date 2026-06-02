using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using PPDS.LiveTests.Infrastructure;
using Xunit;

namespace PPDS.LiveTests.Cli;

/// <summary>
/// E2E tests for ppds model-driven-app add-table and remove-table commands.
/// Tests run against the "PPDS MDA" app in the environment specified by DATAVERSE_URL.
///
/// Prerequisites:
///   - The environment must have an MDA named "PPDS MDA" with "account" in the sitemap.
///   - Credentials must be set: DATAVERSE_URL, PPDS_TEST_APP_ID, PPDS_TEST_CLIENT_SECRET, PPDS_TEST_TENANT_ID.
///   - CLI must be built in Release/net8.0: dotnet build src/PPDS.Cli -c Release -f net8.0
/// </summary>
public class ModelDrivenAppCommandE2ETests : CliE2ETestBase
{
    // The test app and entities are fixed so we can reason about baseline state.
    private const string TestApp = "PPDS MDA";
    private const string TestEntity = "contact"; // added/removed during tests; not in app by default

    private string _profile = string.Empty;

    // E2E tests for mutating a shared MDA are slow; use a generous timeout.
    protected override TimeSpan CommandTimeout => TimeSpan.FromMinutes(5);

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        _profile = GenerateTestProfileName();
        var create = await RunCliAsync(
            "auth", "create",
            "--name", _profile,
            "--applicationId", Configuration.ApplicationId!,
            "--clientSecret", Configuration.ClientSecret!,
            "--tenant", Configuration.TenantId!,
            "--environment", Configuration.DataverseUrl!);
        create.ExitCode.Should().Be(0, $"Profile creation failed: {create.StdErr}");

        // Ensure test entity is absent from the draft before each test.
        await TryRemoveEntityAsync(TestEntity, publish: false);
    }

    public override async Task DisposeAsync()
    {
        // Safety-net cleanup: remove test entity from both draft and published state.
        await TryRemoveEntityAsync(TestEntity, publish: false);
        await base.DisposeAsync();
    }

    #region add-table

    [CliE2EWithCredentials]
    public async Task AddTable_SingleEntity_PersistsInDraft()
    {
        // Act: add contact to the app (no --publish → only draft state changes)
        var addResult = await RunCliAsync(
            "model-driven-app", "add-table", TestEntity,
            "--app", TestApp, "-p", _profile);

        // Assert: command succeeds
        addResult.ExitCode.Should().Be(0, $"add-table failed:\n{addResult.StdErr}");
        addResult.StdErr.Should().Contain($"Added 1 table(s) to app '{TestApp}'");

        // Assert: entity is visible in the DRAFT via the unpublished-read helper.
        var draftXml = await FetchUnpublishedSitemapXmlAsync();
        draftXml.Should().Contain(TestEntity, "entity should be in the unpublished sitemap XML");

        // Assert: entity is NOT visible in the PUBLISHED sitemap (not yet published).
        var sitemapResult = await RunCliAsync(
            "model-driven-app", "sitemap", "--app", TestApp, "-p", _profile);
        sitemapResult.ExitCode.Should().Be(0, $"sitemap failed:\n{sitemapResult.StdErr}");
        sitemapResult.StdOut.Should().NotContain("Contact",
            "unpublished contact should not appear in the published sitemap view");
    }

    [CliE2EWithCredentials]
    public async Task AddTable_AddedTwice_SecondCallFails()
    {
        // First add: should succeed
        var first = await RunCliAsync(
            "model-driven-app", "add-table", TestEntity,
            "--app", TestApp, "-p", _profile);
        first.ExitCode.Should().Be(0, $"First add-table failed:\n{first.StdErr}");

        // Second add: should fail with EntityAlreadyInApp
        var second = await RunCliAsync(
            "model-driven-app", "add-table", TestEntity,
            "--app", TestApp, "-p", _profile);
        second.ExitCode.Should().NotBe(0, "Duplicate add should fail");
        second.StdErr.Should().Contain("already in app");
    }

    [CliE2EWithCredentials]
    public async Task AddTable_EntityAlreadyInPublished_Fails()
    {
        // "account" is always in the published (and draft) baseline sitemap.
        var result = await RunCliAsync(
            "model-driven-app", "add-table", "account",
            "--app", TestApp, "-p", _profile);

        result.ExitCode.Should().NotBe(0);
        result.StdErr.Should().Contain("already in app");
    }

    [CliE2EWithCredentials]
    public async Task AddTable_EntityNotInEnvironment_Fails()
    {
        var result = await RunCliAsync(
            "model-driven-app", "add-table", "ppds_does_not_exist_xyz",
            "--app", TestApp, "-p", _profile);

        result.ExitCode.Should().NotBe(0);
        result.StdErr.Should().Contain("does not exist");
    }

    [CliE2EWithCredentials]
    public async Task AddTable_WithPublish_AppearsInPublishedSitemap()
    {
        try
        {
            // Act: add contact and publish in one step
            var addResult = await RunCliAsync(
                "model-driven-app", "add-table", TestEntity,
                "--app", TestApp, "--publish", "-p", _profile);

            addResult.ExitCode.Should().Be(0, $"add-table --publish failed:\n{addResult.StdErr}");
            addResult.StdErr.Should().Contain($"Added 1 table(s) to app '{TestApp}'");

            // Assert: entity appears in the published sitemap
            var sitemapResult = await RunCliAsync(
                "model-driven-app", "sitemap", "--app", TestApp, "-p", _profile);
            sitemapResult.ExitCode.Should().Be(0, $"sitemap failed:\n{sitemapResult.StdErr}");
            sitemapResult.StdOut.Should().Contain("Contact");
        }
        finally
        {
            // Cleanup: remove from draft and publish to restore baseline state
            await TryRemoveEntityAsync(TestEntity, publish: true);
        }
    }

    #endregion

    #region remove-table

    [CliE2EWithCredentials]
    public async Task RemoveTable_ExistingDraftEntity_Succeeds()
    {
        // Setup: add contact to draft
        var setup = await RunCliAsync(
            "model-driven-app", "add-table", TestEntity,
            "--app", TestApp, "-p", _profile);
        setup.ExitCode.Should().Be(0, $"Setup add-table failed:\n{setup.StdErr}");

        // Act: remove it from the draft
        var removeResult = await RunCliAsync(
            "model-driven-app", "remove-table",
            "--entity", TestEntity, "--app", TestApp, "-p", _profile);

        // Assert: succeeds
        removeResult.ExitCode.Should().Be(0, $"remove-table failed:\n{removeResult.StdErr}");
        removeResult.StdErr.Should().Contain($"Removed table '{TestEntity}'");

        // Assert: entity is gone from the draft
        var draftXml = await FetchUnpublishedSitemapXmlAsync();
        draftXml.Should().NotContain(TestEntity, "entity should be removed from the unpublished sitemap");

        // Assert: second remove fails with EntityNotInApp
        var secondRemove = await RunCliAsync(
            "model-driven-app", "remove-table",
            "--entity", TestEntity, "--app", TestApp, "-p", _profile);
        secondRemove.ExitCode.Should().NotBe(0, "Removing a non-existent entity should fail");
        secondRemove.StdErr.Should().Contain("not in app");
    }

    [CliE2EWithCredentials]
    public async Task RemoveTable_EntityNotInApp_Fails()
    {
        // contact is not in the app (ensured by InitializeAsync)
        var result = await RunCliAsync(
            "model-driven-app", "remove-table",
            "--entity", TestEntity, "--app", TestApp, "-p", _profile);

        result.ExitCode.Should().NotBe(0);
        result.StdErr.Should().Contain("not in app");
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Retrieves the unpublished (draft) sitemap XML for the test app by querying Dataverse directly.
    /// This helper verifies that write operations persist to the draft state without publishing.
    /// Uses <see cref="RetrieveUnpublishedMultipleRequest"/> which works where
    /// <see cref="RetrieveUnpublishedRequest"/> does not for the <c>sitemap</c> entity.
    /// </summary>
    private async Task<string> FetchUnpublishedSitemapXmlAsync()
    {
        using var client = await LiveTestHelpers.CreateServiceClientAsync(Configuration);

        // Resolve the app module ID for the test app.
        var appQuery = new QueryExpression("appmodule")
        {
            ColumnSet = new ColumnSet("appmoduleid"),
            TopCount = 1
        };
        appQuery.Criteria.AddCondition("name", ConditionOperator.Equal, TestApp);
        var appResult = client.RetrieveMultiple(appQuery);
        var appModuleId = appResult.Entities.FirstOrDefault()?.GetAttributeValue<Guid>("appmoduleid")
            ?? throw new InvalidOperationException($"App '{TestApp}' not found in the environment.");

        // Find the sitemap component ID via appmodulecomponent.
        const int ComponentTypeSitemap = 62;
        var compQuery = new QueryExpression("appmodulecomponent")
        {
            ColumnSet = new ColumnSet("objectid"),
            TopCount = 1
        };
        compQuery.Criteria.AddCondition("componenttype", ConditionOperator.Equal, ComponentTypeSitemap);
        var appLink = compQuery.AddLink("appmodule", "appmoduleidunique", "appmoduleidunique");
        appLink.LinkCriteria.AddCondition("appmoduleid", ConditionOperator.Equal, appModuleId);
        var compResult = client.RetrieveMultiple(compQuery);
        var sitemapId = compResult.Entities.FirstOrDefault()?.GetAttributeValue<Guid>("objectid")
            ?? throw new InvalidOperationException($"Sitemap component not found for app '{TestApp}'.");

        // Retrieve the unpublished (draft) version of the sitemap.
        var sitemapQuery = new QueryExpression("sitemap")
        {
            ColumnSet = new ColumnSet("sitemapxml"),
            TopCount = 1
        };
        sitemapQuery.Criteria.AddCondition("sitemapid", ConditionOperator.Equal, sitemapId);

        var request = new RetrieveUnpublishedMultipleRequest { Query = sitemapQuery };
        var response = (RetrieveUnpublishedMultipleResponse)client.Execute(request);

        return response.EntityCollection.Entities.FirstOrDefault()?.GetAttributeValue<string>("sitemapxml")
            ?? "<SiteMap />";
    }

    /// <summary>
    /// Attempts to remove the entity from the app. Ignores errors (used for setup/teardown).
    /// </summary>
    private async Task TryRemoveEntityAsync(string entity, bool publish)
    {
        var args = new List<string>
        {
            "model-driven-app", "remove-table",
            "--entity", entity,
            "--app", TestApp,
            "-p", _profile
        };
        if (publish) args.Add("--publish");

        await RunCliAsync([.. args]);
    }

    #endregion
}
