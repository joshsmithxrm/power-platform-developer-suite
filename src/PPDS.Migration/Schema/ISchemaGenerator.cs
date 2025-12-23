using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Schema
{
    /// <summary>
    /// Interface for generating migration schemas from Dataverse metadata.
    /// </summary>
    public interface ISchemaGenerator
    {
        /// <summary>
        /// Generates a migration schema for the specified entities.
        /// </summary>
        /// <param name="entityLogicalNames">The logical names of entities to include.</param>
        /// <param name="options">Schema generation options.</param>
        /// <param name="progress">Optional progress reporter.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The generated migration schema.</returns>
        Task<MigrationSchema> GenerateAsync(
            IEnumerable<string> entityLogicalNames,
            SchemaGeneratorOptions? options = null,
            IProgressReporter? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets available entities from Dataverse.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>List of entity logical names and display names.</returns>
        Task<IReadOnlyList<EntityInfo>> GetAvailableEntitiesAsync(
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Basic entity information.
    /// </summary>
    public class EntityInfo
    {
        /// <summary>
        /// Gets or sets the entity logical name.
        /// </summary>
        public string LogicalName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the entity display name.
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the entity type code.
        /// </summary>
        public int ObjectTypeCode { get; set; }

        /// <summary>
        /// Gets or sets whether this is a custom entity.
        /// </summary>
        public bool IsCustomEntity { get; set; }
    }
}
