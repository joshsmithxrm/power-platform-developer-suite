using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
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
        /// The <see cref="ParseError.Number"/> of the error that refuses a text the parser did not read to its end
        /// (<see cref="FindTextNotRead"/>). ScriptDom's own error numbers are five-digit numbers from 46000; this one is
        /// outside them.
        /// </summary>
        internal const int TextNotReadErrorNumber = 90001;

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

            using var reader = new StringReader(sql);
            var fragment = _parser.Parse(reader, out IList<ParseError> errors);

            if (errors.Count > 0)
                throw new QueryParseException(errors, sql);

            if (FindTextNotRead(sql, fragment) is { } notRead)
                throw new QueryParseException(new[] { notRead }, sql);

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

            using var reader = new StringReader(sql);
            fragment = _parser.Parse(reader, out errors);

            if (errors.Count > 0)
            {
                fragment = null;
                return false;
            }

            if (FindTextNotRead(sql, fragment) is { } notRead)
            {
                errors = new List<ParseError> { notRead };
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

        /// <summary>
        /// Refuses a text that the parser did not read to its end (#1955). ScriptDom's tokenizer takes U+FFFF outside a
        /// string literal as the end of the input and parses what came before it without an error, so
        /// <c>DELETE FROM account WHERE name = 'a' &lt;U+FFFF&gt; AND name = 'b'</c> parsed as a DELETE on <c>name = 'a'</c>
        /// alone, and a later statement, or the rest of a statement after a line comment, was dropped the same way.
        /// </summary>
        /// <remarks>
        /// Decided by the property, not by the character: the parse result's token stream (every token, white space and
        /// comments included, then the end-of-file token) must cover the text exactly. Each token must start where the
        /// previous one ended, starting at 0; its text must be the text at that offset, character for character; and the
        /// end-of-file token must come at the text's length. A tokenizer that stops early leaves the end-of-file token
        /// before the end; one that skipped or replaced a character would leave a gap or a token text that differs from
        /// the input. Either way the result does not show the whole text, and it is refused. It cannot be fooled by any
        /// character, because it compares the tokens with the input itself, not with a list of characters.
        /// </remarks>
        /// <returns>The error to report, or null when the whole text was read.</returns>
        internal static ParseError? FindTextNotRead(string sql, TSqlFragment? fragment)
        {
            var read = 0;
            var tokens = fragment?.ScriptTokenStream;
            if (tokens is not null)
            {
                foreach (var token in tokens)
                {
                    if (token.TokenType == TSqlTokenType.EndOfFile)
                        return token.Offset == read && read == sql.Length ? null : NotRead(sql, read);

                    var text = token.Text ?? string.Empty;
                    if (token.Offset != read || read + text.Length > sql.Length ||
                        !sql.AsSpan(read, text.Length).SequenceEqual(text.AsSpan()))
                        return NotRead(sql, read);

                    read += text.Length;
                }
            }

            // No end-of-file token: the result does not show where reading ended.
            return NotRead(sql, read);
        }

        private static ParseError NotRead(string sql, int index)
        {
            // Lines and columns are counted in characters, a surrogate pair being one character; a line ends at a line
            // feed, a carriage return, or both together.
            var line = 1;
            var column = 1;
            var character = 1;
            for (var i = 0; i < index && i < sql.Length; i++)
            {
                if (char.IsHighSurrogate(sql[i]) && i + 1 < sql.Length && char.IsLowSurrogate(sql[i + 1]))
                    i++;

                character++;
                if (sql[i] == '\n' || (sql[i] == '\r' && (i + 1 >= sql.Length || sql[i + 1] != '\n')))
                {
                    line++;
                    column = 1;
                }
                else if (sql[i] != '\r')
                {
                    column++;
                }
            }

            string message;
            if (index < sql.Length)
            {
                var codePoint = char.IsHighSurrogate(sql[index]) && index + 1 < sql.Length && char.IsLowSurrogate(sql[index + 1])
                    ? char.ConvertToUtf32(sql[index], sql[index + 1])
                    : sql[index];
                message = string.Format(
                    CultureInfo.InvariantCulture,
                    "the SQL parser does not read U+{0:X4} at character {1} or the text after it, which would be dropped without an error. Remove that character.",
                    codePoint, character);
            }
            else
            {
                message = "the SQL parser's result does not show that it read the text to its end, so part of it could be dropped without an error.";
            }

            return new ParseError(TextNotReadErrorNumber, Math.Min(index, sql.Length), line, column, message);
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
