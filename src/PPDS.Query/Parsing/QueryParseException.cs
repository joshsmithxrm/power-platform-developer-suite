using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace PPDS.Query.Parsing
{
    /// <summary>
    /// Exception thrown when SQL parsing fails.
    /// Contains structured error information including line/column positions
    /// and the underlying ScriptDom parse errors.
    /// </summary>
    public class QueryParseException : Exception
    {
        /// <summary>
        /// Error code for programmatic handling of parse failures.
        /// </summary>
        public const string ErrorCodeValue = "QUERY_PARSE_ERROR";

        /// <summary>
        /// Gets the error code identifying this as a query parse error.
        /// </summary>
        public string ErrorCode { get; } = ErrorCodeValue;

        /// <summary>
        /// Gets the parse errors reported by the SQL parser.
        /// </summary>
        public IReadOnlyList<ParseError> Errors { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="QueryParseException"/> class.
        /// </summary>
        /// <param name="errors">The parse errors from ScriptDom.</param>
        public QueryParseException(IList<ParseError> errors)
            : base(FormatMessage(errors, null))
        {
            Errors = errors.ToList().AsReadOnly();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="QueryParseException"/> class
        /// with the original SQL text for enhanced error hints.
        /// </summary>
        /// <param name="errors">The parse errors from ScriptDom.</param>
        /// <param name="sql">The original SQL text, used to detect missing whitespace.</param>
        public QueryParseException(IList<ParseError> errors, string sql)
            : base(FormatMessage(errors, sql))
        {
            Errors = errors.ToList().AsReadOnly();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="QueryParseException"/> class.
        /// </summary>
        /// <param name="message">The error message.</param>
        public QueryParseException(string message)
            : base(message)
        {
            Errors = Array.Empty<ParseError>();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="QueryParseException"/> class.
        /// </summary>
        /// <param name="message">The error message.</param>
        /// <param name="innerException">The inner exception.</param>
        public QueryParseException(string message, Exception innerException)
            : base(message, innerException)
        {
            Errors = Array.Empty<ParseError>();
        }

        private static readonly string[] HintKeywords =
        {
            "SELECT", "FROM", "WHERE", "GROUP", "HAVING", "ORDER",
            "JOIN", "INNER", "LEFT", "RIGHT", "CROSS", "OUTER",
            "INSERT", "UPDATE", "DELETE", "INTO", "VALUES",
            "UNION", "BETWEEN", "FETCH", "OFFSET"
        };

        private static string FormatMessage(IList<ParseError> errors, string? sql)
        {
            if (errors == null || errors.Count == 0)
                return "SQL parse error.";

            string baseMessage;

            if (errors.Count == 1)
            {
                var e = errors[0];
                baseMessage = $"SQL parse error at line {e.Line}, column {e.Column}: {e.Message}";
            }
            else
            {
                var sb = new StringBuilder();
                sb.Append($"SQL parse failed with {errors.Count} error(s):");

                foreach (var e in errors)
                {
                    sb.AppendLine();
                    sb.Append($"  Line {e.Line}, Column {e.Column}: {e.Message}");
                }

                baseMessage = sb.ToString();
            }

            if (sql != null)
            {
                var hint = DetectMissingWhitespace(sql);
                if (hint != null)
                    baseMessage = baseMessage + "\nHint: " + hint;
            }

            return baseMessage;
        }

        internal static string? DetectMissingWhitespace(string sql)
        {
            foreach (Match match in Regex.Matches(sql, @"\b\w+\b"))
            {
                var word = match.Value;
                if (word.Length < 5)
                    continue;

                foreach (var keyword in HintKeywords)
                {
                    if (word.Length <= keyword.Length)
                        continue;

                    // Keyword at end: accountWHERE → account WHERE
                    if (word.EndsWith(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        var prefix = word.Substring(0, word.Length - keyword.Length);
                        return $"Possible missing whitespace: '{word}' may be " +
                               $"'{prefix} {keyword.ToUpperInvariant()}'";
                    }

                    // Keyword at start: WHEREaccount → WHERE account
                    if (word.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        var suffix = word.Substring(keyword.Length);
                        if (suffix.Length >= 2)
                            return $"Possible missing whitespace: '{word}' may be " +
                                   $"'{keyword.ToUpperInvariant()} {suffix}'";
                    }
                }
            }

            return null;
        }
    }
}
