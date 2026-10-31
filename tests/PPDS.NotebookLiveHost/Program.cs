using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nerdbank.Streams;
using PPDS.Auth.Profiles;
using PPDS.Auth.Credentials;
using PPDS.Cli.Commands.Serve.Handlers;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Services;
using PPDS.Cli.Services.History;
using StreamJsonRpc;

// Validation bootstrap only. Controller, DaemonClient, RPC handlers, identity
// resolution, pools and query engine are unchanged. No credential cache copies.
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var planPath = Environment.GetEnvironmentVariable("PPDS_NOTEBOOK_LIVE_PLAN")
    ?? throw new InvalidOperationException("An approved notebook live plan is required.");
var plan = JsonSerializer.Deserialize<LivePlan>(await File.ReadAllTextAsync(planPath), options)
    ?? throw new InvalidOperationException("Invalid plan.");
plan.Validate();
// Global Discovery checks this before opening a browser. Only InteractiveBrowser
// profiles are permitted below, so it cannot fall back to a device-code flow.
Environment.SetEnvironmentVariable("CI", "true");
var actualHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(RpcMethodHandler).Assembly.Location)));
if (!actualHash.Equals(plan.CandidateSha256, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Loaded CLI binary does not match the reviewed candidate hash.");
if (!Path.GetFullPath(ProfilePaths.DataDirectory).Equals(Path.GetFullPath(plan.CredentialConfigDirectory), StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Credential configuration path does not match the plan.");
var metadataDirectory = Environment.GetEnvironmentVariable("PPDS_NOTEBOOK_LIVE_METADATA")
    ?? throw new InvalidOperationException("Isolated metadata directory is required.");
if (Directory.Exists(metadataDirectory)) throw new InvalidOperationException("Metadata directory must be new.");
Directory.CreateDirectory(metadataDirectory);
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(7));
var ct = deadline.Token;
var metadataOptions = new JsonSerializerOptions(options);
metadataOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
var profileJson = await File.ReadAllTextAsync(Path.Combine(plan.CredentialConfigDirectory, ProfilePaths.ProfilesFileName), ct);
using var profileDocument = JsonDocument.Parse(profileJson);
if (!profileDocument.RootElement.TryGetProperty("version", out var profileVersion) || profileVersion.GetInt32() != 2)
    throw new InvalidOperationException("Only existing v2 metadata is supported; never migrate the original profile store.");
// Do not call LoadAsync on original stores: legacy migration/deduplication can write.
var original = JsonSerializer.Deserialize<ProfileCollection>(profileJson, metadataOptions)
    ?? throw new InvalidOperationException("Invalid profile metadata.");
var selected = plan.Targets.Select(t => original.GetByNameOrIndex(t.ProfileName)?.Clone()
    ?? throw new InvalidOperationException("An explicitly approved profile is unavailable.")).DistinctBy(p => p.Index).ToList();
if (plan.Targets.Any(t => !selected.Any(p => p.Name == t.ProfileName)))
    throw new InvalidOperationException("This fixture requires exact existing profile names, not mutable indices.");
if (selected.Any(p => p.AuthMethod != AuthMethod.InteractiveBrowser))
    throw new InvalidOperationException("This fixture currently requires existing InteractiveBrowser profiles with cached authentication.");
using var profiles = new ProfileStore(Path.Combine(metadataDirectory, ProfilePaths.ProfilesFileName));
var collection = new ProfileCollection { Profiles = selected, ActiveProfileIndex = selected[0].Index, ActiveProfileName = selected[0].Name };
await profiles.SaveAsync(collection, ct);
var originalConfigPath = Path.Combine(plan.CredentialConfigDirectory, ProfilePaths.EnvironmentsFileName);
var config = File.Exists(originalConfigPath)
    ? JsonSerializer.Deserialize<EnvironmentConfigCollection>(await File.ReadAllTextAsync(originalConfigPath, ct), metadataOptions)
        ?? throw new InvalidOperationException("Invalid environment metadata.")
    : new EnvironmentConfigCollection();
// Preserve real policy; never downgrade Production/Unknown prompts.
config.Environments = config.Environments.Where(e => plan.ContainsUrl(e.Url)).ToList();
var isolatedConfigPath = Path.Combine(metadataDirectory, ProfilePaths.EnvironmentsFileName);
var configOptions = new JsonSerializerOptions(options);
configOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
await File.WriteAllTextAsync(isolatedConfigPath, JsonSerializer.Serialize(config, configOptions), ct);
var services = new ServiceCollection();
services.AddLogging();
services.AddCliApplicationServices();
services.RemoveAll<ProfileStore>();
services.AddSingleton(profiles);
services.RemoveAll<EnvironmentConfigStore>();
services.AddSingleton(new EnvironmentConfigStore(isolatedConfigPath));
// History persistence is not under test; never alter the user's history.
services.RemoveAll<IQueryHistoryService>();
await using var provider = services.BuildServiceProvider();
await using var pools = new DaemonConnectionPoolManager(null, profiles.LoadAsync, TimeSpan.FromSeconds(90),
    beforeInteractiveAuth: _ => PreAuthDialogResult.Cancel);
using var handler = new RpcMethodHandler(pools, provider);
var guarded = new ReadOnlyFixtureRpc(handler, profiles, plan, ct);
await guarded.SelectAsync(plan.Targets[0].ProfileName, plan.Targets[0].Url);
using var input = Console.OpenStandardInput();
using var output = Console.OpenStandardOutput();
var duplex = FullDuplexStream.Splice(input, output);
using var messages = new HeaderDelimitedMessageHandler(duplex, duplex, new SystemTextJsonFormatter());
using var rpc = new JsonRpc(messages);
rpc.AddLocalRpcTarget(guarded);
handler.SetRpcContext(rpc);
using var stop = ct.Register(rpc.Dispose);
Console.Error.WriteLine("[notebook live fixture] Production RPC pipeline; isolated metadata bootstrap; fixed organization reads only.");
rpc.StartListening();
await rpc.Completion;

internal sealed record LiveTarget(string Url, string ProfileName);
internal sealed record LivePlan(string Confirmation, string CandidateSha256, string CredentialConfigDirectory, LiveTarget[] Targets)
{
    public static string Normalize(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new InvalidOperationException("Only explicit HTTPS organization origins are permitted.");
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }
    public bool ContainsUrl(string value) => Targets.Any(t => Normalize(t.Url) == Normalize(value));
    public void Validate()
    {
        if (Confirmation != "read-only-two-environment-notebook-demo" || Targets.Length != 2 ||
            Targets.Select(t => Normalize(t.Url)).Distinct().Count() != 2 || Targets.Any(t => string.IsNullOrWhiteSpace(t.ProfileName)) ||
            CandidateSha256.Length != 64 || !CandidateSha256.All(Uri.IsHexDigit) || !Path.IsPathFullyQualified(CredentialConfigDirectory))
            throw new InvalidOperationException("The plan must name two distinct approved environments, explicit profiles and a pinned candidate.");
    }
}

/// <summary>Allowlisted transport: all RPC results come from the production handler.</summary>
internal sealed class ReadOnlyFixtureRpc(RpcMethodHandler handler, ProfileStore profiles, LivePlan plan, CancellationToken lifetime)
{
    public const string Sql = "SELECT TOP 1 organizationid, name FROM organization";
    public const string Fetch = "<fetch top=\"1\"><entity name=\"organization\"><attribute name=\"organizationid\"/><attribute name=\"name\"/></entity></fetch>";
    private int _queryCount;

    [JsonRpcMethod("auth/list")]
    public Task<AuthListResponse> ListAsync(CancellationToken cancellationToken) => handler.AuthListAsync(cancellationToken);
    [JsonRpcMethod("auth/who")]
    public Task<AuthWhoResponse> WhoAsync(CancellationToken cancellationToken) => handler.AuthWhoAsync(cancellationToken);
    [JsonRpcMethod("_heartbeat")]
    public Task<RpcMethodHandler.HeartbeatResponse> HeartbeatAsync() => handler.HeartbeatAsync();
    [JsonRpcMethod("env/list")]
    public async Task<EnvListResponse> EnvironmentsAsync(string? filter = null, bool forceRefresh = false,
        string? profileName = null, CancellationToken cancellationToken = default)
    {
        if (profileName is null || !plan.Targets.Any(t => t.ProfileName == profileName))
            throw new InvalidOperationException("Environment discovery requires an explicitly approved profile.");
        var result = await handler.EnvListAsync(filter, forceRefresh, profileName, cancellationToken);
        result.Environments = result.Environments.Where(e => plan.ContainsUrl(e.ApiUrl)).ToList();
        return result;
    }
    [JsonRpcMethod("env/config/get")]
    public Task<EnvConfigGetResponse> ConfigAsync(string environmentUrl, CancellationToken cancellationToken = default)
    {
        if (!plan.ContainsUrl(environmentUrl)) throw new InvalidOperationException("Unapproved environment.");
        return handler.EnvConfigGetAsync(environmentUrl, cancellationToken);
    }
    [JsonRpcMethod("query/sql")]
    public async Task<QueryResultResponse> SqlAsync(QuerySqlRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Sql != Sql || request.UseTds || request.DmlSafety is not null || request.Count ||
            request.PagingCookie is not null || request.Page is > 1 || request.Top is > 1)
            throw new InvalidOperationException("Only the fixed one-row organization SELECT is permitted.");
        await ValidateQueryAsync(request.EnvironmentUrl, request.ProfileName, request.ProfileIdentity);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        bounded.CancelAfter(TimeSpan.FromSeconds(90));
        return await handler.QuerySqlAsync(request, bounded.Token);
    }
    [JsonRpcMethod("query/fetch")]
    public async Task<QueryResultResponse> FetchAsync(QueryFetchRequest request, CancellationToken cancellationToken = default)
    {
        if (request.FetchXml != Fetch || request.Count || request.PagingCookie is not null || request.Page is > 1 || request.Top is > 1)
            throw new InvalidOperationException("Only the fixed one-row organization FetchXML is permitted.");
        await ValidateQueryAsync(request.EnvironmentUrl, request.ProfileName, request.ProfileIdentity);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        bounded.CancelAfter(TimeSpan.FromSeconds(90));
        return await handler.QueryFetchAsync(request, bounded.Token);
    }
    private async Task ValidateQueryAsync(string? url, string? profile, string? identity)
    {
        if (url is null || !plan.ContainsUrl(url) || profile is null || string.IsNullOrEmpty(identity) ||
            !plan.Targets.Any(t => t.ProfileName == profile && LivePlan.Normalize(t.Url) == LivePlan.Normalize(url)) ||
            Interlocked.Increment(ref _queryCount) > 8)
            throw new InvalidOperationException("Query target/profile must be approved; maximum eight queries per host.");
        var snapshot = (await profiles.LoadAsync(lifetime)).GetByNameOrIndex(profile)?.Clone()
            ?? throw new InvalidOperationException("Approved profile is unavailable.");
        // Use the exact candidate's non-secret fingerprint implementation, never a reimplementation.
        var expected = RpcProfileIdentity.Create(snapshot);
        if (!string.Equals(expected, identity, StringComparison.Ordinal))
            throw new InvalidOperationException("Captured identity does not belong to the approved profile/target pair.");
        // The production handler independently resolves the same opaque identity before pool creation.
    }
    [JsonRpcMethod("fixture/select")]
    public async Task SelectAsync(string profileName, string environmentUrl)
    {
        if (!plan.Targets.Any(t => t.ProfileName == profileName && LivePlan.Normalize(t.Url) == LivePlan.Normalize(environmentUrl)))
            throw new InvalidOperationException("Only an approved profile/target pair can become the isolated default.");
        var collection = await profiles.LoadAsync(lifetime);
        var profile = collection.GetByNameOrIndex(profileName) ?? throw new InvalidOperationException("Profile unavailable.");
        collection.ActiveProfileIndex = profile.Index;
        collection.ActiveProfileName = profile.Name;
        if (profile.Environment is null || LivePlan.Normalize(profile.Environment.Url) != LivePlan.Normalize(environmentUrl))
            profile.Environment = new PPDS.Auth.Profiles.EnvironmentInfo { Url = LivePlan.Normalize(environmentUrl), DisplayName = LivePlan.Normalize(environmentUrl) };
        await profiles.SaveAsync(collection, lifetime);
    }
}
