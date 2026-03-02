using System;

namespace PPDS.Dataverse.Query.Execution.Functions;

/// <summary>
/// T-SQL error functions for TRY/CATCH blocks.
/// ERROR_MESSAGE(), ERROR_NUMBER(), ERROR_SEVERITY(), ERROR_STATE().
/// These read error state from @@ERROR_* variables in the VariableScope.
/// </summary>
public static class ErrorFunctions
{
    /// <summary>
    /// Registers all error functions into the given registry.
    /// </summary>
    /// <param name="registry">The function registry to register into.</param>
    /// <param name="scopeAccessor">
    /// A function that returns the current VariableScope. Called at invocation time.
    /// </param>
    public static void RegisterAll(FunctionRegistry registry, Func<VariableScope?> scopeAccessor)
    {
        registry.Register("ERROR_MESSAGE", new ErrorVariableFunction(scopeAccessor, "@@ERROR_MESSAGE"));
        registry.Register("ERROR_NUMBER", new ErrorVariableFunction(scopeAccessor, "@@ERROR_NUMBER"));
        registry.Register("ERROR_SEVERITY", new ErrorVariableFunction(scopeAccessor, "@@ERROR_SEVERITY"));
        registry.Register("ERROR_STATE", new ErrorVariableFunction(scopeAccessor, "@@ERROR_STATE"));
    }

    /// <summary>
    /// A 0-arg function that reads a value from a @@ERROR_* variable in the VariableScope.
    /// Returns NULL if the variable is not declared (no error has occurred).
    /// </summary>
    private sealed class ErrorVariableFunction : IScalarFunction
    {
        private readonly Func<VariableScope?> _scopeAccessor;
        private readonly string _variableName;

        public int MinArgs => 0;
        public int MaxArgs => 0;

        public ErrorVariableFunction(Func<VariableScope?> scopeAccessor, string variableName)
        {
            _scopeAccessor = scopeAccessor;
            _variableName = variableName;
        }

        public object? Execute(object?[] args)
        {
            var scope = _scopeAccessor();
            if (scope == null || !scope.IsDeclared(_variableName))
                return null;
            return scope.Get(_variableName);
        }
    }
}
