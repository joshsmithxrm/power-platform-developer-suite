using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.Query;

/// <summary>
/// A DML statement was refused because it would affect more rows than the row cap (<c>Query.DmlRowCapExceeded</c>).
/// The message is surface-neutral (it names no CLI flag); the structured properties let each surface say how to
/// proceed (the CLI adds <c>--no-limit</c>, RPC returns them as error data, the TUI explains the cap is fixed there).
/// </summary>
public sealed class PpdsDmlRowCapExceededException : PpdsException
{
    /// <summary>Creates the refusal.</summary>
    public PpdsDmlRowCapExceededException(string operation, string entityLogicalName, int rowCap, string message, Exception inner)
        : base(ErrorCodes.Query.DmlRowCapExceeded, message, inner)
    {
        Operation = operation;
        EntityLogicalName = entityLogicalName;
        RowCap = rowCap;
    }

    /// <summary>The refused operation: INSERT, UPDATE or DELETE.</summary>
    public string Operation { get; }

    /// <summary>The target entity.</summary>
    public string EntityLogicalName { get; }

    /// <summary>The cap that would have been exceeded.</summary>
    public int RowCap { get; }
}

/// <summary>An environment a DML request would write to, as reported with <c>Query.DmlConfirmationRequired</c>.</summary>
/// <param name="Label">The environment's configured label, if any.</param>
/// <param name="Url">The environment URL, when the caller supplied it.</param>
/// <param name="Type">The configured environment type (Production, Sandbox, ...), when known.</param>
/// <param name="Protection">The protection level the guard applied (Production, Test, Development).</param>
public sealed record DmlTarget(string? Label, string? Url, string? Type, string Protection);

/// <summary>
/// DML was requested without confirmation (<c>Query.DmlConfirmationRequired</c>). Carries every environment the
/// request would write to, so a client can show where the change will happen before asking.
/// </summary>
public sealed class PpdsDmlConfirmationRequiredException : PpdsException
{
    /// <summary>Creates the refusal.</summary>
    public PpdsDmlConfirmationRequiredException(string message, IReadOnlyList<DmlTarget> targets)
        : base(ErrorCodes.Query.DmlConfirmationRequired, message)
    {
        Targets = targets;
    }

    /// <summary>The environments the request would write to.</summary>
    public IReadOnlyList<DmlTarget> Targets { get; }
}

/// <summary>
/// A SQL run was stopped by a sign-in failure after it had already changed data (<c>Query.DmlInterrupted</c>). It is
/// never reported with an <c>Auth.*</c> code, so no surface re-authenticates and replays the request (which would run
/// the statements that already ran a second time). <see cref="AuthenticationRequired"/> tells the user to sign in.
/// </summary>
public sealed class PpdsDmlInterruptedException : PpdsException
{
    /// <summary>Creates the exception.</summary>
    public PpdsDmlInterruptedException(string message, bool authenticationRequired, Exception inner)
        : base(ErrorCodes.Query.DmlInterrupted, message, inner)
    {
        AuthenticationRequired = authenticationRequired;
    }

    /// <summary>True when the interruption was a sign-in failure: the user must sign in again before the next request.</summary>
    public bool AuthenticationRequired { get; }
}
