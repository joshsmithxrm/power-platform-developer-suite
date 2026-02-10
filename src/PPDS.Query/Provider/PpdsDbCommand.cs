using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text;
using System.Threading;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Sql.Transpilation;
using PPDS.Query.Parsing;
using PPDS.Query.Planning;

namespace PPDS.Query.Provider;

/// <summary>
/// ADO.NET command that executes SQL against Dataverse via the PPDS query engine.
/// Parses SQL with <see cref="QueryParser"/>, builds an execution plan with
/// <see cref="ExecutionPlanBuilder"/>, and executes it with <see cref="PlanExecutor"/>.
/// </summary>
public sealed class PpdsDbCommand : DbCommand
{
    private string _commandText = string.Empty;
    private PpdsDbConnection? _connection;
    private readonly PpdsDbParameterCollection _parameters = new();
    private int _commandTimeout = 30;

    /// <summary>
    /// Initializes a new instance of the <see cref="PpdsDbCommand"/> class.
    /// </summary>
    public PpdsDbCommand()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PpdsDbCommand"/> class
    /// with the specified command text and connection.
    /// </summary>
    /// <param name="commandText">The SQL command text.</param>
    /// <param name="connection">The connection to use.</param>
    public PpdsDbCommand(string commandText, PpdsDbConnection connection)
    {
        _commandText = commandText ?? string.Empty;
        _connection = connection;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  DbCommand property overrides
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public override string CommandText
    {
        get => _commandText;
#pragma warning disable CS8765 // Nullability of parameter doesn't match overridden member
        set => _commandText = value ?? string.Empty;
#pragma warning restore CS8765
    }

    /// <inheritdoc />
    public override int CommandTimeout
    {
        get => _commandTimeout;
        set => _commandTimeout = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <inheritdoc />
    public override CommandType CommandType { get; set; } = CommandType.Text;

    /// <inheritdoc />
    public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.None;

    /// <inheritdoc />
    protected override DbConnection? DbConnection
    {
        get => _connection;
        set
        {
            if (value != null && value is not PpdsDbConnection)
                throw new ArgumentException($"Connection must be a {nameof(PpdsDbConnection)}.", nameof(value));
            _connection = (PpdsDbConnection?)value;
        }
    }

    /// <inheritdoc />
    protected override DbTransaction? DbTransaction { get; set; }

    /// <inheritdoc />
    public override bool DesignTimeVisible { get; set; }

    /// <inheritdoc />
    protected override DbParameterCollection DbParameterCollection => _parameters;

    /// <summary>
    /// Gets the strongly-typed parameter collection.
    /// </summary>
    public new PpdsDbParameterCollection Parameters => _parameters;

    /// <summary>
    /// Gets or sets the <see cref="PpdsDbConnection"/> used by this command.
    /// </summary>
    public new PpdsDbConnection? Connection
    {
        get => _connection;
        set => _connection = value;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Execution
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        EnsureConnectionOpen();

        var sql = ApplyParameters(_commandText);
        var parser = new QueryParser();
        var fragment = parser.Parse(sql);

        // Build execution plan
        var fetchXmlGenerator = new NullFetchXmlGeneratorService();
        var planBuilder = new ExecutionPlanBuilder(fetchXmlGenerator);
        var options = BuildPlanOptions(sql);

        var planResult = planBuilder.Plan(fragment, options);

        // Execute the plan if we have a query executor
        var executor = _connection!.QueryExecutor;
        if (executor != null)
        {
            var context = new QueryPlanContext(
                executor,
                CancellationToken.None);

            var planExecutor = new PlanExecutor();
            var result = planExecutor.ExecuteAsync(planResult, context).GetAwaiter().GetResult();
            return PpdsDataReader.FromQueryResult(result);
        }

        // No executor: return empty reader with column metadata from plan
        var emptyResult = QueryResult.Empty(planResult.EntityLogicalName);
        return PpdsDataReader.FromQueryResult(emptyResult);
    }

    /// <inheritdoc />
    public override int ExecuteNonQuery()
    {
        EnsureConnectionOpen();

        var sql = ApplyParameters(_commandText);
        var parser = new QueryParser();
        var fragment = parser.Parse(sql);

        var fetchXmlGenerator = new NullFetchXmlGeneratorService();
        var planBuilder = new ExecutionPlanBuilder(fetchXmlGenerator);
        var options = BuildPlanOptions(sql);

        var planResult = planBuilder.Plan(fragment, options);

        // Execute if we have an executor
        var executor = _connection!.QueryExecutor;
        if (executor != null)
        {
            var context = new QueryPlanContext(
                executor,
                CancellationToken.None);

            var planExecutor = new PlanExecutor();
            var result = planExecutor.ExecuteAsync(planResult, context).GetAwaiter().GetResult();
            return result.Count;
        }

        return 0;
    }

    /// <inheritdoc />
    public override object? ExecuteScalar()
    {
        using var reader = ExecuteReader();
        if (reader.Read() && reader.FieldCount > 0)
        {
            var value = reader.GetValue(0);
            return value == DBNull.Value ? null : value;
        }
        return null;
    }

    /// <inheritdoc />
    public override void Prepare()
    {
        // Parse validation only -- ensure the SQL is syntactically valid
        if (!string.IsNullOrWhiteSpace(_commandText))
        {
            var sql = ApplyParameters(_commandText);
            var parser = new QueryParser();
            parser.Parse(sql); // Throws QueryParseException on syntax errors
        }
    }

    /// <inheritdoc />
    public override void Cancel()
    {
        // No-op: cancellation is managed via CancellationToken in async paths
    }

    /// <inheritdoc />
    protected override DbParameter CreateDbParameter()
    {
        return new PpdsDbParameter();
    }

    /// <summary>
    /// Creates a new <see cref="PpdsDbParameter"/>.
    /// </summary>
    /// <returns>A new parameter instance.</returns>
    public new PpdsDbParameter CreateParameter()
    {
        return (PpdsDbParameter)CreateDbParameter();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Internal helpers
    // ═══════════════════════════════════════════════════════════════════

    private void EnsureConnectionOpen()
    {
        if (_connection is null)
            throw new InvalidOperationException("Connection is not set.");

        if (_connection.State != ConnectionState.Open)
            throw new InvalidOperationException("Connection is not open. Call Open() first.");

        if (string.IsNullOrWhiteSpace(_commandText))
            throw new InvalidOperationException("CommandText is not set.");
    }

    private string ApplyParameters(string sql)
    {
        if (_parameters.Count == 0)
            return sql;

        var parameterLiterals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var param in _parameters.InternalList)
        {
            var normalizedName = NormalizeParameterName(param.ParameterName);
            if (normalizedName.Length == 0)
                continue;

            parameterLiterals[normalizedName] = param.ToSqlLiteral();
        }

        if (parameterLiterals.Count == 0)
        {
            return sql;
        }

        var result = new StringBuilder(sql.Length);
        var i = 0;

        while (i < sql.Length)
        {
            var ch = sql[i];

            // Single-quoted string literal
            if (ch == '\'')
            {
                result.Append(ch);
                i++;
                while (i < sql.Length)
                {
                    var c = sql[i];
                    result.Append(c);
                    i++;

                    if (c == '\'')
                    {
                        // Escaped quote ''
                        if (i < sql.Length && sql[i] == '\'')
                        {
                            result.Append(sql[i]);
                            i++;
                            continue;
                        }

                        break;
                    }
                }

                continue;
            }

            // Bracketed identifier
            if (ch == '[')
            {
                result.Append(ch);
                i++;
                while (i < sql.Length)
                {
                    var c = sql[i];
                    result.Append(c);
                    i++;

                    if (c == ']')
                    {
                        // Escaped bracket ]]
                        if (i < sql.Length && sql[i] == ']')
                        {
                            result.Append(sql[i]);
                            i++;
                            continue;
                        }

                        break;
                    }
                }

                continue;
            }

            // Double-quoted identifier/literal
            if (ch == '"')
            {
                result.Append(ch);
                i++;
                while (i < sql.Length)
                {
                    var c = sql[i];
                    result.Append(c);
                    i++;

                    if (c == '"')
                    {
                        // Escaped quote ""
                        if (i < sql.Length && sql[i] == '"')
                        {
                            result.Append(sql[i]);
                            i++;
                            continue;
                        }

                        break;
                    }
                }

                continue;
            }

            // Line comment
            if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                result.Append("--");
                i += 2;
                while (i < sql.Length)
                {
                    var c = sql[i];
                    result.Append(c);
                    i++;
                    if (c == '\r' || c == '\n')
                    {
                        break;
                    }
                }

                continue;
            }

            // Block comment
            if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                result.Append("/*");
                i += 2;
                while (i < sql.Length)
                {
                    var c = sql[i];
                    result.Append(c);
                    i++;

                    if (c == '*' && i < sql.Length && sql[i] == '/')
                    {
                        result.Append('/');
                        i++;
                        break;
                    }
                }

                continue;
            }

            // Parameter token
            if (ch == '@')
            {
                // Keep system variables (e.g. @@ROWCOUNT) untouched.
                if (i + 1 < sql.Length && sql[i + 1] == '@')
                {
                    result.Append("@@");
                    i += 2;
                    continue;
                }

                var tokenStart = i;
                var scan = i + 1;

                if (scan < sql.Length && IsParameterNameStart(sql[scan]))
                {
                    scan++;
                    while (scan < sql.Length && IsParameterNamePart(sql[scan]))
                    {
                        scan++;
                    }

                    var token = sql[tokenStart..scan];
                    if (parameterLiterals.TryGetValue(token, out var literal))
                    {
                        result.Append(literal);
                    }
                    else
                    {
                        result.Append(token);
                    }

                    i = scan;
                    continue;
                }
            }

            result.Append(ch);
            i++;
        }

        return result.ToString();
    }

    private static string NormalizeParameterName(string parameterName)
    {
        if (string.IsNullOrWhiteSpace(parameterName))
            return string.Empty;

        return parameterName.StartsWith("@", StringComparison.Ordinal)
            ? parameterName
            : "@" + parameterName;
    }

    private static bool IsParameterNameStart(char ch)
    {
        return char.IsLetter(ch) || ch == '_' || ch == '#';
    }

    private static bool IsParameterNamePart(char ch)
    {
        return char.IsLetterOrDigit(ch) || ch is '_' or '#' or '$';
    }

    private QueryPlanOptions BuildPlanOptions(string originalSql)
    {
        var options = new QueryPlanOptions
        {
            OriginalSql = originalSql,
            UseTdsEndpoint = _connection?.UseTdsEndpoint ?? false,
            PoolCapacity = _connection?.MaxDegreeOfParallelism ?? 5
        };
        return options;
    }

    /// <summary>
    /// A no-op FetchXML generator for cases where we only need to parse and build plans
    /// without actually generating FetchXML (used when no executor is available).
    /// </summary>
    private sealed class NullFetchXmlGeneratorService : IFetchXmlGeneratorService
    {
        public TranspileResult Generate(TSqlFragment statement)
        {
            return new TranspileResult
            {
                FetchXml = "<fetch><entity name='unknown'/></fetch>",
                VirtualColumns = new Dictionary<string, VirtualColumnInfo>()
            };
        }
    }

}
