using System.Text;
using Microsoft.Data.SqlClient;

namespace PPDS.Query.DifferentialTests.Harness;

/// <summary>
/// Runs a T-SQL batch on the reference server and renders every result set, for the reference observations that are
/// not a single scalar expression (a session setting, a variable, a table variable). Values are rendered with
/// <see cref="Outcome.Render"/>; result sets are separated by <c> || </c>, rows by <c> | </c>, columns by <c>, </c>.
/// An error ends the rendering with <c>error Class (number)</c> after whatever came back before it.
/// </summary>
public static class ReferenceBatch
{
    /// <summary>
    /// Runs <paramref name="batch"/> and renders its results. The connection's reference settings are restored
    /// afterwards, so a batch that changes a session setting cannot change a later evaluation.
    /// </summary>
    public static string Run(SqlConnection connection, string batch)
    {
        var text = new StringBuilder();
        try
        {
            using var command = new SqlCommand(batch, connection);
            using var reader = command.ExecuteReader();
            var first = true;
            do
            {
                if (reader.FieldCount == 0)
                    continue;
                if (!first)
                    text.Append(" || ");
                first = false;
                var rows = new List<string>();
                while (reader.Read())
                {
                    var columns = new List<string>();
                    for (var i = 0; i < reader.FieldCount; i++)
                        columns.Add($"{reader.GetName(i)}={Outcome.Render(reader.GetSqlValue(i))}");
                    rows.Add(string.Join(", ", columns));
                }
                text.Append(rows.Count == 0 ? "(no rows)" : string.Join(" | ", rows));
            }
            while (reader.NextResult());
        }
        catch (SqlException ex)
        {
            var outcome = SqlServerEngine.Classify(batch, ex);
            if (text.Length > 0)
                text.Append(" || ");
            text.Append(outcome);
        }
        finally
        {
            ReferenceSettings.Apply(connection);
        }
        return text.ToString();
    }
}
