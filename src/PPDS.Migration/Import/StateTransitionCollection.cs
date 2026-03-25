using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace PPDS.Migration.Import
{
    /// <summary>
    /// Thread-safe collection of state transitions to apply after record import.
    /// </summary>
    public class StateTransitionCollection
    {
        private readonly ConcurrentDictionary<string, ConcurrentBag<StateTransitionData>> _transitions
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Adds a state transition for a record.
        /// </summary>
        /// <param name="entityName">The entity logical name.</param>
        /// <param name="recordId">The record ID.</param>
        /// <param name="data">The state transition data.</param>
        public void Add(string entityName, Guid recordId, StateTransitionData data)
        {
            ArgumentNullException.ThrowIfNull(data);
            var bag = _transitions.GetOrAdd(entityName, _ => new ConcurrentBag<StateTransitionData>());
            bag.Add(data);
        }

        /// <summary>
        /// Gets all transitions for a given entity.
        /// </summary>
        /// <param name="entityName">The entity logical name.</param>
        /// <returns>A read-only list of transitions, or an empty list if none exist.</returns>
        public IReadOnlyList<StateTransitionData> GetTransitions(string entityName)
        {
            if (_transitions.TryGetValue(entityName, out var bag))
            {
                return bag.ToList();
            }
            return Array.Empty<StateTransitionData>();
        }

        /// <summary>
        /// Gets all entity names that have transitions.
        /// </summary>
        public IEnumerable<string> GetEntityNames() => _transitions.Keys;

        /// <summary>
        /// Gets the total number of transitions across all entities.
        /// </summary>
        public int Count
        {
            get
            {
                var total = 0;
                foreach (var bag in _transitions.Values)
                {
                    total += bag.Count;
                }
                return total;
            }
        }
    }
}
