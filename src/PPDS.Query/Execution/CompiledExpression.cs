using System.Collections.Generic;
using PPDS.Dataverse.Query;

namespace PPDS.Query.Execution;

/// <summary>
/// Compiled scalar expression: evaluates against a row and returns a value.
/// Produced by <see cref="ExpressionCompiler.CompileScalar"/> from ScriptDom AST nodes.
/// </summary>
public delegate object? CompiledScalarExpression(IReadOnlyDictionary<string, QueryValue> row);

/// <summary>
/// Compiled predicate: evaluates against a row and returns true/false.
/// Produced by <see cref="ExpressionCompiler.CompilePredicate"/> from ScriptDom AST nodes.
/// </summary>
public delegate bool CompiledPredicate(IReadOnlyDictionary<string, QueryValue> row);
