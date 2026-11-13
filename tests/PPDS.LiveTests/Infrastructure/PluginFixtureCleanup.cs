using System.Text.Json;
using PPDS.LiveTests.Cli;

namespace PPDS.LiveTests.Infrastructure;

/// <summary>Bounded teardown for the fixed CLI plugin fixture; never unregisters an assembly.</summary>
internal sealed class PluginFixtureCleanup(
    Func<CancellationToken, string[], Task<CliResult>> runCli,
    string configPath,
    string profile,
    string environment,
    Action<string> report,
    Func<bool>? requiresManualReview = null)
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
    private bool _preflightRefused;

    internal string PreflightEvidencePath => configPath + ".preflight.json";
    internal Exception? PreflightFailure { get; private set; }

    /// <summary>Refuses preexisting registrations before executing the test; preserves primary failures.</summary>
    internal async Task RunAsync(Func<Task> test)
    {
        // Setup must never delete registrations belonging to an earlier run. This read-only
        // check is not a creation receipt and does not establish ownership of later inventory.
        // Teardown still needs a receipt-aware, non-expanding product deletion contract.
        await RequireEmptyPreflightAsync();
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
                var detail = exception.GetType().Name;
                cleanupFailures.Add(detail);
                try { report("Fixture cleanup failed: " + detail); }
                catch (Exception reportingFailure)
                {
                    cleanupFailures.Add("Cleanup reporting failed: " + reportingFailure.GetType().Name);
                }
            }
        }
        failure.ThrowIfFailed(cleanupFailures, []);
    }

    private async Task RequireEmptyPreflightAsync()
    {
        if (_preflightRefused)
            throw new InvalidOperationException("Plugin fixture preflight previously refused; manual review required.");
        var refusalReason = "preflight-not-verified";
        try
        {
            if (requiresManualReview?.Invoke() == true)
                throw new InvalidOperationException("CLI fixture was interrupted; plugin preflight skipped.");
            ArgumentException.ThrowIfNullOrWhiteSpace(profile);
            ArgumentException.ThrowIfNullOrWhiteSpace(environment);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var before = await ReadInventoryAsync(deadline.Token);
            if (before.Types != 0 || before.Steps != 0)
            {
                refusalReason = "preexisting-registrations";
                throw new InvalidOperationException("Preexisting plugin registrations found; refusing test mutation and cleanup. Matching names or shapes do not establish ownership.");
            }
            if (before.Assemblies != 0)
            {
                // Compatibility with the existing retained-assembly fixture. This does not
                // authorize ownership: deploy may update this preexisting assembly's bytes.
                File.WriteAllText(PreflightEvidencePath, JsonSerializer.Serialize(new
                {
                    phase = "preflight",
                    assemblyName = AssemblyName,
                    preexistingAssembly = true,
                    ownership = "not-established",
                    deploymentMayUpdatePreexistingAssembly = true,
                    teardownOwnershipUnresolved = true
                }));
                report($"Preexisting empty fixture assembly is unowned; deploy may update its bytes. Teardown ownership remains unresolved. Evidence: {PreflightEvidencePath}");
            }
        }
        catch (Exception exception)
        {
            _preflightRefused = true;
            PreflightFailure = exception;
            // Safe recovery context only: no inventory payload, command arguments, credentials,
            // adapter exception messages, or inferred ownership. Persistence/report failure must
            // not replace the actual preflight failure or clear the refusal.
            try
            {
                File.WriteAllText(PreflightEvidencePath, JsonSerializer.Serialize(new
                {
                    phase = "preflight",
                    requiresManualReview = true,
                    assemblyName = AssemblyName,
                    ownership = "not-established",
                    reason = refusalReason,
                    failureType = exception.GetType().FullName
                }));
            }
            catch (Exception) { /* The refusal remains latched if evidence cannot be written. */ }
            try { report($"Plugin preflight refused; no test mutation or cleanup attempted. Recovery evidence: {PreflightEvidencePath}"); }
            catch (Exception) { /* Preserve the preflight failure. */ }
            // xUnit renders exception messages and inner exceptions. Preserve the original
            // only in memory; neither its message nor its inner chain is safe diagnostics.
            throw SafeFailure(exception, $"Plugin fixture preflight refused: {refusalReason}; automatic cleanup prohibited. Inspect retained evidence.");
        }
    }

    internal async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        if (_preflightRefused)
            throw new InvalidOperationException("Plugin fixture preflight refused; automatic cleanup prohibited.");
        if (requiresManualReview?.Invoke() == true)
            throw new InvalidOperationException("CLI fixture was interrupted; automatic plugin cleanup skipped. Inspect retained evidence before manual recovery.");
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
        var before = await ReadInventoryAsync(token, sanitizeAdapterFailure: true);
        ReportSafely($"Fixture cleanup preflight: {before.Assemblies} assembly record(s), {before.Types} type(s), {before.Steps} step(s).");
        if (before.Types == 0)
        {
            ReportSafely("Fixture registrations verified absent; assembly records retained.");
            return;
        }

        // Never retry a cleanup whose write/readback outcome is unknown, including in finally.
        _unverifiedCleanup = true;
        var clean = await RunCliSafelyAsync(token, ["plugins", "clean", "--config", configPath,
            "--profile", profile, "--environment", environment, "--output-format", "json"]);
        using var document = ParseSuccess(clean, "plugins clean");
        var summaries = RequireArray(document.RootElement.GetProperty("data"), "cleanup summaries");
        if (summaries.GetArrayLength() != 1 || summaries[0].GetProperty("assemblyName").GetString() != AssemblyName)
            throw new InvalidOperationException("Cleanup returned an unexpected assembly scope.");
        // The response also contains remote names/warnings. Only validated numeric counts
        // and GUIDs may cross the diagnostic boundary; none of these IDs establishes ownership.
        var summary = summaries[0];
        var stepsDeleted = ReadCount(summary, "stepsDeleted", before.Steps);
        var typesDeleted = ReadCount(summary, "typesDeleted", before.Types);
        var stepIds = ReadIds(summary, "orphanedSteps", "stepId", before.Steps);
        var typeIds = ReadIds(summary, "orphanedTypes", "typeId", before.Types);
        ReportSafely("Fixture cleanup result: " + JsonSerializer.Serialize(new
        {
            stepsDeleted, typesDeleted, stepIds, typeIds, freeTextOmitted = true
        }));
        var after = await ReadInventoryAsync(token, sanitizeAdapterFailure: true);
        ReportSafely($"Fixture cleanup readback: {after.Assemblies} assembly record(s), {after.Types} type(s), {after.Steps} step(s).");
        if (after.Types != 0 || after.Steps != 0)
            throw new InvalidOperationException($"Fixture cleanup left {after.Types} type(s) and {after.Steps} step(s); assembly records are intentionally retained.");
        _unverifiedCleanup = false;
    }

    private static Exception SafeFailure(Exception original, string safeMessage) => original switch
    {
        OperationCanceledException cancelled => new OperationCanceledException(safeMessage, null, cancelled.CancellationToken),
        TimeoutException => new TimeoutException(safeMessage),
        _ => new InvalidOperationException(safeMessage + " Failure type: " + original.GetType().Name)
    };

    private async Task<CliResult> RunCliSafelyAsync(CancellationToken token, string[] args)
    {
        try { return await runCli(token, args); }
        catch (Exception exception) { throw SafeFailure(exception, "Plugin fixture CLI operation failed."); }
    }

    private void ReportSafely(string message)
    {
        try { report(message); }
        catch (Exception exception) { throw SafeFailure(exception, "Plugin fixture reporting failed."); }
    }

    private static int ReadCount(JsonElement summary, string property, int maximum)
    {
        var count = summary.GetProperty(property);
        if (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var value) || value < 0 || value > maximum)
            throw new InvalidOperationException("Cleanup returned an invalid deletion count.");
        return value;
    }

    private static Guid[] ReadIds(JsonElement summary, string property, string idProperty, int maximum)
    {
        var rows = RequireArray(summary.GetProperty(property), "cleanup identifiers");
        if (rows.GetArrayLength() > maximum)
            throw new InvalidOperationException("Cleanup returned too many identifiers.");
        var ids = new HashSet<Guid>();
        foreach (var row in rows.EnumerateArray())
        {
            var value = row.GetProperty(idProperty);
            if (value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var id) || id == Guid.Empty || !ids.Add(id))
                throw new InvalidOperationException("Cleanup returned an invalid or duplicate identifier.");
        }
        return ids.ToArray();
    }

    private async Task<Inventory> ReadInventoryAsync(CancellationToken cancellationToken, bool sanitizeAdapterFailure = false)
    {
        string[] args = ["plugins", "list", "--assembly", AssemblyName,
            "--include-hidden", "--profile", profile, "--environment", environment, "--output-format", "json"];
        var result = sanitizeAdapterFailure
            ? await RunCliSafelyAsync(cancellationToken, args)
            : await runCli(cancellationToken, args);
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
            throw new InvalidOperationException($"{operation} failed with exit {result.ExitCode}.");
        JsonDocument document;
        try { document = JsonDocument.Parse(result.StdOut); }
        catch (JsonException) { throw new InvalidOperationException($"{operation} returned malformed JSON."); }
        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException($"{operation} did not report structured success.");
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
