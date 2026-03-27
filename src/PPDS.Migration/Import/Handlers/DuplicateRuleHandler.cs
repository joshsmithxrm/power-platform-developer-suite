using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using PPDS.Dataverse.Pooling;

namespace PPDS.Migration.Import.Handlers
{
    /// <summary>
    /// Handles duplicate rule records during import.
    /// Remaps Entity Type Codes during transform and publishes rules after import.
    /// </summary>
    public class DuplicateRuleHandler : IRecordTransformer, IPostImportHandler
    {
        private readonly IDataverseConnectionPool _connectionPool;
        private readonly ILogger<DuplicateRuleHandler>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="DuplicateRuleHandler"/> class.
        /// </summary>
        /// <param name="connectionPool">The Dataverse connection pool.</param>
        /// <param name="logger">Optional logger.</param>
        public DuplicateRuleHandler(IDataverseConnectionPool connectionPool, ILogger<DuplicateRuleHandler>? logger = null)
        {
            _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
            _logger = logger;
        }

        /// <inheritdoc />
        public bool CanHandle(string entityLogicalName)
            => entityLogicalName.Equals("duplicaterule", StringComparison.OrdinalIgnoreCase);

        /// <inheritdoc />
        public Entity Transform(Entity record, ImportContext context)
        {
            // Remap baseentitytypecode and matchingentitytypecode.
            // These are stored as integer Entity Type Codes that differ between environments.
            if (context.TargetEntityTypeCodes != null)
            {
                RemapEtc(record, "baseentitytypecode", context);
                RemapEtc(record, "matchingentitytypecode", context);
            }

            return record;
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(string entityLogicalName, ImportContext context, CancellationToken cancellationToken)
        {
            // Publish all imported duplicate rules
            var rules = context.IdMappings.GetMappingsForEntity("duplicaterule");
            if (rules == null || rules.Count == 0) return;

            foreach (var (_, targetId) in rules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await using var client = await _connectionPool.GetClientAsync(null, cancellationToken: cancellationToken).ConfigureAwait(false);
                    var request = new OrganizationRequest("PublishDuplicateRule")
                    {
                        ["DuplicateRuleId"] = targetId
                    };
                    await client.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogWarning(ex, "Failed to publish duplicate rule {RuleId}", targetId);
                }
            }
        }

        private static void RemapEtc(Entity record, string fieldName, ImportContext context)
        {
            if (record.Contains(fieldName))
            {
                var sourceEtc = record.GetAttributeValue<int>(fieldName);
                // Look up the logical name for this ETC in source, then get target ETC
                if (context.SourceEntityTypeCodes != null &&
                    context.SourceEntityTypeCodes.TryGetValue(sourceEtc, out var logicalName) &&
                    context.TargetEntityTypeCodes!.TryGetValue(logicalName, out var targetEtc))
                {
                    record[fieldName] = targetEtc;
                }
            }
        }
    }
}
