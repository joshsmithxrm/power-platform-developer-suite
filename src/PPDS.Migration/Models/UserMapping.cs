using System;
using System.Collections.Generic;

namespace PPDS.Migration.Models
{
    /// <summary>
    /// Collection of user mappings for migrating user references between environments.
    /// </summary>
    public class UserMappingCollection
    {
        /// <summary>
        /// Gets or sets the user mappings.
        /// Key is source user ID, value is the mapping.
        /// </summary>
        public Dictionary<Guid, UserMapping> Mappings { get; set; } = new();

        /// <summary>
        /// Gets or sets the default user ID to use when no mapping is found.
        /// If null, unmapped users are left as-is.
        /// </summary>
        public Guid? DefaultUserId { get; set; }

        /// <summary>
        /// Gets or sets whether to use the current user as the default when no mapping is found.
        /// Takes precedence over DefaultUserId.
        /// </summary>
        public bool UseCurrentUserAsDefault { get; set; } = true;

        /// <summary>
        /// Tries to get the mapped user ID for a source user.
        /// </summary>
        /// <param name="sourceUserId">The source user ID.</param>
        /// <param name="targetUserId">The mapped target user ID.</param>
        /// <returns>True if a mapping was found or a default applies.</returns>
        public bool TryGetMappedUserId(Guid sourceUserId, out Guid targetUserId)
        {
            if (Mappings.TryGetValue(sourceUserId, out var mapping))
            {
                targetUserId = mapping.TargetUserId;
                return true;
            }

            if (DefaultUserId.HasValue)
            {
                targetUserId = DefaultUserId.Value;
                return true;
            }

            targetUserId = Guid.Empty;
            return false;
        }
    }

    /// <summary>
    /// Represents a mapping from a source user to a target user.
    /// </summary>
    public class UserMapping
    {
        /// <summary>
        /// Gets or sets the source user ID.
        /// </summary>
        public Guid SourceUserId { get; set; }

        /// <summary>
        /// Gets or sets the source user name (for reference/display).
        /// </summary>
        public string? SourceUserName { get; set; }

        /// <summary>
        /// Gets or sets the target user ID.
        /// </summary>
        public Guid TargetUserId { get; set; }

        /// <summary>
        /// Gets or sets the target user name (for reference/display).
        /// </summary>
        public string? TargetUserName { get; set; }
    }
}
