namespace PPDS.Dataverse.Resilience
{
    /// <summary>
    /// Predefined rate control configurations for common scenarios.
    /// </summary>
    public enum RateControlPreset
    {
        /// <summary>
        /// Prioritize avoiding throttles over throughput.
        /// Good for: production bulk jobs, overnight migrations, background processing.
        /// Settings: Factor=180, Threshold=8000, DecreaseFactor=0.4, Stabilization=5, Interval=8s
        /// </summary>
        Conservative,

        /// <summary>
        /// Balance throughput and throttle avoidance.
        /// Good for: general purpose, mixed workloads.
        /// Settings: Factor=250, Threshold=9000, DecreaseFactor=0.5, Stabilization=3, Interval=5s
        /// </summary>
        Balanced,

        /// <summary>
        /// Prioritize throughput, accept occasional short throttles.
        /// Good for: dev/test, time-critical migrations with monitoring.
        /// Settings: Factor=320, Threshold=11000, DecreaseFactor=0.6, Stabilization=2, Interval=3s
        /// </summary>
        Aggressive
    }
}
