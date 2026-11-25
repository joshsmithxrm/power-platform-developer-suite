using System.Xml.Linq;
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
    /// The FetchXML of every admitted compared case, passed through the executor's serialisation with the line break of
    /// Linux and of Windows, carries exactly the case's value: the serialisation changes no admitted condition value.
    /// </summary>
    [Fact]
    public async Task TheExecutorsSerialisation_ChangesNoConditionValue_OfAnyAdmittedComparedCase()
    {
        var checkedCases = 0;
        var wrong = new List<string>();
        foreach (var c in DmlValueAdmissionTable.CompareCases.Where(c => c.Cell.StartsWith('=')))
        {
            var target = DmlValueAdmissionTable.Targets.Single(t => t.Kind == c.Kind);
            var h = await RunAsync(DmlValueAdmissionTable.CompareStatement(c.Literal, target, c.Operator));
            var generated = Assert.Single(h.Generated);

            // What the generator meant (white space kept), and what each serialisation sends.
            var meant = ConditionValue(XDocument.Parse(generated, LoadOptions.PreserveWhitespace), target.Column);
            foreach (var (machine, newLine) in new[] { ("Linux", "\n"), ("Windows", "\r\n") })
            {
                var sent = ConditionValue(FetchXmlSerialization.RoundTrip(generated, newLine), target.Column);
                if (sent != c.Cell[1..] || meant != c.Cell[1..])
                    wrong.Add($"{c.Operator} {Show(c.Literal)}: case {Show(c.Cell[1..])}, generated {Show(meant)}, sent from {machine} {Show(sent)}");
            }
            checkedCases++;
        }

        Assert.True(wrong.Count == 0, string.Join(System.Environment.NewLine, wrong));
        Assert.True(checkedCases >= 35, $"only {checkedCases} admitted cases");
    }

    /// <summary>
    /// Every text an IN value may be is either refused by the gate or kept by the serialisation, on Linux and on Windows:
    /// a carriage return (alone or before a line feed) and text that is only white space are what the serialisation changes,
    /// and the gate refuses them, with empty text and text that is only other Unicode white space. A line feed is written as
    /// the machine's line break and read back as a line feed.
    /// </summary>
    [Theory]
    [InlineData("a", true)]
    [InlineData(" a ", true)]
    [InlineData("a\tb", true)]
    [InlineData("\ta", true)]
    [InlineData("a\u0085b", true)]
    [InlineData("a\u2028b", true)]
    [InlineData("a]]>b", true)]
    [InlineData("&lt;", true)]
    [InlineData("a\nb", true)]
    [InlineData("a\nb\n", true)]
    [InlineData("a\rb", false)]
    [InlineData("a\r\nb", false)]
    [InlineData("\n", false)]
    [InlineData("\r", false)]
    [InlineData(" ", false)]
    [InlineData("\t", false)]
    [InlineData("  \t ", false)]
    [InlineData("", false)]
    [InlineData("\u00A0", false)]
    [InlineData("\u3000", false)]
    public async Task AnInValue_IsRefused_OrSentUnchanged(string text, bool admitted)
    {
        var sql = $"DELETE FROM account WHERE name IN (N'{text}', N'other')";
        var h = DmlTestHarness.Create(rows: 1);

        if (!admitted)
        {
            var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed }));
            Assert.Equal(ErrorCodes.Query.DmlUnsupportedConstruct, ex.ErrorCode);
            Assert.Empty(h.Reads);
            h.AssertNothingChanged();
            return;
        }

        await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed });
        var generated = Assert.Single(h.Generated);
        foreach (var newLine in new[] { "\n", "\r\n" })
        {
            var values = XDocument.Parse(FetchXmlSerialization.RoundTrip(generated, newLine)).Descendants("value").Select(v => v.Value).ToList();
            Assert.Equal([text, "other"], values);
        }
    }

    /// <summary>
    /// The serialisation is the one the executor used before it moved behind one function: the same text as
    /// <c>XDocument.ToString(SaveOptions.DisableFormatting)</c> on this machine.
    /// </summary>
    [Fact]
    public void TheSerialisation_IsTheExecutorsFormerOne()
    {
        const string fetchXml = "<fetch top=\"5\"><entity name=\"account\"><attribute name=\"name\" /><filter><condition attribute=\"name\" operator=\"eq\" value=\"a&#10;b&#13;c&#9;d&apos;&quot;\" />" +
                                "<condition attribute=\"name\" operator=\"in\"><value>x&#13;&#10;y</value><value> </value><value>&lt;&amp;</value></condition></filter></entity></fetch>";

        Assert.Equal(XDocument.Parse(fetchXml).ToString(SaveOptions.DisableFormatting), FetchXmlSerialization.RoundTrip(fetchXml));
        Assert.Equal(XDocument.Parse(fetchXml).ToString(SaveOptions.DisableFormatting), FetchXmlSerialization.RoundTrip(fetchXml, System.Environment.NewLine));
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

    /// <summary>A chain of conditions joined by OR is as deep as it is long.</summary>
    [Fact]
    public async Task AConditionTree_AtTheDepthLimit_IsRun_AndOneLevelDeeper_IsRefused()
    {
        var h = await RunAsync($"DELETE FROM account WHERE {OrChain(DmlStatementSupport.MaxConditionDepth)}");
        Assert.Single(h.Reads);

        var ex = await RefusedBeforeAnythingIsReadAsync($"DELETE FROM account WHERE {OrChain(DmlStatementSupport.MaxConditionDepth + 1)}");
        Assert.Contains($"nested more than {DmlStatementSupport.MaxConditionDepth} levels", ex.Message);
    }

    /// <summary>Parentheses count as levels.</summary>
    [Fact]
    public async Task NestedParentheses_BeyondTheDepthLimit_AreRefused()
    {
        var depth = DmlStatementSupport.MaxConditionDepth;
        await RunAsync($"DELETE FROM account WHERE {new string('(', depth - 1)}name = 'n1'{new string(')', depth - 1)}");
        await RefusedBeforeAnythingIsReadAsync($"DELETE FROM account WHERE {new string('(', depth)}name = 'n1'{new string(')', depth)}");
    }

    /// <summary>A balanced tree is shallow; its width is bounded by the number of conditions.</summary>
    [Fact]
    public async Task AConditionTree_AtTheWidthLimit_IsRun_AndOneConditionMore_IsRefused()
    {
        static string Balanced(int from, int count) => count == 1
            ? $"name = 'n{from}'"
            : $"({Balanced(from, count / 2)} OR {Balanced(from + count / 2, count - count / 2)})";

        var h = await RunAsync($"DELETE FROM account WHERE {Balanced(0, DmlStatementSupport.MaxConditions)}");
        Assert.Single(h.Reads);

        var ex = await RefusedBeforeAnythingIsReadAsync($"DELETE FROM account WHERE {Balanced(0, DmlStatementSupport.MaxConditions + 1)}");
        Assert.Contains($"more than {DmlStatementSupport.MaxConditions} conditions", ex.Message);
    }

    /// <summary>
    /// The size the report saw exhaust the stack (about 6,500 ORed conditions), in the first statement and in a later one
    /// of a script: one refusal, before the first statement runs.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConditionTreeThatExhaustedTheStack_IsRefused_InTheFirstOrALaterStatement(bool later)
    {
        var deep = $"DELETE FROM account WHERE {OrChain(6500)}";
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
    internal static string? ConditionValue(string fetchXml, string column) => ConditionValue(XDocument.Parse(fetchXml), column);

    private static string? ConditionValue(XDocument fetchXml, string column)
    {
        // A negation is sent with a not-null condition on the same column, which has no value.
        var condition = Assert.Single(fetchXml.Descendants("condition"),
            c => (string?)c.Attribute("attribute") == column && (c.Attribute("value") != null || c.Elements("value").Any()));
        return (string?)condition.Attribute("value") ?? Assert.Single(condition.Elements("value")).Value;
    }

    private static string Show(string? text) => text is null ? "<null>" : string.Concat(text.Select(c => c is < ' ' or > '~' ? $"<U+{(int)c:X4}>" : c.ToString()));
}
