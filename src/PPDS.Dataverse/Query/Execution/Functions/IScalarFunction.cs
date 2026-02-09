namespace PPDS.Dataverse.Query.Execution.Functions;

/// <summary>
/// A scalar function that accepts arguments and returns a single value.
/// Implementations handle NULL propagation per SQL semantics.
/// </summary>
public interface IScalarFunction
{
    /// <summary>Minimum number of arguments the function accepts.</summary>
    int MinArgs { get; }

    /// <summary>Maximum number of arguments the function accepts. Use int.MaxValue for variadic.</summary>
    int MaxArgs { get; }

    /// <summary>
    /// Execute the function with the given evaluated argument values.
    /// </summary>
    /// <param name="args">The evaluated argument values (may contain nulls).</param>
    /// <returns>The function result, or null.</returns>
    object? Execute(object?[] args);
}
