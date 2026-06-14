# Task: Fix `views add-column` failure on system-generated "My" views

## Branch Context
This is `pr/views-forms-unpublished-reads` — fixes views/forms to read unpublished customizations before mutating, and adds `--unpublished` flag support.

## Bug Report (from field testing)

### Issue 4: `views add-column` fails on "My" views with invalid layout XML

**Reproduction:**
```
ppds views add-column --entity hsl_veterinarian --view "My Veterinarians" --column hsl_licensenumber:120
```

**Actual result:**
```
Failed to update view 'My Veterinarians' after adding columns:
Dataverse error 0x80040216: Invalid layout xml found.
System.Xml.Schema.XmlSchemaValidationException:
The required attribute 'name' is missing.
```

**Context:** Standard views (Active, Inactive, Associated, Lookup) all work fine. Only the auto-generated "My" personal-style views fail.

**Root cause hypothesis:** "My" views may have a different layoutxml structure (e.g., missing `name` attribute on cells/rows) that the column-insertion logic doesn't account for. When PPDS adds a column, it may generate XML that doesn't include the `name` attribute required by Dataverse's schema validation for this view type.

**Your task:**
1. Find the `views add-column` implementation (likely `src/PlatformTools.Cli/Commands/Views/` or an Application Service).
2. Look at how layoutxml cells are generated — specifically whether `name` attributes are included.
3. Compare what Dataverse expects for "My" view layoutxml vs standard views (you may need to inspect the existing layoutxml structure of a "My" view in the test fixtures or retrieve logic).
4. Fix: either generate schema-valid XML for "My" views, or detect them early and fail with a clear "unsupported view type" message.
5. If you choose to support them, ensure the `name` attribute is populated (typically the column logical name).
6. Run `dotnet test --filter "Category!=Integration" -v q` to verify no regressions.

## Bonus: Verify unpublished-read interaction
Since this branch adds unpublished-read support, confirm that reading "My" views with `--unpublished` doesn't introduce additional issues.
