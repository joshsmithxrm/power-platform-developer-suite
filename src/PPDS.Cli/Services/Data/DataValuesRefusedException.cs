using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.Data;

/// <summary>
/// One reason a <c>data load</c> or <c>data update</c> input is refused: where it is (a data row of the file, a column,
/// or a <c>--set</c> pair) and why. It never holds the content of a cell or a value.
/// </summary>
/// <param name="Row">The 1-based data row of the file (the header is not counted), or null.</param>
/// <param name="Column">The file's column header or the <c>--set</c> column name, or null.</param>
/// <param name="Message">Why, in a sentence that repeats no value.</param>
public sealed record DataValueProblem(int? Row, string? Column, string Message)
{
    /// <summary>The problem as one line: <c>Row 3, column 'rate': ...</c>.</summary>
    public override string ToString()
    {
        var where = (Row, Column) switch
        {
            ({ } r, { } c) => $"Row {r}, column '{c}': ",
            ({ } r, null) => $"Row {r}: ",
            (null, { } c) => $"Column '{c}': ",
            _ => ""
        };
        return where + Message;
    }
}

/// <summary>
/// The input of <c>data load</c> or <c>data update</c> is refused before anything is written (#1916, #1917): the file is
/// not a well-formed CSV of the header's shape, or a value is not exact for its column. <see cref="Problems"/> lists every
/// reason found; nothing was written.
/// </summary>
public sealed class DataValuesRefusedException : PpdsException
{
    private const int ProblemsInMessage = 20;

    /// <summary>
    /// Initializes a new instance of the <see cref="DataValuesRefusedException"/> class.
    /// </summary>
    /// <param name="problems">Every reason found, in input order.</param>
    /// <param name="inner">The library exception that caused it, if any.</param>
    public DataValuesRefusedException(IReadOnlyList<DataValueProblem> problems, Exception? inner = null)
        : base(ErrorCodes.Validation.InvalidValue, BuildMessage(problems), inner)
    {
        Problems = problems;
    }

    /// <summary>Every reason found, in input order.</summary>
    public IReadOnlyList<DataValueProblem> Problems { get; }

    private static string BuildMessage(IReadOnlyList<DataValueProblem> problems)
    {
        var message = new StringBuilder(problems.Count == 1
            ? "1 problem in the input; nothing was written:"
            : $"{problems.Count} problems in the input; nothing was written:");
        foreach (var problem in problems.Take(ProblemsInMessage))
        {
            message.Append(System.Environment.NewLine).Append("  ").Append(problem);
        }

        if (problems.Count > ProblemsInMessage)
        {
            message.Append(System.Environment.NewLine).Append($"  ... and {problems.Count - ProblemsInMessage} more");
        }

        return message.ToString();
    }
}
