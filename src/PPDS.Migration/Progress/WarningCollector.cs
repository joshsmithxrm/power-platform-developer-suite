using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace PPDS.Migration.Progress
{
    /// <summary>
    /// Thread-safe implementation of <see cref="IWarningCollector"/>.
    /// </summary>
    public class WarningCollector : IWarningCollector
    {
        private readonly ConcurrentBag<ImportWarning> _warnings = new();

        /// <inheritdoc />
        public void AddWarning(ImportWarning warning)
        {
            _warnings.Add(warning);
        }

        /// <inheritdoc />
        public IReadOnlyList<ImportWarning> GetWarnings()
        {
            return _warnings.ToList().AsReadOnly();
        }

        /// <inheritdoc />
        public int Count => _warnings.Count;
    }
}
