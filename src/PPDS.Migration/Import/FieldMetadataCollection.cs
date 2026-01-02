using System;
using System.Collections.Generic;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Collection of field metadata for multiple entities.
    /// Provides lookup methods for field validity during import.
    /// </summary>
    public class FieldMetadataCollection
    {
        private readonly IReadOnlyDictionary<string, Dictionary<string, FieldValidity>> _metadata;

        /// <summary>
        /// Initializes a new instance of the <see cref="FieldMetadataCollection"/> class.
        /// </summary>
        /// <param name="metadata">The metadata dictionary indexed by entity name.</param>
        public FieldMetadataCollection(IReadOnlyDictionary<string, Dictionary<string, FieldValidity>> metadata)
        {
            _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        }

        /// <summary>
        /// Gets the number of entities with metadata.
        /// </summary>
        public int EntityCount => _metadata.Count;

        /// <summary>
        /// Gets field metadata for a specific entity.
        /// </summary>
        /// <param name="entityName">The entity logical name.</param>
        /// <returns>Dictionary of field name to validity, or empty dictionary if entity not found.</returns>
        public IReadOnlyDictionary<string, FieldValidity> GetFieldsForEntity(string entityName)
        {
            if (_metadata.TryGetValue(entityName, out var fields))
            {
                return fields;
            }
            return new Dictionary<string, FieldValidity>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Tries to get field validity for a specific field.
        /// </summary>
        /// <param name="entityName">The entity logical name.</param>
        /// <param name="fieldName">The field logical name.</param>
        /// <param name="validity">The field validity if found.</param>
        /// <returns>True if the field was found, false otherwise.</returns>
        public bool TryGetFieldValidity(string entityName, string fieldName, out FieldValidity validity)
        {
            validity = default;
            if (_metadata.TryGetValue(entityName, out var fields) &&
                fields.TryGetValue(fieldName, out validity))
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Checks if metadata exists for an entity.
        /// </summary>
        /// <param name="entityName">The entity logical name.</param>
        /// <returns>True if metadata exists for the entity.</returns>
        public bool HasEntity(string entityName) => _metadata.ContainsKey(entityName);

        /// <summary>
        /// Gets all entity names with metadata.
        /// </summary>
        public IEnumerable<string> EntityNames => _metadata.Keys;
    }
}
