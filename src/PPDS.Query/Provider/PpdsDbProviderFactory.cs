using System.Data.Common;

namespace PPDS.Query.Provider;

/// <summary>
/// ADO.NET provider factory for creating PPDS query engine data access objects.
/// Register with <see cref="DbProviderFactories.RegisterFactory"/> to enable
/// discovery by tools like Entity Framework and Dapper.
/// </summary>
/// <example>
/// <code>
/// DbProviderFactories.RegisterFactory("PPDS.Query", PpdsDbProviderFactory.Instance);
/// var factory = DbProviderFactories.GetFactory("PPDS.Query");
/// using var conn = factory.CreateConnection();
/// conn.ConnectionString = "Url=https://org.crm.dynamics.com";
/// </code>
/// </example>
public sealed class PpdsDbProviderFactory : DbProviderFactory
{
    /// <summary>
    /// Singleton instance of the PPDS provider factory.
    /// </summary>
    public static readonly PpdsDbProviderFactory Instance = new();

    private PpdsDbProviderFactory() { }

    /// <inheritdoc />
    public override DbConnection CreateConnection() => new PpdsDbConnection();

    /// <inheritdoc />
    public override DbCommand CreateCommand() => new PpdsDbCommand();

    /// <inheritdoc />
    public override DbParameter CreateParameter() => new PpdsDbParameter();

    /// <inheritdoc />
    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new PpdsConnectionStringBuilder();

    /// <inheritdoc />
    public override bool CanCreateDataSourceEnumerator => false;
}
