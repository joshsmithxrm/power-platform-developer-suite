using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;

namespace PPDS.Migration.Models
{
    /// <summary>
    /// Container for exported migration data.
    /// </summary>
    public class MigrationData
    {
        /// <summary>
        /// Gets or sets the schema used for this data.
        /// </summary>
        public MigrationSchema Schema { get; set; } = new();

        /// <summary>
        /// Gets or sets the entity data.
        /// Key is entity logical name, value is the list of records.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<Entity>> EntityData { get; set; }
            = new Dictionary<string, IReadOnlyList<Entity>>();

        /// <summary>
        /// Gets or sets the many-to-many relationship data.
        /// Key is entity logical name (source entity), value is list of grouped associations.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<ManyToManyRelationshipData>> RelationshipData { get; set; }
            = new Dictionary<string, IReadOnlyList<ManyToManyRelationshipData>>();

        /// <summary>
        /// Gets or sets the export timestamp.
        /// </summary>
        public DateTime ExportedAt { get; set; }

        /// <summary>
        /// Gets or sets the source environment URL.
        /// </summary>
        public string? SourceEnvironment { get; set; }

        /// <summary>
        /// Gets the total record count across all entities.
        /// </summary>
        public int TotalRecordCount
        {
            get
            {
                var count = 0;
                foreach (var records in EntityData.Values)
                {
                    count += records.Count;
                }
                return count;
            }
        }
    }

    /// <summary>
    /// Represents grouped M2M associations for one source record.
    /// Matches CMT data.xml format where each source has a list of targets.
    /// </summary>
    public class ManyToManyRelationshipData
    {
        /// <summary>
        /// Gets or sets the relationship schema name.
        /// </summary>
        public string RelationshipName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the source entity logical name.
        /// </summary>
        public string SourceEntityName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the source record ID.
        /// </summary>
        public Guid SourceId { get; set; }

        /// <summary>
        /// Gets or sets the target entity logical name.
        /// </summary>
        public string TargetEntityName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the target entity's primary key field name.
        /// </summary>
        public string TargetEntityPrimaryKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the target record IDs.
        /// </summary>
        public List<Guid> TargetIds { get; set; } = new();
    }
}
