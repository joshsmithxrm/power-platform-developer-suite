using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace PPDS.Dataverse.Query;

/// <summary>
/// Executes SQL queries against Dataverse's TDS Endpoint using the SQL Server wire protocol.
/// The TDS Endpoint provides read-only access to Dataverse data on port 5558.
/// </summary>
public class TdsQueryExecutor : ITdsQueryExecutor
{
    private readonly string _orgUrl;
    private readonly Func<CancellationToken, Task<string>> _tokenProvider;
    private readonly ILogger<TdsQueryExecutor>? _logger;

    /// <summary>
    /// The port used by the Dataverse TDS Endpoint.
    /// </summary>
    /// <remarks>Dataverse TDS Endpoint always listens on port 5558.</remarks>
    public const int TdsPort = 5558;

    /// <summary>
    /// Initializes a new instance of the <see cref="TdsQueryExecutor"/> class.
    /// </summary>
    /// <param name="orgUrl">
    /// The Dataverse organization URL (e.g., "https://org.crm.dynamics.com").
    /// </param>
    /// <param name="tokenProvider">
    /// A function that provides a valid MSAL access token for the Dataverse environment.
    /// Called for each query execution to ensure fresh tokens.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public TdsQueryExecutor(
        string orgUrl,
        Func<CancellationToken, Task<string>> tokenProvider,
        ILogger<TdsQueryExecutor>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgUrl);
        _orgUrl = NormalizeOrgUrl(orgUrl);
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<QueryResult> ExecuteSqlAsync(
        string sql,
        int? maxRows = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var compatibility = TdsCompatibilityChecker.CheckCompatibility(sql);
        if (compatibility != TdsCompatibility.Compatible)
        {
            throw new InvalidOperationException(
                $"Query is not compatible with TDS Endpoint: {compatibility}. " +
                "Use FetchXML execution path instead.");
        }

        var stopwatch = Stopwatch.StartNew();

        _logger?.LogDebug("Executing TDS query against {OrgUrl}", _orgUrl);

        var token = await _tokenProvider(cancellationToken).ConfigureAwait(false);
        var connectionString = BuildConnectionString(_orgUrl);

        List<QueryColumn> columns;
        var records = new List<IReadOnlyDictionary<string, QueryValue>>();
        var rowCount = 0;

        await using var connection = new SqlConnection(connectionString);
        connection.AccessToken = token;

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandType = CommandType.Text;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        // Extract column metadata from the reader schema
        columns = ExtractColumnsFromReader(reader);

        // Read rows
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (maxRows.HasValue && rowCount >= maxRows.Value)
            {
                break;
            }

            var record = MapReaderRow(reader, columns);
            records.Add(record);
            rowCount++;
        }

        stopwatch.Stop();

        _logger?.LogInformation(
            "TDS query returned {Count} records in {ElapsedMs}ms",
            rowCount, stopwatch.ElapsedMilliseconds);

        // Infer entity name from the SQL (best effort)
        var entityName = InferEntityName(sql);

        return new QueryResult
        {
            EntityLogicalName = entityName ?? "tds_query",
            Columns = columns,
            Records = records,
            Count = records.Count,
            TotalCount = null,
            MoreRecords = false,
            PagingCookie = null,
            PageNumber = 1,
            ExecutionTimeMs = stopwatch.ElapsedMilliseconds,
            ExecutedFetchXml = null,
            IsAggregate = false
        };
    }

    /// <summary>
    /// Builds the SQL Server connection string for the TDS Endpoint.
    /// </summary>
    /// <param name="orgHost">The normalized org hostname.</param>
    /// <returns>The connection string.</returns>
    public static string BuildConnectionString(string orgHost)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"{orgHost},{TdsPort}",
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = false,
            ConnectTimeout = 30,
        };

        return builder.ConnectionString;
    }

    /// <summary>
    /// Extracts column metadata from the SqlDataReader schema.
    /// </summary>
    public static List<QueryColumn> ExtractColumnsFromReader(SqlDataReader reader)
    {
        var columns = new List<QueryColumn>();

        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            var fieldType = reader.GetFieldType(i);
            var dataType = MapClrTypeToColumnType(fieldType);

            columns.Add(new QueryColumn
            {
                LogicalName = name,
                DataType = dataType
            });
        }

        return columns;
    }

    /// <summary>
    /// Maps a single row from the SqlDataReader to a record dictionary.
    /// </summary>
    public static Dictionary<string, QueryValue> MapReaderRow(
        SqlDataReader reader,
        List<QueryColumn> columns)
    {
        var record = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
            record[column.LogicalName] = MapSqlValue(value);
        }

        return record;
    }

    /// <summary>
    /// Maps a raw SQL value to a <see cref="QueryValue"/>.
    /// Handles DBNull, common .NET types, and falls back to string representation.
    /// </summary>
    public static QueryValue MapSqlValue(object? value)
    {
        if (value == null || value == DBNull.Value)
        {
            return QueryValue.Null;
        }

        return value switch
        {
            string s => QueryValue.Simple(s),
            int i => QueryValue.Simple(i),
            long l => QueryValue.Simple(l),
            decimal d => QueryValue.Simple(d),
            float f => QueryValue.Simple((double)f),
            double dbl => QueryValue.Simple(dbl),
            DateTime dt => QueryValue.WithFormatting(dt, dt.ToString("yyyy-MM-dd HH:mm:ss")),
            DateTimeOffset dto => QueryValue.WithFormatting(dto.UtcDateTime, dto.ToString("yyyy-MM-dd HH:mm:ss")),
            Guid g => QueryValue.Simple(g),
            bool b => QueryValue.WithFormatting(b, b ? "Yes" : "No"),
            byte[] bytes => QueryValue.Simple($"[Binary: {bytes.Length} bytes]"),
            _ => QueryValue.Simple(value.ToString())
        };
    }

    /// <summary>
    /// Maps a CLR type from SqlDataReader to a <see cref="QueryColumnType"/>.
    /// </summary>
    public static QueryColumnType MapClrTypeToColumnType(Type? clrType)
    {
        if (clrType == null)
        {
            return QueryColumnType.Unknown;
        }

        if (clrType == typeof(string))
            return QueryColumnType.String;
        if (clrType == typeof(int))
            return QueryColumnType.Integer;
        if (clrType == typeof(long))
            return QueryColumnType.BigInt;
        if (clrType == typeof(decimal))
            return QueryColumnType.Decimal;
        if (clrType == typeof(float) || clrType == typeof(double))
            return QueryColumnType.Double;
        if (clrType == typeof(bool))
            return QueryColumnType.Boolean;
        if (clrType == typeof(DateTime) || clrType == typeof(DateTimeOffset))
            return QueryColumnType.DateTime;
        if (clrType == typeof(Guid))
            return QueryColumnType.Guid;
        if (clrType == typeof(byte[]))
            return QueryColumnType.Image;

        return QueryColumnType.Unknown;
    }

    /// <summary>
    /// Normalizes an org URL to just the hostname portion.
    /// </summary>
    private static string NormalizeOrgUrl(string orgUrl)
    {
        // Remove protocol prefix if present
        if (Uri.TryCreate(orgUrl, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        // Already a hostname — strip trailing slash
        return orgUrl.TrimEnd('/');
    }

    /// <summary>
    /// Attempts to infer the entity logical name from a SQL statement.
    /// Looks for the FROM clause and extracts the table name.
    /// </summary>
    public static string? InferEntityName(string sql)
    {
        // Simple regex-free approach: find "FROM" keyword and take the next word
        var upper = sql.ToUpperInvariant();
        var fromIndex = upper.IndexOf("FROM", StringComparison.Ordinal);
        if (fromIndex < 0)
        {
            return null;
        }

        var afterFrom = sql.Substring(fromIndex + 4).TrimStart();
        if (afterFrom.Length == 0)
        {
            return null;
        }

        // Take characters until whitespace or special chars
        var endIndex = 0;
        while (endIndex < afterFrom.Length &&
               !char.IsWhiteSpace(afterFrom[endIndex]) &&
               afterFrom[endIndex] != '(' &&
               afterFrom[endIndex] != ')' &&
               afterFrom[endIndex] != ';')
        {
            endIndex++;
        }

        return endIndex > 0 ? afterFrom.Substring(0, endIndex).ToLowerInvariant() : null;
    }
}
