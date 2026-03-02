using System;
using System.Data;
using System.Data.Common;
using PPDS.Dataverse.Query;

namespace PPDS.Query.Provider;

/// <summary>
/// ADO.NET connection to a Dataverse environment via the PPDS query engine.
/// Parses connection strings and manages connection state. The actual Dataverse
/// connection is lazy -- Open() validates the connection string and transitions
/// to <see cref="ConnectionState.Open"/>, but does not create a ServiceClient.
/// </summary>
public sealed class PpdsDbConnection : DbConnection
{
    private ConnectionState _state = ConnectionState.Closed;
    private string _connectionString = string.Empty;
    private PpdsConnectionStringBuilder? _builder;

    // ═══════════════════════════════════════════════════════════════════
    //  Provider-specific properties
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The number of records per FetchXML page. Default is 100.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Maximum degree of parallelism for partitioned queries. Default is 5.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = 5;

    /// <summary>
    /// Whether to route compatible queries through the TDS Endpoint. Default is false.
    /// </summary>
    public bool UseTdsEndpoint { get; set; }

    /// <summary>
    /// When true, UPDATE without a WHERE clause is blocked. Default is true.
    /// </summary>
    public bool BlockUpdateWithoutWhere { get; set; } = true;

    /// <summary>
    /// When true, DELETE without a WHERE clause is blocked. Default is true.
    /// </summary>
    public bool BlockDeleteWithoutWhere { get; set; } = true;

    /// <summary>
    /// The query executor to use for executing plans. Can be injected for testing
    /// or set after Open() when a real Dataverse connection is available.
    /// </summary>
    public IQueryExecutor? QueryExecutor { get; set; }

    // ═══════════════════════════════════════════════════════════════════
    //  Events
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Raised before an INSERT statement is executed. Set <see cref="DmlConfirmationEventArgs.Cancel"/>
    /// to true to abort the operation.
    /// </summary>
    public event EventHandler<DmlConfirmationEventArgs>? PreInsert;

    /// <summary>
    /// Raised before an UPDATE statement is executed. Set <see cref="DmlConfirmationEventArgs.Cancel"/>
    /// to true to abort the operation.
    /// </summary>
    public event EventHandler<DmlConfirmationEventArgs>? PreUpdate;

    /// <summary>
    /// Raised before a DELETE statement is executed. Set <see cref="DmlConfirmationEventArgs.Cancel"/>
    /// to true to abort the operation.
    /// </summary>
    public event EventHandler<DmlConfirmationEventArgs>? PreDelete;

    /// <summary>
    /// Raised to report progress during long-running query execution.
    /// </summary>
    public event EventHandler<ProgressEventArgs>? Progress;

    // ═══════════════════════════════════════════════════════════════════
    //  DbConnection overrides
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public override string ConnectionString
    {
        get => _connectionString;
#pragma warning disable CS8765 // Nullability of parameter doesn't match overridden member
        set
#pragma warning restore CS8765
        {
            if (_state != ConnectionState.Closed)
                throw new InvalidOperationException("Cannot change ConnectionString while the connection is open.");

            _connectionString = value ?? string.Empty;
            _builder = null; // Invalidate cached builder
        }
    }

    /// <inheritdoc />
    public override string Database => string.Empty;

    /// <inheritdoc />
    public override string DataSource
    {
        get
        {
            EnsureBuilder();
            return _builder!.Url;
        }
    }

    /// <inheritdoc />
    public override string ServerVersion => "9.2";

    /// <inheritdoc />
    public override ConnectionState State => _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="PpdsDbConnection"/> class.
    /// </summary>
    public PpdsDbConnection()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PpdsDbConnection"/> class
    /// with the specified connection string.
    /// </summary>
    /// <param name="connectionString">The connection string.</param>
    public PpdsDbConnection(string connectionString)
    {
        ConnectionString = connectionString;
    }

    /// <inheritdoc />
    public override void Open()
    {
        if (_state == ConnectionState.Open)
            return;

        EnsureBuilder();

        if (string.IsNullOrWhiteSpace(_builder!.Url))
            throw new InvalidOperationException("Connection string must contain a Url property.");

        _state = ConnectionState.Open;
    }

    /// <inheritdoc />
    public override void Close()
    {
        _state = ConnectionState.Closed;
    }

    /// <inheritdoc />
    public override void ChangeDatabase(string databaseName)
    {
        throw new NotSupportedException("ChangeDatabase is not supported by the PPDS provider. Use a new connection string with a different Url.");
    }

    /// <inheritdoc />
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        throw new NotSupportedException("Transactions are not supported by the PPDS provider. Dataverse operations are atomic per-record.");
    }

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand()
    {
        return new PpdsDbCommand { Connection = this };
    }

    /// <summary>
    /// Creates a new <see cref="PpdsDbCommand"/> associated with this connection.
    /// </summary>
    /// <returns>A new command instance.</returns>
    public new PpdsDbCommand CreateCommand()
    {
        return (PpdsDbCommand)CreateDbCommand();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Internal helpers for command execution
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Raises the <see cref="PreInsert"/> event and returns whether the operation was cancelled.
    /// </summary>
    internal bool RaisePreInsert(string commandText, string entityName, int estimatedRows)
    {
        var args = new DmlConfirmationEventArgs(commandText, entityName, estimatedRows);
        PreInsert?.Invoke(this, args);
        return args.Cancel;
    }

    /// <summary>
    /// Raises the <see cref="PreUpdate"/> event and returns whether the operation was cancelled.
    /// </summary>
    internal bool RaisePreUpdate(string commandText, string entityName, int estimatedRows)
    {
        var args = new DmlConfirmationEventArgs(commandText, entityName, estimatedRows);
        PreUpdate?.Invoke(this, args);
        return args.Cancel;
    }

    /// <summary>
    /// Raises the <see cref="PreDelete"/> event and returns whether the operation was cancelled.
    /// </summary>
    internal bool RaisePreDelete(string commandText, string entityName, int estimatedRows)
    {
        var args = new DmlConfirmationEventArgs(commandText, entityName, estimatedRows);
        PreDelete?.Invoke(this, args);
        return args.Cancel;
    }

    /// <summary>
    /// Raises the <see cref="Progress"/> event.
    /// </summary>
    internal void RaiseProgress(string message, long rowsProcessed, long totalRows = -1)
    {
        Progress?.Invoke(this, new ProgressEventArgs(message, rowsProcessed, totalRows));
    }

    /// <summary>
    /// Gets the parsed connection string builder. Validates and caches on first access.
    /// </summary>
    internal PpdsConnectionStringBuilder GetBuilder()
    {
        EnsureBuilder();
        return _builder!;
    }

    private void EnsureBuilder()
    {
        _builder ??= new PpdsConnectionStringBuilder(_connectionString);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
        }
        base.Dispose(disposing);
    }
}
