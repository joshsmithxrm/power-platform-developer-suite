using Microsoft.Extensions.Logging;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.WebResources;

/// <summary>
/// Orchestrates pull and push between Dataverse web resources and a local folder.
/// Composes <see cref="IWebResourceService"/> for Dataverse CRUD and manages the
/// local <see cref="WebResourceTrackingFile"/>.
/// </summary>
public class WebResourceSyncService : IWebResourceSyncService
{
    private const int DefaultDownloadParallelism = 8;
    private const int DefaultUploadParallelism = 4;

    private readonly IWebResourceService _webResourceService;
    private readonly ILogger<WebResourceSyncService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebResourceSyncService"/> class.
    /// </summary>
    public WebResourceSyncService(
        IWebResourceService webResourceService,
        ILogger<WebResourceSyncService> logger)
    {
        _webResourceService = webResourceService ?? throw new ArgumentNullException(nameof(webResourceService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<PullResult> PullAsync(PullOptions options, IOperationProgress? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        progress?.ReportStatus("Listing web resources...");
        var listResult = await _webResourceService.ListAsync(
            solutionId: options.SolutionId,
            textOnly: false,
            cancellationToken: cancellationToken);

        var totalServerCount = listResult.TotalCount;
        var resources = (IEnumerable<WebResourceInfo>)listResult.Items;

        if (options.TypeCodes is { Length: > 0 } typeCodes)
        {
            resources = resources.Where(r => typeCodes.Contains(r.WebResourceType));
        }

        if (!string.IsNullOrEmpty(options.NamePattern))
        {
            resources = resources.Where(r => r.Name.Contains(options.NamePattern, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = resources.ToList();

        // Resolve absolute folder path once for traversal checks
        var rootAbsolute = Path.GetFullPath(options.Folder);
        Directory.CreateDirectory(rootAbsolute);

        var existingTracking = options.Force
            ? null
            : await WebResourceTrackingFile.ReadAsync(rootAbsolute, cancellationToken);

        var pulled = new List<PulledResource>();
        var skipped = new List<SkippedResource>();
        var errors = new List<ErrorResource>();
        var newResourceEntries = new Dictionary<string, TrackedResource>(StringComparer.OrdinalIgnoreCase);

        // Per-resource path resolution + traversal validation up front so we can
        // skip downloads for invalid entries without occupying a download slot.
        var processable = new List<(WebResourceInfo Resource, string LocalPath, string AbsolutePath)>();
        foreach (var resource in filtered)
        {
            var localPath = ComputeLocalPath(resource.Name, options.StripPrefix, resource.FileExtension);
            var absolutePath = Path.GetFullPath(Path.Combine(rootAbsolute, localPath));
            if (!IsDescendantOf(absolutePath, rootAbsolute))
            {
                errors.Add(new ErrorResource(resource.Name, ErrorCodes.Validation.PathOutsideWorkspace));
                continue;
            }
            processable.Add((resource, localPath, absolutePath));
        }

        // Local-modification check (skip when --force)
        var toDownload = new List<(WebResourceInfo Resource, string LocalPath, string AbsolutePath, bool IsNew)>();
        foreach (var (resource, localPath, absolutePath) in processable)
        {
            if (!resource.IsTextType)
            {
                // Binary types: record metadata in tracking file but do not download.
                skipped.Add(new SkippedResource(resource.Name, "binary type"));
                continue;
            }

            var existsLocally = File.Exists(absolutePath);
            if (existingTracking != null && existsLocally && existingTracking.Resources.TryGetValue(resource.Name, out var prior))
            {
                var localHash = await WebResourceTrackingFile.ComputeHashAsync(absolutePath, cancellationToken);
                if (!string.Equals(localHash, prior.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add(new SkippedResource(resource.Name, "locally modified"));
                    continue;
                }
            }

            toDownload.Add((resource, localPath, absolutePath, !existsLocally));
        }

        // Parallel download with throttling and CancellationToken propagation (R2)
        var totalToDownload = toDownload.Count;
        progress?.ReportStatus($"Downloading {totalToDownload} resource(s)...");
        using var semaphore = new SemaphoreSlim(DefaultDownloadParallelism);
        var completed = 0;
        var pulledLock = new object();

        async Task DownloadOneAsync((WebResourceInfo Resource, string LocalPath, string AbsolutePath, bool IsNew) item)
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var (resource, localPath, absolutePath, isNew) = item;
                var content = await _webResourceService.GetContentAsync(resource.Id, published: false, cancellationToken);
                if (content?.Content == null)
                {
                    lock (pulledLock)
                    {
                        skipped.Add(new SkippedResource(resource.Name, "no content"));
                    }
                    return;
                }

                var directory = Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await File.WriteAllTextAsync(absolutePath, content.Content, cancellationToken);
                var hash = await WebResourceTrackingFile.ComputeHashAsync(absolutePath, cancellationToken);

                lock (pulledLock)
                {
                    pulled.Add(new PulledResource(resource.Name, localPath, isNew));
                    newResourceEntries[resource.Name] = new TrackedResource(
                        resource.Id,
                        content.ModifiedOn ?? resource.ModifiedOn,
                        hash,
                        localPath,
                        resource.WebResourceType);
                    var done = Interlocked.Increment(ref completed);
                    progress?.ReportProgress(done, totalToDownload, resource.Name);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to download web resource {Name}", item.Resource.Name);
                lock (pulledLock)
                {
                    errors.Add(new ErrorResource(item.Resource.Name, ex.Message));
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        await Task.WhenAll(toDownload.Select(DownloadOneAsync));

        cancellationToken.ThrowIfCancellationRequested();

        // Record binary resources in tracking even though we did not download content.
        // Hash for binary entries is empty — push will skip these regardless.
        foreach (var (resource, localPath, _) in processable)
        {
            if (resource.IsTextType) continue;
            if (newResourceEntries.ContainsKey(resource.Name)) continue;
            newResourceEntries[resource.Name] = new TrackedResource(
                resource.Id,
                resource.ModifiedOn,
                Hash: string.Empty,
                localPath,
                resource.WebResourceType);
        }

        // Merge: locally modified (skipped) entries retain prior tracking; resources no longer
        // returned by the server are pruned. Errored entries also retain prior tracking when present.
        var skippedNames = skipped.Select(s => s.Name)
            .Concat(errors.Select(e => e.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (existingTracking != null)
        {
            var serverNames = filtered.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, prior) in existingTracking.Resources)
            {
                if (!serverNames.Contains(name)) continue; // pruned
                if (newResourceEntries.ContainsKey(name)) continue; // already replaced
                if (skippedNames.Contains(name))
                {
                    newResourceEntries[name] = prior;
                }
            }
        }

        var trackingFile = new WebResourceTrackingFile(
            Version: WebResourceTrackingFile.CurrentVersion,
            EnvironmentUrl: options.EnvironmentUrl,
            Solution: options.SolutionUniqueName,
            StripPrefix: options.StripPrefix,
            PulledAt: DateTime.UtcNow,
            Resources: newResourceEntries);

        await WebResourceTrackingFile.WriteAsync(rootAbsolute, trackingFile, cancellationToken);
        progress?.ReportComplete($"Pulled {pulled.Count} of {totalServerCount} resource(s) ({skipped.Count} skipped, {errors.Count} errors)");

        return new PullResult(totalServerCount, pulled, skipped, errors);
    }

    /// <inheritdoc />
    public async Task<PushResult> PushAsync(PushOptions options, IOperationProgress? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var rootAbsolute = Path.GetFullPath(options.Folder);
        var tracking = await WebResourceTrackingFile.ReadAsync(rootAbsolute, cancellationToken)
            ?? throw new PpdsException(
                ErrorCodes.Validation.FileNotFound,
                $"Tracking file '{WebResourceTrackingFile.TrackingFileRelativePath}' not found in '{options.Folder}'. Run 'ppds webresources pull' first.");

        if (!options.Force && !UrlsEqual(tracking.EnvironmentUrl, options.CurrentEnvironmentUrl))
        {
            throw new PpdsException(
                ErrorCodes.Connection.InvalidEnvironmentUrl,
                $"Environment mismatch: connected to '{options.CurrentEnvironmentUrl}' but tracking file was created from '{tracking.EnvironmentUrl}'. Use --force to override.");
        }

        var skipped = new List<SkippedResource>();
        var pendingUpload = new List<(string Name, TrackedResource Tracked, string AbsolutePath, string Content)>();

        // Phase 1: detect local modifications (hash compare). Build the set of upload candidates.
        foreach (var (name, entry) in tracking.Resources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsTextType(entry.WebResourceType))
            {
                skipped.Add(new SkippedResource(name, "binary type (read-only)"));
                continue;
            }

            var absolutePath = Path.GetFullPath(Path.Combine(rootAbsolute, entry.LocalPath));
            if (!File.Exists(absolutePath))
            {
                _logger.LogWarning("Tracked file missing from disk: {Path}", absolutePath);
                skipped.Add(new SkippedResource(name, "file deleted"));
                continue;
            }

            var localHash = await WebResourceTrackingFile.ComputeHashAsync(absolutePath, cancellationToken);
            if (string.Equals(localHash, entry.Hash, StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(new SkippedResource(name, "unchanged"));
                continue;
            }

            var content = await File.ReadAllTextAsync(absolutePath, cancellationToken);
            pendingUpload.Add((name, entry, absolutePath, content));
        }

        // Phase 2: server modifiedOn check (parallel, R2).
        progress?.ReportStatus($"Checking {pendingUpload.Count} resource(s) for server conflicts...");
        var conflicts = new List<ConflictResource>();
        if (!options.Force && pendingUpload.Count > 0)
        {
            using var conflictSemaphore = new SemaphoreSlim(DefaultUploadParallelism);
            var serverModifiedOnByName = new Dictionary<string, DateTime?>();
            var lockObj = new object();

            async Task FetchOneAsync((string Name, TrackedResource Tracked, string AbsolutePath, string Content) item)
            {
                await conflictSemaphore.WaitAsync(cancellationToken);
                try
                {
                    var serverModified = await _webResourceService.GetModifiedOnAsync(item.Tracked.Id, cancellationToken);
                    lock (lockObj)
                    {
                        serverModifiedOnByName[item.Name] = serverModified;
                    }
                }
                finally
                {
                    conflictSemaphore.Release();
                }
            }

            await Task.WhenAll(pendingUpload.Select(FetchOneAsync));

            foreach (var item in pendingUpload)
            {
                if (serverModifiedOnByName.TryGetValue(item.Name, out var serverModified)
                    && !ModifiedOnEqual(serverModified, item.Tracked.ModifiedOn))
                {
                    conflicts.Add(new ConflictResource(item.Name, item.Tracked.ModifiedOn, serverModified));
                }
            }

            if (conflicts.Count > 0)
            {
                // All-or-nothing: do not push when conflicts exist.
                return new PushResult(
                    Pushed: [],
                    Conflicts: conflicts,
                    Skipped: skipped,
                    DryRun: options.DryRun,
                    PublishedCount: 0);
            }
        }

        if (options.DryRun)
        {
            var pushedDryRun = pendingUpload.Select(u => new PushedResource(u.Name, u.Tracked.LocalPath)).ToList();
            progress?.ReportComplete($"Dry run: would push {pushedDryRun.Count} resource(s) ({skipped.Count} skipped)");
            return new PushResult(
                Pushed: pushedDryRun,
                Conflicts: [],
                Skipped: skipped,
                DryRun: true,
                PublishedCount: 0);
        }

        // Phase 3: parallel uploads (R2).
        progress?.ReportStatus($"Uploading {pendingUpload.Count} resource(s)...");
        var pushed = new List<PushedResource>();
        var uploadedIds = new List<Guid>();
        var uploadedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var uploadedKeys = new Dictionary<string, (string Name, TrackedResource Tracked)>(StringComparer.OrdinalIgnoreCase);
        var uploadCompleted = 0;
        var uploadLock = new object();

        using var uploadSemaphore = new SemaphoreSlim(DefaultUploadParallelism);

        async Task UploadOneAsync((string Name, TrackedResource Tracked, string AbsolutePath, string Content) item)
        {
            await uploadSemaphore.WaitAsync(cancellationToken);
            try
            {
                await _webResourceService.UpdateContentAsync(item.Tracked.Id, item.Content, cancellationToken);
                var newHash = await WebResourceTrackingFile.ComputeHashAsync(item.AbsolutePath, cancellationToken);
                lock (uploadLock)
                {
                    pushed.Add(new PushedResource(item.Name, item.Tracked.LocalPath));
                    uploadedIds.Add(item.Tracked.Id);
                    uploadedHashes[item.Name] = newHash;
                    uploadedKeys[item.Name] = (item.Name, item.Tracked);
                    var done = Interlocked.Increment(ref uploadCompleted);
                    progress?.ReportProgress(done, pendingUpload.Count, item.Name);
                }
            }
            finally
            {
                uploadSemaphore.Release();
            }
        }

        await Task.WhenAll(pendingUpload.Select(UploadOneAsync));

        var publishedCount = 0;
        if (options.Publish && uploadedIds.Count > 0)
        {
            progress?.ReportStatus($"Publishing {uploadedIds.Count} resource(s)...");
            publishedCount = await _webResourceService.PublishAsync(uploadedIds, cancellationToken);
        }

        // Phase 4: refresh tracking — re-query modifiedOn for uploaded resources, swap in new hashes.
        if (uploadedIds.Count > 0)
        {
            using var refreshSemaphore = new SemaphoreSlim(DefaultUploadParallelism);
            var refreshedModifiedOn = new Dictionary<string, DateTime?>();
            var refreshLock = new object();

            async Task RefreshOneAsync(KeyValuePair<string, (string Name, TrackedResource Tracked)> kv)
            {
                await refreshSemaphore.WaitAsync(cancellationToken);
                try
                {
                    var modified = await _webResourceService.GetModifiedOnAsync(kv.Value.Tracked.Id, cancellationToken);
                    lock (refreshLock)
                    {
                        refreshedModifiedOn[kv.Key] = modified;
                    }
                }
                finally
                {
                    refreshSemaphore.Release();
                }
            }

            await Task.WhenAll(uploadedKeys.Select(RefreshOneAsync));

            var updatedResources = new Dictionary<string, TrackedResource>(tracking.Resources, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, (_, tracked)) in uploadedKeys)
            {
                var newHash = uploadedHashes[name];
                refreshedModifiedOn.TryGetValue(name, out var newModified);
                updatedResources[name] = tracked with
                {
                    Hash = newHash,
                    ModifiedOn = newModified ?? tracked.ModifiedOn,
                };
            }

            var updatedTracking = tracking with { Resources = updatedResources };
            await WebResourceTrackingFile.WriteAsync(rootAbsolute, updatedTracking, cancellationToken);
        }

        progress?.ReportComplete($"Pushed {pushed.Count} resource(s) ({skipped.Count} skipped)");
        return new PushResult(
            Pushed: pushed,
            Conflicts: [],
            Skipped: skipped,
            DryRun: false,
            PublishedCount: publishedCount);
    }

    private static bool IsTextType(int code) => code is 1 or 2 or 3 or 4 or 9 or 11 or 12;

    private static bool ModifiedOnEqual(DateTime? a, DateTime? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        // Allow sub-second drift introduced by serialization round-trips.
        return Math.Abs((a.Value - b.Value).TotalMilliseconds) < 1;
    }

    private static bool UrlsEqual(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return true;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        return string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDescendantOf(string candidate, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCandidate = candidate;
        if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeLocalPath(string resourceName, bool stripPrefix, string fileExtension)
    {
        var sanitized = SanitizeName(resourceName);

        if (stripPrefix)
        {
            // Pattern: <prefix>_<rest> at the start, e.g. new_/scripts/app.js → scripts/app.js
            // Detect by looking for the first '_' before the first '/'.
            var firstSlash = sanitized.IndexOf('/');
            if (firstSlash > 0)
            {
                var prefixSegment = sanitized[..firstSlash];
                var underscore = prefixSegment.IndexOf('_');
                if (underscore >= 0)
                {
                    sanitized = sanitized[(underscore + 1)..];
                    if (sanitized.StartsWith('/'))
                    {
                        sanitized = sanitized[1..];
                    }
                }
            }
        }

        if (!Path.HasExtension(sanitized))
        {
            sanitized += "." + fileExtension;
        }

        return sanitized.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string SanitizeName(string name)
    {
        // Defense in depth: collapse leading separators, reject obvious traversal attempts.
        return name.TrimStart('/', '\\');
    }
}
