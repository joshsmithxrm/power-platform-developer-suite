using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Models;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Resolves entity references using cascading lookup resolution.
    /// </summary>
    public class EntityReferenceMapper
    {
        private readonly IdMappingCollection _idMappings;
        private readonly IDataverseConnectionPool _pool;
        private readonly ImportOptions _options;
        private readonly ILogger? _logger;
        private readonly ConcurrentDictionary<(string entityName, Guid sourceId), Guid?> _cache = new();

        private static readonly Dictionary<string, string> MatchFieldOverrides = new(StringComparer.OrdinalIgnoreCase)
        {
            ["transactioncurrency"] = "isocurrencycode",
            ["businessunit"] = "name",
            ["role"] = "name",
            ["uom"] = "name",
            ["uomschedule"] = "name"
        };

        /// <summary>
        /// Initializes a new instance of the <see cref="EntityReferenceMapper"/> class.
        /// </summary>
        /// <param name="idMappings">The ID mapping collection.</param>
        /// <param name="pool">The connection pool.</param>
        /// <param name="options">The import options.</param>
        /// <param name="logger">Optional logger.</param>
        public EntityReferenceMapper(IdMappingCollection idMappings, IDataverseConnectionPool pool, ImportOptions options, ILogger<EntityReferenceMapper>? logger = null)
        {
            _idMappings = idMappings ?? throw new ArgumentNullException(nameof(idMappings));
            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger;
        }

        /// <summary>
        /// Resolves a source entity reference to its target GUID.
        /// </summary>
        /// <param name="targetEntityName">The target entity logical name.</param>
        /// <param name="sourceId">The source record GUID.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The resolved target GUID, or null if unresolved.</returns>
        public async Task<Guid?> ResolveAsync(string targetEntityName, Guid sourceId, CancellationToken cancellationToken)
        {
            if (_idMappings.TryGetNewId(targetEntityName, sourceId, out var mappedId)) return mappedId;
            if (!_options.ResolveExternalLookups) return null;
            var ck = (targetEntityName, sourceId);
            if (_cache.TryGetValue(ck, out var cached)) return cached;
            var dr = await TryDirectIdCheckAsync(targetEntityName, sourceId, cancellationToken).ConfigureAwait(false);
            if (dr.HasValue) { _cache[ck] = dr.Value; return dr.Value; }
            _cache[ck] = null;
            return null;
        }

        /// <summary>
        /// Resolves a lookup with name-based fallback.
        /// </summary>
        /// <param name="targetEntityName">The target entity logical name.</param>
        /// <param name="sourceId">The source record GUID.</param>
        /// <param name="nameValue">The name value for name-based resolution.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The resolved target GUID, or null if unresolved.</returns>
        public async Task<Guid?> ResolveWithNameAsync(string targetEntityName, Guid sourceId, string? nameValue, CancellationToken cancellationToken)
        {
            if (_idMappings.TryGetNewId(targetEntityName, sourceId, out var mappedId)) return mappedId;
            if (!_options.ResolveExternalLookups) return null;
            var ck = (targetEntityName, sourceId);
            if (_cache.TryGetValue(ck, out var cached)) return cached;
            var dr = await TryDirectIdCheckAsync(targetEntityName, sourceId, cancellationToken).ConfigureAwait(false);
            if (dr.HasValue) { _cache[ck] = dr.Value; return dr.Value; }
            if (string.IsNullOrEmpty(nameValue)) { _cache[ck] = null; return null; }
            var mf = GetMatchField(targetEntityName);
            if (string.IsNullOrEmpty(mf)) { _cache[ck] = null; return null; }
            var nr = await QueryByNameAsync(targetEntityName, mf, nameValue, cancellationToken).ConfigureAwait(false);
            _cache[ck] = nr;
            return nr;
        }

        /// <summary>
        /// Gets the match field for name-based resolution.
        /// </summary>
        /// <param name="entityName">The entity logical name.</param>
        /// <returns>The match field name, or null if no match field is known.
        /// Full implementation would require entity metadata lookup for primary name attribute.</returns>
        public static string? GetMatchField(string entityName)
        {
            return MatchFieldOverrides.TryGetValue(entityName, out var f) ? f : null;
        }

        private async Task<Guid?> TryDirectIdCheckAsync(string en, Guid sid, CancellationToken ct)
        {
            try
            {
                await using var c = await _pool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
                var req = new RetrieveRequest { Target = new EntityReference(en, sid), ColumnSet = new ColumnSet(false) };
                await c.ExecuteAsync(req, ct).ConfigureAwait(false);
                return sid;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        }

        private async Task<Guid?> QueryByNameAsync(string en, string mf, string nv, CancellationToken ct)
        {
            try
            {
                await using var c = await _pool.GetClientAsync(cancellationToken: ct).ConfigureAwait(false);
                var q = new QueryExpression(en) { ColumnSet = new ColumnSet(false), TopCount = 1 };
                q.Criteria.AddCondition(mf, ConditionOperator.Equal, nv);
                var r = await c.RetrieveMultipleAsync(q, ct).ConfigureAwait(false);
                return r.Entities.Count > 0 ? r.Entities[0].Id : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        }
    }
}
