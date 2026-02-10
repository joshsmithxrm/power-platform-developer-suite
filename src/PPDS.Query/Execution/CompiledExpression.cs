// Delegate types CompiledScalarExpression and CompiledPredicate have been moved to
// PPDS.Dataverse.Query.Execution namespace (PPDS.Dataverse assembly) so they can be used by
// plan node classes without circular project dependencies.
//
// For convenience, re-export them into this namespace via global using aliases
// so existing code referencing PPDS.Query.Execution.CompiledScalarExpression still compiles.

global using CompiledScalarExpression = PPDS.Dataverse.Query.Execution.CompiledScalarExpression;
global using CompiledPredicate = PPDS.Dataverse.Query.Execution.CompiledPredicate;
