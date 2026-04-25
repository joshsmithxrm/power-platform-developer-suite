using System.Text.RegularExpressions;
using System.Xml.Linq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Query.Parsing;
using PPDS.Query.Transpilation;

namespace PPDS.Cli.Services.Data;

/// <summary>
/// Transpiles SQL-like filter expressions to FetchXML filter XML for schema embedding.
/// </summary>
public static class FilterTranspiler
{
    private static readonly Regex EntityNamePattern = new(@"^[a-z_][a-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>
    /// Transpiles a SQL WHERE clause expression to a FetchXML filter element string.
    /// </summary>
    /// <param name="entityName">The entity logical name.</param>
    /// <param name="expression">The SQL WHERE clause expression (e.g., "statecode = 0").</param>
    /// <returns>The FetchXML filter XML string.</returns>
    /// <exception cref="PpdsException">Thrown when the expression cannot be parsed.</exception>
    public static string TranspileToFetchXmlFilter(string entityName, string expression)
    {
        if (!EntityNamePattern.IsMatch(entityName))
        {
            throw new PpdsException(
                ErrorCodes.Validation.InvalidValue,
                $"Invalid entity name \"{entityName}\". Entity names must match [a-z_][a-z0-9_]*.");
        }

        var sql = $"SELECT {entityName}id FROM {entityName} WHERE {expression}";

        string fetchXml;
        try
        {
            var parser = new QueryParser();
            var stmt = parser.ParseStatement(sql);
            var generator = new FetchXmlGenerator();
            fetchXml = generator.Generate(stmt);
        }
        catch (Exception ex)
        {
            throw new PpdsException(
                ErrorCodes.Query.ParseError,
                $"Failed to parse filter expression: {ex.Message}",
                ex);
        }

        var doc = XDocument.Parse(fetchXml);
        var entityElement = doc.Root?.Element("entity");
        var filterElements = entityElement?.Elements("filter").ToList();

        if (filterElements == null || filterElements.Count == 0)
        {
            throw new PpdsException(
                ErrorCodes.Query.ParseError,
                "Filter expression produced no FetchXML filter conditions.");
        }

        if (filterElements.Count == 1)
            return filterElements[0].ToString(SaveOptions.DisableFormatting);

        var combined = new XElement("filter", new XAttribute("type", "and"), filterElements);
        return combined.ToString(SaveOptions.DisableFormatting);
    }
}
