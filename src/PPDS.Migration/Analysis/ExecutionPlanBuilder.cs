using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using PPDS.Migration.Models;

namespace PPDS.Migration.Analysis
{
    /// <summary>
    /// Builds execution plans with deferred field identification.
    /// </summary>
    public class ExecutionPlanBuilder : IExecutionPlanBuilder
    {
        private readonly ILogger<ExecutionPlanBuilder>? _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="ExecutionPlanBuilder"/> class.
        /// </summary>
        public ExecutionPlanBuilder()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ExecutionPlanBuilder"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        public ExecutionPlanBuilder(ILogger<ExecutionPlanBuilder> logger)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public ExecutionPlan Build(DependencyGraph graph, MigrationSchema schema)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            if (schema == null) throw new ArgumentNullException(nameof(schema));

            _logger?.LogInformation("Building execution plan for {TierCount} tiers", graph.TierCount);

            // Build import tiers
            var tiers = new List<ImportTier>();
            var entityTierMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < graph.Tiers.Count; i++)
            {
                var tierEntities = graph.Tiers[i];
                var hasCircular = graph.CircularReferences.Any(cr =>
                    cr.Entities.Any(e => tierEntities.Contains(e)));

                tiers.Add(new ImportTier
                {
                    TierNumber = i,
                    Entities = tierEntities.ToList(),
                    HasCircularReferences = hasCircular,
                    RequiresWait = true
                });

                foreach (var entity in tierEntities)
                {
                    entityTierMap[entity] = i;
                }
            }

            // Identify deferred fields
            var deferredFields = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var circularRef in graph.CircularReferences)
            {
                var circularSet = new HashSet<string>(circularRef.Entities, StringComparer.OrdinalIgnoreCase);
                var entityOrder = DetermineCircularProcessingOrder(circularRef, schema);

                for (var i = 0; i < entityOrder.Count; i++)
                {
                    var entityName = entityOrder[i];
                    var entitySchema = schema.GetEntity(entityName);
                    if (entitySchema == null) continue;

                    var deferred = new List<string>();

                    foreach (var field in entitySchema.Fields)
                    {
                        if (!field.IsLookup || string.IsNullOrEmpty(field.LookupEntity))
                        {
                            continue;
                        }

                        // If target is in circular reference and processed after this entity, defer
                        // Also defer self-referencing lookups (entity references itself) since parent
                        // records may be in the same batch as children
                        if (circularSet.Contains(field.LookupEntity))
                        {
                            var targetIndex = entityOrder.IndexOf(field.LookupEntity);
                            var isSelfReference = field.LookupEntity.Equals(entityName, StringComparison.OrdinalIgnoreCase);
                            if (targetIndex > i || isSelfReference)
                            {
                                deferred.Add(field.LogicalName);
                                _logger?.LogDebug("Deferring {Entity}.{Field} -> {Target}{Reason}",
                                    entityName, field.LogicalName, field.LookupEntity,
                                    isSelfReference ? " (self-reference)" : "");
                            }
                        }
                    }

                    if (deferred.Count > 0)
                    {
                        deferredFields[entityName] = deferred;
                    }
                }
            }

            // Identify M2M relationships
            var m2mRelationships = schema.GetAllManyToManyRelationships().ToList();

            _logger?.LogInformation("Built plan with {Tiers} tiers, {DeferredCount} deferred fields, {M2MCount} M2M relationships",
                tiers.Count, deferredFields.Sum(d => d.Value.Count), m2mRelationships.Count);

            return new ExecutionPlan
            {
                Tiers = tiers,
                DeferredFields = deferredFields,
                ManyToManyRelationships = m2mRelationships
            };
        }

        private List<string> DetermineCircularProcessingOrder(CircularReference circularRef, MigrationSchema schema)
        {
            // Heuristic: Process entities in order of lookup count (fewer lookups first)
            // This minimizes the number of deferred fields
            var lookupCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var circularSet = new HashSet<string>(circularRef.Entities, StringComparer.OrdinalIgnoreCase);

            foreach (var entityName in circularRef.Entities)
            {
                var entitySchema = schema.GetEntity(entityName);
                if (entitySchema == null)
                {
                    lookupCounts[entityName] = 0;
                    continue;
                }

                // Count lookups to other entities in the circular reference
                var count = entitySchema.Fields.Count(f =>
                    f.IsLookup &&
                    !string.IsNullOrEmpty(f.LookupEntity) &&
                    circularSet.Contains(f.LookupEntity));

                lookupCounts[entityName] = count;
            }

            // Sort by lookup count (ascending), then alphabetically for consistency
            return circularRef.Entities
                .OrderBy(e => lookupCounts.GetValueOrDefault(e, 0))
                .ThenBy(e => e, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
