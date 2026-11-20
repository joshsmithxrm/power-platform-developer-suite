# Live observations owed by the faithful query harness

The fake Dataverse (`FetchXmlEvaluator`, `AuditDataset`) evaluates only what the November 2026 live SELECT audit recorded.
This list is everything only a live run can settle. Each line gives the query to run and what to compare.

Conventions:
- Run against the audit's tables, seeded by its `SeedAsync` (`SelectFidelityLiveTests.cs` on `test/select-fidelity-live`).
  The report calls them P, C and B (`ppds_sqlp`, `ppds_sqlc`, `ppds_sqlb`).
- Run raw FetchXML with `RetrieveMultiple`, as the audit's `RawFilterAsync` does, and SQL through PPDS, as its `RunCaseAsync`
  does.
- An observation is adopted by adding a replayed case or an OBSERVED line that sends the shape. `ObservedShapesTests` then
  requires it in `ObservedShapes.All`.

## 1. Shapes the fake refuses because no recorded case sends them

`ObservedShapes.All` holds the 138 shapes the 436 replayed cases and the OBSERVED lines send. Every other shape is refused,
naming the shape.

### 1a. Shapes existing tests need (those tests are marked Skip, "waiting for a live observation")

| Shape | Skipped test | FetchXML to run | Compare |
|---|---|---|---|
| `condition eq Lookup` | `R13_BracedGuidOnALookup_Matches` | `<fetch><entity name="ppds_sqlc"><attribute name="ppds_key" /><filter><condition attribute="ppds_parentid" operator="eq" value="{PARENT-1-ID}" /></filter></entity></fetch>`, once braced upper case and once as plain `D` form | the child keys of parent key 1, for both forms |
| `condition ge Double` | `Double_ComparesNumerically_Ge` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_flt" operator="ge" value="2.5" /></filter></entity></fetch>` | the keys with `ppds_flt` ≥ 2.5 |
| `condition lt String`, `condition ge String` | `R4_StringRange_UsesTheSameOrder` | `<condition attribute="ppds_name" operator="lt" value="b" />` and `operator="ge" value="Zeta"` in the same fetch | the keys, against the order of "order by name asc" (R4) |
| `condition not-null Money` | `R14_Distinct_ComparesMoneyByValue_AndKeepsColumnsApart` | `<fetch distinct="true"><entity name="ppds_sqlp"><attribute name="ppds_money" /><filter><condition attribute="ppds_money" operator="not-null" /></filter></entity></fetch>` | the distinct money values and their order |
| date-time literal with a negative offset (`9999-99-99A99:99:99-99:99`) | `R7_DateTimeWithANegativeOffset_IsHonoured` | `<condition attribute="ppds_dt" operator="eq" value="2024-01-14T18:30:00-06:00" />` | key 1 (stored 2024-01-15T00:30Z) |
| whole-number fault for `-1.5`, `1e1`, `5,0`, `5.` | `R13_WholeNumberFault_OtherValueForms` | `<condition attribute="ppds_num" operator="eq" value="…" />`, one per value | fault type and message, against the W15 message; or rows, if the value is accepted |
| a link without `link-type`, or a link nested under a link | `NestedLinkEntity_JoinsOnItsParentLink`, `R11_NullKeys_NeverJoin` | `<link-entity name="ppds_sqlc" from="ppds_parentid" to="ppds_sqlpid" alias="c">` with no `link-type`, and the same with a second link nested inside it | the rows (is the default inner?) and the nested join's rows |
| `link join Lookup=Lookup` | `R11_NullKeys_NeverJoin` | a link from `ppds_parentid` to a lookup column of the same parent | whether NULL keys join (expected: never) |
| rows that tie on every ORDER BY key but return different values | `R10_TopLimitsTheRows`, `DroppedTop_FailsTheTest`, `TopAndOrderBy_OverTiedRows`, and replayed case T06 (now "cannot be replayed") | `SELECT ppds_key, ppds_num FROM P ORDER BY ppds_num` and T06's `SELECT TOP 3 WITH TIES ppds_key, ppds_num FROM P WHERE ppds_num IS NOT NULL ORDER BY ppds_num` | the order of the keys within each tied `ppds_num`: id order (R16) or table order (R6) |
| `returntotalrecordcount` | `Paging_ReportsMoreRecordsCookieAndTotal`, `SqlQueryServiceTests.ExecuteAsync_PassesRequestParametersToExecutor` | `<fetch count="5" page="1" returntotalrecordcount="true"><entity name="ppds_sqlb"><attribute name="ppds_key" /></entity></fetch>` | `TotalRecordCount`, `TotalRecordCountLimitExceeded`, `MoreRecords`, and the cookie text |

### 1b. Other shapes the fake evaluated before this round and now refuses (no test needs them yet)

Each is refused by its own shape name; run the FetchXML and compare the rows with the fake's former rule.

- **Conditions by column type.** Examples the check found:
  - `eq` on a float;
  - `ne`, `in`, `eq` on a lookup;
  - `lt`, `le`, `ge`, `not-between` on a string;
  - `gt`, `between` on a choice;
  - `in` on a date-time and on a two-option;
  - most operators on decimal, money and date-only.

  For each: `<condition attribute="ppds_<col>" operator="<op>" value="<v>" />` over P. Compare the keys.
- **Orders by column type:** float, date-only, money descending and two-option descending. `<order attribute="ppds_<col>" descending="true|false" />` over P. Compare the full key order, including where NULL goes.
- **Value forms:**
  - `eq` with a `<value>` child: `<condition attribute="ppds_num" operator="eq"><value>5</value></condition>`.
  - `in` with `value=`: `<condition attribute="ppds_num" operator="in" value="5" />`.

  Compare the keys, or the fault.
- **Date-time literal forms:**
  - a space and no seconds: `2024-01-15 00:30`;
  - fractional seconds: `2024-01-15T00:30:00.5`;
  - a local value in daylight-saving months: `2024-07-01T12:00:00`. Compare which key matches. This also settles section 5's caller offset.
- **Sibling filters:** `<filter><condition … /></filter><filter><condition … /></filter>` directly under the entity. Are they ANDed? Compare the keys.
- **`<order entityname="c" …>`** on a linked column. Compare the order.
- **Sibling links** (J14 is the only three-table case, and it cannot be replayed): two `<link-entity>` under P. Compare the rows.
- **`<all-attributes />` inside a link.** Compare the columns returned.
- **A paging cookie of another form:** `paging-cookie="garbage"` with `page="2"`. Fault or rows?
- **Group by and count on a lookup:** `<fetch aggregate="true"><entity name="ppds_sqlc"><attribute name="ppds_parentid" groupby="true" alias="g" /><attribute name="ppds_key" aggregate="count" alias="n" /></entity></fetch>`. Compare the groups and counts.
- **Conditions on `createdon`, and `statuscodename`:**
  - `<condition attribute="createdon" operator="gt" value="…" />`
  - `<condition attribute="statuscodename" operator="eq" value="Inactive" />`

  Compare the keys.
- **Distinct without order** on float, two-option, date-time, date-only, multi-select, id and lookup columns. Before this round these threw a raw `InvalidOperationException`; now they are refused. `<fetch distinct="true"><entity name="ppds_sqlp"><attribute name="ppds_<col>" /></entity></fetch>`. Compare the values and their order.
- **A query on an empty table.** Create an empty table and run `count`, `sum`, `max`, and a plain select. Compare: 0 rows, and the aggregate values (A10 covers only sum and max of a whole number).

## 2. Comparisons the record allows only in part

- **242 of the 355 row results are partial:** the report shows 1 to 6 rows (1,099 of 38,917 rows are compared). Owed: re-run the audit's suite with the row preview unbounded. Compare every row of every case.
- **Order is compared in 44 cases:**
  - every case with a top-level ORDER BY and more than one shown row;
  - every `SELECT DISTINCT` without ORDER BY (R14).

  Elsewhere, row order is Dataverse's own (R16, fitted to the replay). Owed: `SELECT ppds_key, Pid FROM P` and `SELECT ppds_key, Cid FROM C`. Compare the full order with id order.
- **Ids:** 563 shown ids are compared and 19 are masked, because the dataset generated them (parent key 27, 13 child ids, all bulk ids). Owed: the same two queries, plus `SELECT ppds_key, Bid FROM B`.
- **Error messages:** compared segment by segment, but the report cut 47 of 70. The whole-number fault's text after `Exception raised: Input string` is not in the record. The fake ends it with `was not in a correct format.`, which is the .NET Framework text and not observed. Owed: `SELECT ppds_key FROM P WHERE ppds_num = '5.0'`. Compare the whole `FaultException` message, with no redaction.

## 3. Wrong cases whose fix the record cannot show

For each case below, the wrong offline result still matches what the audit expected (`FixNotVisible` in `AuditCaseReplayTests`). A fix would leave the replay green. For each, run the SQL and compare every row with T-SQL's result.

| Case | Issue | SQL | The rows that decide it |
|---|---|---|---|
| FN10 | no issue yet | `SELECT ppds_key, REPLACE(ppds_name, 'a', 'o') AS v FROM P` | key 22 (`'Ångström'` vs `'ongström'`) |
| FN11 | no issue yet | `SELECT ppds_key, CHARINDEX('a', ppds_name) AS v FROM P` | key 22 (0 vs 1) |
| FN12 | no issue yet | `SELECT ppds_key, PATINDEX('%a%', ppds_name) AS v FROM P` | key 22 (0 vs 1) |
| FN17 | no issue yet | `SELECT ppds_key, STUFF(ppds_name, 2, 1, 'X') AS v FROM P` | keys 17, 28, 29 |
| FN69 | #1622 | `SELECT ppds_key, CASE WHEN ppds_name = 'trail' THEN 1 ELSE 0 END AS v FROM P` | key 8 |
| FN86 | #1621 | `SELECT ppds_key, TRY_CAST(ppds_name AS int) AS v FROM P` | keys 26, 27 |
| FN87 | #1621 | `SELECT ppds_key, TRY_CONVERT(int, ppds_name) AS v FROM P` | keys 26, 27 |
| J16 | #1614 | `SELECT p.ppds_key AS pk, COUNT(c.ppds_key) AS n FROM P p LEFT JOIN C c ON c.ppds_parentid = p.Pid GROUP BY p.ppds_key` | the 27 parents with no child (n = 0, not 1) |
| O07 | #1628 | `SELECT ppds_key, ppds_colorcode FROM P ORDER BY ppds_colorcode, ppds_key` | the full order after the NULL rows (by value, not label) |
| U02 | #1623 | `SELECT ppds_num FROM P UNION ALL SELECT ppds_qty FROM C` | all 60 rows (18 differ) |

## 4. Behaviours the record cannot settle

The mutation results that support this section are in the pull request description.

- **Tie order among rows equal on every ORDER BY key.** The fake breaks ties by id (R16); the audit's R6 says "table order". No recorded case tells them apart: a mutation that breaks ties by table order failed no test. So the fake now refuses ties between rows that return different values (see 1a). Owed: the same query as in 1a.
- **Paging cookie format, and the order Dataverse returns rows in without ORDER BY.** Both are fitted to the replay, not observed. Owed:
  - PG01 and PG02 with the cookie text recorded;
  - `SELECT ppds_key FROM B` page by page. Compare the order.

## 5. Dataset values inferred, not taken from the audit

- **Record ids.** These were generated:
  - parent key 27 (O05 differs for that reason);
  - 13 child ids;
  - all 5,050 bulk ids.

  Owed: `SELECT ppds_key, Pid FROM P ORDER BY ppds_key`, `SELECT ppds_key, Cid FROM C ORDER BY ppds_key`, `SELECT ppds_key, Bid FROM B ORDER BY ppds_key`. Compare with `AuditDataset.LiveParentIds` and `LiveChildIds`.
- **`createdon`.** The values are invented. Replayed aggregate cases send queries ordered on it, and no recorded result shows a value. Owed: `SELECT ppds_key, createdon FROM P ORDER BY ppds_key` (and C, B). Compare the values and the order they give.
- **The caller's offset, fixed at UTC−6.** This comes from one January value; there is no daylight-saving evidence. Owed:
  - `SELECT ppds_key FROM P WHERE ppds_dt = '2024-06-30 06:00'` (key 3 is stored 2024-06-30T12:00Z);
  - the caller's time-zone setting.

  Compare which key matches.
- **`statecode` and `statuscode` option sets.** These are invented; V05 shows only 'Active'. Owed: `SELECT DISTINCT statecode, statecodename, statuscode, statuscodename FROM P`. Compare with the fake's choices.
- **System columns.** `ownerid`, `modifiedon` and the other columns a live `SELECT *` returns (PRJ02, which the report cut) are absent from the fake. Owed: `SELECT * FROM P`. Compare the full column list.
- **`RetrieveTotalRecordCount`.** The exact value and the limit flag are not observed; see 1a.
