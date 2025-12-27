using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Models;

namespace PPDS.Migration.UserMapping
{
    /// <summary>
    /// Generates user mapping files for cross-environment migration.
    /// Matches users by Azure AD Object ID or domain name.
    /// </summary>
    public class UserMappingGenerator : IUserMappingGenerator
    {
        private readonly ILogger<UserMappingGenerator>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="UserMappingGenerator"/> class.
        /// </summary>
        public UserMappingGenerator()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="UserMappingGenerator"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        public UserMappingGenerator(ILogger<UserMappingGenerator> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task<UserMappingResult> GenerateAsync(
            IDataverseConnectionPool sourcePool,
            IDataverseConnectionPool targetPool,
            UserMappingOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new UserMappingOptions();

            _logger?.LogInformation("Querying users from source environment");
            var sourceUsers = await QueryUsersAsync(sourcePool, cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation("Found {Count} users in source", sourceUsers.Count);

            _logger?.LogInformation("Querying users from target environment");
            var targetUsers = await QueryUsersAsync(targetPool, cancellationToken).ConfigureAwait(false);
            _logger?.LogInformation("Found {Count} users in target", targetUsers.Count);

            // Build lookup by AAD Object ID
            var targetByAadId = targetUsers
                .Where(u => u.AadObjectId.HasValue)
                .ToDictionary(u => u.AadObjectId!.Value, u => u);

            // Build lookup by domain name as fallback
            var targetByDomain = targetUsers
                .Where(u => !string.IsNullOrEmpty(u.DomainName))
                .GroupBy(u => u.DomainName!.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First());

            // Match users
            var mappings = new List<UserMappingMatch>();
            var unmapped = new List<UserInfo>();

            foreach (var sourceUser in sourceUsers)
            {
                UserInfo? targetUser = null;
                string? matchedBy = null;

                // Try AAD Object ID first (most reliable)
                if (sourceUser.AadObjectId.HasValue &&
                    targetByAadId.TryGetValue(sourceUser.AadObjectId.Value, out targetUser))
                {
                    matchedBy = "AadObjectId";
                }
                // Fallback to domain name
                else if (!string.IsNullOrEmpty(sourceUser.DomainName) &&
                         targetByDomain.TryGetValue(sourceUser.DomainName.ToLowerInvariant(), out targetUser))
                {
                    matchedBy = "DomainName";
                }

                if (targetUser != null && matchedBy != null)
                {
                    mappings.Add(new UserMappingMatch
                    {
                        Source = sourceUser,
                        Target = targetUser,
                        MatchedBy = matchedBy
                    });
                }
                else
                {
                    unmapped.Add(sourceUser);
                }
            }

            _logger?.LogInformation("Matched {Matched} users, {Unmapped} unmapped",
                mappings.Count, unmapped.Count);

            return new UserMappingResult
            {
                SourceUserCount = sourceUsers.Count,
                TargetUserCount = targetUsers.Count,
                Mappings = mappings,
                UnmappedUsers = unmapped,
                MatchedByAadId = mappings.Count(m => m.MatchedBy == "AadObjectId"),
                MatchedByDomain = mappings.Count(m => m.MatchedBy == "DomainName")
            };
        }

        /// <inheritdoc />
        public async Task WriteAsync(
            UserMappingResult result,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (string.IsNullOrEmpty(outputPath)) throw new ArgumentNullException(nameof(outputPath));

            _logger?.LogInformation("Writing user mapping to {Path}", outputPath);

            var doc = new XDocument(
                new XElement("mappings",
                    new XAttribute("useCurrentUserAsDefault", "true"),
                    new XComment($" Generated {DateTime.UtcNow:O} "),
                    new XComment($" Source: {result.SourceUserCount} users, Target: {result.TargetUserCount} users "),
                    new XComment($" Matched: {result.Mappings.Count} (AAD: {result.MatchedByAadId}, Domain: {result.MatchedByDomain}) "),
                    result.Mappings.Select(m => new XElement("mapping",
                        new XAttribute("sourceId", m.Source.SystemUserId),
                        new XAttribute("sourceName", m.Source.FullName),
                        new XAttribute("targetId", m.Target.SystemUserId),
                        new XAttribute("targetName", m.Target.FullName)
                    ))
                )
            );

            // Ensure directory exists
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

#if NET8_0_OR_GREATER
            await using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await doc.SaveAsync(stream, SaveOptions.None, cancellationToken).ConfigureAwait(false);
#else
            doc.Save(outputPath);
            await Task.CompletedTask;
#endif

            _logger?.LogInformation("Wrote {Count} user mappings", result.Mappings.Count);
        }

        private static async Task<List<UserInfo>> QueryUsersAsync(
            IDataverseConnectionPool pool,
            CancellationToken cancellationToken)
        {
            await using var client = await pool.GetClientAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            var query = new QueryExpression("systemuser")
            {
                ColumnSet = new ColumnSet(
                    "systemuserid",
                    "fullname",
                    "domainname",
                    "internalemailaddress",
                    "azureactivedirectoryobjectid",
                    "isdisabled",
                    "accessmode"
                ),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        // Exclude disabled users
                        new ConditionExpression("isdisabled", ConditionOperator.Equal, false)
                    }
                }
            };

            var results = await client.RetrieveMultipleAsync(query).ConfigureAwait(false);

            return results.Entities.Select(e => new UserInfo
            {
                SystemUserId = e.Id,
                FullName = e.GetAttributeValue<string>("fullname") ?? "(no name)",
                DomainName = e.GetAttributeValue<string>("domainname"),
                Email = e.GetAttributeValue<string>("internalemailaddress"),
                AadObjectId = e.GetAttributeValue<Guid?>("azureactivedirectoryobjectid"),
                IsDisabled = e.GetAttributeValue<bool>("isdisabled"),
                AccessMode = e.GetAttributeValue<Microsoft.Xrm.Sdk.OptionSetValue>("accessmode")?.Value ?? 0
            }).ToList();
        }
    }

    /// <summary>
    /// Interface for generating user mappings.
    /// </summary>
    public interface IUserMappingGenerator
    {
        /// <summary>
        /// Generates user mappings between source and target environments.
        /// </summary>
        Task<UserMappingResult> GenerateAsync(
            IDataverseConnectionPool sourcePool,
            IDataverseConnectionPool targetPool,
            UserMappingOptions? options = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Writes user mappings to an XML file.
        /// </summary>
        Task WriteAsync(
            UserMappingResult result,
            string outputPath,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Options for user mapping generation.
    /// </summary>
    public class UserMappingOptions
    {
        /// <summary>
        /// Gets or sets whether to include disabled users.
        /// </summary>
        public bool IncludeDisabledUsers { get; set; }
    }

    /// <summary>
    /// Result of user mapping generation.
    /// </summary>
    public class UserMappingResult
    {
        /// <summary>
        /// Gets or sets the number of users in the source environment.
        /// </summary>
        public int SourceUserCount { get; set; }

        /// <summary>
        /// Gets or sets the number of users in the target environment.
        /// </summary>
        public int TargetUserCount { get; set; }

        /// <summary>
        /// Gets or sets the matched user mappings.
        /// </summary>
        public IReadOnlyList<UserMappingMatch> Mappings { get; set; } = Array.Empty<UserMappingMatch>();

        /// <summary>
        /// Gets or sets the unmapped source users.
        /// </summary>
        public IReadOnlyList<UserInfo> UnmappedUsers { get; set; } = Array.Empty<UserInfo>();

        /// <summary>
        /// Gets or sets the count of users matched by AAD Object ID.
        /// </summary>
        public int MatchedByAadId { get; set; }

        /// <summary>
        /// Gets or sets the count of users matched by domain name.
        /// </summary>
        public int MatchedByDomain { get; set; }
    }

    /// <summary>
    /// A matched user mapping.
    /// </summary>
    public class UserMappingMatch
    {
        /// <summary>
        /// Gets or sets the source user.
        /// </summary>
        public UserInfo Source { get; set; } = null!;

        /// <summary>
        /// Gets or sets the target user.
        /// </summary>
        public UserInfo Target { get; set; } = null!;

        /// <summary>
        /// Gets or sets how the match was determined.
        /// </summary>
        public string MatchedBy { get; set; } = "";
    }

    /// <summary>
    /// Information about a system user.
    /// </summary>
    public class UserInfo
    {
        /// <summary>
        /// Gets or sets the system user ID.
        /// </summary>
        public Guid SystemUserId { get; set; }

        /// <summary>
        /// Gets or sets the full name.
        /// </summary>
        public string FullName { get; set; } = "";

        /// <summary>
        /// Gets or sets the domain name (UPN).
        /// </summary>
        public string? DomainName { get; set; }

        /// <summary>
        /// Gets or sets the email address.
        /// </summary>
        public string? Email { get; set; }

        /// <summary>
        /// Gets or sets the Azure AD Object ID.
        /// </summary>
        public Guid? AadObjectId { get; set; }

        /// <summary>
        /// Gets or sets whether the user is disabled.
        /// </summary>
        public bool IsDisabled { get; set; }

        /// <summary>
        /// Gets or sets the access mode.
        /// </summary>
        public int AccessMode { get; set; }
    }
}
