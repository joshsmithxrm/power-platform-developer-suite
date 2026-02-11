namespace PPDS.Dataverse.Query.Execution.Functions;

/// <summary>
/// T-SQL null-handling functions evaluated client-side.
/// </summary>
public static class NullFunctions
{
    /// <summary>
    /// Registers all null-handling functions into the given registry.
    /// </summary>
    public static void RegisterAll(FunctionRegistry registry)
    {
        registry.Register("ISNULL", new IsNullFunction());
    }

    /// <summary>
    /// ISNULL(check_expression, replacement_value) — returns replacement if check is NULL.
    /// </summary>
    private sealed class IsNullFunction : IScalarFunction
    {
        public int MinArgs => 2;
        public int MaxArgs => 2;

        public object? Execute(object?[] args)
        {
            return args[0] ?? args[1];
        }
    }
}
