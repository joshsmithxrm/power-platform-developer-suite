using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Metadata.Authoring;

namespace PPDS.Cli.Services.Metadata.Authoring;

public partial class DataverseMetadataAuthoringService
{
    /// <inheritdoc />
    public async Task<MetadataDeletePreviewResult> PreviewDeleteAsync(MetadataDeleteTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        _guard.EnsureCanMutate(target.Kind switch
        {
            MetadataDeleteKind.Table => "metadata.table.delete", MetadataDeleteKind.Column => "metadata.column.delete",
            MetadataDeleteKind.Relationship => "metadata.relationship.delete", MetadataDeleteKind.Key => "metadata.key.delete",
            MetadataDeleteKind.GlobalChoice => "metadata.globalChoice.delete", _ => throw new ArgumentOutOfRangeException(nameof(target))
        });
        ValidateDeleteTarget(target);
        return await InspectDeleteAsync(target, ct).ConfigureAwait(false);
    }

    private void ValidateDeleteTarget(MetadataDeleteTarget target)
    {
        _validator.ValidateRequiredString(target.SolutionUniqueName, "SolutionUniqueName");
        if (target.Kind is MetadataDeleteKind.Table or MetadataDeleteKind.Column or MetadataDeleteKind.Key)
            _validator.ValidateRequiredString(target.EntityLogicalName, "EntityLogicalName");
        if (target.Kind != MetadataDeleteKind.Table)
            _validator.ValidateRequiredString(target.Name, target.Kind switch
            {
                MetadataDeleteKind.Column => "ColumnLogicalName", MetadataDeleteKind.Relationship => "SchemaName",
                MetadataDeleteKind.Key => "KeyLogicalName", _ => "Name"
            });
    }

    private async Task EnsureDeleteAllowedAsync(MetadataDeleteTarget target, CancellationToken ct)
    {
        var preview = await InspectDeleteAsync(target, ct).ConfigureAwait(false);
        if (!preview.IsClear) throw preview.Refusal();
    }

    private async Task<MetadataDeletePreviewResult> InspectDeleteAsync(MetadataDeleteTarget target, CancellationToken ct)
    {
        var result = new MetadataDeletePreviewResult();
        (OrganizationRequest request, string key, int type) requestInfo = target.Kind switch
        {
            MetadataDeleteKind.Table => (new RetrieveEntityRequest { LogicalName = target.EntityLogicalName, EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = true }, "EntityMetadata", 1),
            MetadataDeleteKind.Column => (new RetrieveAttributeRequest { EntityLogicalName = target.EntityLogicalName, LogicalName = target.Name, RetrieveAsIfPublished = true }, "AttributeMetadata", 2),
            MetadataDeleteKind.Relationship => (new RetrieveRelationshipRequest { Name = target.Name, RetrieveAsIfPublished = true }, "RelationshipMetadata", 10),
            MetadataDeleteKind.Key => (new RetrieveEntityKeyRequest { EntityLogicalName = target.EntityLogicalName, LogicalName = target.Name, RetrieveAsIfPublished = true }, "EntityKeyMetadata", 14),
            MetadataDeleteKind.GlobalChoice => (new RetrieveOptionSetRequest { Name = target.Name, RetrieveAsIfPublished = true }, "OptionSetMetadata", 9),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
        var (request, key, type) = requestInfo;
        var response = await ExecuteDeleteReadAsync(request, ct).ConfigureAwait(false);
        var expectedResponse = target.Kind switch
        {
            MetadataDeleteKind.Table => response is RetrieveEntityResponse,
            MetadataDeleteKind.Column => response is RetrieveAttributeResponse,
            MetadataDeleteKind.Relationship => response is RetrieveRelationshipResponse,
            MetadataDeleteKind.Key => response is RetrieveEntityKeyResponse,
            MetadataDeleteKind.GlobalChoice => response is RetrieveOptionSetResponse,
            _ => false
        };
        response.Results.TryGetValue(key, out var raw);
        MetadataBase? metadata = raw as MetadataBase;
        var identityMatches = raw switch
        {
            EntityMetadata entity when target.Kind == MetadataDeleteKind.Table => Same(entity.LogicalName, target.EntityLogicalName),
            AttributeMetadata attribute when target.Kind == MetadataDeleteKind.Column => Same(attribute.LogicalName, target.Name) && OptionalParentMatches(attribute.EntityLogicalName, target.EntityLogicalName),
            RelationshipMetadataBase relationship when target.Kind == MetadataDeleteKind.Relationship => Same(relationship.SchemaName, target.Name),
            EntityKeyMetadata entityKey when target.Kind == MetadataDeleteKind.Key => Same(entityKey.LogicalName, target.Name) && OptionalParentMatches(entityKey.EntityLogicalName, target.EntityLogicalName),
            OptionSetMetadataBase choice when target.Kind == MetadataDeleteKind.GlobalChoice => Same(choice.Name, target.Name) && choice.IsGlobal == true,
            _ => false
        };
        if (!expectedResponse || !identityMatches || metadata?.MetadataId is not { } id || id == Guid.Empty)
            return Incomplete(result, "Target metadata did not establish the requested identity.");
        result.TargetExistenceChecked = true; result.TargetObjectId = id; result.TargetComponentType = type;

        var dependencies = await ExecuteDeleteReadAsync(new RetrieveDependenciesForDeleteRequest { ObjectId = id, ComponentType = type }, ct).ConfigureAwait(false);
        if (dependencies is not RetrieveDependenciesForDeleteResponse || !dependencies.Results.TryGetValue("EntityCollection", out var collection) || collection is not EntityCollection rows)
            return Incomplete(result, "The dependency response was missing or malformed.");
        result.BlockingComponents = rows.Entities.Select(row => new MetadataDeleteDependency
        {
            DependencyId = row.Id == Guid.Empty ? null : row.Id,
            ObjectId = ReadGuid(row, "dependentcomponentobjectid"), ComponentType = ReadType(row, "dependentcomponenttype"),
            DependencyType = ReadType(row, "dependencytype"), RequiredObjectId = ReadGuid(row, "requiredcomponentobjectid"),
            RequiredComponentType = ReadType(row, "requiredcomponenttype")
        }).ToArray();
        if (rows.MoreRecords || rows.TotalRecordCountLimitExceeded || rows.TotalRecordCount > rows.Entities.Count || result.BlockingComponents.Any(r => r.ObjectId is null || r.ComponentType is null))
            return Incomplete(result, "Dependency rows were incomplete or contained missing component identities.");
        result.DependenciesChecked = true;
        result.Status = rows.Entities.Count == 0 ? "no-blocking-dependencies" : "blocked";
        if (rows.Entities.Count == 0) return result;

        var warnings = new List<string>();
        var nameIssues = new List<MetadataDependencyNameIssue>();
        foreach (var group in result.BlockingComponents.GroupBy(r => (Type: r.ComponentType!.Value, Id: r.ObjectId!.Value)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var name = await ReadDependencyNameAsync(group.Key.Type, group.Key.Id, ct).ConfigureAwait(false);
                foreach (var row in group) { row.Name = name; row.NameResolutionStatus = name == null ? "unavailable" : "resolved"; }
                if (name == null) warnings.Add($"Name unavailable for component type {group.Key.Type}, object {group.Key.Id}; the blocker is retained.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failure = ex as PpdsException ?? NormalizeDeleteReadFailure(ex);
                result.FailureCause ??= failure;
                nameIssues.Add(new MetadataDependencyNameIssue(group.Key.Type, group.Key.Id,
                    failure.ErrorCode,
                    failure is PpdsAuthException { RequiresReauthentication: true }));
                foreach (var row in group) row.NameResolutionStatus = "unavailable";
                warnings.Add($"Name lookup failed for component type {group.Key.Type}, object {group.Key.Id}; the blocker is retained.");
            }
        }
        result.Warnings = warnings.ToArray();
        result.NameResolutionIssues = nameIssues.ToArray();
        return result;
    }

    private async Task<OrganizationResponse> ExecuteDeleteReadAsync(OrganizationRequest request, CancellationToken ct)
    {
        await using var client = await GetMetadataClientAsync(ct).ConfigureAwait(false);
        try
        {
            return await client.ExecuteAsync(request, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The metadata response was missing.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PpdsException and not MetadataValidationException)
        {
            throw NormalizeDeleteReadFailure(ex);
        }
    }

    private static PpdsException NormalizeDeleteReadFailure(Exception ex)
    {
        const string message = "Could not inspect deletion metadata or dependencies.";
        // Only classification is retained. The mapper's provider-derived message/target
        // must never enter the public exception or serialized preview.
        var (mapped, exitCode) = ExceptionMapper.MapWithExitCode(ex);
        if (exitCode == ExitCodes.AuthError)
            return new PpdsAuthException(mapped.Code, message, ex)
            {
                RequiresReauthentication = ex is PPDS.Dataverse.Resilience.DataverseAuthenticationException { RequiresReauthentication: true }
            };

        var code = exitCode == ExitCodes.ConnectionError ? mapped.Code : MetadataErrorCodes.SdkOperationFailed;
        return new PpdsException(code, message, ex);
    }

    private async Task<string?> ReadDependencyNameAsync(int type, Guid id, CancellationToken ct)
    {
        OrganizationRequest? request = type switch
        {
            1 => new RetrieveEntityRequest { MetadataId = id, EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = true },
            2 => new RetrieveAttributeRequest { MetadataId = id, RetrieveAsIfPublished = true },
            9 => new RetrieveOptionSetRequest { MetadataId = id, RetrieveAsIfPublished = true },
            10 => new RetrieveRelationshipRequest { MetadataId = id, RetrieveAsIfPublished = true },
            14 => new RetrieveEntityKeyRequest { MetadataId = id, RetrieveAsIfPublished = true },
            _ => null
        };
        if (request != null)
        {
            var response = await ExecuteDeleteReadAsync(request, ct).ConfigureAwait(false);
            var metadata = response.Results.Values.OfType<MetadataBase>().SingleOrDefault(m => m.MetadataId == id);
            return (type, response, metadata) switch
            {
                (1, RetrieveEntityResponse, EntityMetadata m) => m.LogicalName,
                (2, RetrieveAttributeResponse, AttributeMetadata m) => m.LogicalName,
                (9, RetrieveOptionSetResponse, OptionSetMetadataBase m) => m.Name,
                (10, RetrieveRelationshipResponse, RelationshipMetadataBase m) => m.SchemaName,
                (14, RetrieveEntityKeyResponse, EntityKeyMetadata m) => m.LogicalName,
                _ => null
            };
        }
        (string Table, string PrimaryId, string Name)? mapping = type switch
        {
            20 => ("role", "roleid", "name"), 24 or 60 => ("systemform", "formid", "name"),
            26 => ("savedquery", "savedqueryid", "name"), 29 => ("workflow", "workflowid", "name"),
            61 => ("webresource", "webresourceid", "name"), 70 => ("fieldsecurityprofile", "fieldsecurityprofileid", "name"),
            90 => ("plugintype", "plugintypeid", "name"), 91 => ("pluginassembly", "pluginassemblyid", "name"),
            92 => ("sdkmessageprocessingstep", "sdkmessageprocessingstepid", "name"), _ => null
        };
        if (type == 10377)
        {
            // The reporter's modern type code must be verified against this environment.
            var response = await ExecuteDeleteReadAsync(new RetrieveEntityRequest { LogicalName = "controlconfiguration", EntityFilters = EntityFilters.Entity, RetrieveAsIfPublished = true }, ct).ConfigureAwait(false);
            if (response.Results.TryGetValue("EntityMetadata", out var raw) && raw is EntityMetadata entity && entity.ObjectTypeCode == type && Same(entity.LogicalName, "controlconfiguration") && !string.IsNullOrEmpty(entity.PrimaryIdAttribute) && !string.IsNullOrEmpty(entity.PrimaryNameAttribute))
                mapping = (entity.LogicalName, entity.PrimaryIdAttribute, entity.PrimaryNameAttribute);
        }
        if (mapping is not { } table) return null;
        var query = new QueryExpression(table.Table) { ColumnSet = new ColumnSet(table.Name) };
        query.Criteria.AddCondition(table.PrimaryId, ConditionOperator.Equal, id);
        EntityCollection rows;
        await using (var client = await GetMetadataClientAsync(ct).ConfigureAwait(false))
            rows = await client.RetrieveMultipleAsync(query, ct).ConfigureAwait(false);
        if (rows == null || rows.MoreRecords || rows.TotalRecordCountLimitExceeded || rows.TotalRecordCount > rows.Entities.Count || rows.Entities.Count != 1 || rows.Entities[0].Id != id || !Same(rows.Entities[0].LogicalName, table.Table))
            return null;
        return rows.Entities[0].GetAttributeValue<string>(table.Name);
    }

    private static MetadataDeletePreviewResult Incomplete(MetadataDeletePreviewResult result, string reason)
    { result.Warnings = [reason]; result.FailureCause = new InvalidOperationException(reason); return result; }
    private static bool Same(string? actual, string? requested) => !string.IsNullOrEmpty(actual) && string.Equals(actual, requested, StringComparison.OrdinalIgnoreCase);
    private static bool OptionalParentMatches(string? actual, string? requested) => string.IsNullOrEmpty(actual) || Same(actual, requested);
    private static Guid? ReadGuid(Entity row, string field) => row.Attributes.TryGetValue(field, out var value) && value is Guid id && id != Guid.Empty ? id : null;
    private static int? ReadType(Entity row, string field) => row.Attributes.TryGetValue(field, out var value) ? value switch { OptionSetValue option => option.Value, int number => number, _ => null } : null;
}
