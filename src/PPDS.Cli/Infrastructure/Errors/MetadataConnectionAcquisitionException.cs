namespace PPDS.Cli.Infrastructure.Errors;

/// <summary>
/// Coded acquisition failure for the bounded metadata workflows. The original
/// mapper exit is retained because a code category alone cannot preserve every
/// prior exit (for example, file-not-found validation codes originally exit 5).
/// </summary>
internal sealed class MetadataConnectionAcquisitionException(
    string errorCode, string userMessage, int exitCode, Exception innerException)
    : PpdsException(errorCode, userMessage, innerException)
{
    internal int ExitCode { get; } = exitCode;
}
