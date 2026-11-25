using System;
using System.Collections.Generic;
using System.Linq;

namespace PPDS.Migration.Models
{
    /// <summary>
    /// Parsed migration schema containing entity definitions.
    /// </summary>
    public class MigrationSchema
    {
        /// <summary>
        /// Gets or sets the schema version.
        /// </summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the timestamp when the schema was generated.
        /// </summary>
        public DateTime? GeneratedAt { get; set; }

        /// <summary>
        /// Gets or sets the entity definitions.
        /// </summary>
        public IReadOnlyList<EntitySchema> Entities { get; set; } = Array.Empty<EntitySchema>();

        /// <summary>
        /// Gets or sets the problems found in attributes only an import reads (<c>importMode</c>,
        /// <c>disableplugins</c>, <c>dateMode</c>, and CMT's <c>skipupdate</c> and <c>forcecreate</c>):
        /// a value the import cannot apply exactly. An export ignores them; an import refuses a schema
        /// that has any, before anything is read from or written to the target.
        /// </summary>
        public IReadOnlyList<string> ImportIssues { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Gets an entity by its logical name.
        /// </summary>
        /// <param name="logicalName">The entity logical name.</param>
        /// <returns>The entity schema, or null if not found.</returns>
        public EntitySchema? GetEntity(string logicalName)
            => Entities.FirstOrDefault(e => string.Equals(e.LogicalName, logicalName, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Gets all lookup fields across all entities.
        /// </summary>
        public IEnumerable<(EntitySchema Entity, FieldSchema Field)> GetAllLookupFields()
        {
            foreach (var entity in Entities)
            {
                foreach (var field in entity.Fields)
                {
                    if (field.IsLookup)
                    {
                        yield return (entity, field);
                    }
                }
            }
        }

        /// <summary>
        /// Gets all many-to-many relationships across all entities.
        /// </summary>
        public IEnumerable<RelationshipSchema> GetAllManyToManyRelationships()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entity in Entities)
            {
                foreach (var relationship in entity.Relationships)
                {
                    if (relationship.IsManyToMany && seen.Add(relationship.Name))
                    {
                        yield return relationship;
                    }
                }
            }
        }
    }
}
