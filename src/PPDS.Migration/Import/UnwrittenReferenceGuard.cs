using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Models;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// What may be done with a lookup or association whose target is a record.
    /// </summary>
    public enum ReferenceDecision
    {
        /// <summary>Send it: the target was written by this import, or it is known to exist in the target environment.</summary>
        Send,

        /// <summary>Do not send it: the target is a record of the data file that this import did not write and that is not known to exist in the target.</summary>
        NotSent,

        /// <summary>The target is not a record of the data file (or its table is not imported yet): the caller keeps its own behaviour.</summary>
        OutsideTheFile
    }

    /// <summary>
    /// Applies the rule "nothing is written that points at a record this import did not write,
    /// unless the record is known to exist in the target" to lookups and many-to-many associations
    /// whose target is a record of the data file (#1934 r2 MF2). A record of the file that was not
    /// written (a create that failed, a record a filter left out, a record of a table with
    /// <c>importMode="skip"</c>, a table whose write result was unproven) is looked up in the target by
    /// its id; a reference to it is sent only when it is found. Every reference not sent is counted
    /// per table and field or relationship, so the import can report it and is not a success.
    /// </summary>
    public sealed class UnwrittenReferenceGuard
    {
        private const int IdsPerQuery = 100;

        private readonly MigrationData _data;
        private readonly IdMappingCollection _mappings;
        private readonly IDataverseConnectionPool? _pool;
        private readonly ConcurrentDictionary<string, HashSet<Guid>> _idsInFile = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _tablesDone = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<(string Table, Guid Id), bool> _existsInTarget = new();
        private readonly ConcurrentDictionary<(string Table, string Via, bool Association), int> _notSent = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="UnwrittenReferenceGuard"/> class.
        /// </summary>
        /// <param name="data">The data being imported.</param>
        /// <param name="mappings">The mappings of the records proven written.</param>
        /// <param name="pool">The pool used to look records up in the target; without one, nothing unwritten is known to exist.</param>
        public UnwrittenReferenceGuard(MigrationData data, IdMappingCollection mappings, IDataverseConnectionPool? pool)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _mappings = mappings ?? throw new ArgumentNullException(nameof(mappings));
            _pool = pool;
        }

        /// <summary>Records that the entity phase of <paramref name="table"/> has finished, so an unmapped record of it was not written.</summary>
        public void MarkTableDone(string table) => _tablesDone.TryAdd(table, 0);

        /// <summary>
        /// Decides about a reference to <paramref name="table"/> record <paramref name="id"/>, and gives
        /// the id to send. Call <see cref="EnsureCheckedAsync"/> for the targets first.
        /// </summary>
        public ReferenceDecision Decide(string table, Guid id, out Guid targetId)
        {
            if (_mappings.TryGetNewId(table, id, out targetId))
            {
                return ReferenceDecision.Send;
            }

            targetId = id;
            if (!IsUnwrittenRecordOfTheFile(table, id))
            {
                return ReferenceDecision.OutsideTheFile;
            }

            return _existsInTarget.TryGetValue((table, id), out var exists) && exists
                ? ReferenceDecision.Send
                : ReferenceDecision.NotSent;
        }

        /// <summary>Counts a lookup through <paramref name="field"/> of <paramref name="table"/> that was not sent.</summary>
        public void CountLookupNotSent(string table, string field) =>
            _notSent.AddOrUpdate((table, field, false), 1, (_, n) => n + 1);

        /// <summary>Counts an association of <paramref name="table"/> through <paramref name="relationship"/> that was not sent.</summary>
        public void CountAssociationNotSent(string table, string relationship) =>
            _notSent.AddOrUpdate((table, relationship, true), 1, (_, n) => n + 1);

        /// <summary>The references not sent: table, field or relationship, whether an association, and how many.</summary>
        public IReadOnlyList<(string Table, string Via, bool Association, int Count)> NotSent =>
            _notSent.Select(kv => (kv.Key.Table, kv.Key.Via, kv.Key.Association, kv.Value))
                .OrderBy(x => x.Table, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Via, StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>
        /// Looks up in the target, by id, every target among <paramref name="targets"/> that is a record
        /// of the data file this import did not write, so <see cref="Decide"/> can tell whether it exists.
        /// A failed lookup leaves the record not known to exist.
        /// </summary>
        public async Task EnsureCheckedAsync(IEnumerable<EntityReference> targets, CancellationToken cancellationToken)
        {
            var toCheck = targets
                .Where(t => t != null && IsUnwrittenRecordOfTheFile(t.LogicalName, t.Id) && !_existsInTarget.ContainsKey((t.LogicalName, t.Id)))
                .GroupBy(t => t.LogicalName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (toCheck.Count == 0)
            {
                return;
            }

            foreach (var group in toCheck)
            {
                var ids = group.Select(t => t.Id).Distinct().ToList();
                var primaryKey = _data.Schema.GetEntity(group.Key)?.PrimaryIdField ?? $"{group.Key}id";
                foreach (var chunk in ids.Chunk(IdsPerQuery))
                {
                    var found = await FindInTargetAsync(group.Key, primaryKey, chunk, cancellationToken).ConfigureAwait(false);
                    foreach (var id in chunk)
                    {
                        _existsInTarget[(group.Key, id)] = found != null && found.Contains(id);
                    }
                }
            }
        }

        private async Task<HashSet<Guid>?> FindInTargetAsync(string table, string primaryKey, Guid[] ids, CancellationToken cancellationToken)
        {
            if (_pool == null)
            {
                return null;
            }

            try
            {
                await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                var query = new QueryExpression(table) { ColumnSet = new ColumnSet(primaryKey) };
                query.Criteria.AddCondition(primaryKey, ConditionOperator.In, ids.Cast<object>().ToArray());
                var result = await client.RetrieveMultipleAsync(query, cancellationToken).ConfigureAwait(false);
                return new HashSet<Guid>(result.Entities.Select(e => e.Id));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not known to exist: the references are not sent and are reported.
                return null;
            }
        }

        private bool IsUnwrittenRecordOfTheFile(string table, Guid id)
        {
            if (id == Guid.Empty || !_tablesDone.ContainsKey(table) || _mappings.TryGetNewId(table, id, out _))
            {
                return false;
            }

            var ids = _idsInFile.GetOrAdd(table, t =>
                _data.EntityData.TryGetValue(t, out var records)
                    ? new HashSet<Guid>(records.Select(r => r.Id))
                    : new HashSet<Guid>());
            return ids.Contains(id);
        }
    }
}
