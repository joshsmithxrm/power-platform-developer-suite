using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Services.WebResources;
using PPDS.Dataverse.Models;
using Xunit;

namespace PPDS.Cli.Tests.Services.WebResources;

public class WebResourceSyncServiceTests : IDisposable
{
    private readonly string _folder;
    private readonly FakeWebResourceService _fake;
    private readonly WebResourceSyncService _service;

    public WebResourceSyncServiceTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "ppds-sync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        _fake = new FakeWebResourceService();
        _service = new WebResourceSyncService(_fake, NullLogger<WebResourceSyncService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best effort */ }
    }

    private PullOptions PullOpts(
        bool stripPrefix = false,
        bool force = false,
        int[]? typeCodes = null,
        string? namePattern = null,
        Guid? solutionId = null,
        string? solutionName = null,
        string envUrl = "https://org.crm.dynamics.com")
        => new(_folder, envUrl, solutionId, solutionName, typeCodes, namePattern, stripPrefix, force);

    private PushOptions PushOpts(
        bool force = false,
        bool dryRun = false,
        bool publish = false,
        string envUrl = "https://org.crm.dynamics.com")
        => new(_folder, envUrl, force, dryRun, publish);

    /// <summary>AC-WR-34: pull writes files preserving hierarchical path structure.</summary>
    [Fact]
    public async Task PullCreatesDirectoryStructure()
    {
        _fake.AddText("new_/scripts/app.js", "alert(1);");
        _fake.AddText("new_/styles/site.css", "body { color: red; }");

        var result = await _service.PullAsync(PullOpts(), null);

        result.Pulled.Should().HaveCount(2);
        File.Exists(Path.Combine(_folder, "new_", "scripts", "app.js")).Should().BeTrue();
        File.Exists(Path.Combine(_folder, "new_", "styles", "site.css")).Should().BeTrue();
        result.TotalServerCount.Should().Be(2);
    }

    /// <summary>AC-WR-35: tracking file is written with version, environmentUrl, solution, resources.</summary>
    [Fact]
    public async Task PullCreatesTrackingFile()
    {
        _fake.AddText("new_/scripts/app.js", "x");

        await _service.PullAsync(PullOpts(solutionName: "core"), null);

        var tracking = await WebResourceTrackingFile.ReadAsync(_folder);
        tracking.Should().NotBeNull();
        tracking!.Version.Should().Be(1);
        tracking.EnvironmentUrl.Should().Be("https://org.crm.dynamics.com");
        tracking.Solution.Should().Be("core");
        tracking.Resources.Should().ContainKey("new_/scripts/app.js");
    }

    /// <summary>AC-WR-37: strip-prefix removes publisher prefix from local paths.</summary>
    [Fact]
    public async Task StripPrefixRemovesPublisherPrefix()
    {
        _fake.AddText("new_/scripts/app.js", "x");

        var result = await _service.PullAsync(PullOpts(stripPrefix: true), null);

        result.Pulled.Should().ContainSingle();
        result.Pulled[0].LocalPath.Should().Be(Path.Combine("scripts", "app.js"));
        File.Exists(Path.Combine(_folder, "scripts", "app.js")).Should().BeTrue();
        File.Exists(Path.Combine(_folder, "new_", "scripts", "app.js")).Should().BeFalse();
    }

    /// <summary>AC-WR-38: parallel downloads — multiple GetContentAsync calls observed concurrently.</summary>
    // SUGGESTION: This already uses the deterministic counter pattern requested
    // (FakeWebResourceService.GetContentAsync increments/decrements _currentParallel via
    // Interlocked and tracks PeakConcurrency). The 80ms latency only ensures the in-flight
    // window overlaps during the run; the assertion observes concurrency directly, not
    // wall-clock time. BeGreaterThan(1) is the loosest claim we can make without coupling
    // to thread-pool scheduling (the service's MaxParallelism is intentionally an upper
    // bound, not a guarantee of how many slots fill at any instant on small inputs).
    [Fact]
    public async Task PullDownloadsInParallel()
    {
        for (int i = 0; i < 6; i++)
        {
            _fake.AddText($"new_/file{i}.js", $"// file {i}");
        }
        _fake.SimulateContentLatencyMs = 80;
        _fake.TrackConcurrency = true;
        var progress = new RecordingOperationProgress();

        await _service.PullAsync(PullOpts(), progress);

        _fake.PeakConcurrency.Should().BeGreaterThan(1);
        _fake.GetContentCallCount.Should().Be(6);
        // Progress reporting (AC-WR-38): one ReportProgress call per resource,
        // current values cover 1..6, total is 6 for every call.
        progress.ProgressCalls.Should().HaveCount(6);
        progress.ProgressCalls.Select(c => c.Current).OrderBy(c => c).Should().Equal(1, 2, 3, 4, 5, 6);
        progress.ProgressCalls.Should().OnlyContain(c => c.Total == 6);
    }

    /// <summary>AC-WR-39: pull without --force skips files with local hash drift.</summary>
    [Fact]
    public async Task PullSkipsLocallyModifiedFiles()
    {
        _fake.AddText("new_/scripts/app.js", "alert('original');");
        await _service.PullAsync(PullOpts(), null);

        var localFile = Path.Combine(_folder, "new_", "scripts", "app.js");
        await File.WriteAllTextAsync(localFile, "alert('modified locally');");
        // Update server with new content (would otherwise overwrite)
        _fake.UpdateText("new_/scripts/app.js", "alert('updated on server');");

        var result = await _service.PullAsync(PullOpts(), null);

        result.Skipped.Should().ContainSingle(s => s.Name == "new_/scripts/app.js" && s.Reason == "locally modified");
        (await File.ReadAllTextAsync(localFile)).Should().Be("alert('modified locally');");
    }

    /// <summary>AC-WR-40: --force overwrites locally modified files.</summary>
    [Fact]
    public async Task PullForceOverwritesModifiedFiles()
    {
        _fake.AddText("new_/scripts/app.js", "original");
        await _service.PullAsync(PullOpts(), null);

        var localFile = Path.Combine(_folder, "new_", "scripts", "app.js");
        await File.WriteAllTextAsync(localFile, "modified locally");
        _fake.UpdateText("new_/scripts/app.js", "server-side update");

        var result = await _service.PullAsync(PullOpts(force: true), null);

        result.Pulled.Should().ContainSingle(p => p.Name == "new_/scripts/app.js");
        (await File.ReadAllTextAsync(localFile)).Should().Be("server-side update");
    }

    /// <summary>AC-WR-41: type-code filter applies in the service (Constitution A1).</summary>
    [Fact]
    public async Task PullFiltersByTypeCode()
    {
        _fake.AddText("new_/scripts/app.js", "x");      // type 3
        _fake.AddText("new_/scripts/util.js", "x");     // type 3
        _fake.AddText("new_/styles/site.css", "x", type: 2);

        var result = await _service.PullAsync(PullOpts(typeCodes: [3]), null);

        result.Pulled.Should().HaveCount(2);
        result.Pulled.Should().OnlyContain(p => p.Name.EndsWith(".js"));
    }

    /// <summary>AC-WR-41: name-pattern filter applies in the service (Constitution A1).</summary>
    [Fact]
    public async Task PullFiltersByNamePattern()
    {
        _fake.AddText("new_/scripts/app.js", "x");
        _fake.AddText("new_/scripts/util.js", "x");
        _fake.AddText("new_/styles/site.css", "x", type: 2);

        var result = await _service.PullAsync(PullOpts(namePattern: "util"), null);

        result.Pulled.Should().ContainSingle(p => p.Name == "new_/scripts/util.js");
    }

    /// <summary>AC-WR-51: resources whose computed path escapes the workspace are rejected.</summary>
    [Fact]
    public async Task PullRejectsPathTraversal()
    {
        _fake.AddText("../../etc/passwd", "x");

        var result = await _service.PullAsync(PullOpts(), null);

        result.Errors.Should().ContainSingle(e => e.Name == "../../etc/passwd");
        result.Errors[0].Error.Should().Be("name resolves outside target folder");
    }

    /// <summary>AC-WR-37 follow-up: --strip-prefix collisions across publishers do not silently overwrite — second-and-later claimants are reported as errors.</summary>
    [Fact]
    public async Task PullRejectsStripPrefixCollisions()
    {
        _fake.AddText("new_/scripts/app.js", "from-new");
        _fake.AddText("dev_/scripts/app.js", "from-dev");

        var result = await _service.PullAsync(PullOpts(stripPrefix: true), null);

        // First wins, second errors. We don't assert which is "first" because
        // it depends on enumeration order, but exactly one must succeed and one error.
        result.Pulled.Should().HaveCount(1);
        result.Errors.Should().HaveCount(1);
        result.Errors[0].Error.Should().Contain("collides with");
    }

    /// <summary>AC-WR-55: tracking-file merge retains skipped entries and prunes resources removed from server.</summary>
    [Fact]
    public async Task PullMergesTrackingFile()
    {
        _fake.AddText("new_/a.js", "a-original");
        _fake.AddText("new_/b.js", "b-original");
        _fake.AddText("new_/c.js", "c-original");
        await _service.PullAsync(PullOpts(), null);

        // Modify a.js locally so it gets skipped on next pull; remove c from the server
        await File.WriteAllTextAsync(Path.Combine(_folder, "new_", "a.js"), "a-modified-locally");
        _fake.UpdateText("new_/a.js", "a-server-update");
        _fake.Remove("new_/c.js");

        var result = await _service.PullAsync(PullOpts(), null);
        var tracking = await WebResourceTrackingFile.ReadAsync(_folder);

        tracking.Should().NotBeNull();
        // a.js: skipped, retains its prior tracking entry
        tracking!.Resources.Should().ContainKey("new_/a.js");
        // b.js: still on server, refreshed
        tracking.Resources.Should().ContainKey("new_/b.js");
        // c.js: pruned (no longer on server)
        tracking.Resources.Should().NotContainKey("new_/c.js");
        result.Skipped.Should().Contain(s => s.Name == "new_/a.js");
    }

    /// <summary>AC-WR-50: round-trip pull → edit → push leaves tracking entry consistent.</summary>
    [Fact]
    public async Task RoundTripPullEditPush()
    {
        _fake.AddText("new_/scripts/app.js", "alert('v1');");
        await _service.PullAsync(PullOpts(), null);

        var localFile = Path.Combine(_folder, "new_", "scripts", "app.js");
        await File.WriteAllTextAsync(localFile, "alert('v2');");

        var pushResult = await _service.PushAsync(PushOpts(), null);

        pushResult.Pushed.Should().ContainSingle(p => p.Name == "new_/scripts/app.js");
        pushResult.Conflicts.Should().BeEmpty();
        _fake.GetContent("new_/scripts/app.js").Should().Be("alert('v2');");
    }

    /// <summary>AC-WR-43: push skips files whose hash matches the tracked baseline.</summary>
    [Fact]
    public async Task PushSkipsUnchangedFiles()
    {
        _fake.AddText("new_/scripts/app.js", "x");
        await _service.PullAsync(PullOpts(), null);
        // No local edits

        var result = await _service.PushAsync(PushOpts(), null);

        result.Pushed.Should().BeEmpty();
        result.Skipped.Should().ContainSingle(s => s.Name == "new_/scripts/app.js" && s.Reason == "unchanged");
        _fake.UpdateContentCallCount.Should().Be(0);
    }

    /// <summary>AC-WR-44: server-side change detected as conflict; no upload occurs.</summary>
    [Fact]
    public async Task PushDetectsServerConflict()
    {
        _fake.AddText("new_/scripts/app.js", "v1");
        await _service.PullAsync(PullOpts(), null);
        await File.WriteAllTextAsync(Path.Combine(_folder, "new_", "scripts", "app.js"), "v2-local");
        // Bump server modifiedOn behind our back
        _fake.BumpModifiedOn("new_/scripts/app.js");

        var result = await _service.PushAsync(PushOpts(), null);

        result.Conflicts.Should().ContainSingle(c => c.Name == "new_/scripts/app.js");
        result.Pushed.Should().BeEmpty();
        _fake.UpdateContentCallCount.Should().Be(0);
    }

    /// <summary>AC-WR-45: --force bypasses conflict detection and uploads anyway.</summary>
    [Fact]
    public async Task PushForceSkipsConflictCheck()
    {
        _fake.AddText("new_/scripts/app.js", "v1");
        await _service.PullAsync(PullOpts(), null);
        await File.WriteAllTextAsync(Path.Combine(_folder, "new_", "scripts", "app.js"), "v2-local");
        _fake.BumpModifiedOn("new_/scripts/app.js");

        var result = await _service.PushAsync(PushOpts(force: true), null);

        result.Conflicts.Should().BeEmpty();
        result.Pushed.Should().ContainSingle();
        _fake.UpdateContentCallCount.Should().Be(1);
    }

    /// <summary>AC-WR-46: --dry-run reports planned uploads without mutation.</summary>
    [Fact]
    public async Task PushDryRunNoMutation()
    {
        _fake.AddText("new_/scripts/app.js", "v1");
        await _service.PullAsync(PullOpts(), null);
        await File.WriteAllTextAsync(Path.Combine(_folder, "new_", "scripts", "app.js"), "v2-local");

        var result = await _service.PushAsync(PushOpts(dryRun: true), null);

        result.DryRun.Should().BeTrue();
        result.Pushed.Should().ContainSingle(p => p.Name == "new_/scripts/app.js");
        _fake.UpdateContentCallCount.Should().Be(0);
        _fake.GetContent("new_/scripts/app.js").Should().Be("v1");
    }

    /// <summary>AC-WR-47: --publish only publishes successfully uploaded resources.</summary>
    [Fact]
    public async Task PushWithPublishCallsPublishAsync()
    {
        _fake.AddText("new_/a.js", "v1");
        _fake.AddText("new_/b.js", "v1");
        await _service.PullAsync(PullOpts(), null);
        await File.WriteAllTextAsync(Path.Combine(_folder, "new_", "a.js"), "v2");
        // b.js unchanged → should not be in publish set

        var result = await _service.PushAsync(PushOpts(publish: true), null);

        // Make precision-loss bugs in tracking-file ModifiedOn round-trip fail visibly:
        // if any conflict were spuriously detected, the affected resource would not be uploaded
        // and the publish counts below would silently disagree with intent.
        result.Conflicts.Should().BeEmpty();
        result.PublishedCount.Should().Be(1);
        _fake.PublishCalls.Should().ContainSingle();
        _fake.PublishCalls[0].Should().HaveCount(1);
        _fake.PublishCalls[0][0].Should().Be(_fake.IdOf("new_/a.js"));
    }

    /// <summary>AC-WR-48: tracking file is updated with new modifiedOn and hash after upload.</summary>
    [Fact]
    public async Task PushUpdatesTrackingFile()
    {
        _fake.AddText("new_/scripts/app.js", "v1");
        await _service.PullAsync(PullOpts(), null);

        var trackingBefore = await WebResourceTrackingFile.ReadAsync(_folder);
        var hashBefore = trackingBefore!.Resources["new_/scripts/app.js"].Hash;

        await File.WriteAllTextAsync(Path.Combine(_folder, "new_", "scripts", "app.js"), "v2-changed");
        _fake.SetNextWriteModifiedOn("new_/scripts/app.js", new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        await _service.PushAsync(PushOpts(), null);

        var trackingAfter = await WebResourceTrackingFile.ReadAsync(_folder);
        var entry = trackingAfter!.Resources["new_/scripts/app.js"];
        entry.Hash.Should().NotBe(hashBefore);
        entry.ModifiedOn.Should().Be(new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc));
    }

    /// <summary>AC-WR-53: push skips binary types (read-only) with warning reason.</summary>
    [Fact]
    public async Task PushSkipsBinaryTypes()
    {
        _fake.AddBinary("new_/images/logo.png", type: 5);
        await _service.PullAsync(PullOpts(), null);

        var result = await _service.PushAsync(PushOpts(), null);

        result.Skipped.Should().ContainSingle(s => s.Name == "new_/images/logo.png" && s.Reason == "binary type (read-only)");
        _fake.UpdateContentCallCount.Should().Be(0);
    }

    /// <summary>AC-WR-54: tracked files missing from disk are warned and skipped, not deleted.</summary>
    [Fact]
    public async Task PushSkipsDeletedFiles()
    {
        _fake.AddText("new_/scripts/app.js", "v1");
        await _service.PullAsync(PullOpts(), null);

        File.Delete(Path.Combine(_folder, "new_", "scripts", "app.js"));

        var result = await _service.PushAsync(PushOpts(), null);

        result.Skipped.Should().ContainSingle(s => s.Name == "new_/scripts/app.js" && s.Reason == "file deleted");
        _fake.UpdateContentCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PushErrorsOnEnvironmentMismatch_WhenNotForced()
    {
        _fake.AddText("new_/scripts/app.js", "v1");
        await _service.PullAsync(PullOpts(envUrl: "https://org-A.crm.dynamics.com"), null);

        var act = async () => await _service.PushAsync(PushOpts(envUrl: "https://org-B.crm.dynamics.com"), null);

        await act.Should().ThrowAsync<PPDS.Cli.Infrastructure.Errors.PpdsException>()
            .Where(ex => ex.ErrorCode == PPDS.Cli.Infrastructure.Errors.ErrorCodes.Connection.InvalidEnvironmentUrl);
    }

    [Fact]
    public async Task PushErrorsOnMissingTrackingFile()
    {
        // No pull performed → no tracking file
        var act = async () => await _service.PushAsync(PushOpts(), null);

        await act.Should().ThrowAsync<PPDS.Cli.Infrastructure.Errors.PpdsException>()
            .Where(ex => ex.ErrorCode == PPDS.Cli.Infrastructure.Errors.ErrorCodes.Validation.FileNotFound);
    }

    /// <summary>R2: PullAsync threads CancellationToken through the call chain — pre-cancelled token aborts.</summary>
    [Fact]
    public async Task PullAsyncRespectsCancellationToken()
    {
        _fake.AddText("new_/scripts/app.js", "x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await _service.PullAsync(PullOpts(), null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>R2: PushAsync threads CancellationToken through the call chain — pre-cancelled token aborts.</summary>
    [Fact]
    public async Task PushAsyncRespectsCancellationToken()
    {
        _fake.AddText("new_/scripts/app.js", "v1");
        await _service.PullAsync(PullOpts(), null);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await _service.PushAsync(PushOpts(), null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}

/// <summary>
/// Test double for IWebResourceService that holds an in-memory map of resources.
/// </summary>
internal sealed class FakeWebResourceService : IWebResourceService
{
    private readonly Dictionary<string, ResourceState> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, string> _idToName = [];
    private int _currentParallel;
    private int _peak;
    private readonly Dictionary<string, DateTime> _nextWriteModifiedOn = new(StringComparer.OrdinalIgnoreCase);

    public int GetContentCallCount;
    public int UpdateContentCallCount;
    public int PeakConcurrency => _peak;
    public bool TrackConcurrency;
    public int SimulateContentLatencyMs;
    public List<IReadOnlyList<Guid>> PublishCalls { get; } = new();

    public void AddText(string name, string content, int type = 3)
    {
        var id = Guid.NewGuid();
        _byName[name] = new ResourceState
        {
            Id = id,
            Name = name,
            Type = type,
            Content = content,
            ModifiedOn = DateTime.UtcNow,
        };
        _idToName[id] = name;
    }

    public void AddBinary(string name, int type)
    {
        var id = Guid.NewGuid();
        _byName[name] = new ResourceState
        {
            Id = id,
            Name = name,
            Type = type,
            Content = null,
            ModifiedOn = DateTime.UtcNow,
        };
        _idToName[id] = name;
    }

    public void UpdateText(string name, string content)
    {
        _byName[name].Content = content;
        _byName[name].ModifiedOn = DateTime.UtcNow.AddSeconds(1);
    }

    public void Remove(string name)
    {
        if (_byName.Remove(name, out var state))
        {
            _idToName.Remove(state.Id);
        }
    }

    public void BumpModifiedOn(string name)
    {
        _byName[name].ModifiedOn = DateTime.UtcNow.AddMinutes(5);
    }

    public void SetNextWriteModifiedOn(string name, DateTime modifiedOn)
    {
        _nextWriteModifiedOn[name] = modifiedOn;
    }

    public Guid IdOf(string name) => _byName[name].Id;

    public string? GetContent(string name) => _byName.TryGetValue(name, out var s) ? s.Content : null;

    public Task<ListResult<WebResourceInfo>> ListAsync(Guid? solutionId = null, bool textOnly = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = _byName.Values
            .Select(s => new WebResourceInfo(s.Id, s.Name, null, s.Type, false, null, DateTime.UtcNow, null, s.ModifiedOn))
            .Where(i => !textOnly || i.IsTextType)
            .ToList();
        return Task.FromResult(new ListResult<WebResourceInfo>
        {
            Items = items,
            TotalCount = items.Count,
            FiltersApplied = [],
        });
    }

    public Task<WebResourceInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!_idToName.TryGetValue(id, out var name)) return Task.FromResult<WebResourceInfo?>(null);
        var s = _byName[name];
        return Task.FromResult<WebResourceInfo?>(new WebResourceInfo(s.Id, s.Name, null, s.Type, false, null, DateTime.UtcNow, null, s.ModifiedOn));
    }

    public async Task<WebResourceContent?> GetContentAsync(Guid id, bool published = false, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref GetContentCallCount);
        if (TrackConcurrency)
        {
            var current = Interlocked.Increment(ref _currentParallel);
            int oldPeak;
            do { oldPeak = _peak; } while (current > oldPeak && Interlocked.CompareExchange(ref _peak, current, oldPeak) != oldPeak);
        }
        try
        {
            if (SimulateContentLatencyMs > 0)
            {
                await Task.Delay(SimulateContentLatencyMs, cancellationToken);
            }
            if (!_idToName.TryGetValue(id, out var name)) return null;
            var s = _byName[name];
            return new WebResourceContent(s.Id, s.Name, s.Type, s.Content, s.ModifiedOn);
        }
        finally
        {
            if (TrackConcurrency) Interlocked.Decrement(ref _currentParallel);
        }
    }

    public Task<DateTime?> GetModifiedOnAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!_idToName.TryGetValue(id, out var name)) return Task.FromResult<DateTime?>(null);
        return Task.FromResult<DateTime?>(_byName[name].ModifiedOn);
    }

    public Task UpdateContentAsync(Guid id, string content, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref UpdateContentCallCount);
        if (!_idToName.TryGetValue(id, out var name)) throw new KeyNotFoundException(id.ToString());
        var s = _byName[name];
        s.Content = content;
        s.ModifiedOn = _nextWriteModifiedOn.TryGetValue(name, out var stamp) ? stamp : DateTime.UtcNow;
        return Task.CompletedTask;
    }

    public Task<int> PublishAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default)
    {
        PublishCalls.Add(ids.ToList());
        return Task.FromResult(ids.Count);
    }

    public Task PublishAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private sealed class ResourceState
    {
        public Guid Id { get; set; }
        public required string Name { get; set; }
        public int Type { get; set; }
        public string? Content { get; set; }
        public DateTime ModifiedOn { get; set; }
    }
}

internal sealed class RecordingOperationProgress : IOperationProgress
{
    public List<string> StatusMessages { get; } = new();
    public List<(int Current, int Total, string? Message)> ProgressCalls { get; } = new();
    private readonly object _lock = new();

    public void ReportStatus(string message)
    {
        lock (_lock) { StatusMessages.Add(message); }
    }

    public void ReportProgress(int current, int total, string? message = null)
    {
        lock (_lock) { ProgressCalls.Add((current, total, message)); }
    }

    public void ReportProgress(double fraction, string? message = null) { }
    public void ReportComplete(string message) { }
    public void ReportError(string message) { }
}
