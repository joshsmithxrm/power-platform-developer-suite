using System;

namespace PPDS.Dataverse.Sql.Parsing;

/// <summary>
/// Exception thrown when SQL parsing fails.
/// Includes position information for error reporting.
/// </summary>
public class SqlParseException : Exception
{
    /// <summary>
    /// The character position in the source where the error occurred.
    /// </summary>
    public int Position { get; }

    /// <summary>
    /// The line number (1-based) where the error occurred.
    /// </summary>
    public int Line { get; }

    /// <summary>
    /// The column number (1-based) where the error occurred.
    /// </summary>
    public int Column { get; }

    /// <summary>
    /// A snippet of the source around the error location.
    /// </summary>
    public string? ContextSnippet { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlParseException"/> class.
    /// </summary>
    public SqlParseException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlParseException"/> class
    /// with position information.
    /// </summary>
    public SqlParseException(string message, int position, string? source = null)
        : base(FormatMessage(message, position, source))
    {
        Position = position;

        if (source != null)
        {
            (Line, Column) = CalculateLineAndColumn(source, position);
            ContextSnippet = ExtractContext(source, position);
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlParseException"/> class
    /// with position information and inner exception.
    /// </summary>
    public SqlParseException(string message, int position, string? source, Exception innerException)
        : base(FormatMessage(message, position, source), innerException)
    {
        Position = position;

        if (source != null)
        {
            (Line, Column) = CalculateLineAndColumn(source, position);
            ContextSnippet = ExtractContext(source, position);
        }
    }

    /// <summary>
    /// Creates an exception at a specific position in the source.
    /// </summary>
    public static SqlParseException AtPosition(string message, int position, string source)
    {
        return new SqlParseException(message, position, source);
    }

    /// <summary>
    /// Creates an exception for an unexpected token.
    /// </summary>
    public static SqlParseException UnexpectedToken(SqlToken token, string? expected = null, string? source = null)
    {
        var message = expected != null
            ? $"Unexpected token '{token.Value}' ({token.Type}), expected {expected}"
            : $"Unexpected token '{token.Value}' ({token.Type})";

        return new SqlParseException(message, token.Position, source);
    }

    /// <summary>
    /// Creates an exception for an expected token that was not found.
    /// </summary>
    public static SqlParseException Expected(string expected, SqlToken actual, string? source = null)
    {
        var message = $"Expected {expected}, but found '{actual.Value}' ({actual.Type})";
        return new SqlParseException(message, actual.Position, source);
    }

    private static string FormatMessage(string message, int position, string? source)
    {
        if (source == null)
        {
            return $"{message} at position {position}";
        }

        var (line, column) = CalculateLineAndColumn(source, position);
        return $"{message} at line {line}, column {column}";
    }

    private static (int line, int column) CalculateLineAndColumn(string source, int position)
    {
        var line = 1;
        var column = 1;

        for (var i = 0; i < position && i < source.Length; i++)
        {
            if (source[i] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        return (line, column);
    }

    private static string ExtractContext(string source, int position, int contextLength = 40)
    {
        var start = Math.Max(0, position - contextLength / 2);
        var end = Math.Min(source.Length, position + contextLength / 2);

        var context = source[start..end];
        var prefix = start > 0 ? "..." : "";
        var suffix = end < source.Length ? "..." : "";

        // Replace newlines with visible markers
        context = context.Replace("\r\n", "↵").Replace("\n", "↵").Replace("\r", "↵");

        return $"{prefix}{context}{suffix}";
    }
}
