using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Query.DifferentialTests.Harness;

/// <summary>
/// Where the reference SQL Server is. Read from the environment, never logged: no message of the harness contains the
/// host name or the password.
/// </summary>
public static class ReferenceServer
{
    /// <summary>The server: a host name, <c>host,port</c>, or <c>(localdb)\MSSQLLocalDB</c>.</summary>
    public const string HostVariable = "PPDS_REFSQL_HOST";

    /// <summary>The password of a SQL login. When unset, the harness signs in with integrated security (LocalDB).</summary>
    public const string PasswordVariable = "PPDS_REFSQL_PASSWORD";

    /// <summary>The SQL login; <c>sa</c> when unset.</summary>
    public const string UserVariable = "PPDS_REFSQL_USER";

    /// <summary>The reason the server tests skip, or null when a server is configured.</summary>
    public static string? SkipReason => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HostVariable))
        ? $"No reference SQL Server: set {HostVariable} (and {PasswordVariable} for a SQL login) to run the differential tests. See tests/PPDS.Query.DifferentialTests/README.md."
        : null;

    /// <summary>A connection string to <paramref name="database"/> on the configured server.</summary>
    public static string ConnectionString(string database)
    {
        var host = Environment.GetEnvironmentVariable(HostVariable)
                   ?? throw new InvalidOperationException($"{HostVariable} is not set.");
        var password = Environment.GetEnvironmentVariable(PasswordVariable);
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = host,
            InitialCatalog = database,
            Encrypt = true,
            // A local or CI server has a self-signed certificate. The server holds only what this harness creates.
            TrustServerCertificate = true,
            ConnectTimeout = 30,
            ApplicationName = "PPDS differential harness",
            Pooling = false,
        };
        if (string.IsNullOrEmpty(password))
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = Environment.GetEnvironmentVariable(UserVariable) is { Length: > 0 } user ? user : "sa";
            builder.Password = password;
        }
        return builder.ConnectionString;
    }

    /// <summary>Removes the host name from a message, so that a connection error can be reported.</summary>
    public static string Redact(string message)
    {
        var host = Environment.GetEnvironmentVariable(HostVariable);
        var password = Environment.GetEnvironmentVariable(PasswordVariable);
        if (!string.IsNullOrEmpty(host))
            message = message.Replace(host, "<reference server>", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(password))
            message = message.Replace(password, "<password>", StringComparison.Ordinal);
        return message;
    }
}

/// <summary>A fact that needs the reference SQL Server; skipped, with the reason, when none is configured.</summary>
public sealed class ReferenceServerFactAttribute : FactAttribute
{
    /// <summary>Sets <see cref="FactAttribute.Skip"/> when no server is configured.</summary>
    public ReferenceServerFactAttribute() => Skip = ReferenceServer.SkipReason;
}

/// <summary>A theory that needs the reference SQL Server; skipped, with the reason, when none is configured.</summary>
public sealed class ReferenceServerTheoryAttribute : TheoryAttribute
{
    /// <summary>Sets <see cref="FactAttribute.Skip"/> when no server is configured.</summary>
    public ReferenceServerTheoryAttribute() => Skip = ReferenceServer.SkipReason;
}

/// <summary>
/// The harness's own database on the reference server, created for one test run and dropped at its end. Its name is
/// <c>ppds_sqldiff_</c> followed by the UTC time and a random suffix. It changes no other database and drops only the
/// database it created in this run, by the name it created it with. The one object it creates outside that database is
/// the temporary table <c>#t</c> that reference observation R04e creates in tempdb, reads back from tempdb's catalog
/// and drops in the same batch.
/// </summary>
public sealed partial class ReferenceDatabase : IDisposable
{
    /// <summary>The prefix of every database the harness creates.</summary>
    public const string NamePrefix = "ppds_sqldiff_";

    /// <summary>
    /// The collation the harness database is created with, so that text results do not depend on the server's default.
    /// It is the reference server's collation and, by #1799's probe, to be aligned with the Dataverse endpoint's.
    /// </summary>
    public const string Collation = "SQL_Latin1_General_CP1_CI_AS";

    private SqlConnection? _connection;

    /// <summary>Creates the database and the operand table when a server is configured; otherwise does nothing.</summary>
    public ReferenceDatabase()
    {
        if (ReferenceServer.SkipReason is not null)
            return;

        Name = NamePrefix + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "_"
               + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        try
        {
            using (var master = Open("master"))
            {
                Execute(master, $"CREATE DATABASE [{Name}] COLLATE {Collation};");
                Created = true;
                using var version = new SqlCommand("SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)) + N' ' + CAST(SERVERPROPERTY('Edition') AS nvarchar(128)), CAST(DATABASEPROPERTYEX(@db, 'Collation') AS nvarchar(128))", master);
                version.Parameters.AddWithValue("@db", Name);
                using (var reader = version.ExecuteReader())
                {
                    reader.Read();
                    var collation = reader.GetString(1);
                    if (collation != Collation)
                        throw new HarnessException($"The harness database has collation {collation}, not {Collation}.");
                    ServerDescription = reader.GetString(0) + ", collation " + collation;
                }
            }

            _connection = Open(Name);
            ReferenceSettings.Apply(_connection);
            Execute(_connection, Operands.CreateTableSql());
            Execute(_connection, Operands.InsertRowsSql());
            RowValues = ReadRows(_connection);
        }
        catch (Exception ex)
        {
            // Every failure here, including one while cleaning up, is reported without the host name or the password.
            var message = "Could not set up the reference database: " + ReferenceServer.Redact(ex.Message);
            try
            {
                Dispose();
            }
            catch (Exception cleanup)
            {
                message += " Cleaning up also failed: " + ReferenceServer.Redact(cleanup.Message);
            }
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>The name of the database this run created; null when no server is configured.</summary>
    public string? Name { get; }

    /// <summary>True once the database exists.</summary>
    public bool Created { get; private set; }

    /// <summary>Server version, edition and database collation, for the report.</summary>
    public string ServerDescription { get; } = "";

    /// <summary>The open connection to the harness database, with the reference settings applied.</summary>
    public SqlConnection Connection => _connection ?? throw new InvalidOperationException(ReferenceServer.SkipReason ?? "The reference database is not open.");

    /// <summary>The stored operand rows as the engine under test receives them, by row id.</summary>
    public IReadOnlyDictionary<int, IReadOnlyDictionary<string, QueryValue>> RowValues { get; } =
        new Dictionary<int, IReadOnlyDictionary<string, QueryValue>>();

    /// <summary>Drops the database this run created, and nothing else.</summary>
    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
        if (!Created || Name is null)
            return;
        if (!OwnName().IsMatch(Name))
            throw new InvalidOperationException("Refusing to drop a database whose name the harness did not generate.");

        try
        {
            using var master = Open("master");
            Execute(master, $"IF DB_ID(N'{Name}') IS NOT NULL BEGIN ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}]; END;");
            Created = false;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not drop the harness database {Name}: " + ReferenceServer.Redact(ex.Message));
        }
    }

    [GeneratedRegex("^ppds_sqldiff_[0-9]{14}_[0-9a-f]{8}$")]
    private static partial Regex OwnName();

    private static SqlConnection Open(string database)
    {
        var connection = new SqlConnection(ReferenceServer.ConnectionString(database));
        connection.Open();
        return connection;
    }

    private static void Execute(SqlConnection connection, string sql)
    {
        using var command = new SqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    private static Dictionary<int, IReadOnlyDictionary<string, QueryValue>> ReadRows(SqlConnection connection)
    {
        var rows = new Dictionary<int, IReadOnlyDictionary<string, QueryValue>>();
        using var command = new SqlCommand("SELECT row_id, " + string.Join(", ", Operands.Columns.Select(c => c.Name)) + " FROM dbo.Operands;", connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var values = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < Operands.Columns.Count; i++)
            {
                var value = reader.GetValue(i + 1);
                values[Operands.Columns[i].Name] = QueryValue.Simple(value is DBNull ? null : value);
            }
            rows[reader.GetInt32(0)] = values;
        }
        return rows;
    }
}

/// <summary>
/// The session settings every reference evaluation runs under. They are SQL Server's defaults for a modern client,
/// stated so that a different client or server default cannot change a result.
/// </summary>
public static class ReferenceSettings
{
    /// <summary>The settings, as one batch.</summary>
    public const string Batch =
        "SET ANSI_NULLS ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON; SET ARITHABORT ON; "
        + "SET CONCAT_NULL_YIELDS_NULL ON; SET NUMERIC_ROUNDABORT OFF; SET QUOTED_IDENTIFIER ON; "
        + "SET LANGUAGE us_english; SET DATEFORMAT ymd;";

    private const string Check =
        "SELECT CAST(SESSIONPROPERTY('ANSI_NULLS') AS int), CAST(SESSIONPROPERTY('ANSI_PADDING') AS int), CAST(SESSIONPROPERTY('ANSI_WARNINGS') AS int), "
        + "CAST(SESSIONPROPERTY('ARITHABORT') AS int), CAST(SESSIONPROPERTY('CONCAT_NULL_YIELDS_NULL') AS int), CAST(SESSIONPROPERTY('NUMERIC_ROUNDABORT') AS int), "
        + "CAST(SESSIONPROPERTY('QUOTED_IDENTIFIER') AS int), CAST(@@LANGUAGE AS nvarchar(128)), "
        + "(SELECT CAST(date_format AS nvarchar(16)) FROM sys.dm_exec_sessions WHERE session_id = @@SPID), "
        + "(SELECT CAST(compatibility_level AS int) FROM sys.databases WHERE name = DB_NAME())";

    /// <summary>Applies the settings to an open connection and checks that they hold.</summary>
    public static void Apply(SqlConnection connection)
    {
        using (var command = new SqlCommand(Batch, connection))
            command.ExecuteNonQuery();
        Verify(connection);
    }

    /// <summary>Throws a <see cref="HarnessException"/> unless the session runs under the reference settings and compatibility level.</summary>
    public static void Verify(SqlConnection connection)
    {
        using var command = new SqlCommand(Check, connection);
        using var reader = command.ExecuteReader();
        reader.Read();
        var actual = $"ANSI_NULLS {reader.GetInt32(0)}, ANSI_PADDING {reader.GetInt32(1)}, ANSI_WARNINGS {reader.GetInt32(2)}, ARITHABORT {reader.GetInt32(3)}, "
                     + $"CONCAT_NULL_YIELDS_NULL {reader.GetInt32(4)}, NUMERIC_ROUNDABORT {reader.GetInt32(5)}, QUOTED_IDENTIFIER {reader.GetInt32(6)}, "
                     + $"language {reader.GetString(7)}, dateformat {reader.GetString(8)}, compatibility {reader.GetInt32(9)}";
        const string expected = "ANSI_NULLS 1, ANSI_PADDING 1, ANSI_WARNINGS 1, ARITHABORT 1, CONCAT_NULL_YIELDS_NULL 1, NUMERIC_ROUNDABORT 0, QUOTED_IDENTIFIER 1, "
                                + "language us_english, dateformat ymd, compatibility 160";
        if (actual != expected)
            throw new HarnessException($"The session does not run under the reference settings: [{actual}], expected [{expected}].");
    }
}

/// <summary>Shares one reference database across the server tests of a run.</summary>
[CollectionDefinition(Name)]
public sealed class ReferenceServerCollection : ICollectionFixture<ReferenceDatabase>
{
    /// <summary>The collection name.</summary>
    public const string Name = "Reference SQL Server";
}
