using System.Text.Json;
using PPDS.LiveTests.Cli;

namespace PPDS.LiveTests.Infrastructure;

/// <summary>Bounded teardown for the fixed CLI plugin fixture; never unregisters an assembly.</summary>
internal sealed class PluginFixtureCleanup(
    Func<CancellationToken, string[], Task<CliResult>> runCli,
    string configPath,
    string profile,
    string environment,
    Action<string> report)
{
    internal const string AssemblyName = "PPDS.LiveTests.Fixtures";
    internal const string EmptyConfigJson = """
        {
          "version": "1.0",
          "assemblies": [{
            "name": "PPDS.LiveTests.Fixtures",
            "type": "Assembly",
            "path": "PPDS.LiveTests.Fixtures.dll",
            "allTypeNames": [],
            "types": []
          }]
        }
        """;

    private bool _unverifiedCleanup;

    /// <summary>Starts empty, executes the test, then verifies teardown while retaining primary failures.</summary>
    internal async Task RunAsync(Func<Task> test)
    {
        await CleanupAsync();
        var failure = new FixtureFailureEvidence();
        var cleanupFailures = new List<string>();
        try
        {
            await test();
        }
        catch (Exception exception)
        {
            failure.CapturePrimary(exception);
        }
        finally
        {
            try { await CleanupAsync(); }
            catch (Exception exception)
            {
                var detail = exception.GetType().Name + ": " + exception.Message;
                cleanupFailures.Add(detail);
                try { report("Fixture cleanup failed: " + detail); }
                catch (Exception reportingFailure)
                {
                    cleanupFailures.Add("Cleanup reporting failed: " + reportingFailure.Message);
                }
            }
        }
        failure.ThrowIfFailed(cleanupFailures, []);
    }

    internal async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        if (_unverifiedCleanup)
            throw new InvalidOperationException("A prior fixture cleanup did not verify; refusing another mutation. Inspect retained evidence.");
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        token.ThrowIfCancellationRequested();

        // The file is regenerated from a literal allowlist, never from the populated deploy config.
        await File.WriteAllTextAsync(configPath, EmptyConfigJson, token);
        var before = await ReadInventoryAsync(token);
        report($"Fixture cleanup preflight: {before.Assemblies} assembly record(s), {before.Types} type(s), {before.Steps} step(s).");
        if (before.Types == 0)
        {
            report("Fixture registrations verified absent; assembly records retained.");
            return;
        }

        // Never retry a cleanup whose write/readback outcome is unknown, including in finally.
        _unverifiedCleanup = true;
        var clean = await runCli(token, ["plugins", "clean", "--config", configPath,
            "--profile", profile, "--environment", environment, "--output-format", "json"]);
        using var document = ParseSuccess(clean, "plugins clean");
        var summaries = RequireArray(document.RootElement.GetProperty("data"), "cleanup summaries");
        if (summaries.GetArrayLength() != 1 || summaries[0].GetProperty("assemblyName").GetString() != AssemblyName)
            throw new InvalidOperationException("Cleanup returned an unexpected assembly scope.");
        // This result contains only deletion counts and fixture registration IDs/names, not auth arguments.
        report("Fixture cleanup result: " + summaries[0].GetRawText());
        var after = await ReadInventoryAsync(token);
        report($"Fixture cleanup readback: {after.Assemblies} assembly record(s), {after.Types} type(s), {after.Steps} step(s).");
        if (after.Types != 0 || after.Steps != 0)
            throw new InvalidOperationException($"Fixture cleanup left {after.Types} type(s) and {after.Steps} step(s); assembly records are intentionally retained.");
        _unverifiedCleanup = false;
    }

    private async Task<Inventory> ReadInventoryAsync(CancellationToken cancellationToken)
    {
        var result = await runCli(cancellationToken, ["plugins", "list", "--assembly", AssemblyName,
            "--include-hidden", "--profile", profile, "--environment", environment, "--output-format", "json"]);
        using var document = ParseSuccess(result, "plugins list");
        var data = document.RootElement.GetProperty("data");
        var assemblies = RequireArray(data.GetProperty("assemblies"), "assemblies");
        var packages = RequireArray(data.GetProperty("packages"), "packages");
        if (assemblies.GetArrayLength() > 1 || packages.GetArrayLength() != 0)
            throw new InvalidOperationException("Fixture inventory has unexpected assemblies or packages; refusing cleanup.");
        var typeNames = new HashSet<string>(StringComparer.Ordinal);
        var stepCount = 0;
        foreach (var assembly in assemblies.EnumerateArray())
        {
            if (assembly.GetProperty("name").GetString() != AssemblyName)
                throw new InvalidOperationException("Fixture inventory contains another assembly; refusing cleanup.");
            foreach (var type in RequireArray(assembly.GetProperty("types"), "types").EnumerateArray())
            {
                var name = type.GetProperty("typeName").GetString() ?? "";
                var account = name == AssemblyName + ".TestAccountCreatePlugin";
                if ((!account && name != AssemblyName + ".TestContactUpdatePlugin") || !typeNames.Add(name))
                    throw new InvalidOperationException("Fixture inventory contains an unexpected or duplicate type; refusing cleanup.");
                var steps = RequireArray(type.GetProperty("steps"), "steps");
                if (steps.GetArrayLength() > 1)
                    throw new InvalidOperationException("Fixture inventory contains extra steps; refusing cleanup.");
                foreach (var step in steps.EnumerateArray())
                {
                    if (step.GetProperty("message").GetString() != (account ? "Create" : "Update") ||
                        step.GetProperty("entity").GetString() != (account ? "account" : "contact") ||
                        step.GetProperty("stage").GetString() != (account ? "PreOperation" : "PostOperation") ||
                        step.GetProperty("mode").GetString() != (account ? "Synchronous" : "Asynchronous") ||
                        step.GetProperty("executionOrder").GetInt32() != 1 ||
                        HasText(step, "filteringAttributes") || HasText(step, "unsecureConfiguration") || HasText(step, "runAsUser"))
                        throw new InvalidOperationException("Fixture inventory contains a step outside the declared registration; refusing cleanup.");
                    var images = RequireArray(step.GetProperty("images"), "images");
                    if (images.GetArrayLength() > (account ? 0 : 1))
                        throw new InvalidOperationException("Fixture inventory contains extra images; refusing cleanup.");
                    foreach (var image in images.EnumerateArray())
                    {
                        var attributes = image.GetProperty("attributes").GetString()?.Split(',').Select(value => value.Trim()).ToHashSet(StringComparer.Ordinal);
                        if (image.GetProperty("name").GetString() != "PreImage" || image.GetProperty("imageType").GetString() != "PreImage" ||
                            image.GetProperty("entityAlias").GetString() != "PreImage" || attributes is null || !attributes.SetEquals(["firstname", "lastname"]))
                            throw new InvalidOperationException("Fixture inventory contains an image outside the declared registration; refusing cleanup.");
                    }
                    stepCount++;
                }
            }
        }
        return new(assemblies.GetArrayLength(), typeNames.Count, stepCount);
    }

    private static bool HasText(JsonElement value, string property)
        => value.TryGetProperty(property, out var text) && text.ValueKind != JsonValueKind.Null && !string.IsNullOrEmpty(text.GetString());

    private static JsonElement RequireArray(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Array ? value : throw new InvalidOperationException($"Malformed fixture {name}; refusing cleanup.");

    private static JsonDocument ParseSuccess(CliResult result, string operation)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{operation} failed with exit {result.ExitCode}: {result.StdErr}");
        var document = JsonDocument.Parse(result.StdOut);
        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException($"{operation} did not report structured success: {result.StdErr}");
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private sealed record Inventory(int Assemblies, int Types, int Steps);
}
