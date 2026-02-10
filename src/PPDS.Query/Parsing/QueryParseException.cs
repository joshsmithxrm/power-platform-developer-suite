using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
            : base(FormatMessage(errors))
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

        private static string FormatMessage(IList<ParseError> errors)
        {
            if (errors == null || errors.Count == 0)
                return "SQL parse error.";

            if (errors.Count == 1)
            {
                var e = errors[0];
                return $"SQL parse error at line {e.Line}, column {e.Column}: {e.Message}";
            }

            var sb = new StringBuilder();
            sb.Append($"SQL parse failed with {errors.Count} error(s):");

            foreach (var e in errors)
            {
                sb.AppendLine();
                sb.Append($"  Line {e.Line}, Column {e.Column}: {e.Message}");
            }

            return sb.ToString();
        }
    }
}
