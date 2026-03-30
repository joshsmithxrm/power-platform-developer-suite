using System;

namespace PPDS.Migration.Progress
{
    /// <summary>
    /// Creates <see cref="IProgress{T}"/> adapters that bridge
    /// <see cref="Dataverse.Progress.ProgressSnapshot"/> to <see cref="IProgressReporter"/>.
    /// </summary>
    public static class ProgressAdapterFactory
    {
        /// <summary>
        /// Creates an <see cref="IProgress{ProgressSnapshot}"/> that maps each snapshot
        /// to a <see cref="ProgressEventArgs"/> via the supplied <paramref name="mapper"/>
        /// and forwards it to the <paramref name="reporter"/>.
        /// </summary>
        /// <param name="reporter">The progress reporter to forward to.</param>
        /// <param name="mapper">
        /// A function that converts a <see cref="Dataverse.Progress.ProgressSnapshot"/>
        /// into a <see cref="ProgressEventArgs"/>.
        /// </param>
        /// <returns>An <see cref="IProgress{ProgressSnapshot}"/> adapter.</returns>
        public static IProgress<Dataverse.Progress.ProgressSnapshot> Create(
            IProgressReporter reporter,
            Func<Dataverse.Progress.ProgressSnapshot, ProgressEventArgs> mapper)
        {
            if (reporter == null) throw new ArgumentNullException(nameof(reporter));
            if (mapper == null) throw new ArgumentNullException(nameof(mapper));

            return new SynchronousProgress(reporter, mapper);
        }

        /// <summary>
        /// Synchronous <see cref="IProgress{T}"/> that invokes the mapper and reporter
        /// inline on the calling thread. Unlike <see cref="Progress{T}"/>, this does not
        /// post to a <see cref="System.Threading.SynchronizationContext"/>.
        /// </summary>
        private sealed class SynchronousProgress : IProgress<Dataverse.Progress.ProgressSnapshot>
        {
            private readonly IProgressReporter _reporter;
            private readonly Func<Dataverse.Progress.ProgressSnapshot, ProgressEventArgs> _mapper;

            public SynchronousProgress(
                IProgressReporter reporter,
                Func<Dataverse.Progress.ProgressSnapshot, ProgressEventArgs> mapper)
            {
                _reporter = reporter;
                _mapper = mapper;
            }

            public void Report(Dataverse.Progress.ProgressSnapshot value)
            {
                _reporter.Report(_mapper(value));
            }
        }
    }
}
