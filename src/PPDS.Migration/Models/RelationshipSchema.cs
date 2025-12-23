namespace PPDS.Migration.Models
{
    /// <summary>
    /// Schema definition for a relationship.
    /// </summary>
    public class RelationshipSchema
    {
        /// <summary>
        /// Gets or sets the relationship schema name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the first entity in the relationship.
        /// </summary>
        public string Entity1 { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the attribute on the first entity.
        /// </summary>
        public string Entity1Attribute { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the second entity in the relationship.
        /// </summary>
        public string Entity2 { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the attribute on the second entity.
        /// </summary>
        public string Entity2Attribute { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets whether this is a many-to-many relationship.
        /// </summary>
        public bool IsManyToMany { get; set; }

        /// <summary>
        /// Gets or sets the intersect entity name for M2M relationships.
        /// </summary>
        public string? IntersectEntity { get; set; }

        /// <summary>
        /// Gets or sets whether this is a reflexive (self-referential) relationship.
        /// </summary>
        public bool IsReflexive { get; set; }

        /// <summary>
        /// Gets or sets the target entity's primary key field name (e.g., "roleid").
        /// Required for CMT format compatibility.
        /// </summary>
        public string? TargetEntityPrimaryKey { get; set; }

        /// <inheritdoc />
        public override string ToString() => IsManyToMany
            ? $"{Name} (M2M: {Entity1} <-> {Entity2})"
            : $"{Name} ({Entity1} -> {Entity2})";
    }
}
