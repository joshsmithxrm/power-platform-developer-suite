using System.Security.Cryptography;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Services.Plugins;
using PPDS.Dataverse.Generated;

namespace PPDS.Cli.Plugins.Registration;

public sealed partial class PluginRegistrationService
{
    /// <inheritdoc />
    public async Task<PluginPackageResolution> ResolvePackageAsync(byte[] nupkgContent,
        string? solutionName = null, string? existingSelector = null,
        CancellationToken cancellationToken = default, IProgressReporter? progress = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = PluginPackageMetadataReader.Read(nupkgContent);
            return await ResolvePackageIdentityAsync(metadata.Id, metadata.Version,
                Convert.ToHexString(SHA256.HashData(nupkgContent)), solutionName, existingSelector,
                cancellationToken, progress);
        }
        catch (OperationCanceledException) { throw; }
        catch (PpdsException) { throw; }
        catch (Exception ex) { throw PackageIdentityFailure("Package identity could not be resolved.", ex); }
    }

    /// <inheritdoc />
    public async Task<PluginPackageResolution> ResolvePackageForCleanupAsync(byte[] nupkgContent,
        string? solutionName = null, string? existingSelector = null,
        CancellationToken cancellationToken = default, IProgressReporter? progress = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = PluginPackageMetadataReader.Read(nupkgContent);
            return await ResolvePackageIdentityAsync(metadata.Id, metadata.Version,
                Convert.ToHexString(SHA256.HashData(nupkgContent)), solutionName, existingSelector,
                cancellationToken, progress, isCleanupLookup: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (PpdsException) { throw; }
        catch (Exception ex) { throw PackageIdentityFailure("Package identity for cleanup could not be resolved.", ex); }
    }

    /// <inheritdoc />
    public async Task ValidatePackageResolutionAsync(PluginPackageResolution resolution,
        CancellationToken cancellationToken = default, IProgressReporter? progress = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await ResolvePackageIdentityAsync(resolution.ArtifactId, resolution.ArtifactVersion,
                resolution.ArtifactSha256, resolution.SolutionName, resolution.ExplicitSelector,
                cancellationToken, progress, resolution.IsCleanupLookup);
            // Re-observation is deliberately not a new plan. A disappeared target cannot become
            // Create, a newly appeared target cannot become Update, and names/publisher/content stay bound.
            if (current != resolution)
                throw new PpdsException(ErrorCodes.Plugin.PackageIdentityChanged,
                    "Package identity, content or solution publisher changed after preflight; no package write was performed.");
        }
        catch (OperationCanceledException) { throw; }
        catch (PpdsException) { throw; }
        catch (Exception ex) { throw PackageIdentityFailure("Package identity could not be revalidated.", ex); }
    }

    /// <inheritdoc />
    public async Task<Guid> UpsertResolvedPackageAsync(PluginPackageResolution resolution, byte[] nupkgContent,
        CancellationToken cancellationToken = default, IProgressReporter? progress = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectCleanupUploadIntent(resolution);
            _guard.EnsureCanMutate("plugins.package.upsert");
            var metadata = PluginPackageMetadataReader.Read(nupkgContent);
            if (!string.Equals(metadata.Id, resolution.ArtifactId, StringComparison.OrdinalIgnoreCase) ||
                metadata.Version != resolution.ArtifactVersion ||
                Convert.ToHexString(SHA256.HashData(nupkgContent)) != resolution.ArtifactSha256)
                throw new PpdsException(ErrorCodes.Plugin.PackageIdentityMismatch,
                    "The package bytes differ from the artifact inspected during preflight.");

            await ValidatePackageResolutionAsync(resolution, cancellationToken, progress);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.ReportPhase("Uploading plugin package", resolution.RegisteredName);
            if (resolution.ExistingPackageId is Guid existingId)
            {
                var request = new UpdateRequest
                {
                    Target = new PluginPackage { Id = existingId, Content = Convert.ToBase64String(nupkgContent) }
                };
                if (resolution.SolutionName is not null)
                    request.Parameters["SolutionUniqueName"] = resolution.SolutionName;
                await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
                await ExecuteAsync(request, client, cancellationToken);
                return existingId;
            }

            var entity = new PluginPackage
            {
                Name = resolution.RegisteredName,
                UniqueName = resolution.RegisteredUniqueName,
                Version = resolution.ArtifactVersion,
                Content = Convert.ToBase64String(nupkgContent)
            };
            return await CreateWithSolutionHeaderAsync(entity, resolution.SolutionName, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (PpdsException) { throw; }
        catch (Exception ex) { throw PackageIdentityFailure("Package upload could not be completed.", ex); }
    }

    /// <inheritdoc />
    public async Task<PluginPackageResolution> VerifyUploadedPackageAsync(PluginPackageResolution previous, Guid uploadedId,
        CancellationToken cancellationToken = default, IProgressReporter? progress = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectCleanupUploadIntent(previous);
            if (uploadedId == Guid.Empty || previous.ExistingPackageId is Guid existing && existing != uploadedId)
                throw new PpdsException(ErrorCodes.Plugin.PackageIdentityMismatch,
                    "The upload acknowledgement does not identify the planned package.");
            var current = await ResolvePackageIdentityAsync(previous.ArtifactId, previous.ArtifactVersion,
                previous.ArtifactSha256, previous.SolutionName, uploadedId.ToString(), cancellationToken, progress);
            if (current.ExistingPackageId != uploadedId || current.RegisteredName != previous.RegisteredName ||
                current.RegisteredUniqueName != previous.RegisteredUniqueName ||
                current.SolutionId != previous.SolutionId || current.PublisherId != previous.PublisherId ||
                current.PublisherPrefix != previous.PublisherPrefix ||
                current.ExistingArtifactSha256 != previous.ArtifactSha256)
                throw new PpdsException(ErrorCodes.Plugin.PackageIdentityChanged,
                    "The acknowledged package upload did not read back with the expected identity and exact artifact bytes. No subsequent mutation is safe to infer from this acknowledgement.");
            // Keep the original selector semantics in the successor: an automatic lookup
            // must still reject new canonical/legacy collisions during later cleanup checks.
            var successor = previous.ExplicitSelector == uploadedId.ToString() ? current :
                await ResolvePackageIdentityAsync(previous.ArtifactId, previous.ArtifactVersion,
                    previous.ArtifactSha256, previous.SolutionName, previous.ExplicitSelector,
                    cancellationToken, progress);
            if (successor.ExistingPackageId != uploadedId ||
                successor.RegisteredName != current.RegisteredName ||
                successor.RegisteredUniqueName != current.RegisteredUniqueName ||
                successor.ExistingArtifactSha256 != current.ExistingArtifactSha256 ||
                successor.SolutionId != current.SolutionId || successor.PublisherId != current.PublisherId ||
                successor.PublisherPrefix != current.PublisherPrefix)
                throw new PpdsException(ErrorCodes.Plugin.PackageIdentityChanged,
                    "The package upload's original selection no longer resolves to its verified target.");
            return successor;
        }
        catch (OperationCanceledException) { throw; }
        catch (PpdsException) { throw; }
        catch (Exception ex) { throw PackageIdentityFailure("The uploaded package could not be verified.", ex); }
    }

    private async Task<PluginPackageResolution> ResolvePackageIdentityAsync(string artifactId, string artifactVersion,
        string artifactHash, string? solutionName, string? existingSelector, CancellationToken cancellationToken,
        IProgressReporter? progress, bool isCleanupLookup = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.ReportPhase("Resolving plugin package identity", artifactId);
        var solution = string.IsNullOrWhiteSpace(solutionName) ? null :
            await ResolvePackageSolutionAsync(solutionName, cancellationToken);
        PluginPackageInfo? selected;
        string matchKind;
        if (existingSelector is not null)
        {
            if (string.IsNullOrWhiteSpace(existingSelector))
                throw new PpdsException(ErrorCodes.Validation.InvalidValue, "An existing package selector must not be empty.");
            if (Guid.TryParse(existingSelector, out var id))
            {
                selected = await GetPackageByIdAsync(id, cancellationToken);
                matchKind = "ExplicitId";
            }
            else
            {
                selected = await GetPackageByNameAsync(existingSelector, cancellationToken);
                matchKind = selected is not null && EqualsIdentity(selected.UniqueName, existingSelector)
                    ? "ExplicitUniqueName" : "ExplicitName";
            }
            if (selected is null)
                throw new PpdsException(ErrorCodes.Plugin.NotFound,
                    $"The explicitly selected package '{existingSelector}' was not found; update cannot create a package.");
        }
        else
        {
            var names = new[] { artifactId, solution is null ? artifactId : solution.Prefix + "_" + artifactId }
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var candidates = new List<PluginPackageInfo>();
            foreach (var name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                candidates.AddRange(await ListPackagesAsync(name, new PluginListOptions(IncludeMicrosoft: true), cancellationToken));
            }
            var uniqueMatches = candidates.Where(p => names.Any(n => EqualsIdentity(p.UniqueName, n))).ToList();
            var authoritative = uniqueMatches.Count > 0;
            selected = SelectSinglePackage(authoritative ? uniqueMatches :
                candidates.Where(p => names.Any(n => EqualsIdentity(p.Name, n))), artifactId);
            matchKind = selected is null ? "Create" : authoritative ? "UniqueName" : "DisplayName";
        }

        if (selected is null)
        {
            if (isCleanupLookup)
            {
                matchKind = "Absent";
                return NewResolution(artifactId, null, null, null);
            }
            if (solution is null)
                throw new PpdsException(ErrorCodes.Validation.RequiredField,
                    "Creating a plugin package requires an explicit solution with a verified publisher. Specify --solution; no default publisher is inferred.");
            var canonical = solution.Prefix + "_" + artifactId;
            // Both attributes are explicitly set on Create. Never truncate an artifact identity.
            if (canonical.Length > 100)
                throw new PpdsException(ErrorCodes.Validation.InvalidValue,
                    "The publisher-prefixed package identity exceeds Dataverse's 100-character package name limit.");
            return NewResolution(canonical, canonical, null, null);
        }

        if (selected.Id == Guid.Empty)
            throw new PpdsException(ErrorCodes.Plugin.PackageIdentityMismatch, "The selected package has no valid record identity.");
        var current = await GetPackageByIdAsync(selected.Id, cancellationToken)
            ?? throw new PpdsException(ErrorCodes.Plugin.PackageIdentityChanged,
                "The selected package disappeared during identity resolution.");
        if (current.Id != selected.Id || current.Name != selected.Name || current.UniqueName != selected.UniqueName)
            throw new PpdsException(ErrorCodes.Plugin.PackageIdentityChanged,
                "The selected package identity changed during resolution.");
        if (current.IsManaged)
            throw new PpdsException(ErrorCodes.Validation.InvalidValue, "Managed plugin packages cannot be updated directly.");
        if ((matchKind is "DisplayName" or "ExplicitName") && solution is not null)
            await RequirePackageSolutionMembershipAsync(current.Id, solution.Id, cancellationToken);

        // Registered strings select a row, but only its stored nuspec identifies the artifact.
        // Verify every selected row, including canonical and legacy UniqueName matches.
        var stored = await DownloadPackageAsync(current.Id, cancellationToken, progress);
        var storedMetadata = PluginPackageMetadataReader.Read(stored.Content);
        if (!EqualsIdentity(storedMetadata.Id, artifactId))
            throw new PpdsException(ErrorCodes.Plugin.PackageIdentityMismatch,
                $"Selected package '{current.Id}' contains artifact '{storedMetadata.Id}', not '{artifactId}'.");
        return NewResolution(current.Name, current.UniqueName, current.Id,
            Convert.ToHexString(SHA256.HashData(stored.Content)));

        PluginPackageResolution NewResolution(string name, string? uniqueName, Guid? id, string? storedHash)
            => new()
            {
                ArtifactId = artifactId, ArtifactVersion = artifactVersion, ArtifactSha256 = artifactHash,
                ExistingPackageId = id, RegisteredName = name, RegisteredUniqueName = uniqueName,
                ExistingArtifactSha256 = storedHash, ExplicitSelector = existingSelector,
                SolutionName = solution?.Name, SolutionId = solution?.Id,
                PublisherId = solution?.PublisherId, PublisherPrefix = solution?.Prefix, MatchKind = matchKind,
                IsCleanupLookup = isCleanupLookup
            };
    }

    private static void RejectCleanupUploadIntent(PluginPackageResolution resolution)
    {
        if (resolution.IsCleanupLookup)
            throw new PpdsException(ErrorCodes.Plugin.PackageIdentityMismatch,
                "A cleanup-only package lookup cannot authorize an upload or verify an upload acknowledgement.");
    }

    private sealed record PackageSolution(Guid Id, string Name, Guid PublisherId, string Prefix);

    private async Task<PackageSolution> ResolvePackageSolutionAsync(string solutionName, CancellationToken cancellationToken)
    {
        var query = new QueryExpression(Solution.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(Solution.Fields.UniqueName, Solution.Fields.PublisherId, Solution.Fields.IsManaged)
        };
        query.Criteria.AddCondition(Solution.Fields.UniqueName, ConditionOperator.Equal, solutionName);
        List<Entity> rows;
        await using (var client = await _pool.GetClientAsync(cancellationToken: cancellationToken))
            rows = await RetrieveAllPagesAsync(query, client, cancellationToken);
        if (rows.Count != 1 || rows[0].Id == Guid.Empty || rows[0].LogicalName != Solution.EntityLogicalName ||
            !EqualsIdentity(rows[0].GetAttributeValue<string>(Solution.Fields.UniqueName), solutionName) ||
            rows[0].GetAttributeValue<bool?>(Solution.Fields.IsManaged) != false)
            throw new PpdsException(ErrorCodes.Validation.InvalidValue,
                $"Solution '{solutionName}' must resolve to exactly one readable unmanaged solution.");
        var publisher = rows[0].GetAttributeValue<EntityReference>(Solution.Fields.PublisherId);
        if (publisher is null || publisher.Id == Guid.Empty || publisher.LogicalName != Publisher.EntityLogicalName)
            throw new PpdsException(ErrorCodes.Validation.InvalidValue, "The selected solution has no verified publisher.");
        Entity publisherRow;
        await using (var client = await _pool.GetClientAsync(cancellationToken: cancellationToken))
            publisherRow = await RetrieveAsync(Publisher.EntityLogicalName, publisher.Id,
                new ColumnSet(Publisher.Fields.CustomizationPrefix), client, cancellationToken);
        var prefix = publisherRow.GetAttributeValue<string>(Publisher.Fields.CustomizationPrefix);
        if (publisherRow.Id != publisher.Id || publisherRow.LogicalName != Publisher.EntityLogicalName ||
            string.IsNullOrWhiteSpace(prefix) || prefix.Any(char.IsWhiteSpace))
            throw new PpdsException(ErrorCodes.Validation.InvalidValue, "The selected publisher's customization prefix is unreadable or invalid.");
        return new PackageSolution(rows[0].Id, rows[0].GetAttributeValue<string>(Solution.Fields.UniqueName), publisher.Id, prefix);
    }

    private async Task RequirePackageSolutionMembershipAsync(Guid packageId, Guid solutionId, CancellationToken cancellationToken)
    {
        var query = new QueryExpression(SolutionComponent.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(SolutionComponent.Fields.ObjectId, SolutionComponent.Fields.SolutionId)
        };
        query.Criteria.AddCondition(SolutionComponent.Fields.ObjectId, ConditionOperator.Equal, packageId);
        query.Criteria.AddCondition(SolutionComponent.Fields.SolutionId, ConditionOperator.Equal, solutionId);
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var rows = await RetrieveAllPagesAsync(query, client, cancellationToken);
        if (!rows.Any(r => r.GetAttributeValue<Guid?>(SolutionComponent.Fields.ObjectId) == packageId &&
            r.GetAttributeValue<EntityReference>(SolutionComponent.Fields.SolutionId)?.Id == solutionId))
            throw new PpdsException(ErrorCodes.Plugin.PackageIdentityMismatch,
                "A display-name package match is not a verified member of the selected solution.");
    }

    private static bool EqualsIdentity(string? left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static PluginPackageInfo? SelectExactPackage(IEnumerable<PluginPackageInfo> packages, string selector)
    {
        var rows = packages.ToList();
        var unique = rows.Where(p => EqualsIdentity(p.UniqueName, selector)).ToArray();
        return SelectSinglePackage(unique.Length > 0 ? unique : rows.Where(p => EqualsIdentity(p.Name, selector)), selector);
    }

    private static PluginPackageInfo? SelectSinglePackage(IEnumerable<PluginPackageInfo> packages, string selector)
    {
        var matches = packages.GroupBy(p => p.Id).Select(g => g.First()).ToArray();
        if (matches.Length > 1)
            throw new PpdsException(ErrorCodes.Operation.Duplicate,
                $"Package identity '{selector}' matches multiple records; select an exact package ID or resolve the collision.");
        return matches.SingleOrDefault();
    }

    private static PpdsException PackageIdentityFailure(string message, Exception ex)
        => new(ExceptionMapper.Map(ex).Code, message, ex);
}
