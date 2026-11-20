# Live observations owed by the faithful query harness

The fake Dataverse (`FetchXmlEvaluator`, `AuditDataset`) evaluates only what the November 2026 live SELECT audit recorded.
This list is everything only a live run can settle. Each line gives the query to run and what to compare.

Conventions:
- Run against the audit's tables, seeded by its `SeedAsync` (`SelectFidelityLiveTests.cs` on `test/select-fidelity-live`).
  The tables are `ppds_sqlp`, `ppds_sqlc` and `ppds_sqlb`; the report calls them P, C and B.
- Run raw FetchXML with `RetrieveMultiple`, as the audit's `RawFilterAsync` does, and SQL through PPDS, as its `RunCaseAsync`
  does.
- An observation is adopted by adding a replayed case or an OBSERVED line that sends the shape. `ObservedShapesTests` then
  requires the shape in `ObservedShapes.All`.

## 1. Shapes the fake refuses because no recorded case sends them

`ObservedShapes.All` holds the 147 shapes that the replayed cases and the OBSERVED lines send. Every other shape is
refused, and the refusal names the shape.

### 1a. Shapes existing tests need (those tests are marked Skip, "waiting for a live observation")

| Shape | Skipped test | FetchXML to run | Compare |
|---|---|---|---|
| `condition eq Lookup` | `R13_BracedGuidOnALookup_Matches` | `<fetch><entity name="ppds_sqlc"><attribute name="ppds_key" /><filter><condition attribute="ppds_parentid" operator="eq" value="{A3A6E420-0107-49F7-86CF-52CFB3670557}" /></filter></entity></fetch>`, and the same with `value="a3a6e420-0107-49f7-86cf-52cfb3670557"` | the child keys of parent key 1, for both forms |
| `condition ge Double` | `Double_ComparesNumerically_Ge` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_flt" operator="ge" value="2.5" /></filter></entity></fetch>` | the keys |
| `condition lt String`, `condition ge String` | `R4_StringRange_UsesTheSameOrder` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_name" operator="lt" value="b" /></filter></entity></fetch>` and `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_name" operator="ge" value="Zeta" /></filter></entity></fetch>` | the keys, against the order of "order by name asc" (R4) |
| `condition not-null Money` | `R14_Distinct_ComparesMoneyByValue_AndKeepsColumnsApart` | `<fetch distinct="true"><entity name="ppds_sqlp"><attribute name="ppds_money" /><filter><condition attribute="ppds_money" operator="not-null" /></filter></entity></fetch>` | the distinct values and their order |
| a date-time literal with a negative offset | `R7_DateTimeWithANegativeOffset_IsHonoured` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_dt" operator="eq" value="2024-01-14T18:30:00-06:00" /></filter></entity></fetch>` | key 1 (stored 2024-01-15T00:30Z) |
| the whole-number fault for `-1.5`, `1e1`, `5,0`, `5.` | `R13_WholeNumberFault_OtherValueForms` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_num" operator="eq" value="-1.5" /></filter></entity></fetch>`, then the same with `1e1`, `5,0` and `5.` | the fault type and full message, or rows if the value is accepted |
| an empty value with `begins-with` | `R2_BeginsWithAnEmptyValue_DoesNotMatchNullRows` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_name" operator="begins-with" value="" /></filter></entity></fetch>` | the keys (expected: every non-NULL name), or the fault |
| a link without `link-type`, and a link nested under a link | `NestedLinkEntity_JoinsOnItsParentLink`, `R11_NullKeys_NeverJoin` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><link-entity name="ppds_sqlc" from="ppds_parentid" to="ppds_sqlpid" alias="c"><attribute name="ppds_key" /><link-entity name="ppds_sqlp" from="ppds_sqlpid" to="ppds_parentid" alias="p2"><attribute name="ppds_key" /></link-entity></link-entity></entity></fetch>` | the rows (is the default inner?) and the nested join |
| `link join Lookup=Lookup` | `R11_NullKeys_NeverJoin` | `<fetch><entity name="ppds_sqlc"><attribute name="ppds_key" /><link-entity name="ppds_sqlc" from="ppds_parentid" to="ppds_parentid" link-type="inner" alias="c2"><attribute name="ppds_key" /></link-entity></entity></fetch>` | the pairs; a child with a NULL parent joins nothing |
| `returntotalrecordcount` | `Paging_ReportsMoreRecordsCookieAndTotal` | `<fetch count="5" page="1" returntotalrecordcount="true"><entity name="ppds_sqlb"><attribute name="ppds_n" /></entity></fetch>` | `TotalRecordCount`, `TotalRecordCountLimitExceeded`, `MoreRecords` and the cookie text |
| `top 0` | `R10_TopZero_ReturnsNoRow` | `<fetch top="0"><entity name="ppds_sqlp"><attribute name="ppds_key" /></entity></fetch>` | no rows, or the fault; T04 (`SELECT TOP 0`) is answered by the engine without sending `top="0"` |

### 1b. Condition and order pairs by column type that no recorded case sends

These are generated from `ObservedShapes.All`. They are the pairs the evaluator's type rules would otherwise accept:
- LIKE-family operators on strings only;
- no range operator on an id, a lookup or a two-option;
- a multi-select only with `null` and `not-null`.

67 condition pairs and 9 order pairs.

| Refused pair | FetchXML to run | Compare |
|---|---|---|
| `condition lt String` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_name" operator="lt" value="Alpha" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition le String` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_name" operator="le" value="Alpha" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition ge String` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_name" operator="ge" value="Alpha" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-between String` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_name" operator="not-between"><value>Alpha</value><value>Beta</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition ne Decimal` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_dec" operator="ne" value="1.25" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition lt Decimal` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_dec" operator="lt" value="1.25" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition gt Decimal` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_dec" operator="gt" value="1.25" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition le Decimal` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_dec" operator="le" value="1.25" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition ge Decimal` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_dec" operator="ge" value="1.25" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition in Decimal` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_dec" operator="in"><value>1.25</value><value>5</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-in Decimal` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_dec" operator="not-in"><value>1.25</value><value>5</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-between Decimal` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_dec" operator="not-between"><value>1.25</value><value>5</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-null Decimal` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_dec" operator="not-null" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition ne Money` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_money" operator="ne" value="100" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition lt Money` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_money" operator="lt" value="100" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition gt Money` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_money" operator="gt" value="100" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition le Money` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_money" operator="le" value="100" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition in Money` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_money" operator="in"><value>100</value><value>500</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-in Money` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_money" operator="not-in"><value>100</value><value>500</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition between Money` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_money" operator="between"><value>100</value><value>500</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-between Money` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_money" operator="not-between"><value>100</value><value>500</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-null Money` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_money" operator="not-null" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition eq Double` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_flt" operator="eq" value="3.25" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition ne Double` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_flt" operator="ne" value="3.25" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition gt Double` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_flt" operator="gt" value="3.25" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition le Double` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_flt" operator="le" value="3.25" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition ge Double` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_flt" operator="ge" value="3.25" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition in Double` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_flt" operator="in"><value>3.25</value><value>10</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-in Double` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_flt" operator="not-in"><value>3.25</value><value>10</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition between Double` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_flt" operator="between"><value>3.25</value><value>10</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-between Double` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_flt" operator="not-between"><value>3.25</value><value>10</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-null Double` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_flt" operator="not-null" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition in Boolean` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_flag" operator="in"><value>1</value><value>0</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-in Boolean` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_flag" operator="not-in"><value>1</value><value>0</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-null Boolean` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_flag" operator="not-null" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition le DateTime` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_dt" operator="le" value="2024-01-15T00:30:00Z" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition in DateTime` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_dt" operator="in"><value>2024-01-15T00:30:00Z</value><value>2024-06-30T12:00:00Z</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-in DateTime` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_dt" operator="not-in"><value>2024-01-15T00:30:00Z</value><value>2024-06-30T12:00:00Z</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-between DateTime` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_dt" operator="not-between"><value>2024-01-15T00:30:00Z</value><value>2024-06-30T12:00:00Z</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-null DateTime` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_dt" operator="not-null" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition ne DateOnly` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_donly" operator="ne" value="2024-01-15" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition lt DateOnly` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_donly" operator="lt" value="2024-01-15" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition gt DateOnly` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_donly" operator="gt" value="2024-01-15" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition le DateOnly` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_donly" operator="le" value="2024-01-15" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition in DateOnly` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_donly" operator="in"><value>2024-01-15</value><value>2024-06-30</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-in DateOnly` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_donly" operator="not-in"><value>2024-01-15</value><value>2024-06-30</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-between DateOnly` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_donly" operator="not-between"><value>2024-01-15</value><value>2024-06-30</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-null DateOnly` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_donly" operator="not-null" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition lt Choice` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_colorcode" operator="lt" value="374110000" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition gt Choice` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_colorcode" operator="gt" value="374110000" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition le Choice` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_colorcode" operator="le" value="374110000" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition ge Choice` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_colorcode" operator="ge" value="374110000" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-in Choice` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_colorcode" operator="not-in"><value>374110000</value><value>374110001</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition between Choice` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_colorcode" operator="between"><value>374110000</value><value>374110001</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-between Choice` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_colorcode" operator="not-between"><value>374110000</value><value>374110001</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-null Choice` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_colorcode" operator="not-null" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-null MultiChoice` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_multi" operator="not-null" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition eq Lookup` | `<fetch><entity name="ppds_sqlc"><attribute name="ppds_key" /><filter><condition attribute="ppds_parentid" operator="eq" value="a3a6e420-0107-49f7-86cf-52cfb3670557" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition ne Lookup` | `<fetch><entity name="ppds_sqlc"><attribute name="ppds_key" /><filter><condition attribute="ppds_parentid" operator="ne" value="a3a6e420-0107-49f7-86cf-52cfb3670557" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition in Lookup` | `<fetch><entity name="ppds_sqlc"><attribute name="ppds_key" /><filter><condition attribute="ppds_parentid" operator="in"><value>a3a6e420-0107-49f7-86cf-52cfb3670557</value><value>ad6a1d7e-4347-43da-aa01-b99c95af9d19</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-in Lookup` | `<fetch><entity name="ppds_sqlc"><attribute name="ppds_key" /><filter><condition attribute="ppds_parentid" operator="not-in"><value>a3a6e420-0107-49f7-86cf-52cfb3670557</value><value>ad6a1d7e-4347-43da-aa01-b99c95af9d19</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition null Lookup` | `<fetch><entity name="ppds_sqlc"><attribute name="ppds_key" /><filter><condition attribute="ppds_parentid" operator="null" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition ne Uniqueidentifier` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_sqlpid" operator="ne" value="a3a6e420-0107-49f7-86cf-52cfb3670557" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition in Uniqueidentifier` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_sqlpid" operator="in"><value>a3a6e420-0107-49f7-86cf-52cfb3670557</value><value>ad6a1d7e-4347-43da-aa01-b99c95af9d19</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-in Uniqueidentifier` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_sqlpid" operator="not-in"><value>a3a6e420-0107-49f7-86cf-52cfb3670557</value><value>ad6a1d7e-4347-43da-aa01-b99c95af9d19</value></condition></filter></entity></fetch>` | the keys returned, or the fault |
| `condition null Uniqueidentifier` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_sqlpid" operator="null" /></filter></entity></fetch>` | the keys returned, or the fault |
| `condition not-null Uniqueidentifier` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_sqlpid" operator="not-null" /></filter></entity></fetch>` | the keys returned, or the fault |
| `order descending Decimal` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><attribute name="ppds_dec" /><order attribute="ppds_dec" descending="true" /><order attribute="ppds_key" /></entity></fetch>` | the full key order (where NULL goes, and the order of ties by ppds_key) |
| `order descending Money` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><attribute name="ppds_money" /><order attribute="ppds_money" descending="true" /><order attribute="ppds_key" /></entity></fetch>` | the full key order (where NULL goes, and the order of ties by ppds_key) |
| `order ascending Double` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><attribute name="ppds_flt" /><order attribute="ppds_flt" /><order attribute="ppds_key" /></entity></fetch>` | the full key order (where NULL goes, and the order of ties by ppds_key) |
| `order descending Double` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><attribute name="ppds_flt" /><order attribute="ppds_flt" descending="true" /><order attribute="ppds_key" /></entity></fetch>` | the full key order (where NULL goes, and the order of ties by ppds_key) |
| `order descending Boolean` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><attribute name="ppds_flag" /><order attribute="ppds_flag" descending="true" /><order attribute="ppds_key" /></entity></fetch>` | the full key order (where NULL goes, and the order of ties by ppds_key) |
| `order ascending DateTime` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><attribute name="ppds_dt" /><order attribute="ppds_dt" /><order attribute="ppds_key" /></entity></fetch>` | the full key order (where NULL goes, and the order of ties by ppds_key) |
| `order ascending DateOnly` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><attribute name="ppds_donly" /><order attribute="ppds_donly" /><order attribute="ppds_key" /></entity></fetch>` | the full key order (where NULL goes, and the order of ties by ppds_key) |
| `order descending DateOnly` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><attribute name="ppds_donly" /><order attribute="ppds_donly" descending="true" /><order attribute="ppds_key" /></entity></fetch>` | the full key order (where NULL goes, and the order of ties by ppds_key) |
| `order descending Choice` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><attribute name="ppds_colorcode" /><order attribute="ppds_colorcode" descending="true" /><order attribute="ppds_key" /></entity></fetch>` | the full key order (where NULL goes, and the order of ties by ppds_key) |


### 1c. Other forms no recorded case sends

`FetchXmlEvaluatorRefusalProbeTests` sends each of these to a small table, and requires the refusal it names. Removing any
one refusal fails its probe. Here each form is over the audit's tables.

| Form | FetchXML to run | Compare |
|---|---|---|
| distinct with an order returning a float, money, date-time, two-option or date-only column | `<fetch distinct="true"><entity name="ppds_sqlp"><attribute name="ppds_key" /><attribute name="ppds_flt" /><order attribute="ppds_key" /></entity></fetch>`, then the same with `ppds_money`, `ppds_dt`, `ppds_flag` and `ppds_donly` in place of `ppds_flt` | the rows and their order |
| distinct ordered by a column it does not return | `<fetch distinct="true"><entity name="ppds_sqlp"><attribute name="ppds_key" /><order attribute="ppds_name" /></entity></fetch>` | the rows, or the fault |
| distinct with a link attribute | `<fetch distinct="true"><entity name="ppds_sqlp"><attribute name="ppds_key" /><link-entity name="ppds_sqlc" from="ppds_parentid" to="ppds_sqlpid" link-type="inner" alias="c"><attribute name="ppds_name" /></link-entity></entity></fetch>` | the rows |
| distinct with no attributes | `<fetch distinct="true"><entity name="ppds_sqlp" /></fetch>` | the columns and rows returned |
| distinct with `top` (D07 goes out with `count`) | `<fetch distinct="true" top="3"><entity name="ppds_sqlp"><attribute name="ppds_num" /><order attribute="ppds_num" /></entity></fetch>` | the 3 rows, against D07 |
| an order on a multi-select, an id or a lookup column | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><order attribute="ppds_multi" /></entity></fetch>`, then `ppds_sqlpid`; and `<fetch><entity name="ppds_sqlc"><attribute name="ppds_key" /><order attribute="ppds_parentid" /></entity></fetch>` | the order, or the fault |
| an order on `createdon` in a plain query (not the engine's `top="1"` probe) | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><attribute name="createdon" /><order attribute="createdon" /></entity></fetch>` | the `createdon` values and the order (see section 5) |
| an aggregate fetch with `<all-attributes/>` | `<fetch aggregate="true"><entity name="ppds_sqlp"><all-attributes /></entity></fetch>` | the fault, or the row |
| group by over no rows | `<fetch aggregate="true"><entity name="ppds_sqlp"><attribute name="ppds_itemtype" groupby="true" alias="g" /><attribute name="ppds_sqlpid" aggregate="count" alias="n" /><filter><condition attribute="ppds_key" operator="lt" value="0" /></filter></entity></fetch>` | no rows, or one |
| raw aggregates over no matching rows | `<fetch aggregate="true"><entity name="ppds_sqlp"><attribute name="ppds_sqlpid" aggregate="count" alias="c" /><attribute name="ppds_num" aggregate="sum" alias="s" /><attribute name="ppds_num" aggregate="max" alias="m" /><attribute name="ppds_dec" aggregate="sum" alias="ds" /><attribute name="ppds_num" aggregate="avg" alias="a" /><filter><condition attribute="ppds_key" operator="lt" value="0" /></filter></entity></fetch>` | each value, 0 or NULL. A10 shows the engine returns 0 for sum and max of a whole number; the audit calls its cause unverified. |
| sum and max of a whole number over rows whose values are all NULL (A10 observed no rows only) | `<fetch aggregate="true"><entity name="ppds_sqlp"><attribute name="ppds_num2" aggregate="sum" alias="s" /><attribute name="ppds_num2" aggregate="max" alias="m" /><filter><condition attribute="ppds_num2" operator="null" /></filter></entity></fetch>` | 0 or NULL for each |
| `mapping="logical"`, `distinct="false"`, `aggregate="false"` | `<fetch mapping="logical"><entity name="ppds_sqlp"><attribute name="ppds_key" /></entity></fetch>`, then `distinct="false"` and `aggregate="false"` in place of `mapping="logical"` | the same rows as without the attribute |
| a `statuscode` condition; distinct on `statecode` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="statuscode" operator="eq" value="1" /></filter></entity></fetch>`; `<fetch distinct="true"><entity name="ppds_sqlp"><attribute name="statecode" /></entity></fetch>` | the keys; the values (see section 5) |
| an empty attribute name (the record has no `Name = ''` fault) | `<fetch><entity name="ppds_sqlp"><attribute name="" /></entity></fetch>`; `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="" operator="eq" value="1" /></filter></entity></fetch>`; `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><order attribute="" /></entity></fetch>`; `<fetch aggregate="true"><entity name="ppds_sqlp"><attribute name="" aggregate="countcolumn" alias="c" /></entity></fetch>` | the fault text of each |
| an empty value with `like`, `begins-with`, `ends-with`, `not-begin-with` | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_name" operator="like" value="" /></filter></entity></fetch>`, then the same with each other operator | the keys, or the fault |
| LIKE `[a-]%`, `[-a]%`, `[^^]%`, `[z-a]%`, and an emoji | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_name" operator="like" value="[a-]%" /></filter></entity></fetch>`, then the same with each other pattern (the emoji as `&#x1F600;%`) | the keys, or the fault |
| numbers of more than 15 digits; a full-width digit | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_dec" operator="eq" value="1.250000000000000000000000000000001" /></filter></entity></fetch>`; `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_num" operator="eq" value="99999999999999999999999999999999" /></filter></entity></fetch>`; `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_num" operator="eq" value="1５" /></filter></entity></fetch>` | rows (and rounding), or the fault |
| a paging cookie for another page, and one with a page out of range | `<fetch count="2" page="2" paging-cookie="&lt;cookie page=&quot;5&quot; /&gt;"><entity name="ppds_sqlp"><attribute name="ppds_key" /><order attribute="ppds_key" /></entity></fetch>`, then the same with `page=&quot;99999999999999999999&quot;` | which rows come back, or the fault |
| a link to an empty table | Create an empty table `ppds_sqle` with a whole-number column `ppds_key` (the audit has no empty table), then `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><link-entity name="ppds_sqle" from="ppds_key" to="ppds_key" link-type="outer" alias="e"><attribute name="ppds_key" /></link-entity></entity></fetch>` | every parent row, with `e.ppds_key` NULL |
| a link with only one of `from` and `to` empty | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><link-entity name="ppds_sqlc" from="" to="ppds_sqlpid" link-type="outer" alias="x" /></entity></fetch>` | the fault text (the fake answered `Name = ''` before) |
| a local date-time at the end of the range | `<fetch><entity name="ppds_sqlp"><attribute name="ppds_key" /><filter><condition attribute="ppds_dt" operator="eq" value="9999-12-31T23:59:59" /></filter></entity></fetch>` | the fault, or rows |

## 2. Comparisons the record allows only in part

These are the pinned numbers (`AuditCaseReplayTests.Coverage_Totals`, computed from the record).

- **Rows.** Of the 425 replayed cases, 356 return rows and 69 are errors. Of the 356, 113 are compared in full and 243 in part, because the report shows 1 to 6 rows. In all, 1,101 of 38,953 rows are compared. Owed: re-run the audit's suite with the row preview unbounded. Compare every row of every case.
- **Order.** It is compared in 45 cases:
  - those with a top-level ORDER BY and more than one shown row;
  - every `SELECT DISTINCT` without ORDER BY (R14).

  Elsewhere the order is Dataverse's own: id order, fitted to the replay (R16). Owed: `SELECT ppds_key, Pid FROM P` and `SELECT ppds_key, Cid FROM C` unbounded. Compare the full order with id order.
- **Ids.** All 584 ids that the shown rows hold in full are compared; none is masked. The ids never shown are generated (section 5).
- **Error messages.** They are compared segment by segment, and up to the end where the record has no cut at its end. Where the record goes on into live-organisation detail (`orgIndex…`, the metadata cache's version), the offline message must end where that detail begins. Owed, the parts the report cut or redacted:
  - The value text that E06, X02, X03 and X12 record as `'[REDACTED]'`. Run each case's SQL and compare the whole fault message:
    - E06: `SELECT ppds_key FROM P WHERE ppds_num = 'abc'`
    - X02: `DECLARE @i int = 5; SELECT ppds_key FROM P WHERE ppds_num = @i`
    - X03: `DECLARE @i int; SET @i = 7; SELECT ppds_key FROM P WHERE ppds_num > @i`
    - X12: `DECLARE @i int = 5; SELECT ppds_key FROM P WHERE ppds_num IN (@i, 10)`
  - The whole-number fault's text after `Exception raised: Input string`, cut in every record. The fake ends it with `was not in a correct format.`, which is .NET Framework's text and is not observed. Owed: W15, `SELECT ppds_key FROM P WHERE ppds_num = '5.0'`. Compare the whole message.
  - A17 records exactly `Attribute: name not found in XNode:`, and the fake says no more. Owed: `SELECT SUM(ppds_num * 2) AS s FROM P`. Compare the whole message, including any line after the first.

## 3. Wrong cases whose fix the record cannot show

`AuditCaseReplay.FixVisibleInRecord` decides this from the record. For each case below, the expected result has the
same count and columns as the recorded wrong one, and every row the record shows would stay. A fix would leave the replay
green. For each, run the SQL and compare every row with T-SQL's result.

| Case | Issue | SQL | The rows that decide it |
|---|---|---|---|
| FN10 | #1632 | `SELECT ppds_key, REPLACE(ppds_name, 'a', 'o') AS v FROM P` | key 22 (`'Ångström'` vs `'ongström'`) |
| FN11 | #1632 | `SELECT ppds_key, CHARINDEX('a', ppds_name) AS v FROM P` | key 22 (0 vs 1) |
| FN12 | #1632 | `SELECT ppds_key, PATINDEX('%a%', ppds_name) AS v FROM P` | key 22 (0 vs 1) |
| FN68 | #1622 | `SELECT ppds_key, CASE WHEN ppds_name = 'cafe' THEN 1 ELSE 0 END AS v FROM P` | keys 5 and 6 (1, not 0) |
| FN69 | #1622 | `SELECT ppds_key, CASE WHEN ppds_name = 'trail' THEN 1 ELSE 0 END AS v FROM P` | key 8 |
| FN71 | #1621 | `SELECT ppds_key, IIF(ppds_flag = 1, 'yes', 'no') AS v FROM P` | the rows where `ppds_flag` is 1 |
| FN86 | #1621 | `SELECT ppds_key, TRY_CAST(ppds_name AS int) AS v FROM P` | keys 26 and 27 |
| FN87 | #1621 | `SELECT ppds_key, TRY_CONVERT(int, ppds_name) AS v FROM P` | keys 26 and 27 |
| J16 | #1614 | `SELECT p.ppds_key AS pk, COUNT(c.ppds_key) AS n FROM P p LEFT JOIN C c ON c.ppds_parentid = p.Pid GROUP BY p.ppds_key` | the 27 parents with no child (n = 0, not 1) |
| O07 | #1628 | `SELECT ppds_key, ppds_colorcode FROM P ORDER BY ppds_colorcode, ppds_key` | the order after the NULL rows (by value, not label) |
| U02 | #1623 | `SELECT ppds_num FROM P UNION ALL SELECT ppds_qty FROM C` | all 60 rows (18 differ) |

## 4. Behaviours the record cannot settle

Each was checked by a mutation of the fake, switched by an environment variable in a scratch copy, against the 1,117
query tests.

- **The order of rows that tie on every ORDER BY key.** The fake breaks ties by id (R16); the audit's R6 says "table order". A mutation that breaks ties by table order failed no test. So the fake refuses ties between rows that return different values, among the returned rows and at their edges. Owed: `SELECT ppds_key, ppds_num FROM P ORDER BY ppds_num` unbounded. Compare the order of the keys within each tied `ppds_num`.
- **A `top` lost or changed only in the FetchXML.** The planner applies TOP as its own row limit too, so no replayed case changes when the fake ignores `top` or returns one row too many. Only `R10_TopLimitsTheRows` fails on those mutations. Through the engine the record cannot catch them. `DroppedTop_FailsTheTest` covers an engine that loses TOP altogether. Owed: nothing live; this is a property of the engine.
- **The paging-cookie format, and Dataverse's order without ORDER BY.** Both are fitted to the replay, not observed. Owed:
  - PG01 and PG02 with the cookie text recorded;
  - `SELECT ppds_n FROM B` page by page, unbounded. Compare the order.
- **A NULL attribute absent or present.** The replay renders both alike, so only the OBSERVED lines and the R3 and R8 tests catch the difference. Owed: nothing; the OBSERVED lines settle it.
- **Formatted values (date-time, lookup).** The record holds raw values, so only the OBSERVED date-time line and the R15 tests catch them. Owed: a lookup's formatted value. Run `<fetch><entity name="ppds_sqlc"><attribute name="ppds_parentid" /></entity></fetch>` and compare `FormattedValues["ppds_parentid"]` with the parent's name.

## 5. Dataset values inferred, not taken from the audit

- **Record ids.** Generated: 12 of the 24 child ids and 5,039 of the 5,050 bulk ids. Key 27's id is shown only cut (W35: `84c24e9c-a9af-4bba-83b4-bf2…`); its prefix is kept and the rest is zeros, which fixes its place in id order. Two more are shown only cut and are not adopted:
  - bulk key 4 (PG02, PG05: `eafb57ca-f124-4fec-9…`);
  - child key 5 (J13: `216…`).

  Their prefixes stop before the last group, which SQL Server compares first, so they fix nothing about the order. Owed: `SELECT ppds_key, Pid FROM P ORDER BY ppds_key`, `SELECT ppds_key, Cid FROM C ORDER BY ppds_key`, `SELECT ppds_n, Bid FROM B ORDER BY ppds_n`. Compare with `AuditDataset`'s id tables.
- **`createdon`.** The values are invented. Replayed aggregate cases send `top="1"` queries ordered on it, and no recorded result shows a value. Owed: `SELECT ppds_key, createdon FROM P ORDER BY ppds_key` (and C, B). Compare the values and the order they give.
- **The caller's offset, fixed at UTC−6.** It comes from one January value, with no daylight-saving evidence. Owed:
  - `SELECT ppds_key FROM P WHERE ppds_dt = '2024-06-30 06:00'` (key 3 is stored 2024-06-30T12:00Z);
  - the caller's time-zone setting.

  Compare which key matches.
- **`statecode` and `statuscode` option sets.** These are invented; V05 shows only 'Active'. Owed: `SELECT DISTINCT statecode, statecodename, statuscode, statuscodename FROM P`. Compare with the fake's choices.
- **System columns.** `ownerid`, `modifiedon` and the other columns a live `SELECT *` returns (PRJ02, which the report cut) are absent from the fake. Owed: `SELECT * FROM P`. Compare the full column list.

## 6. Wrong cases filed from this list

### J19 (#1840): a joined table's choice-name column reads NULL
- **SQL:** `SELECT c.ppds_key AS ck, p.ppds_colorcodename FROM C c INNER JOIN P p ON c.ppds_parentid = p.Pid`
- **Recorded:** 20 rows, `[ck, ckname, p.ppds_colorcodename, Cid]`, with `p.ppds_colorcodename` NULL for the 17 children whose parent has a colour, e.g. `(2,'2',NULL,{…})`. The engine sent `<link-entity … alias="p"><attribute name="ppds_colorcodename" /></link-entity>`.
- **Correct:** 20 rows `[ck, ppds_colorcodename]`, each child with its parent's colour label, e.g. `(1,'Blue') (2,'Green') (3,NULL) (4,'Red')`. NULL only where the parent has no colour.
- **Cause (my reading):** the generator turns a `*name` column into its base column plus the formatted value only for the main entity (`src/PPDS.Query/Transpilation/FetchXmlGenerator.cs:434-436`, "Only process columns belonging to the main entity"). On a joined table, `p.ppds_colorcodename` goes out as a plain link attribute, which Dataverse does not return, so it reads NULL. The unrequested `ckname` and `Cid` are #1625's defect.
- **Live check:** none needed; the record shows it.

### S13 (#1841): `IN (subquery)` on a column the SELECT does not return gives no rows
- **SQL:** `SELECT ppds_key FROM P WHERE ppds_num IN (SELECT ppds_num FROM C)`
- **Recorded:** `[] 0 rows`. The engine sent only `<fetch><entity name="P"><attribute name="ppds_key" /></entity></fetch>`.
- **Correct:** 9 rows `[ppds_key]`, keys 2, 4, 5, 18, 21, 28 and 3 more: the parents whose `ppds_num` occurs in C.
- **Cause (my reading):** `PlanInSubquery` removes the IN predicate and re-plans the outer query (`src/PPDS.Query/Planning/ExecutionPlanBuilder.Join.cs:157`). It then joins the outer rows on `ppds_num` (`:177`, `:190`), but nothing adds `ppds_num` to the outer FetchXML. Each outer row therefore lacks the key, which reads as NULL, and the semi-join skips NULL keys (`src/PPDS.Query/Planning/Nodes/HashSemiJoinNode.cs:82-89`), so every row is dropped.
- **Live check:** none needed; the record shows it.
