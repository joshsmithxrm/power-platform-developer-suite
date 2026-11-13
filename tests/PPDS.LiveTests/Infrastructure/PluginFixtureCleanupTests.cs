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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_PreflightPreservesTimeoutOrCancellationOutcomeWithoutSecretMessages(bool cancellation)
    {
        const string secret = "dummy-secret-preflight-outcome";
        using var source = new CancellationTokenSource();
        source.Cancel();
        Exception primary = cancellation
            ? new OperationCanceledException(secret, new IOException(secret), source.Token)
            : new TimeoutException(secret, new IOException(secret));
        using var harness = new Harness();
        harness.OnCommand = () => throw primary;
        harness.OnReport = _ => throw new IOException(secret);
        var bodyCalled = false;
        var task = harness.Fixture.RunAsync(() =>
        {
            bodyCalled = true;
            return Task.CompletedTask;
        });
        var actual = await Record.ExceptionAsync(() => task);
        if (cancellation)
        {
            var cancelled = Assert.IsType<OperationCanceledException>(actual);
            Assert.Equal(source.Token, cancelled.CancellationToken);
            Assert.True(task.IsCanceled);
        }
        else
        {
            Assert.IsType<TimeoutException>(actual);
            Assert.True(task.IsFaulted);
        }
        Assert.Same(primary, harness.Fixture.PreflightFailure);
        Assert.Null(actual!.InnerException);
        Assert.DoesNotContain(secret, actual.ToString());
        Assert.All(harness.Messages, message => Assert.DoesNotContain(secret, message));
        Assert.DoesNotContain(secret, await File.ReadAllTextAsync(harness.Fixture.PreflightEvidencePath));
        Assert.False(bodyCalled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Fixture.CleanupAsync());
        Assert.Single(harness.Calls);
    }

    [Fact]
    public async Task Cleanup_ReportsOnlyValidatedCountsAndGuidsOmittingRemoteFreeText()
    {
        const string secret = "dummy-secret-remote-cleanup";
        var response = JsonNode.Parse(CleanSuccess)!;
        var summary = response["data"]![0]!;
        summary["warnings"] = new JsonArray(secret);
        summary["orphanedSteps"]![0]!["stepName"] = secret;
        summary["orphanedSteps"]![0]!["typeName"] = secret;
        summary["unexpectedRemoteField"] = secret;
        using var harness = new Harness(Ok(PopulatedInventory), Ok(response.ToJsonString()), Ok(EmptyInventory));
        await harness.Fixture.CleanupAsync();
        Assert.All(harness.Messages, message => Assert.DoesNotContain(secret, message));
        var message = Assert.Single(harness.Messages, value => value.StartsWith("Fixture cleanup result:", StringComparison.Ordinal));
        using var safe = JsonDocument.Parse(message["Fixture cleanup result: ".Length..]);
        Assert.Equal(2, safe.RootElement.GetProperty("stepsDeleted").GetInt32());
        Assert.Equal(2, safe.RootElement.GetProperty("typesDeleted").GetInt32());
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), safe.RootElement.GetProperty("stepIds")[0].GetGuid());
        Assert.True(safe.RootElement.GetProperty("freeTextOmitted").GetBoolean());
        Assert.Equal(5, safe.RootElement.EnumerateObject().Count());
    }

    [Theory]
    [InlineData("negative-count")]
    [InlineData("excess-count")]
    [InlineData("string-count")]
    [InlineData("invalid-id")]
    [InlineData("empty-id")]
    [InlineData("duplicate-id")]
    public async Task Cleanup_RejectsMalformedDiagnosticCountsAndIdentifiers(string variant)
    {
        const string secret = "dummy-secret-malformed-summary";
        var response = JsonNode.Parse(CleanSuccess)!;
        var summary = response["data"]![0]!;
        switch (variant)
        {
            case "negative-count": summary["stepsDeleted"] = -1; break;
            case "excess-count": summary["stepsDeleted"] = 3; break;
            case "string-count": summary["stepsDeleted"] = secret; break;
            case "invalid-id": summary["orphanedSteps"]![0]!["stepId"] = secret; break;
            case "empty-id": summary["orphanedSteps"]![0]!["stepId"] = Guid.Empty.ToString(); break;
            case "duplicate-id": summary["orphanedSteps"]!.AsArray().Add(summary["orphanedSteps"]![0]!.DeepClone()); break;
        }
        using var harness = new Harness(Ok(PopulatedInventory), Ok(response.ToJsonString()));
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Fixture.CleanupAsync());
        Assert.DoesNotContain(secret, actual.ToString());
        Assert.All(harness.Messages, message => Assert.DoesNotContain(secret, message));
        Assert.DoesNotContain(harness.Messages, message => message.StartsWith("Fixture cleanup result:", StringComparison.Ordinal));
        Assert.Equal(2, harness.Calls.Count);
    }

    [Theory]
    [InlineData("adapter", false)]
    [InlineData("adapter", true)]
    [InlineData("reporter", false)]
    [InlineData("reporter", true)]
    [InlineData("failure-reporter", false)]
    [InlineData("failure-reporter", true)]
    public async Task Run_TeardownDiagnosticsOmitAdapterAndReporterSecretsPreservingBodyFailure(string variant, bool bodyFails)
    {
        const string secret = "dummy-secret-teardown-exception";
        using var harness = new Harness(Ok(EmptyInventory), Ok(PopulatedInventory), Ok(CleanSuccess), Ok(EmptyInventory));
        if (variant != "reporter")
            harness.OnCommand = () =>
            {
                if (harness.Calls.Count == 2) throw new IOException(secret, new Exception(secret));
            };
        harness.OnReport = message =>
        {
            if ((variant == "reporter" && message.StartsWith("Fixture cleanup result:", StringComparison.Ordinal)) ||
                (variant == "failure-reporter" && message.StartsWith("Fixture cleanup failed:", StringComparison.Ordinal)))
                throw new IOException(secret, new Exception(secret));
        };
        var primary = new InvalidOperationException("original body failure");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Fixture.RunAsync(
            () => bodyFails ? Task.FromException(primary) : Task.CompletedTask));
        if (bodyFails) Assert.Same(primary, actual);
        else Assert.Contains("Fixture recovery requires manual review", actual.Message);
        Assert.DoesNotContain(secret, actual.ToString());
        Assert.All(harness.Messages, message => Assert.DoesNotContain(secret, message));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_RefusesMatchingPreexistingRegistrationsBeforeTestOrCleanup(bool typesWithoutSteps)
    {
        var inventory = JsonNode.Parse(PopulatedInventory)!;
        if (typesWithoutSteps)
            foreach (var type in inventory["data"]!["assemblies"]![0]!["types"]!.AsArray())
                type!["steps"] = new JsonArray();
        using var harness = new Harness(Ok(inventory.ToJsonString()));
        await File.WriteAllTextAsync(harness.Path, "existing recovery file");
        var bodyCalls = 0;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Fixture.RunAsync(() =>
        {
            bodyCalls++;
            return Task.CompletedTask;
        }));

        Assert.Contains("preflight refused", failure.Message);
        Assert.Contains("preexisting-registrations", failure.Message);
        Assert.Contains("Preexisting plugin registrations", harness.Fixture.PreflightFailure!.Message);
        Assert.Equal(0, bodyCalls);
        Assert.Single(harness.Calls);
        Assert.Equal("list", harness.Calls[0][1]);
        Assert.Equal("existing recovery file", await File.ReadAllTextAsync(harness.Path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Fixture.CleanupAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Fixture.RunAsync(() => Task.CompletedTask));
        Assert.Single(harness.Calls);
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(harness.Fixture.PreflightEvidencePath));
        Assert.Equal("not-established", evidence.RootElement.GetProperty("ownership").GetString());
        Assert.True(evidence.RootElement.GetProperty("requiresManualReview").GetBoolean());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("{\"success\":false}")]
    [InlineData("{\"success\":true,\"data\":{\"assemblies\":[],\"packages\":[{}]}}")]
    public async Task Run_RefusesUnusablePreflightWithoutTestOrCleanup(string inventory)
    {
        using var harness = new Harness(Ok(inventory));
        var bodyCalled = false;
        await Assert.ThrowsAnyAsync<Exception>(() => harness.Fixture.RunAsync(() =>
        {
            bodyCalled = true;
            return Task.CompletedTask;
        }));
        Assert.False(bodyCalled);
        Assert.Single(harness.Calls);
        Assert.Equal("list", harness.Calls[0][1]);
        Assert.False(File.Exists(harness.Path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Fixture.CleanupAsync());
        Assert.Single(harness.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_PreflightFailureSurvivesEvidenceAndReportingFailures(bool evidenceWriteFails)
    {
        const string secret = "dummy-secret-never-log";
        using var harness = new Harness();
        var primary = new IOException(secret);
        harness.OnCommand = () => throw primary;
        harness.OnReport = _ => throw new InvalidOperationException(secret);
        if (evidenceWriteFails) Directory.CreateDirectory(harness.Fixture.PreflightEvidencePath);
        try
        {
            var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Fixture.RunAsync(() => Task.CompletedTask));
            Assert.Same(primary, harness.Fixture.PreflightFailure);
            Assert.Contains(nameof(Run_PreflightFailureSurvivesEvidenceAndReportingFailures), primary.StackTrace);
            Assert.DoesNotContain(secret, actual.ToString());
            Assert.Null(actual.InnerException);
            Assert.All(harness.Messages, message => Assert.DoesNotContain(secret, message));
            if (!evidenceWriteFails)
                Assert.DoesNotContain(secret, await File.ReadAllTextAsync(harness.Fixture.PreflightEvidencePath));
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Fixture.CleanupAsync());
            Assert.Single(harness.Calls);
        }
        finally
        {
            if (evidenceWriteFails) Directory.Delete(harness.Fixture.PreflightEvidencePath);
        }
    }

    [Theory]
    [InlineData(7, "not json")]
    [InlineData(0, "{\"success\":false}")]
    [InlineData(0, "dummy-secret-never-log")]
    public async Task Run_PreflightDiagnosticsExcludeStderrAndMalformedOutput(int exitCode, string stdout)
    {
        const string secret = "dummy-secret-never-log";
        using var harness = new Harness(new CliResult { ExitCode = exitCode, StdOut = stdout, StdErr = secret });
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Fixture.RunAsync(() => Task.CompletedTask));
        Assert.DoesNotContain(secret, thrown.ToString());
        Assert.All(harness.Messages, message => Assert.DoesNotContain(secret, message));
        Assert.DoesNotContain(secret, await File.ReadAllTextAsync(harness.Fixture.PreflightEvidencePath));
        if (exitCode != 0) Assert.Contains("exit 7", harness.Fixture.PreflightFailure!.Message);
        Assert.Single(harness.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_EmptyPreflightStillExercisesBodyAndOrdinaryTeardown(bool retainedEmptyAssembly)
    {
        var empty = retainedEmptyAssembly ? EmptyInventory : "{\"success\":true,\"data\":{\"assemblies\":[],\"packages\":[]}}";
        using var harness = new Harness(Ok(empty), Ok(PopulatedInventory), Ok(CleanSuccess), Ok(EmptyInventory));
        var bodyCalls = 0;
        await harness.Fixture.RunAsync(() =>
        {
            Assert.False(File.Exists(harness.Path));
            bodyCalls++;
            return Task.CompletedTask;
        });
        Assert.Equal(1, bodyCalls);
        Assert.Equal(["list", "list", "clean", "list"], harness.Calls.Select(args => args[1]));
        Assert.Equal(retainedEmptyAssembly, File.Exists(harness.Fixture.PreflightEvidencePath));
        if (retainedEmptyAssembly)
        {
            using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(harness.Fixture.PreflightEvidencePath));
            Assert.True(evidence.RootElement.GetProperty("preexistingAssembly").GetBoolean());
            Assert.True(evidence.RootElement.GetProperty("deploymentMayUpdatePreexistingAssembly").GetBoolean());
            Assert.True(evidence.RootElement.GetProperty("teardownOwnershipUnresolved").GetBoolean());
            Assert.Equal("not-established", evidence.RootElement.GetProperty("ownership").GetString());
        }
        // This only preserves coverage of the existing teardown. Its ownership/cascade
        // safety contract remains unresolved; an empty preflight is not creation proof.
    }

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

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"unexpected\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("false")]
    public async Task Cleanup_RejectsNonObjectInventoryBeforeMutation(string json)
    {
        using var harness = new Harness(Ok(json));
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Fixture.CleanupAsync());
        Assert.Contains("plugins list did not report structured success", thrown.Message);
        Assert.Single(harness.Calls);
        Assert.Equal("list", harness.Calls[0][1]);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"unexpected\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("false")]
    public async Task Run_PreservesPrimaryFailureWhenCleanupInventoryIsNotObject(string json)
    {
        using var harness = new Harness(Ok(EmptyInventory), Ok(json));
        var original = new InvalidOperationException("original diff failure");
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Fixture.RunAsync(() => Task.FromException(original)));
        Assert.Same(original, thrown);
        Assert.Equal(2, harness.Calls.Count);
        Assert.All(harness.Calls, args => Assert.Equal("list", args[1]));
        Assert.Contains(harness.Messages, message => message == "Fixture cleanup failed: InvalidOperationException");
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
        Assert.Contains(harness.Messages, message => message == "Fixture cleanup failed: InvalidOperationException");
        Assert.Single(harness.Calls, args => args[1] == "clean");
    }

    [Fact]
    public async Task Run_SuccessfulBodyStillFailsIfCleanupFails()
    {
        using var harness = new Harness(Ok(EmptyInventory), Ok(PopulatedInventory),
            new CliResult { ExitCode = 7, StdErr = "cleanup denied" });
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Fixture.RunAsync(() => Task.CompletedTask));
        Assert.Contains("Fixture recovery requires manual review", thrown.Message);
        Assert.Contains("InvalidOperationException", thrown.Message);
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
        Assert.Contains("exit 7", thrown.Message);
        Assert.Contains(harness.Messages, message => message == "Fixture cleanup failed: InvalidOperationException");
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
        internal Action? OnCommand { get; set; }
        internal PluginFixtureCleanup Fixture { get; }

        internal Harness(params CliResult[] responses)
        {
            var queue = new Queue<CliResult>(responses);
            Fixture = new((token, args) =>
            {
                token.ThrowIfCancellationRequested();
                Calls.Add(args);
                Tokens.Add(token);
                OnCommand?.Invoke();
                return Task.FromResult(queue.Dequeue());
            }, Path, "fixture-profile", "https://fixture.crm.dynamics.com", message =>
            {
                Messages.Add(message);
                OnReport?.Invoke(message);
            });
        }

        public void Dispose()
        {
            File.Delete(Path);
            File.Delete(Fixture.PreflightEvidencePath);
        }
    }
}
