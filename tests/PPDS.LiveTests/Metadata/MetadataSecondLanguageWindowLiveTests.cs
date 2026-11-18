using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.LiveTests.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace PPDS.LiveTests.Metadata;

/// <summary>
/// Pins a Dataverse behaviour observed on an English-base organisation with French provisioned (2026-11-17/18, see
/// specs/metadata-authoring.md "Second-language labels dropped for a while"): after a global choice is created,
/// published, updated and deleted, the first table a process creates can open a window of several minutes in which
/// Dataverse acknowledges metadata writes but stores none of their second-language labels (local options, column
/// display names, new global choice options), while it still returns translations stored before. It reproduced in 3 of
/// 4 fresh processes and in no process without that sequence.
/// <para>
/// What the product depends on is asserted whether or not the window opens: through the real service, a base-language
/// label write in that state (option label, choice display name and description) is confirmed and keeps every
/// translation stored before it. When the window did not open, the test is reported as skipped with that reason,
/// never as passed.
/// </para>
/// <para>
/// Opt-in (PPDS_TEST_LABEL_WINDOW=1) and run on its own, so that its table create is the first of its test process:
/// <code>
/// dotnet test tests/PPDS.LiveTests/PPDS.LiveTests.csproj -f net8.0 --filter "FullyQualifiedName~MetadataSecondLanguageWindowLiveTests" --logger "console;verbosity=detailed"
/// </code>
/// with the metadata live-test settings and PPDS_TEST_SECOND_LCID.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class MetadataSecondLanguageWindowLiveTests(ITestOutputHelper output) : LiveTestBase
{
    private static string Prefix => Environment.GetEnvironmentVariable("PPDS_TEST_PREFIX")!.Trim().ToLowerInvariant();
    private static string Solution => Environment.GetEnvironmentVariable("PPDS_TEST_SOLUTION")!.Trim();

    [MetadataLiveWindowFact]
    public async Task SecondLanguageWindow_BaseLanguageWritesKeepStoredTranslations()
    {
        var second = MetadataLivePrerequisites.SecondLanguage!.Value;
        await using var main = await PoolAsync("window-main");
        var baseLanguage = await LiveOrganizationLanguage.ReadBaseLanguageAsync(main.Pool);
        if (second == baseLanguage) throw new XunitException($"PPDS_TEST_SECOND_LCID={second} is the base language.");
        var service = new DataverseMetadataAuthoringService(main.Pool, new SchemaValidator(), new NoShakedown());
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var choice = $"{Prefix}_ppdswin{suffix}";
        var table = $"{Prefix}_ppdswint{suffix}";

        Label Two(string text, string translated) { var l = new Label(text, baseLanguage); l.LocalizedLabels.Add(new LocalizedLabel(translated, second)); return l; }
        string? Text(Label? l, int lcid) => l?.LocalizedLabels?.FirstOrDefault(x => x.LanguageCode == lcid)?.Label;
        string Pairs(Label? l) => string.Join(", ", (l?.LocalizedLabels ?? []).OrderBy(x => x.LanguageCode).Select(x => $"({x.LanguageCode}, {x.Label})"));
        async Task<OptionSetMetadata> Choice() => (OptionSetMetadata)((RetrieveOptionSetResponse)await Exec(main.Pool, new RetrieveOptionSetRequest { Name = choice, RetrieveAsIfPublished = true })).OptionSetMetadata;

        // 1. A global choice whose display name, description and option carry translations (not a table: this
        //    process's first table create must come after the trigger).
        output.WriteLine($"Creating global choice '{choice}' in solution '{Solution}'.");
        var set = new OptionSetMetadata { Name = choice, IsGlobal = true, OptionSetType = OptionSetType.Picklist, DisplayName = Two("Window choice", "Choix fenêtre"), Description = Two("Window description", "Description fenêtre") };
        set.Options.Add(new OptionMetadata(Two("Open", "Ouvert"), null));
        await Exec(main.Pool, new CreateOptionSetRequest { OptionSet = set, SolutionUniqueName = Solution });
        var tableCreated = false;
        try
        {
            var before = await Choice();
            var value = before.Options.Single().Value!.Value;
            output.WriteLine($"OBSERVED before: display {Pairs(before.DisplayName)}; description {Pairs(before.Description)}; option {Pairs(before.Options.Single().Label)}");
            if (Text(before.DisplayName, second) is null || Text(before.Description, second) is null || Text(before.Options.Single().Label, second) is null)
                Skip.If(true, "Setup: this process was already in a window (the choice's translations were not stored), so there is no stored translation to keep.");

            // 2. The trigger, on its own connection (as a preceding test would run it).
            await using (var earlier = await PoolAsync("window-trigger"))
            {
                var name = $"{Prefix}_ppdswinx{suffix}";
                var trigger = new OptionSetMetadata { Name = name, IsGlobal = true, OptionSetType = OptionSetType.Picklist, DisplayName = new Label("Trigger", baseLanguage), Description = new Label("Trigger", baseLanguage) };
                trigger.Options.Add(new OptionMetadata(new Label("One", baseLanguage), null));
                output.WriteLine($"Creating global choice '{name}' (trigger).");
                await Exec(earlier.Pool, new CreateOptionSetRequest { OptionSet = trigger, SolutionUniqueName = Solution });
                try
                {
                    await Exec(earlier.Pool, new PublishXmlRequest { ParameterXml = $"<importexportxml><optionsets><optionset>{name}</optionset></optionsets></importexportxml>" });
                    var read = (OptionSetMetadata)((RetrieveOptionSetResponse)await Exec(earlier.Pool, new RetrieveOptionSetRequest { Name = name, RetrieveAsIfPublished = true })).OptionSetMetadata;
                    read.Description = new Label("Edited", baseLanguage);
                    await Exec(earlier.Pool, new UpdateOptionSetRequest { OptionSet = read, MergeLabels = true, SolutionUniqueName = Solution });
                }
                finally
                {
                    await Exec(earlier.Pool, new DeleteOptionSetRequest { Name = name });
                    output.WriteLine($"Deleted global choice '{name}'.");
                }
            }

            // 3. This process's first table create, then a column carrying a translation: is the window open?
            output.WriteLine($"Creating table '{table}' in solution '{Solution}'.");
            await Exec(main.Pool, new CreateEntityRequest
            {
                Entity = new EntityMetadata { SchemaName = table, DisplayName = new Label("Window table", baseLanguage), DisplayCollectionName = new Label("Window tables", baseLanguage), OwnershipType = OwnershipTypes.UserOwned, IsActivity = false },
                PrimaryAttribute = new StringAttributeMetadata { SchemaName = $"{Prefix}_wname{suffix}", MaxLength = 100, RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None), DisplayName = new Label("Name", baseLanguage) },
                SolutionUniqueName = Solution
            });
            tableCreated = true;
            var local = new OptionSetMetadata { IsGlobal = false, OptionSetType = OptionSetType.Picklist };
            local.Options.Add(new OptionMetadata(Two("Probe", "Sonde"), null));
            var column = $"{Prefix}_wprobe{suffix}";
            await Exec(main.Pool, new CreateAttributeRequest { EntityName = table, SolutionUniqueName = Solution, Attribute = new PicklistAttributeMetadata { SchemaName = column, DisplayName = Two("Probe", "Sonde"), OptionSet = local, RequiredLevel = new AttributeRequiredLevelManagedProperty(AttributeRequiredLevel.None) } });
            var probe = (PicklistAttributeMetadata)((RetrieveAttributeResponse)await Exec(main.Pool, new RetrieveAttributeRequest { EntityLogicalName = table, LogicalName = column, RetrieveAsIfPublished = true })).AttributeMetadata;
            var window = Text(probe.DisplayName, second) is null && Text(probe.OptionSet.Options.Single().Label, second) is null;
            output.WriteLine($"OBSERVED new column with a translation, after the trigger and this process's first table create: display {Pairs(probe.DisplayName)}; option {Pairs(probe.OptionSet.Options.Single().Label)}; window {(window ? "OPEN" : "not open")}");
            var during = await Choice();
            output.WriteLine($"OBSERVED stored translations read in this state: display {Pairs(during.DisplayName)}; option {Pairs(during.Options.Single().Label)}");

            // 4. Base-language writes through the real service, in this state.
            await service.UpdateOptionValueAsync(new PPDS.Dataverse.Metadata.Authoring.UpdateOptionValueRequest { OptionSetName = choice, Value = value, NewLabel = "Closed", SolutionUniqueName = Solution });
            await service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest { SolutionUniqueName = Solution, Name = choice, DisplayName = "Window choice renamed", Description = "Window description changed" });
            var after = await Choice();
            output.WriteLine($"OBSERVED after service writes: display {Pairs(after.DisplayName)}; description {Pairs(after.Description)}; option {Pairs(after.Options.Single().Label)}");
            Text(after.Options.Single().Label, baseLanguage).Should().Be("Closed");
            Text(after.Options.Single().Label, second).Should().Be("Ouvert", "a base-language write keeps the stored translation");
            Text(after.DisplayName, baseLanguage).Should().Be("Window choice renamed");
            Text(after.DisplayName, second).Should().Be("Choix fenêtre", "a base-language write keeps the stored translation");
            Text(after.Description, second).Should().Be("Description fenêtre", "a base-language write keeps the stored translation");

            Skip.IfNot(window, "The window did not open in this process (it opened in 3 of 4 fresh processes); the base-language writes above kept the stored translations, but the dropped-label state was not exercised.");
            Text(probe.OptionSet.Options.Single().Label, baseLanguage).Should().Be("Probe", "in the window the base-language text is still stored");
        }
        finally
        {
            if (tableCreated)
            {
                try { await Exec(main.Pool, new DeleteEntityRequest { LogicalName = table }); output.WriteLine($"Deleted table '{table}'."); }
                catch (Exception ex) { output.WriteLine($"Cleanup failed: table '{table}' was left; delete it manually. {ex.Message}"); throw; }
            }
            try { await Exec(main.Pool, new DeleteOptionSetRequest { Name = choice }); output.WriteLine($"Deleted global choice '{choice}'."); }
            catch (Exception ex) { output.WriteLine($"Cleanup failed: global choice '{choice}' was left; delete it manually. {ex.Message}"); throw; }
        }
    }

    private async Task<OwnedPool> PoolAsync(string name)
    {
        IConnectionSource source = Configuration.HasClientSecretCredentials
            ? await LiveTestHelpers.CreateConnectionSourceAsync(Configuration, name, maxPoolSize: 3)
            : await LiveTestProfileConnectionSource.CreateAsync(Configuration, name, maxPoolSize: 3);
        return new OwnedPool(source, LiveTestHelpers.CreateConnectionPool(new[] { source }));
    }

    private sealed class OwnedPool(IConnectionSource source, DataverseConnectionPool pool) : IAsyncDisposable
    {
        public DataverseConnectionPool Pool { get; } = pool;
        public async ValueTask DisposeAsync() { await Pool.DisposeAsync(); source.Dispose(); }
    }

    private Task<OrganizationResponse> Exec(IDataverseConnectionPool pool, OrganizationRequest request)
        => CustomizationLockRetry.RunAsync(async () =>
        {
            await using var client = await pool.GetClientAsync();
            return await client.ExecuteAsync(request);
        }, request.RequestName, output.WriteLine);

    private sealed class NoShakedown : IShakedownGuard
    {
        public void EnsureCanMutate(string operationDescription)
        {
            if (Environment.GetEnvironmentVariable("PPDS_SHAKEDOWN") == "1")
                throw new PPDS.Cli.Infrastructure.Errors.PpdsException(PPDS.Cli.Infrastructure.Errors.ErrorCodes.Safety.ShakedownActive, $"Shakedown active: '{operationDescription}' refused.");
        }
    }

    public override async Task DisposeAsync()
    {
        Configuration.Dispose();
        await base.DisposeAsync();
    }
}
