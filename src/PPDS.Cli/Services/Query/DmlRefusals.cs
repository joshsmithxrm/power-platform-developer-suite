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
