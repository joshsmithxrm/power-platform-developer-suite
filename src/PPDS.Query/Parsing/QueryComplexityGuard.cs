using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace PPDS.Query.Parsing
{
    /// <summary>
    /// Refuses SQL text that is too long or too deeply nested for the ScriptDom parser, and for the engine's own recursive walks
    /// of its tree, to handle on a thread's stack, before either runs (#1952). A stack overflow cannot be caught: it ends the
    /// process (the CLI, the TUI, the daemon behind the VS Code extension, the MCP server). Nothing here recurses: the text is
    /// scanned character by character, then the token stream is walked once with a stack of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// How the bounds were chosen (measured with a probe process, Release build, Linux x64, .NET 10): each kind of nesting was
    /// made deeper until a thread created with a given stack size overflowed, both for the parser alone and for the query
    /// service as a whole (its DML checks, column validation, planner and FetchXML generation, and the script runner). A
    /// bound is admitted when the parse and every walk of that statement kind survive it on a 512 KB stack: half of the
    /// smallest default stack .NET documents, the 1 MB the linker writes into a Windows executable's header for its main
    /// thread (learn.microsoft.com/en-us/windows/win32/procthread/thread-stack-size: "The default stack reservation size used
    /// by the linker is 1 MB"). Other threads get 1.5 MB on Windows and macOS and 8 MB on Linux
    /// (learn.microsoft.com/en-us/dotnet/core/runtime-config/threading: "The default stack size for .NET apps is 1.5 MB on
    /// Windows and macOS, and 8 MB on Linux"). The thread pool's default is not documented on either page.
    /// </para>
    /// <para>
    /// The per-level costs (the weights below, in bytes) are 524,288 divided by the deepest level that survived on 512 KB:
    /// conditions in parentheses 540; a value in parentheses 264; a function call 208; a subquery 123; CASE 236; BEGIN ... END
    /// 288; nested IF or ELSE IF 226 (the script runner); AND or OR in a SELECT 416 (FetchXML generation); AND or OR elsewhere
    /// 4,416 (the parser: a write's own walk is bounded by its 200-level limit first); NOT 2,816; arithmetic and unary
    /// operators 1,464; UNION, EXCEPT or INTERSECT 4,384; nested block comments 1,344 (the tokenizer). An IN list is flat
    /// (100,000 values parse on 128 KB) and is not bounded here. Frames on Windows are larger: one CI crash overflowed after
    /// 5,995 levels where Linux needs about 0.7 MB, an estimated 2.3 times as much per level. The margin on Windows is
    /// therefore not measured here; the test at the bound on a 1 MB thread, run on the Windows CI job, is that measurement.
    /// </para>
    /// <para>
    /// The tests run a Debug build, whose frames for the engine's own walks are larger. Every kind at its bound was also run
    /// in a Debug build (Linux): it survives a 448 KB stack, 1 MB divided by the estimated Windows ratio, after the weights of
    /// NOT, of arithmetic and unary operators and of set operations were raised for it (at the Release-only weights those
    /// bounds needed 704 KB, 768 KB and 576 KB in Debug). So those three are bounded more tightly than Release alone needs.
    /// </para>
    /// </remarks>
    public static class QueryComplexityGuard
    {
        /// <summary>The longest SQL text read, in characters (the tokenizer and the parser held 20 million; this bounds their time and memory).</summary>
        public const int MaxTextLength = 8_388_608;

        /// <summary>
        /// The most <c>/*</c> a text may hold. The tokenizer recurses into a nested block comment (1,344 levels overflow 512 KB);
        /// every <c>/*</c> is counted, nested or not, so the count bounds the nesting without reading the comments.
        /// </summary>
        public const int MaxBlockComments = 1_000;

        /// <summary>
        /// The most conditions a SELECT may chain with AND or OR at one level of its tree (FetchXML generation recurses once per
        /// condition: 416 overflow 512 KB). One constant, apart from the write limit (<c>DmlStatementSupport.MaxConditionDepth</c>),
        /// so that it can be changed alone.
        /// </summary>
        public const int MaxSelectConditionChain = 200;

        /// <summary>The most stack the deepest point of a text may need, in bytes, by the weights below (about 84% of 512 KB).</summary>
        public const long StackBudget = 440_000;

        internal const int ConditionParenthesisWeight = 971;
        internal const int ValueParenthesisWeight = 1_986;
        internal const int FunctionCallWeight = 2_521;
        internal const int SubqueryWeight = 4_263;
        internal const int CaseWeight = 2_222;
        internal const int BlockWeight = 1_821;
        internal const int ControlFlowWeight = 2_320;
        internal const int SelectConditionWeight = 1_261;
        internal const int ConditionWeight = 119;
        // NOT, operators and set operations cost far more per level in a Debug build (the tests) than in Release; these three
        // are raised so that each at its bound also survives the Debug build on 448 KB (see the remarks).
        internal const int NotWeight = 326;
        internal const int OperatorWeight = 677;
        internal const int SetOperationWeight = 169;

        /// <summary>Throws <see cref="QueryTooComplexException"/> when <see cref="FindRefusal"/> refuses <paramref name="sql"/>.</summary>
        /// <param name="sql">The SQL text.</param>
        /// <param name="parser">The parser whose tokenizer reads the text.</param>
        public static void ThrowIfTooComplex(string sql, TSqlParser parser)
        {
            if (FindRefusal(sql, parser) is { } refusal)
                throw new QueryTooComplexException(refusal);
        }

        /// <summary>The reason <paramref name="sql"/> is too long or too deeply nested to parse safely, or null.</summary>
        /// <param name="sql">The SQL text.</param>
        /// <param name="parser">The parser whose tokenizer reads the text.</param>
        /// <returns>The refusal message, or null.</returns>
        public static string? FindRefusal(string sql, TSqlParser parser)
        {
            ArgumentNullException.ThrowIfNull(parser);
            if (FindTextRefusal(sql) is { } refusal)
                return refusal;

            IList<TSqlParserToken> tokens;
            using (var reader = new StringReader(sql))
                tokens = parser.GetTokenStream(reader, out _);
            return FindTooDeep(tokens);
        }

        /// <summary>
        /// The reason <paramref name="sql"/> is too long, or holds too many block comments, for the tokenizer to read safely, or
        /// null. A caller that only tokenizes the text (syntax highlighting) checks this before it does.
        /// </summary>
        /// <param name="sql">The SQL text.</param>
        /// <returns>The refusal message, or null.</returns>
        public static string? FindTextRefusal(string sql)
        {
            ArgumentNullException.ThrowIfNull(sql);
            if (sql.Length > MaxTextLength)
                return Refusal($"it is {sql.Length:N0} characters long, more than the {MaxTextLength:N0} read", "Split it into several requests.");
            var comments = CountBlockCommentOpenings(sql);
            if (comments > MaxBlockComments)
                return Refusal($"it holds {comments:N0} block comments (/*), more than the {MaxBlockComments:N0} read", "Remove the block comments, or use -- comments.");
            return null;
        }

        private static int CountBlockCommentOpenings(string sql)
        {
            var count = 0;
            for (var i = 0; i + 1 < sql.Length; i++)
            {
                if (sql[i] == '/' && sql[i + 1] == '*')
                {
                    count++;
                    i++;
                }
            }
            return count;
        }

        private static string Refusal(string why, string instead)
            => $"The SQL was not read: {why}, which is more than PPDS can read safely. Nothing was executed. {instead}";

        private enum GroupKind { Root, ConditionParenthesis, ValueParenthesis, FunctionCall, Subquery, Case, Block }

        /// <summary>One level of nesting on the path to the current token, and the chain of operators of its current item.</summary>
        private sealed class Group
        {
            public GroupKind Kind;
            public long Weight;
            public int Ors;
            public int AndRun;
            public int AndMax;
            public int Nots;
            public int Operators;
            public int SetOperations;

            /// <summary>The operators of the current item's AND/OR chain at this level, the depth they add (a left-deep tree).</summary>
            public int Conditions => Ors + AndMax;

            public long ChainCost(int conditionWeight)
                => (long)Conditions * conditionWeight + (long)Nots * NotWeight + (long)Operators * OperatorWeight + (long)SetOperations * SetOperationWeight;

            public void ResetItem()
            {
                Ors = AndRun = AndMax = Nots = Operators = 0;
            }
        }

        private static string? FindTooDeep(IList<TSqlParserToken> all)
        {
            var tokens = new List<TSqlParserToken>(all.Count);
            var writes = false;
            foreach (var token in all)
            {
                if (token.TokenType is TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment or TSqlTokenType.EndOfFile)
                    continue;
                tokens.Add(token);
                writes |= token.TokenType is TSqlTokenType.Insert or TSqlTokenType.Update or TSqlTokenType.Delete or TSqlTokenType.Merge;
            }

            var conditionKinds = ClassifyParentheses(tokens);
            // In a text that writes, a chain of AND and OR reaches the engine's recursive walks only after the 200-level write
            // limit, checked without recursion; in a SELECT it reaches FetchXML generation as it is.
            var conditionWeight = writes ? ConditionWeight : SelectConditionWeight;

            var path = new Stack<Group>();
            path.Push(new Group { Kind = GroupKind.Root });
            long fixedCost = 0, controlFlow = 0, chainCost = 0;

            void Charge(Group group, Action change)
            {
                chainCost -= group.ChainCost(conditionWeight);
                change();
                chainCost += group.ChainCost(conditionWeight);
            }

            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                var top = path.Peek();
                switch (token.TokenType)
                {
                    case TSqlTokenType.LeftParenthesis:
                    {
                        var kind = conditionKinds[i];
                        var group = new Group { Kind = kind, Weight = Weight(kind) };
                        fixedCost += group.Weight;
                        path.Push(group);
                        break;
                    }
                    case TSqlTokenType.RightParenthesis:
                        if (top.Kind is GroupKind.ConditionParenthesis or GroupKind.ValueParenthesis or GroupKind.FunctionCall or GroupKind.Subquery)
                            Close(path.Pop());
                        break;
                    case TSqlTokenType.Case:
                    {
                        var group = new Group { Kind = GroupKind.Case, Weight = CaseWeight };
                        fixedCost += group.Weight;
                        path.Push(group);
                        break;
                    }
                    case TSqlTokenType.Begin when !(Next(tokens, i) is TSqlTokenType.Tran or TSqlTokenType.Transaction or TSqlTokenType.Distributed
                                                    || Next(tokens, i) is TSqlTokenType.Identifier && Text(tokens, i + 1) is "DIALOG" or "CONVERSATION"):
                    {
                        var group = new Group { Kind = GroupKind.Block, Weight = BlockWeight };
                        fixedCost += group.Weight;
                        path.Push(group);
                        break;
                    }
                    case TSqlTokenType.End:
                        // END closes the innermost CASE or BEGIN; a parenthesis left open inside it is closed with it.
                        if (HasOpen(path, GroupKind.Case, GroupKind.Block))
                        {
                            while (path.Peek().Kind is not (GroupKind.Case or GroupKind.Block))
                                Close(path.Pop());
                            Close(path.Pop());
                        }
                        break;
                    case TSqlTokenType.If or TSqlTokenType.While:
                        // A statement under IF or WHILE is nested in it, and where it ends is not known from the tokens:
                        // every IF and WHILE of the batch is counted, nested or not.
                        controlFlow += ControlFlowWeight;
                        break;
                    case TSqlTokenType.Or:
                        Charge(top, () => { top.Ors++; top.AndRun = 0; });
                        break;
                    case TSqlTokenType.And:
                        Charge(top, () => { top.AndRun++; top.AndMax = Math.Max(top.AndMax, top.AndRun); });
                        break;
                    case TSqlTokenType.Not:
                        Charge(top, () => top.Nots++);
                        break;
                    case TSqlTokenType.Plus or TSqlTokenType.Minus or TSqlTokenType.Divide or TSqlTokenType.PercentSign
                        or TSqlTokenType.Ampersand or TSqlTokenType.VerticalLine or TSqlTokenType.Circumflex or TSqlTokenType.Tilde:
                        Charge(top, () => top.Operators++);
                        break;
                    case TSqlTokenType.Star when i > 0 && IsOperand(tokens[i - 1].TokenType):
                        Charge(top, () => top.Operators++);
                        break;
                    case TSqlTokenType.Union or TSqlTokenType.Except or TSqlTokenType.Intersect:
                        Charge(top, () => top.SetOperations++);
                        break;
                    case TSqlTokenType.Comma or TSqlTokenType.Select or TSqlTokenType.From or TSqlTokenType.Where or TSqlTokenType.Set
                        or TSqlTokenType.Values or TSqlTokenType.On or TSqlTokenType.When or TSqlTokenType.Then or TSqlTokenType.Else
                        or TSqlTokenType.Having or TSqlTokenType.Group or TSqlTokenType.Order or TSqlTokenType.Into:
                        // A new item or clause: a new subtree, whose chain starts again (a set operation chain goes on).
                        Charge(top, () => top.ResetItem());
                        break;
                    case TSqlTokenType.Semicolon when top.Kind == GroupKind.Root:
                        Charge(top, () => { top.ResetItem(); top.SetOperations = 0; });
                        break;
                }

                // n conditions are chained by n - 1 operators.
                if (!writes && path.Peek().Conditions + 1 > MaxSelectConditionChain)
                {
                    return Refusal($"a SELECT chains more than {MaxSelectConditionChain} conditions with AND or OR at one level (line {token.Line})",
                        "Compare one column with many values with IN (...), or split the statement into several.");
                }

                if (fixedCost + controlFlow + chainCost > StackBudget)
                {
                    return Refusal($"it is nested too deeply ({Describe(path, controlFlow, token.Line)})",
                        "Compare one column with many values with IN (...), compute values before the statement, or split the statement into several.");
                }
            }
            return null;

            void Close(Group group)
            {
                fixedCost -= group.Weight;
                chainCost -= group.ChainCost(conditionWeight);
            }
        }

        private static long Weight(GroupKind kind) => kind switch
        {
            GroupKind.ConditionParenthesis => ConditionParenthesisWeight,
            GroupKind.ValueParenthesis => ValueParenthesisWeight,
            GroupKind.FunctionCall => FunctionCallWeight,
            GroupKind.Subquery => SubqueryWeight,
            GroupKind.Case => CaseWeight,
            GroupKind.Block => BlockWeight,
            _ => 0
        };

        /// <summary>
        /// The kind of each opening parenthesis, by index: a subquery when SELECT or WITH follows it; a function call (or a
        /// column list) when anything but an operator, a punctuation mark or a keyword that starts a condition or a value
        /// precedes it (so a function keyword the list does not know is weighed as a call, the heavier kind); otherwise conditions in parentheses when its own level
        /// holds a comparison or a logical operator, or only parenthesised conditions, and a value in parentheses when not.
        /// </summary>
        private static Dictionary<int, GroupKind> ClassifyParentheses(List<TSqlParserToken> tokens)
        {
            var kinds = new Dictionary<int, GroupKind>();
            var open = new Stack<(int Index, bool Condition)>();
            for (var i = 0; i < tokens.Count; i++)
            {
                var type = tokens[i].TokenType;
                if (type == TSqlTokenType.LeftParenthesis)
                {
                    open.Push((i, false));
                }
                else if (type == TSqlTokenType.RightParenthesis && open.Count > 0)
                {
                    var (index, condition) = open.Pop();
                    var next = Next(tokens, index);
                    var kind = next is TSqlTokenType.Select or TSqlTokenType.With ? GroupKind.Subquery
                        : index > 0 && IsCallee(tokens[index - 1].TokenType) ? GroupKind.FunctionCall
                        : condition ? GroupKind.ConditionParenthesis
                        : GroupKind.ValueParenthesis;
                    kinds[index] = kind;
                    if (kind == GroupKind.ConditionParenthesis && open.Count > 0)
                        open.Push(MarkCondition(open.Pop()));
                }
                else if (IsConditionToken(type) && open.Count > 0)
                {
                    open.Push(MarkCondition(open.Pop()));
                }
            }
            // A parenthesis never closed (the parser will report it) is weighed as the heaviest kind it can be.
            foreach (var (index, _) in open)
                kinds[index] = GroupKind.Subquery;
            return kinds;

            static (int, bool) MarkCondition((int Index, bool Condition) entry) => (entry.Index, true);
        }

        private static bool IsConditionToken(TSqlTokenType type) => type is TSqlTokenType.EqualsSign or TSqlTokenType.LessThan
            or TSqlTokenType.GreaterThan or TSqlTokenType.Bang or TSqlTokenType.And or TSqlTokenType.Or or TSqlTokenType.Not
            or TSqlTokenType.In or TSqlTokenType.Like or TSqlTokenType.Between or TSqlTokenType.Is or TSqlTokenType.Exists;

        private static bool IsCallee(TSqlTokenType type) => type is not (TSqlTokenType.LeftParenthesis or TSqlTokenType.Comma
            or TSqlTokenType.Semicolon or TSqlTokenType.EqualsSign or TSqlTokenType.LessThan or TSqlTokenType.GreaterThan
            or TSqlTokenType.Bang or TSqlTokenType.Plus or TSqlTokenType.Minus or TSqlTokenType.Star or TSqlTokenType.Divide
            or TSqlTokenType.PercentSign or TSqlTokenType.Ampersand or TSqlTokenType.VerticalLine or TSqlTokenType.Circumflex
            or TSqlTokenType.Tilde or TSqlTokenType.And or TSqlTokenType.Or or TSqlTokenType.Not or TSqlTokenType.In
            or TSqlTokenType.Values or TSqlTokenType.Where or TSqlTokenType.On or TSqlTokenType.When or TSqlTokenType.Then
            or TSqlTokenType.Else or TSqlTokenType.Having or TSqlTokenType.Select or TSqlTokenType.Set or TSqlTokenType.Return
            or TSqlTokenType.If or TSqlTokenType.While or TSqlTokenType.Between or TSqlTokenType.Like or TSqlTokenType.Is
            or TSqlTokenType.Exists or TSqlTokenType.Case or TSqlTokenType.Begin or TSqlTokenType.Union or TSqlTokenType.Except
            or TSqlTokenType.Intersect or TSqlTokenType.All or TSqlTokenType.Top or TSqlTokenType.From or TSqlTokenType.Join);

        private static bool IsOperand(TSqlTokenType type) => type is TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier
            or TSqlTokenType.Integer or TSqlTokenType.Numeric or TSqlTokenType.Real or TSqlTokenType.Money
            or TSqlTokenType.AsciiStringLiteral or TSqlTokenType.UnicodeStringLiteral or TSqlTokenType.Variable
            or TSqlTokenType.RightParenthesis or TSqlTokenType.Null;

        private static TSqlTokenType? Next(List<TSqlParserToken> tokens, int index)
            => index + 1 < tokens.Count ? tokens[index + 1].TokenType : null;

        private static string? Text(List<TSqlParserToken> tokens, int index)
            => index < tokens.Count ? tokens[index].Text?.ToUpperInvariant() : null;

        private static bool HasOpen(Stack<Group> path, GroupKind a, GroupKind b)
        {
            foreach (var group in path)
            {
                if (group.Kind == a || group.Kind == b)
                    return true;
            }
            return false;
        }

        /// <summary>What the deepest point is inside, by kind and count (no text of the statement).</summary>
        private static string Describe(Stack<Group> path, long controlFlow, int line)
        {
            var counts = new Dictionary<GroupKind, int>();
            int conditions = 0, operators = 0, nots = 0, setOperations = 0;
            foreach (var group in path)
            {
                counts[group.Kind] = counts.GetValueOrDefault(group.Kind) + 1;
                conditions += group.Conditions;
                operators += group.Operators;
                nots += group.Nots;
                setOperations += group.SetOperations;
            }
            var parts = new List<string>();
            void Add(int n, string what)
            {
                if (n > 0)
                    parts.Add($"{n:N0} {what}");
            }
            Add(counts.GetValueOrDefault(GroupKind.ConditionParenthesis) + counts.GetValueOrDefault(GroupKind.ValueParenthesis), "nested parentheses");
            Add(counts.GetValueOrDefault(GroupKind.FunctionCall), "nested function calls");
            Add(counts.GetValueOrDefault(GroupKind.Subquery), "nested subqueries");
            Add(counts.GetValueOrDefault(GroupKind.Case), "nested CASE expressions");
            Add(counts.GetValueOrDefault(GroupKind.Block), "nested BEGIN ... END blocks");
            Add((int)(controlFlow / ControlFlowWeight), "IF or WHILE statements");
            Add(conditions, "conditions chained with AND or OR");
            Add(nots, "NOT");
            Add(operators, "chained operators");
            Add(setOperations, "UNION, EXCEPT or INTERSECT");
            return $"near line {line}: {string.Join(", ", parts)}";
        }
    }

    /// <summary>
    /// SQL text refused by <see cref="QueryComplexityGuard"/> before it was parsed: too long, or nested too deeply to parse
    /// safely (#1952). A <see cref="QueryParseException"/>, so every caller that handles a parse error refuses it too.
    /// </summary>
    public sealed class QueryTooComplexException : QueryParseException
    {
        /// <summary>The error code of this refusal.</summary>
        public const string TooComplexCode = "QUERY_TOO_COMPLEX";

        /// <summary>Initializes a new instance with the refusal message.</summary>
        /// <param name="message">What was refused and what to do instead.</param>
        public QueryTooComplexException(string message) : base(message)
        {
        }
    }
}
