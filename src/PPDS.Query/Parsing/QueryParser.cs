using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Sql.Parsing;

namespace PPDS.Query.Parsing;

/// <summary>
/// Wraps TSql160Parser to parse SQL text into a TSqlFragment AST.
/// Provides a clean API for the query engine, converting ScriptDom
/// parse errors into <see cref="SqlParseException"/>.
/// </summary>
public sealed class QueryParser
{
    private readonly TSql160Parser _parser;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryParser"/> class.
    /// </summary>
    public QueryParser()
    {
        // initialQuotedIdentifiers: true enables [bracketed] identifiers by default
        _parser = new TSql160Parser(initialQuotedIdentifiers: true);
    }

    /// <summary>
    /// Parses SQL text into a TSqlFragment AST.
    /// </summary>
    /// <param name="sql">The SQL text to parse.</param>
    /// <returns>The parsed TSqlFragment AST.</returns>
    /// <exception cref="SqlParseException">If parsing fails with syntax errors.</exception>
    /// <exception cref="ArgumentNullException">If sql is null.</exception>
    public TSqlFragment Parse(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        using var reader = new StringReader(sql);
        var fragment = _parser.Parse(reader, out var errors);

        if (errors != null && errors.Count > 0)
        {
            throw CreateParseException(errors, sql);
        }

        return fragment;
    }

    /// <summary>
    /// Parses SQL text into a TSqlScript (the root AST node for batches).
    /// </summary>
    /// <param name="sql">The SQL text to parse.</param>
    /// <returns>The parsed TSqlScript.</returns>
    /// <exception cref="SqlParseException">If parsing fails with syntax errors.</exception>
    /// <exception cref="ArgumentNullException">If sql is null.</exception>
    public TSqlScript ParseScript(string sql)
    {
        var fragment = Parse(sql);

        if (fragment is not TSqlScript script)
        {
            throw new SqlParseException("Expected a SQL script, but got " + fragment.GetType().Name);
        }

        return script;
    }

    /// <summary>
    /// Attempts to parse SQL text, returning errors instead of throwing.
    /// </summary>
    /// <param name="sql">The SQL text to parse.</param>
    /// <param name="fragment">The parsed fragment if successful; null if failed.</param>
    /// <param name="errors">Parse errors if any; empty list if successful.</param>
    /// <returns>True if parsing succeeded with no errors; false otherwise.</returns>
    public bool TryParse(string sql, out TSqlFragment? fragment, out IReadOnlyList<ParseError> errors)
    {
        ArgumentNullException.ThrowIfNull(sql);

        using var reader = new StringReader(sql);
        fragment = _parser.Parse(reader, out var parseErrors);
        errors = parseErrors ?? (IReadOnlyList<ParseError>)Array.Empty<ParseError>();

        return errors.Count == 0;
    }

    /// <summary>
    /// Parses SQL text and returns the first statement.
    /// Throws if no statements are present or if parsing fails.
    /// </summary>
    /// <typeparam name="T">Expected statement type.</typeparam>
    /// <param name="sql">The SQL text to parse.</param>
    /// <returns>The first statement cast to type T.</returns>
    /// <exception cref="SqlParseException">If parsing fails or statement type doesn't match.</exception>
    public T ParseStatement<T>(string sql) where T : TSqlStatement
    {
        var script = ParseScript(sql);

        if (script.Batches.Count == 0 || script.Batches[0].Statements.Count == 0)
        {
            throw new SqlParseException("No SQL statements found in input.");
        }

        var statement = script.Batches[0].Statements[0];

        if (statement is not T typedStatement)
        {
            throw new SqlParseException(
                $"Expected {typeof(T).Name}, but found {statement.GetType().Name}");
        }

        return typedStatement;
    }

    /// <summary>
    /// Gets the first statement from a parsed script, or null if empty.
    /// </summary>
    /// <param name="script">The parsed script.</param>
    /// <returns>The first statement, or null if no statements.</returns>
    public static TSqlStatement? GetFirstStatement(TSqlScript script)
    {
        if (script.Batches.Count == 0 || script.Batches[0].Statements.Count == 0)
        {
            return null;
        }

        return script.Batches[0].Statements[0];
    }

    /// <summary>
    /// Gets all statements from a parsed script.
    /// </summary>
    /// <param name="script">The parsed script.</param>
    /// <returns>All statements across all batches.</returns>
    public static IEnumerable<TSqlStatement> GetAllStatements(TSqlScript script)
    {
        foreach (var batch in script.Batches)
        {
            foreach (var statement in batch.Statements)
            {
                yield return statement;
            }
        }
    }

    /// <summary>
    /// Converts ScriptDom parse errors to a SqlParseException with detailed error information.
    /// </summary>
    private static SqlParseException CreateParseException(IList<ParseError> errors, string source)
    {
        // ScriptDom positions are 1-based line/column; convert to 0-based offset
        var firstError = errors[0];
        var position = CalculateOffset(source, firstError.Line, firstError.Column);

        if (errors.Count == 1)
        {
            return new SqlParseException(firstError.Message, position, source);
        }

        // Multiple errors: format them all
        var sb = new StringBuilder();
        sb.AppendLine($"{errors.Count} parse errors:");

        foreach (var error in errors.Take(5)) // Limit to 5 errors
        {
            sb.AppendLine($"  Line {error.Line}, Column {error.Column}: {error.Message}");
        }

        if (errors.Count > 5)
        {
            sb.AppendLine($"  ... and {errors.Count - 5} more errors");
        }

        return new SqlParseException(sb.ToString().TrimEnd(), position, source);
    }

    /// <summary>
    /// Converts 1-based line/column to 0-based character offset.
    /// </summary>
    private static int CalculateOffset(string source, int line, int column)
    {
        var offset = 0;
        var currentLine = 1;

        for (var i = 0; i < source.Length && currentLine < line; i++)
        {
            if (source[i] == '\n')
            {
                currentLine++;
            }
            offset++;
        }

        // Add column offset (convert from 1-based to 0-based)
        offset += Math.Max(0, column - 1);

        return Math.Min(offset, source.Length);
    }
}

/// <summary>
/// Extension methods for working with ScriptDom AST nodes.
/// </summary>
public static class ScriptDomExtensions
{
    /// <summary>
    /// Gets the original SQL text for a fragment from its token stream.
    /// </summary>
    /// <param name="fragment">The AST fragment.</param>
    /// <returns>The SQL text, or empty string if unavailable.</returns>
    public static string GetSqlText(this TSqlFragment fragment)
    {
        if (fragment.ScriptTokenStream == null || fragment.FirstTokenIndex < 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var i = fragment.FirstTokenIndex; i <= fragment.LastTokenIndex; i++)
        {
            sb.Append(fragment.ScriptTokenStream[i].Text);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Gets the start offset of a fragment in the source text.
    /// </summary>
    /// <param name="fragment">The AST fragment.</param>
    /// <returns>The 0-based character offset.</returns>
    public static int GetStartOffset(this TSqlFragment fragment)
    {
        if (fragment.ScriptTokenStream == null || fragment.FirstTokenIndex < 0)
        {
            return 0;
        }

        return fragment.ScriptTokenStream[fragment.FirstTokenIndex].Offset;
    }

    /// <summary>
    /// Gets the end offset of a fragment in the source text.
    /// </summary>
    /// <param name="fragment">The AST fragment.</param>
    /// <returns>The 0-based character offset past the last character.</returns>
    public static int GetEndOffset(this TSqlFragment fragment)
    {
        if (fragment.ScriptTokenStream == null || fragment.LastTokenIndex < 0)
        {
            return 0;
        }

        var lastToken = fragment.ScriptTokenStream[fragment.LastTokenIndex];
        return lastToken.Offset + lastToken.Text.Length;
    }

    /// <summary>
    /// Gets the line number (1-based) of the start of a fragment.
    /// </summary>
    /// <param name="fragment">The AST fragment.</param>
    /// <returns>The 1-based line number.</returns>
    public static int GetStartLine(this TSqlFragment fragment)
    {
        return fragment.StartLine;
    }

    /// <summary>
    /// Gets the column number (1-based) of the start of a fragment.
    /// </summary>
    /// <param name="fragment">The AST fragment.</param>
    /// <returns>The 1-based column number.</returns>
    public static int GetStartColumn(this TSqlFragment fragment)
    {
        return fragment.StartColumn;
    }
}
