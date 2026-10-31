using System.Text.Json;
using System.Text.Json.Nodes;
using PPDS.LiveTests.Cli;
using Xunit;

namespace PPDS.LiveTests.Infrastructure;

/// <summary>Offline regression coverage: fake CLI responses only, no credentials or Dataverse calls.</summary>
public sealed class PluginFixtureCleanupTests
{
    private const string PopulatedInventory = """
        {"success":true,"data":{"assemblies":[{"name":"PPDS.LiveTests.Fixtures","types":[
          {"typeName":"PPDS.LiveTests.Fixtures.TestAccountCreatePlugin","steps":[
            {"message":"Create","entity":"account","stage":"PreOperation","mode":"Synchronous","executionOrder":1,"images":[]}]},
          {"typeName":"PPDS.LiveTests.Fixtures.TestContactUpdatePlugin","steps":[
            {"message":"Update","entity":"contact","stage":"PostOperation","mode":"Asynchronous","executionOrder":1,"images":[
              {"name":"PreImage","entityAlias":"PreImage","imageType":"PreImage","attributes":"firstname,lastname"}]}]}
        ]}],"packages":[]}}
        """;
    private const string EmptyInventory = """
        {"success":true,"data":{"assemblies":[{"name":"PPDS.LiveTests.Fixtures","types":[]}],"packages":[]}}
        """;
    private const string CleanSuccess = """
        {"success":true,"data":[{"assemblyName":"PPDS.LiveTests.Fixtures","stepsDeleted":2,"typesDeleted":2,
          "orphanedSteps":[{"stepId":"11111111-1111-1111-1111-111111111111"}],"orphanedTypes":[],"warnings":[]}]}
        """;

    [Fact]
    public async Task Cleanup_UsesOnlyEmptyFixtureConfigAndPinnedTarget()
    {
        using var harness = new Harness(Ok(PopulatedInventory), Ok(CleanSuccess), Ok(EmptyInventory));
        await harness.Fixture.CleanupAsync();

        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(harness.Path));
        var assemblies = config.RootElement.GetProperty("assemblies");
        Assert.Equal(1, assemblies.GetArrayLength());
        Assert.Equal(PluginFixtureCleanup.AssemblyName, assemblies[0].GetProperty("name").GetString());
        Assert.Equal("Assembly", assemblies[0].GetProperty("type").GetString());
        Assert.Empty(assemblies[0].GetProperty("types").EnumerateArray());
        Assert.Empty(assemblies[0].GetProperty("allTypeNames").EnumerateArray());
        Assert.Equal(["list", "clean", "list"], harness.Calls.Select(args => args[1]));
        Assert.Equal(harness.Path, Value(harness.Calls[1], "--config"));
        Assert.All(harness.Calls, args =>
        {
            Assert.Equal("fixture-profile", Value(args, "--profile"));
            Assert.Equal("https://fixture.crm.dynamics.com", Value(args, "--environment"));
            Assert.Equal("json", Value(args, "--output-format"));
            Assert.DoesNotContain("unregister", args);
        });
        Assert.All(harness.Calls.Where(args => args[1] == "list"), args =>
        {
            Assert.Equal(PluginFixtureCleanup.AssemblyName, Value(args, "--assembly"));
            Assert.Contains("--include-hidden", args);
        });
        Assert.All(harness.Tokens, token => Assert.True(token.CanBeCanceled));
        Assert.Contains(harness.Messages, message => message.Contains("11111111-1111-1111-1111-111111111111", StringComparison.Ordinal));
        Assert.Contains(harness.Messages, message => message.Contains("1 assembly record(s), 0 type(s), 0 step(s)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("assembly")]
    [InlineData("duplicate-assembly")]
    [InlineData("package")]
    [InlineData("type")]
    [InlineData("duplicate-type")]
    [InlineData("extra-step")]
    [InlineData("entity")]
    [InlineData("configuration")]
    [InlineData("image")]
    public async Task Cleanup_RejectsForeignInventoryBeforeMutation(string variant)
    {
        var data = JsonNode.Parse(PopulatedInventory)!["data"]!;
        var assemblies = data["assemblies"]!.AsArray();
        var types = assemblies[0]!["types"]!.AsArray();
        switch (variant)
        {
            case "assembly": assemblies[0]!["name"] = "Other.Assembly"; break;
            case "duplicate-assembly": assemblies.Add(assemblies[0]!.DeepClone()); break;
            case "package": data["packages"]!.AsArray().Add(new JsonObject { ["name"] = "Foreign.Package" }); break;
            case "type": types[0]!["typeName"] = "PPDS.LiveTests.Fixtures.UnapprovedPlugin"; break;
            case "duplicate-type": types.Add(types[0]!.DeepClone()); break;
            case "extra-step": types[0]!["steps"]!.AsArray().Add(types[0]!["steps"]![0]!.DeepClone()); break;
            case "entity": types[0]!["steps"]![0]!["entity"] = "opportunity"; break;
            case "configuration": types[0]!["steps"]![0]!["unsecureConfiguration"] = "foreign configuration"; break;
            case "image": types[1]!["steps"]![0]!["images"]![0]!["name"] = "ForeignImage"; break;
        }
        using var harness = new Harness(Ok(data.Parent!.ToJsonString()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Fixture.CleanupAsync());
        Assert.Single(harness.Calls);
        Assert.Equal("list", harness.Calls[0][1]);
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("structured-failure")]
    [InlineData("wrong-clean-scope")]
    [InlineData("readback-exit")]
    [InlineData("residual")]
    [InlineData("residual-type-only")]
    public async Task Cleanup_RejectsFailureAndResidualRegistrations(string variant)
    {
        var clean = variant switch
        {
            "exit" => new CliResult { ExitCode = 7, StdErr = "cleanup denied" },
            "structured-failure" => Ok("{\"success\":false}"),
            "wrong-clean-scope" => Ok(CleanSuccess.Replace(PluginFixtureCleanup.AssemblyName, "Other.Assembly", StringComparison.Ordinal)),
            _ => Ok(CleanSuccess)
        };
        var readback = variant == "readback-exit" ? new CliResult { ExitCode = 9, StdErr = "inventory unavailable" } : Ok(PopulatedInventory);
        if (variant == "residual-type-only")
        {
            var retainedTypes = JsonNode.Parse(PopulatedInventory)!;
            foreach (var type in retainedTypes["data"]!["assemblies"]![0]!["types"]!.AsArray())
                type!["steps"] = new JsonArray();
            readback = Ok(retainedTypes.ToJsonString());
        }
        using var harness = new Harness(Ok(PopulatedInventory), clean, readback);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Fixture.CleanupAsync());
        var callsBeforeRetry = harness.Calls.Count;
        var retry = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Fixture.CleanupAsync());
        Assert.Contains("refusing another mutation", retry.Message);
        Assert.Equal(callsBeforeRetry, harness.Calls.Count);
    }

    [Theory]
    [InlineData("{\"success\":false}")]
    [InlineData("{\"success\":true,\"data\":{\"assemblies\":[],\"packages\":{}}}")]
    [InlineData("not json")]
    public async Task Cleanup_RejectsMalformedOrFailedInventoryBeforeMutation(string json)
    {
        using var harness = new Harness(Ok(json));
        await Assert.ThrowsAnyAsync<Exception>(() => harness.Fixture.CleanupAsync());
        Assert.Single(harness.Calls);
        Assert.Equal("list", harness.Calls[0][1]);
    }

    [Fact]
    public async Task Cleanup_EmptyAssemblyNeedsNoMutation()
    {
        using var harness = new Harness(Ok(EmptyInventory));
        await harness.Fixture.CleanupAsync();
        Assert.Single(harness.Calls);
        Assert.Contains(harness.Messages, message => message.Contains("assembly records retained", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cleanup_CancelledBeforeStartSendsNoCommand()
    {
        using var harness = new Harness();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Fixture.CleanupAsync(cancellation.Token));
        Assert.Empty(harness.Calls);
    }

    [Fact]
    public async Task Run_PreservesPrimaryFailureAndReportsCleanupFailure()
    {
        using var harness = new Harness(Ok(EmptyInventory), Ok(PopulatedInventory),
            new CliResult { ExitCode = 7, StdErr = "cleanup denied" });
        var original = new InvalidOperationException("original diff failure");
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Fixture.RunAsync(() => Task.FromException(original)));
        Assert.Same(original, thrown);
        Assert.Contains(harness.Messages, message => message.Contains("exit 7: cleanup denied", StringComparison.Ordinal));
        Assert.Single(harness.Calls, args => args[1] == "clean");
    }

    [Fact]
    public async Task Run_SuccessfulBodyStillFailsIfCleanupFails()
    {
        using var harness = new Harness(Ok(EmptyInventory), Ok(PopulatedInventory),
            new CliResult { ExitCode = 7, StdErr = "cleanup denied" });
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Fixture.RunAsync(() => Task.CompletedTask));
        Assert.Contains("Fixture recovery requires manual review", thrown.Message);
        Assert.Contains("exit 7: cleanup denied", thrown.Message);
    }

    [Fact]
    public async Task Run_PreservesPrimaryEvenWhenCleanupReportingFails()
    {
        using var harness = new Harness(Ok(EmptyInventory), Ok(PopulatedInventory),
            new CliResult { ExitCode = 7, StdErr = "cleanup denied" });
        harness.OnReport = message =>
        {
            if (message.StartsWith("Fixture cleanup failed:", StringComparison.Ordinal))
                throw new InvalidOperationException("test output unavailable");
        };
        var original = new InvalidOperationException("original diff failure");
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Fixture.RunAsync(() => Task.FromException(original)));
        Assert.Same(original, thrown);
    }

    [Fact]
    public async Task Run_DoesNotRetryUnverifiedCleanupFromBody()
    {
        using var harness = new Harness(Ok(EmptyInventory), Ok(PopulatedInventory),
            new CliResult { ExitCode = 7, StdErr = "cleanup denied" });
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Fixture.RunAsync(() => harness.Fixture.CleanupAsync()));
        Assert.Contains("exit 7: cleanup denied", thrown.Message);
        Assert.Contains(harness.Messages, message => message.Contains("refusing another mutation", StringComparison.Ordinal));
        Assert.Single(harness.Calls, args => args[1] == "clean");
    }

    private static CliResult Ok(string json) => new() { ExitCode = 0, StdOut = json };
    private static string Value(string[] args, string option) => args[Array.IndexOf(args, option) + 1];

    private sealed class Harness : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ppds-cleanup-unit-{Guid.NewGuid():N}.json");
        internal List<string[]> Calls { get; } = [];
        internal List<CancellationToken> Tokens { get; } = [];
        internal List<string> Messages { get; } = [];
        internal Action<string>? OnReport { get; set; }
        internal PluginFixtureCleanup Fixture { get; }

        internal Harness(params CliResult[] responses)
        {
            var queue = new Queue<CliResult>(responses);
            Fixture = new((token, args) =>
            {
                token.ThrowIfCancellationRequested();
                Calls.Add(args);
                Tokens.Add(token);
                return Task.FromResult(queue.Dequeue());
            }, Path, "fixture-profile", "https://fixture.crm.dynamics.com", message =>
            {
                Messages.Add(message);
                OnReport?.Invoke(message);
            });
        }

        public void Dispose() => File.Delete(Path);
    }
}
