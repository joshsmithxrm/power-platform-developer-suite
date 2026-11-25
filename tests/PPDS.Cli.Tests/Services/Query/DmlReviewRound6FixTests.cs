using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Query;
using PPDS.Query.Planning;
using PPDS.Tests.Shared;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// The fix round after the final review of the value admission (#1698, review round 6), end to end through
/// <see cref="SqlQueryService"/>. The table cells of each item are in <see cref="DmlValueAdmissionTable"/>; these are the
/// cases a table cell cannot state: a script of two statements, the FetchXML the executor sends, the status pair, the
/// size of a condition tree, and the codes of failures during a SELECT.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DmlReviewRound6FixTests
{
    private static readonly DmlSafetyOptions Confirmed = new() { IsConfirmed = true };

    private static readonly DmlSafetyOptions[] EveryRequest = [Confirmed, new DmlSafetyOptions(), new DmlSafetyOptions { IsDryRun = true }];

    /// <summary>The statement is refused with <paramref name="code"/> before anything is read, for every kind of request.</summary>
    private static async Task<PpdsException> RefusedBeforeAnythingIsReadAsync(string sql, string code = ErrorCodes.Query.DmlUnsupportedConstruct)
    {
        PpdsException? first = null;
        foreach (var safety in EveryRequest)
        {
            var h = DmlTestHarness.Create(rows: 3);
            var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = safety }));
            Assert.Equal(code, ex.ErrorCode);
            Assert.Empty(h.Reads);
            h.AssertNothingChanged();
            first ??= ex;
        }
        return first!;
    }

    private static async Task<DmlTestHarness> RunAsync(string sql, int rows = 1)
    {
        var h = DmlTestHarness.Create(rows: rows);
        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed });
        return h;
    }

    // ---- MF1: SQL Server's line continuation ------------------------------------------------------------------

    /// <summary>
    /// A backslash directly before a line break is removed by SQL Server with the line break: refused as a written value
    /// and as an operand of a write's WHERE, before anything is read (the reference test has SQL Server's reading).
    /// </summary>
    [Theory]
    [InlineData("UPDATE account SET name = N'abc\\\ndef' WHERE accountid = '00000000-0000-0000-0000-000000000001'")]
    [InlineData("UPDATE account SET name = 'abc\\\ndef' WHERE name = 'n1'")]
    [InlineData("INSERT INTO account (name) VALUES (N'abc\\\r\ndef')")]
    [InlineData("INSERT INTO account (name) VALUES (N'abc\\\rdef')")]
    [InlineData("INSERT INTO account (name) SELECT N'abc\\\ndef' AS name FROM account WHERE name = 'n1'")]
    [InlineData("DELETE FROM account WHERE name = N'abc\\\ndef'")]
    [InlineData("DELETE FROM account WHERE name = 'abc\\\r\ndef'")]
    [InlineData("DELETE FROM account WHERE name <> N'abc\\\\\ndef'")]
    [InlineData("DELETE FROM account WHERE name IN (N'x', N'abc\\\ndef')")]
    [InlineData("DELETE FROM account WHERE name LIKE N'abc\\\n%'")]
    [InlineData("DELETE FROM account WHERE name = 'n1' OR description = N'abc\\\ndef'")]
    [InlineData("UPDATE account SET description = 'x' WHERE accountid = CAST('00000000-0000-0000-0000-000000000001\\\n' AS uniqueidentifier)")]
    public async Task ALineContinuation_IsRefused_WrittenOrCompared_BeforeAnythingIsRead(string sql)
    {
        var ex = await RefusedBeforeAnythingIsReadAsync(sql);
        if (!sql.Contains("CAST", StringComparison.Ordinal))
            Assert.Contains("line continuation", ex.Message);
    }

    /// <summary>A backslash before a space or a tab and then a line break is read as typed, by SQL Server and here.</summary>
    [Fact]
    public async Task ABackslashBeforeASpaceAndALineBreak_IsWrittenAndComparedAsTyped()
    {
        var h = await RunAsync("UPDATE account SET name = N'abc\\ \ndef' WHERE description = N'x\\\t\ny'");

        Assert.Equal("abc\\ \ndef", Assert.Single(h.WrittenEntities)["name"]);
        Assert.Equal("x\\\t\ny", ConditionValue(Assert.Single(h.Reads).FetchXml, "description"));
    }

    // ---- MF2: what the executor sends ------------------------------------------------------------------------

    /// <summary>
    /// The FetchXML of every admitted compared case is sent as the same characters from Linux and from Windows, and carries
    /// exactly the case's value for a reader that normalises line breaks and for one that does not (MF-A). The harness
    /// records that text (B08).
    /// </summary>
    [Fact]
    public async Task EveryAdmittedComparedCase_IsSentAsTheSameCharactersOnEveryMachine_WithExactlyItsValue()
    {
        var checkedCases = 0;
        var wrong = new List<string>();
        foreach (var c in DmlValueAdmissionTable.CompareCases.Where(c => c.Cell.StartsWith('=')))
        {
            var target = DmlValueAdmissionTable.CompareTarget(c.Kind);
            var h = await RunAsync(DmlValueAdmissionTable.CompareStatement(c.Literal, target, c.Operator));
            var sent = SentFetchXml.Sent(Assert.Single(h.Generated));
            Assert.Equal(sent, Assert.Single(h.Reads).FetchXml);
            var value = Assert.Single(SentFetchXml.Values(sent, target.Column));
            if (value != c.Cell[1..])
                wrong.Add($"{c.Operator} {Show(c.Literal)}: case {Show(c.Cell[1..])}, sent {Show(value)}");
            checkedCases++;
        }

        Assert.True(wrong.Count == 0, string.Join(System.Environment.NewLine, wrong));
        Assert.True(checkedCases >= 40, $"only {checkedCases} admitted cases");
    }

    /// <summary>
    /// Every text an IN value may be is either refused by the gate or sent as the same characters on every machine and read
    /// back unchanged by both kinds of reader: a line break (a carriage return or a line feed, MF-A), empty text and text that
    /// is only white space are refused, with and without N (MF-C).
    /// </summary>
    [Theory]
    [InlineData("N'a'", "a")]
    [InlineData("N' a '", " a ")]
    [InlineData("N'a\tb'", "a\tb")]
    [InlineData("N'\ta'", "\ta")]
    [InlineData("N'a\u0085b'", "a\u0085b")]
    [InlineData("N'a\u2028b'", "a\u2028b")]
    [InlineData("N'a]]>b'", "a]]>b")]
    [InlineData("N'&lt;'", "&lt;")]
    [InlineData("'a'", "a")]
    [InlineData("N'a\nb'", null)]
    [InlineData("N'a\nb\n'", null)]
    [InlineData("N'a\rb'", null)]
    [InlineData("N'a\r\nb'", null)]
    [InlineData("N'\n'", null)]
    [InlineData("N'\r'", null)]
    [InlineData("N' '", null)]
    [InlineData("N'\t'", null)]
    [InlineData("N'  \t '", null)]
    [InlineData("N''", null)]
    [InlineData("N'\u00A0'", null)]
    [InlineData("N'\u3000'", null)]
    [InlineData("'a\nb'", null)]
    [InlineData("'a\rb'", null)]
    [InlineData("''", null)]
    [InlineData("' '", null)]
    [InlineData("'\t'", null)]
    public async Task AnInValue_IsRefused_OrSentUnchanged(string escapedLiteral, string? escapedValue)
    {
        var literal = System.Text.RegularExpressions.Regex.Unescape(escapedLiteral);
        var sql = $"DELETE FROM account WHERE name IN ({literal}, N'other')";
        var h = DmlTestHarness.Create(rows: 1);

        if (escapedValue == null)
        {
            var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed }));
            Assert.Equal(ErrorCodes.Query.DmlUnsupportedConstruct, ex.ErrorCode);
            Assert.Empty(h.Reads);
            h.AssertNothingChanged();
            return;
        }

        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed });
        var sent = SentFetchXml.Sent(Assert.Single(h.Generated));
        Assert.Equal([System.Text.RegularExpressions.Regex.Unescape(escapedValue), "other"], SentFetchXml.Values(sent, "name"));
    }

    /// <summary>
    /// The serialisation writes the same characters with either line break, keeps every line break and tab, in an attribute
    /// value and in element text, and still empties an element that is only white space (which the gate refuses).
    /// </summary>
    [Fact]
    public void TheSerialisation_WritesTheSameCharactersOnEveryMachine_AndKeepsEveryLineBreak()
    {
        const string fetchXml = "<fetch top=\"5\"><entity name=\"account\"><attribute name=\"name\" /><filter><condition attribute=\"name\" operator=\"eq\" value=\"a&#10;b&#13;c&#9;d&apos;&quot;\" />" +
                                "<condition attribute=\"name\" operator=\"in\"><value>x&#13;&#10;y</value><value>p&#13;q&#10;r</value><value> </value><value>&lt;&amp;</value></condition></filter></entity></fetch>";

        var sent = SentFetchXml.Sent(fetchXml);

        Assert.Equal(
            "<fetch top=\"5\"><entity name=\"account\"><attribute name=\"name\" /><filter><condition attribute=\"name\" operator=\"eq\" value=\"a&#xA;b&#xD;c&#x9;d'&quot;\" />" +
            "<condition attribute=\"name\" operator=\"in\"><value>x&#xD;\ny</value><value>p&#xD;q\nr</value><value></value><value>&lt;&amp;</value></condition></filter></entity></fetch>",
            sent);
        Assert.Equal(["a\nb\rc\td'\"", "x\r\ny", "p\rq\nr", "", "<&"], SentFetchXml.Values(sent, "name"));
    }

    /// <summary>
    /// The harness records the text sent, not the generated text (B08): for a carriage return in an operand the generator
    /// writes <c>&amp;#13;</c> and the serialisation <c>&amp;#xD;</c>, and the recorded read is the latter.
    /// </summary>
    [Fact]
    public async Task TheHarness_RecordsTheTextSent_NotTheGeneratedText()
    {
        var h = await RunAsync("DELETE FROM account WHERE name = N'a\rb'");

        var generated = Assert.Single(h.Generated);
        var read = Assert.Single(h.Reads).FetchXml;
        Assert.Contains("a&#13;b", generated);
        Assert.Contains("a&#xD;b", read);
        Assert.Equal(SentFetchXml.Sent(generated), read);
    }

    // ---- MF3: XML 1.0 characters ------------------------------------------------------------------------------

    /// <summary>
    /// The report's script: the first statement would write, the second compares text that is not XML 1.0 characters. The
    /// script is refused before its first statement runs: nothing is read and nothing is written.
    /// </summary>
    [Theory]
    [InlineData("UPDATE account SET name = 'a' WHERE name = 'x'; DELETE FROM account WHERE name = N'a<U+D800>b'")]
    [InlineData("UPDATE account SET name = 'a' WHERE name = 'x'; DELETE FROM account WHERE name LIKE N'<U+D83D>%'")]
    [InlineData("UPDATE account SET name = 'a' WHERE name = 'x'; DELETE FROM account WHERE name = N'a<U+FFFE>b'")]
    [InlineData("UPDATE account SET name = 'a' WHERE name = 'x'; DELETE FROM account WHERE name IN (N'x', N'a<U+FFFE>b')")]
    [InlineData("UPDATE account SET name = 'a' WHERE name = 'x'; DELETE FROM account WHERE name = N'a<U+DC00>'")]
    public async Task AScript_WhoseLaterStatementComparesTextOutsideXml_WritesNothing(string sql)
    {
        var ex = await RefusedBeforeAnythingIsReadAsync(DmlValueAdmissionTable.Sql(sql));
        Assert.Contains("XML 1.0", ex.Message);
    }

    /// <summary>U+FFFF never reaches the gate: the parser refuses it. The gate would refuse it too.</summary>
    [Fact]
    public async Task UFFFF_IsRefusedByTheParser_BeforeAnythingIsRead()
    {
        await RefusedBeforeAnythingIsReadAsync("UPDATE account SET name = 'a' WHERE name = 'x'; DELETE FROM account WHERE name = N'a\uFFFFb'", ErrorCodes.Query.ParseError);
        Assert.True(DmlValueAdmission.HasCharacterOutsideXml("a\uFFFFb"));
        Assert.True(DmlValueAdmission.HasCharacterOutsideXml("a\uFFFEb"));
        Assert.True(DmlValueAdmission.HasCharacterOutsideXml("a\uD800b"));
        Assert.True(DmlValueAdmission.HasCharacterOutsideXml("b\uDC00"));
        Assert.True(DmlValueAdmission.HasCharacterOutsideXml("\uDE00\uD83D"));
        Assert.True(DmlValueAdmission.HasCharacterOutsideXml("a\u0001"));
        Assert.False(DmlValueAdmission.HasCharacterOutsideXml("a\uD83D\uDE00b\t\r\n\uFFFD\u007F"));
    }

    /// <summary>Half of a surrogate pair is refused as a written value; a whole pair is written.</summary>
    [Fact]
    public async Task AnUnpairedSurrogate_IsRefusedAsAWrittenValue_AWholePairIsWritten()
    {
        await RefusedBeforeAnythingIsReadAsync("UPDATE account SET name = N'a\uD800b' WHERE name = 'n1'");
        await RefusedBeforeAnythingIsReadAsync("INSERT INTO account (name) VALUES (N'ab\uDBFF')");

        var h = await RunAsync("UPDATE account SET name = N'a\uD83D\uDE00b' WHERE name = 'n1'");
        Assert.Equal("a\uD83D\uDE00b", Assert.Single(h.WrittenEntities)["name"]);
    }

    // ---- MF4: the bounds of a written number hold for a compared one --------------------------------------------

    /// <summary>A decimal operand with more places than the column keeps (ppds_rate keeps 2) is refused; 2 places are compared as written.</summary>
    [Theory]
    [InlineData("UPDATE account SET description = 'x' WHERE ppds_rate = 1.005")]
    [InlineData("UPDATE account SET description = 'x' WHERE ppds_rate IN (1.01, 1.005)")]
    [InlineData("UPDATE account SET description = 'x' WHERE ppds_rate BETWEEN 1.00 AND 1.005")]
    [InlineData("DELETE FROM account WHERE ppds_rate > -0.001")]
    [InlineData("DELETE FROM account WHERE creditlimit = 1.23456")]
    [InlineData("DELETE FROM account WHERE revenue BETWEEN 0.00000000001 AND 1")]
    [InlineData("DELETE FROM account WHERE address1_latitude = 0.1234567890123456789")]
    [InlineData("DELETE FROM account WHERE address1_latitude < 9007199254740993")]
    public async Task ACompareOperandOutsideTheWriteBounds_IsRefused(string sql)
    {
        var ex = await RefusedBeforeAnythingIsReadAsync(sql);
        Assert.DoesNotContain("1.005", ex.Message);
    }

    [Fact]
    public async Task ACompareOperandWithinTheColumnsPlaces_IsComparedAsWritten()
    {
        var h = await RunAsync("UPDATE account SET description = 'x' WHERE ppds_rate = 1.01 AND creditlimit = 1.2345 AND address1_latitude = 0.1");

        var fetchXml = Assert.Single(h.Reads).FetchXml;
        Assert.Equal("1.01", ConditionValue(fetchXml, "ppds_rate"));
        Assert.Equal("1.2345", ConditionValue(fetchXml, "creditlimit"));
        Assert.Equal("0.1", ConditionValue(fetchXml, "address1_latitude"));
    }

    // ---- MF5: the gates no test protected ----------------------------------------------------------------------

    /// <summary>
    /// A line feed and a carriage return in an = operand are sent as character references: without them the XML reader
    /// would turn each into a space, and the statement would select the rows named "a b".
    /// </summary>
    [Theory]
    [InlineData("N'a\nb'", "a&#10;b", "a\nb")]
    [InlineData("N'a\rb'", "a&#13;b", "a\rb")]
    [InlineData("'a\r\nb'", "a&#13;&#10;b", "a\r\nb")]
    public async Task ALineBreakInAnEqualsOperand_IsSentAsACharacterReference(string literal, string encoded, string value)
    {
        var h = await RunAsync($"DELETE FROM account WHERE name = {literal}");

        Assert.Contains($"value=\"{encoded}\"", Assert.Single(h.Generated));
        Assert.Equal(value, ConditionValue(Assert.Single(h.Reads).FetchXml, "name"));
    }

    /// <summary>A new row always starts active: INSERT that sets statecode or statuscode is refused, whatever the value.</summary>
    [Theory]
    [InlineData("INSERT INTO account (name, statecode) VALUES ('n', 0)", "statecode")]
    [InlineData("INSERT INTO account (name, statuscode) VALUES ('n', 1)", "statuscode")]
    [InlineData("INSERT INTO account (name, statecode, statuscode) VALUES ('n', 0, 1)", "statecode")]
    [InlineData("INSERT INTO account (name, statecode) SELECT name, statecode FROM account WHERE name = 'n1'", "statecode")]
    public async Task AnInsertThatSetsTheStatus_IsRefused(string sql, string column)
    {
        var ex = await RefusedBeforeAnythingIsReadAsync(sql);
        Assert.Contains($"INSERT cannot set account.{column}", ex.Message);
    }

    // ---- S1: a lower-case n prefix ------------------------------------------------------------------------------

    [Theory]
    [InlineData("UPDATE account SET name = n'x' WHERE name = 'n1'")]
    [InlineData("UPDATE account SET description = 'x' WHERE name = n'x'")]
    [InlineData("INSERT INTO account (name) VALUES (n'x')")]
    [InlineData("DELETE FROM account WHERE name IN ('a', n'b')")]
    [InlineData("DELETE FROM account WHERE name LIKE n'a%'")]
    public async Task ALowerCaseNPrefix_IsRefused(string sql)
    {
        var ex = await RefusedBeforeAnythingIsReadAsync(sql);
        Assert.Contains("lower-case n prefix", ex.Message);
    }

    [Fact]
    public async Task AnUpperCaseNPrefix_IsWritten()
    {
        var h = await RunAsync("UPDATE account SET name = N'x' WHERE name = N'n1'");
        Assert.Equal("x", Assert.Single(h.WrittenEntities)["name"]);
    }

    // ---- S2: -0 in the status pair ------------------------------------------------------------------------------

    /// <summary>-0 is the statecode 0, and status reason 2 belongs to statecode 1: refused as 0 is.</summary>
    [Theory]
    [InlineData("UPDATE account SET statecode = -0, statuscode = 2 WHERE name = 'n1'")]
    [InlineData("UPDATE account SET statecode = 0, statuscode = 2 WHERE name = 'n1'")]
    [InlineData("UPDATE account SET statuscode = 2, statecode = -0 WHERE name = 'n1'")]
    [InlineData("UPDATE account SET statecode = 1, statuscode = -1 WHERE name = 'n1'")]
    public async Task AStatusReasonOfAnotherStatus_IsRefused_WithOrWithoutTheMinusSign(string sql)
        => await RefusedBeforeAnythingIsReadAsync(sql);

    [Fact]
    public async Task MinusZeroWithItsOwnStatusReason_IsWrittenAsZero()
    {
        var h = await RunAsync("UPDATE account SET statecode = -0, statuscode = 1 WHERE name = 'n1'");

        var entity = Assert.Single(h.WrittenEntities);
        Assert.Equal(0, Assert.IsType<Microsoft.Xrm.Sdk.OptionSetValue>(entity["statecode"]).Value);
        Assert.Equal(1, Assert.IsType<Microsoft.Xrm.Sdk.OptionSetValue>(entity["statuscode"]).Value);
    }

    // ---- S3: the size of a condition tree ---------------------------------------------------------------------

    private static string OrChain(int conditions) => string.Join(" OR ", Enumerable.Range(0, conditions).Select(i => $"name = 'n{i}'"));

    /// <summary>
    /// The WHERE of each kind of write the limits apply to (should-fix 4): DELETE, UPDATE and the WHERE of an INSERT ...
    /// SELECT. <c>{0}</c> is the condition.
    /// </summary>
    public static TheoryData<string> EveryWriteWithAWhere => new()
    {
        "DELETE FROM account WHERE {0}",
        "UPDATE account SET description = 'x' WHERE {0}",
        "INSERT INTO account (description) SELECT description FROM account WHERE {0}",
    };

    /// <summary>
    /// A chain of conditions joined by OR is as deep as it is long: 200 levels run, 201 are refused. The limit is pinned by
    /// its number, not by the constant (should-fix 4).
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryWriteWithAWhere))]
    public async Task AConditionTree_200LevelsDeep_IsRun_And201_IsRefused(string write)
    {
        Assert.Equal(200, DmlStatementSupport.MaxConditionDepth);
        var h = await RunAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, write, OrChain(200)));
        Assert.Single(h.Reads);

        var ex = await RefusedBeforeAnythingIsReadAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, write, OrChain(201)));
        Assert.Contains("nested more than 200 levels", ex.Message);
    }

    /// <summary>Parentheses count as levels.</summary>
    [Fact]
    public async Task NestedParentheses_BeyondTheDepthLimit_AreRefused()
    {
        await RunAsync($"DELETE FROM account WHERE {new string('(', 199)}name = 'n1'{new string(')', 199)}");
        await RefusedBeforeAnythingIsReadAsync($"DELETE FROM account WHERE {new string('(', 200)}name = 'n1'{new string(')', 200)}");
    }

    private static string Balanced(int from, int count) => count == 1
        ? $"name = 'n{from}'"
        : $"({Balanced(from, count / 2)} OR {Balanced(from + count / 2, count - count / 2)})";

    /// <summary>A balanced tree is shallow; its width is bounded by the number of conditions: 1,000 run, 1,001 are refused.</summary>
    [Theory]
    [MemberData(nameof(EveryWriteWithAWhere))]
    public async Task AConditionTree_Of1000Conditions_IsRun_And1001_IsRefused(string write)
    {
        Assert.Equal(1000, DmlStatementSupport.MaxConditions);
        var h = await RunAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, write, Balanced(0, 1000)));
        Assert.Single(h.Reads);

        var ex = await RefusedBeforeAnythingIsReadAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, write, Balanced(0, 1001)));
        Assert.Contains("more than 1000 conditions", ex.Message);
    }

    /// <summary>
    /// The whole-script check looks at the size of every write's condition tree before anything else (should-fix 4, E03):
    /// in a script whose first statement is a SELECT, the too-large tree of the second statement is the refusal, not the
    /// SELECT, because no recursive walk of the script may run before the size is known.
    /// </summary>
    [Fact]
    public void TheWholeScriptCheck_RefusesATreeTooLarge_BeforeAnythingElse()
    {
        var script = new PPDS.Query.Parsing.QueryParser().Parse($"SELECT name FROM account; DELETE FROM account WHERE {Balanced(0, 1001)}");

        var refusal = DmlStatementSupport.FindScriptRefusal(script);

        Assert.NotNull(refusal);
        Assert.StartsWith("Statement 2 (line 1, column 27): ", refusal!.Message);
        Assert.Contains("more than 1000 conditions", refusal.Message);
    }

    /// <summary>
    /// A chain of 1,001 ORed conditions, over both write limits (200 levels, 1,000 conditions), in the first statement and
    /// in a later one of a script: one refusal, before the first statement runs. The size is far below what the SQL
    /// parser's own recursion survives on any stack the tests run on (thousands of levels on 1 MB), so the test shows the
    /// write limits, not the parser's; a chain of several thousand overflowed the parser on a Windows test thread before any
    /// check could run.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConditionTreeOverTheWriteLimits_IsRefused_InTheFirstOrALaterStatement(bool later)
    {
        var deep = $"DELETE FROM account WHERE {OrChain(1001)}";
        var sql = later ? $"UPDATE account SET description = 'x' WHERE name = 'n1'; {deep}" : deep;

        var ex = await RefusedBeforeAnythingIsReadAsync(sql);
        Assert.Contains("nested more than", ex.Message);
    }

    // ---- S9: the codes of failures during a SELECT ---------------------------------------------------------------

    public static TheoryData<Exception, string, int> SelectFailures() => new()
    {
        { new PPDS.Dataverse.Security.DataverseConnectionException("no connection"), ErrorCodes.Connection.Failed, ExitCodes.ConnectionError },
        { new PPDS.Dataverse.Resilience.ServiceProtectionException("throttled"), ErrorCodes.Connection.Throttled, ExitCodes.ConnectionError },
        { new TimeoutException("timed out"), ErrorCodes.Connection.Timeout, ExitCodes.ConnectionError },
        { new NotSupportedException("not supported"), ErrorCodes.Operation.NotSupported, ExitCodes.Failure },
        { new InvalidOperationException("broken"), ErrorCodes.Query.ExecutionFailed, ExitCodes.Failure },
        { new FormatException("bad"), ErrorCodes.Query.ExecutionFailed, ExitCodes.Failure },
    };

    /// <summary>
    /// A failure during a SELECT keeps the code (and the exit code) it had before the service wrapped its failures; only a
    /// failure the mapper does not name is <c>Query.ExecutionFailed</c> (before, <c>Operation.Internal</c>).
    /// </summary>
    [Theory]
    [MemberData(nameof(SelectFailures))]
    public async Task AFailureDuringASelect_KeepsItsCode(Exception failure, string code, int exitCode)
    {
        var executor = new Mock<IQueryExecutor>();
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        executor.Setup(x => x.ExecuteFetchXmlAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<QueryExecutionOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        var service = new SqlQueryService(executor.Object, guard: new InactiveFakeShakedownGuard(), bulkOperationExecutor: Mock.Of<IBulkOperationExecutor>(),
            metadataProvider: TestMetadata.Provider);

        var ex = await Assert.ThrowsAsync<PpdsException>(() => service.ExecuteAsync(new SqlQueryRequest { Sql = "SELECT name FROM account" }));

        Assert.Equal(code, ex.ErrorCode);
        Assert.Equal(exitCode, ExceptionMapper.ToExitCode(ex));
        Assert.Same(failure, ex.InnerException);
    }

    // ---- S12 -----------------------------------------------------------------------------------------------------

    /// <summary>An identifier with a line feed after it is not a logical name (the anchor matched before a trailing line feed).</summary>
    [Fact]
    public void ALogicalName_HasNothingAfterIt()
    {
        Assert.True(DmlStatementSupport.IsLogicalName("account"));
        Assert.False(DmlStatementSupport.IsLogicalName("account\n"));
        Assert.False(DmlStatementSupport.IsLogicalName("\naccount"));
    }

    /// <summary>Written text that begins or ends with U+180E, U+200B or U+FEFF is refused as white space is; inside the text they are written.</summary>
    [Theory]
    [InlineData("\u180Ea")]
    [InlineData("a\u180E")]
    [InlineData("\u200Ba")]
    [InlineData("a\u200B")]
    [InlineData("\uFEFFa")]
    [InlineData("a\uFEFF")]
    public async Task AnInvisibleCharacterAtEitherEndOfWrittenText_IsRefused(string text)
    {
        var ex = await RefusedBeforeAnythingIsReadAsync($"UPDATE account SET name = N'{text}' WHERE name = 'n1'", ErrorCodes.Query.DmlValueStoredDifferently);
        Assert.Contains("U+200B", ex.Message);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    /// <summary>The value of the condition on <paramref name="column"/>: its value attribute, or the text of its one value element.</summary>
    /// <summary>
    /// The one value of the condition on <paramref name="column"/> in the text sent (a negation's not-null condition has
    /// none), read by a reader that normalises line breaks and by one that does not, which must agree.
    /// </summary>
    internal static string? ConditionValue(string fetchXml, string column) => Assert.Single(SentFetchXml.Values(fetchXml, column));

    private static string Show(string? text) => SentFetchXml.Show(text);
}
