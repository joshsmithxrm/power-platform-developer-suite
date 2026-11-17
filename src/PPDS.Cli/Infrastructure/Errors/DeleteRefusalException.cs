namespace PPDS.Cli.Infrastructure.Errors;

/// <summary>
/// A schema delete refused because its dependency check found blockers or could not be completed.
/// Carries the exit classification so the command preflight and the service recheck exit alike
/// (blocked 10, incomplete 12) while the error code and message are unchanged.
/// </summary>
internal sealed class DeleteRefusalException : PpdsException
{
    /// <summary>The process exit code for this refusal.</summary>
    public int ExitCode { get; }

    public DeleteRefusalException(string errorCode, string userMessage, int exitCode,
        IDictionary<string, object> context, Exception? inner = null)
        : base(errorCode, userMessage, context, inner) => ExitCode = exitCode;
}
