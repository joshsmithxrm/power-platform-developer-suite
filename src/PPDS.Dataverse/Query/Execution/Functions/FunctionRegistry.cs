using System;
using System.Collections.Generic;

namespace PPDS.Dataverse.Query.Execution.Functions;

/// <summary>
/// Registry of scalar functions available for expression evaluation.
/// Function names are case-insensitive per SQL convention.
/// </summary>
public sealed class FunctionRegistry
{
    private readonly Dictionary<string, IScalarFunction> _functions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a function under the given name (case-insensitive).
    /// </summary>
    public void Register(string name, IScalarFunction function)
    {
        _functions[name] = function ?? throw new ArgumentNullException(nameof(function));
    }

    /// <summary>
    /// Invokes a registered function by name with the given arguments.
    /// </summary>
    /// <param name="name">The function name (case-insensitive).</param>
    /// <param name="args">The evaluated argument values.</param>
    /// <returns>The function result.</returns>
    /// <exception cref="NotSupportedException">Thrown when the function is not registered.</exception>
    /// <exception cref="ArgumentException">Thrown when argument count is out of range.</exception>
    public object? Invoke(string name, object?[] args)
    {
        if (!_functions.TryGetValue(name, out var function))
        {
            throw new NotSupportedException($"Function '{name}' is not supported.");
        }

        if (args.Length < function.MinArgs || args.Length > function.MaxArgs)
        {
            throw new ArgumentException(
                $"Function '{name}' expects {function.MinArgs}" +
                (function.MinArgs == function.MaxArgs
                    ? ""
                    : $"-{(function.MaxArgs == int.MaxValue ? "N" : function.MaxArgs.ToString())}") +
                $" argument(s), but got {args.Length}.");
        }

        return function.Execute(args);
    }

    /// <summary>
    /// Returns true if a function with the given name is registered.
    /// </summary>
    public bool IsRegistered(string name) => _functions.ContainsKey(name);

    /// <summary>
    /// Creates a FunctionRegistry pre-loaded with all built-in functions.
    /// </summary>
    public static FunctionRegistry CreateDefault()
    {
        var registry = new FunctionRegistry();
        StringFunctions.RegisterAll(registry);
        DateFunctions.RegisterAll(registry);
        MathFunctions.RegisterAll(registry);
        JsonFunctions.RegisterAll(registry);
        DataverseFunctions.RegisterAll(registry);
        return registry;
    }
}
