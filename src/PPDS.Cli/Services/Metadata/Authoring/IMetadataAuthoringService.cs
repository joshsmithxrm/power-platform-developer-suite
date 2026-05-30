using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Metadata.Authoring;

namespace PPDS.Cli.Services.Metadata.Authoring;

/// <summary>
/// Domain service for Dataverse metadata authoring operations (schema CRUD).
/// All methods require solution context and support validation/dry-run.
/// </summary>
public interface IMetadataAuthoringService
{
    // Tables

    /// <summary>Creates a new Dataverse table (entity) in the specified solution.</summary>
    Task<CreateTableResult> CreateTableAsync(CreateTableRequest request, IMetadataAuthoringProgressReporter? reporter = null, CancellationToken ct = default);

    /// <summary>Updates an existing Dataverse table (entity).</summary>
    Task UpdateTableAsync(UpdateTableRequest request, IMetadataAuthoringProgressReporter? reporter = null, CancellationToken ct = default);

    /// <summary>Deletes a Dataverse table (entity).</summary>
    Task DeleteTableAsync(DeleteTableRequest request, IMetadataAuthoringProgressReporter? reporter = null, CancellationToken ct = default);

    // Columns

    /// <summary>Creates a new column (attribute) on a Dataverse table.</summary>
    Task<CreateColumnResult> CreateColumnAsync(CreateColumnRequest request, IMetadataAuthoringProgressReporter? reporter = null, CancellationToken ct = default);

    /// <summary>Updates an existing column (attribute) on a Dataverse table.</summary>
    Task UpdateColumnAsync(UpdateColumnRequest request, IMetadataAuthoringProgressReporter? reporter = null, CancellationToken ct = default);

    /// <summary>Deletes a column (attribute) from a Dataverse table.</summary>
    Task DeleteColumnAsync(DeleteColumnRequest request, IMetadataAuthoringProgressReporter? reporter = null, CancellationToken ct = default);

    // Relationships

    /// <summary>Creates a one-to-many (1:N) relationship.</summary>
    Task<CreateRelationshipResult> CreateOneToManyAsync(CreateOneToManyRequest request, IMetadataAuthoringProgressReporter? reporter = null, CancellationToken ct = default);

    /// <summary>Creates a many-to-many (N:N) relationship.</summary>
    Task<CreateRelationshipResult> CreateManyToManyAsync(CreateManyToManyRequest request, IMetadataAuthoringProgressReporter? reporter = null, CancellationToken ct = default);

    /// <summary>Updates an existing relationship.</summary>
    Task UpdateRelationshipAsync(UpdateRelationshipRequest request, IMetadataAuthoringProgressReporter? reporter = null, CancellationToken ct = default);

    /// <summary>Deletes a relationship.</summary>
    Task DeleteRelationshipAsync(DeleteRelationshipRequest request, IMetadataAuthoringProgressReporter? reporter = null, CancellationToken ct = default);

    // Choices

    /// <summary>Creates a new global choice (option set).</summary>
    Task<CreateChoiceResult> CreateGlobalChoiceAsync(CreateGlobalChoiceRequest request, IMetadataAuthoringProgressReporter? reporter = null, CancellationToken ct = default);

    /// <summary>Updates an existing global choice (option set).</summary>
    Task UpdateGlobalChoiceAsync(UpdateGlobalChoiceRequest request, IMetadataAuthoringProgressReporter? reporter = null, CancellationToken ct = default);

    /// <summary>Deletes a global choice (option set).</summary>
    Task DeleteGlobalChoiceAsync(DeleteGlobalChoiceRequest request, IMetadataAuthoringProgressReporter? reporter = null, CancellationToken ct = default);

    /// <summary>Adds a new option value to an existing option set.</summary>
    Task<int> AddOptionValueAsync(AddOptionValueRequest request, CancellationToken ct = default);

    /// <summary>Updates an existing option value in an option set.</summary>
    Task UpdateOptionValueAsync(UpdateOptionValueRequest request, CancellationToken ct = default);

    /// <summary>Deletes an option value from an option set.</summary>
    Task DeleteOptionValueAsync(DeleteOptionValueRequest request, CancellationToken ct = default);

    /// <summary>Reorders the option values in an option set.</summary>
    Task ReorderOptionsAsync(ReorderOptionsRequest request, CancellationToken ct = default);

    /// <summary>Updates a state or status option value label.</summary>
    Task UpdateStateValueAsync(UpdateStateValueRequest request, CancellationToken ct = default);

    // Alternate Keys

    /// <summary>Creates an alternate key on a Dataverse table.</summary>
    Task<CreateKeyResult> CreateKeyAsync(CreateKeyRequest request, IMetadataAuthoringProgressReporter? reporter = null, CancellationToken ct = default);

    /// <summary>Deletes an alternate key from a Dataverse table.</summary>
    Task DeleteKeyAsync(DeleteKeyRequest request, IMetadataAuthoringProgressReporter? reporter = null, CancellationToken ct = default);

    /// <summary>Reactivates a failed alternate key.</summary>
    Task ReactivateKeyAsync(ReactivateKeyRequest request, CancellationToken ct = default);

    // Status Reasons

    /// <summary>Adds a status reason (statuscode option value) to an entity.</summary>
    Task<int> AddStatusReasonAsync(AddStatusReasonRequest request, IMetadataAuthoringProgressReporter? reporter = null, CancellationToken ct = default);

    /// <summary>Lists all status reasons for an entity's statuscode attribute.</summary>
    Task<IReadOnlyList<StatusReasonInfo>> ListStatusReasonsAsync(string entityLogicalName, CancellationToken ct = default);

    /// <summary>Updates an existing status reason on an entity.</summary>
    Task UpdateStatusReasonAsync(UpdateStatusReasonRequest request, CancellationToken ct = default);

    /// <summary>Removes a status reason from an entity's statuscode attribute.</summary>
    Task RemoveStatusReasonAsync(RemoveStatusReasonRequest request, CancellationToken ct = default);

    // Local (column-scoped) options (#1161)

    /// <summary>Lists the options on a column's local option set.</summary>
    Task<IReadOnlyList<ColumnOptionInfo>> ListColumnOptionsAsync(string entityLogicalName, string columnLogicalName, CancellationToken ct = default);

    /// <summary>Adds an option to a column's local option set, deriving the value when not explicit.</summary>
    Task<int> AddColumnOptionAsync(AddColumnOptionRequest request, CancellationToken ct = default);

    /// <summary>Updates an option (label and/or color) on a column's local option set.</summary>
    Task UpdateColumnOptionAsync(UpdateColumnOptionRequest request, CancellationToken ct = default);

    /// <summary>Removes an option from a column's local option set.</summary>
    Task RemoveColumnOptionAsync(RemoveColumnOptionRequest request, CancellationToken ct = default);
}
