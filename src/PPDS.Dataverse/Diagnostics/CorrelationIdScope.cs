using System;
using System.Threading;

namespace PPDS.Dataverse.Diagnostics
{
    /// <summary>
    /// Ambient correlation-ID scope shared by the CLI, the RPC daemon, and every
    /// Dataverse service called from either surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses <see cref="AsyncLocal{T}"/> so the current correlation-ID flows across
    /// <c>await</c> continuations, <c>Task.Run</c>, and <c>Parallel.ForEach</c>
    /// without needing to thread it through every method signature.
    /// </para>
    /// <para>
    /// Lives in <c>PPDS.Dataverse.Diagnostics</c> — the lowest common dependency
    /// between the CLI daemon and the Dataverse services — so both sides can
    /// read/write the same ambient slot without introducing a new shared package.
    /// </para>
    /// <para>
    /// v1 stores the correlation id locally only; this scope is consumed by the
    /// CLI console loggers and log enrichers. No remote telemetry is emitted.
    /// </para>
    /// </remarks>
    public static class CorrelationIdScope
    {
        private static readonly AsyncLocal<string?> _current = new();

        /// <summary>
        /// Gets the correlation id for the current async flow, or <see langword="null"/>
        /// when no scope is active.
        /// </summary>
        public static string? Current => _current.Value;

        /// <summary>
        /// Establishes a correlation-id scope for the lifetime of the returned
        /// <see cref="IDisposable"/>. The previous value is restored on dispose so
        /// nested scopes (e.g., CLI entry → per-RPC-call) behave correctly.
        /// </summary>
        /// <param name="correlationId">
        /// The correlation id to install. A <see langword="null"/> or whitespace
        /// value is treated as "clear the slot" for this scope.
        /// </param>
        /// <returns>A disposable handle that restores the previous value.</returns>
        public static IDisposable Push(string? correlationId)
        {
            var previous = _current.Value;
            _current.Value = string.IsNullOrWhiteSpace(correlationId) ? null : correlationId;
            return new PopHandle(previous);
        }

        /// <summary>
        /// Generates a new GUID-based correlation id (lowercase hyphenated form).
        /// Callers should prefer <see cref="Push(string?)"/> to install a value;
        /// this helper exists so CLI entrypoints can seed the initial id without
        /// duplicating the format string.
        /// </summary>
        public static string NewId() => Guid.NewGuid().ToString("D");

        private sealed class PopHandle : IDisposable
        {
            private readonly string? _previous;
            private bool _disposed;

            public PopHandle(string? previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _current.Value = _previous;
            }
        }
    }
}
