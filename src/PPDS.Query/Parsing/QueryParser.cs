using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace PPDS.Query.Parsing
{
    /// <summary>
    /// Parses SQL strings into ScriptDom AST fragments.
    /// Wraps <see cref="TSql160Parser"/> with error formatting and convenience methods.
    /// </summary>
    public sealed class QueryParser
    {
        private readonly TSql160Parser _parser = new(initialQuotedIdentifiers: true);

        /// <summary>
        /// Parses a SQL string into a <see cref="TSqlFragment"/> AST.
        /// </summary>
        /// <param name="sql">The SQL text to parse.</param>
        /// <returns>The parsed AST fragment.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="sql"/> is null.</exception>
        /// <exception cref="QueryParseException">The SQL text contains syntax errors.</exception>
        public TSqlFragment Parse(string sql)
        {
            if (sql is null)
                throw new ArgumentNullException(nameof(sql));

            var fragment = _parser.Parse(new StringReader(sql), out IList<ParseError> errors);

            if (errors.Count > 0)
                throw new QueryParseException(errors);

            return fragment;
        }

        /// <summary>
        /// Attempts to parse a SQL string without throwing on errors.
        /// </summary>
        /// <param name="sql">The SQL text to parse.</param>
        /// <param name="fragment">The parsed AST fragment, or null if parsing failed.</param>
        /// <param name="errors">The parse errors, empty on success.</param>
        /// <returns>True if parsing succeeded with no errors; otherwise false.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="sql"/> is null.</exception>
        public bool TryParse(string sql, out TSqlFragment? fragment, out IList<ParseError> errors)
        {
            if (sql is null)
                throw new ArgumentNullException(nameof(sql));

            fragment = _parser.Parse(new StringReader(sql), out errors);

            if (errors.Count > 0)
            {
                fragment = null;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Parses a SQL string and returns the first statement from the batch.
        /// </summary>
        /// <param name="sql">The SQL text containing a single statement.</param>
        /// <returns>The first <see cref="TSqlStatement"/> in the parsed batch.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="sql"/> is null.</exception>
        /// <exception cref="QueryParseException">The SQL text contains syntax errors or no statements.</exception>
        public TSqlStatement ParseStatement(string sql)
        {
            var fragment = Parse(sql);

            var statements = GetStatements(fragment);

            if (statements.Count == 0)
                throw new QueryParseException("SQL text does not contain any statements.");

            return statements[0];
        }

        /// <summary>
        /// Parses a SQL string and returns all statements from the batch.
        /// </summary>
        /// <param name="sql">The SQL text containing one or more statements.</param>
        /// <returns>A read-only list of all statements in the batch.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="sql"/> is null.</exception>
        /// <exception cref="QueryParseException">The SQL text contains syntax errors.</exception>
        public IReadOnlyList<TSqlStatement> ParseBatch(string sql)
        {
            var fragment = Parse(sql);

            return GetStatements(fragment);
        }

        /// <summary>
        /// Gets the type of the first statement in a SQL string.
        /// </summary>
        /// <param name="sql">The SQL text to inspect.</param>
        /// <returns>The <see cref="Type"/> of the first statement.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="sql"/> is null.</exception>
        /// <exception cref="QueryParseException">The SQL text contains syntax errors or no statements.</exception>
        public Type GetStatementType(string sql)
        {
            return ParseStatement(sql).GetType();
        }

        private static IReadOnlyList<TSqlStatement> GetStatements(TSqlFragment fragment)
        {
            if (fragment is TSqlScript script)
            {
                return script.Batches
                    .SelectMany(b => b.Statements)
                    .ToList()
                    .AsReadOnly();
            }

            if (fragment is TSqlStatement statement)
                return new[] { statement };

            return Array.Empty<TSqlStatement>();
        }
    }
}
