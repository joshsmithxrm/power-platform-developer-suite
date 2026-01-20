# PPDS.Migration: CMT Compatibility

## Overview

The CMT Compatibility subsystem provides read/write support for Microsoft's Configuration Migration Tool (CMT) file format. PPDS uses this format for data migration to ensure compatibility with existing CMT-based workflows while extending the format with additional metadata for enhanced import capabilities. The format uses ZIP archives containing XML files for schema definitions and record data.

## Public API

### Interfaces

| Interface | Purpose |
|-----------|---------|
| `ICmtSchemaReader` | Reads CMT schema.xml files |
| `ICmtDataReader` | Reads CMT data.zip archives |
| `ICmtSchemaWriter` | Writes CMT-compatible schema.xml files |
| `ICmtDataWriter` | Writes CMT-compatible data.zip archives |

### Classes

| Class | Purpose |
|-------|---------|
| `CmtSchemaReader` | Parses schema.xml into `MigrationSchema` |
| `CmtDataReader` | Reads ZIP archives into `MigrationData` |
| `CmtSchemaWriter` | Serializes `MigrationSchema` to XML |
| `CmtDataWriter` | Creates ZIP archives from `MigrationData` |
| `UserMappingReader` | Reads user mapping XML files |

### DTOs/Models

| Type | Purpose |
|------|---------|
| `MigrationSchema` | Complete schema definition |
| `EntitySchema` | Entity metadata with fields and relationships |
| `FieldSchema` | Field definition with type and constraints |
| `RelationshipSchema` | Relationship definition (1:M or M:M) |
| `MigrationData` | Complete export with schema, records, and M2M |
| `ManyToManyRelationshipData` | M2M associations grouped by source |

## File Format Structure

### ZIP Archive

```
data.zip
├── [Content_Types].xml     # CMT required: MIME type declarations
├── data.xml                # Record data and M2M associations
└── data_schema.xml         # Schema definition (or schema.xml)
```

### Content Types XML

```xml
<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="xml" ContentType="application/octet-stream" />
</Types>
```

### Schema XML Structure

#### Root Element
```xml
<entities version="1.0" timestamp="2025-01-19T00:00:00Z">
  <!-- Entity definitions -->
</entities>
```

**Attributes:**
- `version`: Schema version (default: "1.0")
- `timestamp`: ISO 8601 datetime when generated

#### Entity Element
```xml
<entity name="account"
        displayname="Account"
        etc="1"
        primaryidfield="accountid"
        primarynamefield="name"
        disableplugins="false">
  <fields>...</fields>
  <relationships>...</relationships>
  <filter>...</filter>
</entity>
```

| Attribute | Required | Default | Description |
|-----------|----------|---------|-------------|
| `name` | Yes | - | Logical name |
| `displayname` | No | - | Display name |
| `etc` | No | - | Entity type code |
| `primaryidfield` | No | `{name}id` | Primary key field |
| `primarynamefield` | No | `name` | Primary name field |
| `disableplugins` | No | `false` | Disable plugins during import |

#### Field Element
```xml
<field name="name"
       displayname="Name"
       type="string"
       maxlength="100"
       precision="2"
       lookupType="account"
       customfield="false"
       isrequired="false"
       primaryKey="false"
       isValidForCreate="true"
       isValidForUpdate="true" />
```

| Attribute | Required | Default | Description |
|-----------|----------|---------|-------------|
| `name` | Yes | - | Logical name |
| `displayname` | No | - | Display name |
| `type` | No | Inferred | Data type (see table) |
| `lookupType` | No | - | Target entity (pipe-delimited for polymorphic) |
| `maxlength` | No | - | String max length |
| `precision` | No | - | Decimal precision |
| `customfield` | No | `false` | Is custom field |
| `isrequired` | No | `false` | Is required |
| `primaryKey` | No | `false` | Is primary key |
| `isValidForCreate` | No | `true` | Valid for create operations |
| `isValidForUpdate` | No | `true` | Valid for update operations |

#### Filter Element
```xml
<filter>&lt;filter&gt;&lt;condition attribute='statecode' operator='eq' value='0'/&gt;&lt;/filter&gt;</filter>
```

Contains HTML-encoded FetchXML filter fragment for export scoping.

### Data XML Structure

#### Root Element
```xml
<entities xmlns:xsd="http://www.w3.org/2001/XMLSchema"
          xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
          timestamp="2025-01-19T20:48:00.0000000Z">
  <!-- Entity data -->
</entities>
```

#### Entity Data Section
```xml
<entity name="account" displayname="Account">
  <records>
    <record id="guid">
      <field name="fieldname" value="fieldvalue" />
    </record>
  </records>
  <m2mrelationships>
    <!-- M2M associations -->
  </m2mrelationships>
</entity>
```

#### Record Element
```xml
<record id="11111111-1111-1111-1111-111111111111">
  <field name="accountid" value="11111111-1111-1111-1111-111111111111" />
  <field name="name" value="Contoso Inc" />
  <field name="parentaccountid"
         value="22222222-2222-2222-2222-222222222222"
         lookupentity="account"
         lookupentityname="Parent Account" />
</record>
```

#### M2M Relationship Element
```xml
<m2mrelationship sourceid="11111111-1111-1111-1111-111111111111"
                 targetentityname="role"
                 targetentitynameidfield="roleid"
                 m2mrelationshipname="systemuserroles_association">
  <targetids>
    <targetid>22222222-2222-2222-2222-222222222222</targetid>
    <targetid>33333333-3333-3333-3333-333333333333</targetid>
  </targetids>
</m2mrelationship>
```

## Behaviors

### Type Mappings

#### Field Types (Reading)

| Schema Type | Aliases | .NET Type |
|-------------|---------|-----------|
| `string` | `nvarchar`, `memo` | `string` |
| `int` | `integer`, `number` | `int` |
| `bigint` | - | `long` |
| `decimal` | `money` | `decimal` |
| `float` | `double` | `double` |
| `bool` | `boolean` | `bool` |
| `datetime` | - | `DateTime` |
| `guid` | `uniqueidentifier` | `Guid` |
| `lookup` | `entityreference`, `customer`, `owner`, `partylist` | `EntityReference` |
| `optionset` | `optionsetvalue`, `picklist`, `state`, `status` | `OptionSetValue` |

#### Field Value Serialization (Writing)

| .NET Type | Format | Example |
|-----------|--------|---------|
| `string` | As-is | `value="Hello"` |
| `int` | Numeric string | `value="42"` |
| `long` | Numeric string | `value="9999999999"` |
| `decimal` | Invariant culture | `value="1000.50"` |
| `double` | Invariant culture | `value="3.14159"` |
| `bool` | "True"/"False" | `value="True"` |
| `DateTime` | ISO 8601 + 7 decimals | `value="2025-01-19T20:48:00.0000000Z"` |
| `Guid` | Standard format | `value="11111111-1111-1111-1111-111111111111"` |
| `EntityReference` | GUID + attributes | `value="guid" lookupentity="account"` |
| `OptionSetValue` | Integer | `value="100000000"` |
| `Money` | Invariant decimal | `value="1000.50"` |

### Reading Process

1. **Open ZIP archive** in read mode
2. **Read schema**: `data_schema.xml` (or fallback to `schema.xml`)
3. **Parse schema**: Build `MigrationSchema` from XML
4. **Read data**: `data.xml`
5. **Parse records**: For each entity, parse `<record>` elements
6. **Parse M2M**: For each entity, parse `<m2mrelationship>` elements
7. **Return** `MigrationData` with schema, entity data, and M2M data

### Writing Process

1. **Create ZIP archive** with optimal compression
2. **Write `[Content_Types].xml`**: CMT-required metadata
3. **Write `data_schema.xml`**: Serialized schema
4. **Write `data.xml`**: Serialized records and M2M relationships
5. **Close archive**

### Lifecycle

- **Reading**: Stateless; `ReadAsync` returns complete `MigrationData`
- **Writing**: Stateless; `WriteAsync` creates complete ZIP from `MigrationData`
- **Progress**: Reports phases during read/write operations

## Edge Cases

| Scenario | Behavior | Notes |
|----------|----------|-------|
| Missing schema file | Falls back to `schema.xml` | Supports legacy naming |
| Missing type attribute | Infers from `lookupentity` presence | Type = "lookup" if lookupentity exists |
| Polymorphic lookup | `lookupType` pipe-delimited | e.g., `account|contact` |
| Null field value | Field element omitted | Not serialized |
| Empty entity (no records) | `<records>` element empty | Valid state |
| Empty M2M | `<m2mrelationships>` omitted | Optional section |
| Boolean "1"/"0" | Accepted on read | "True"/"False" on write |
| Missing validity flags | Default to `true` | Backwards compatible |
| ImportExportXml wrapper | Handled | Supports `<ImportExportXml><entities>` |

## Error Handling

| Exception | Condition | Recovery |
|-----------|-----------|----------|
| `ArgumentNullException` | Null stream/path provided | Validate input |
| `InvalidOperationException` | ZIP entry not found | Ensure valid CMT archive |
| `XmlException` | Malformed XML | Fix source file |
| `FormatException` | Invalid value format | Check type/value match |

## Dependencies

- **Internal**:
  - `PPDS.Migration.Models` - Schema and data models
  - `PPDS.Migration.Progress` - `IProgressReporter`
- **External**:
  - `System.IO.Compression` - ZIP archive handling
  - `System.Xml.Linq` - XML parsing (XDocument)

## Configuration

The CMT format readers/writers have no configuration. Format behavior is defined by the CMT specification with PPDS extensions.

## Thread Safety

- **Readers/Writers**: Stateless, thread-safe for concurrent calls
- **Models**: Immutable after construction
- **Streams**: Not shared; each operation uses its own stream

All I/O operations are async with `CancellationToken` support.

## CMT Format Extensions

PPDS extends the standard CMT format with:

| Extension | Purpose |
|-----------|---------|
| `[Content_Types].xml` | Required for CMT tooling compatibility |
| `isValidForCreate` | Field validity for create operations |
| `isValidForUpdate` | Field validity for update operations |
| Pipe-delimited `lookupType` | Polymorphic lookup support |
| `m2mTargetEntityPrimaryKey` | Target entity primary key for M2M |

## Compatibility Notes

### Standard CMT Compatibility
- Reads files created by Microsoft CMT
- Writes files readable by Microsoft CMT (with extensions)
- Boolean values written as "True"/"False" (CMT uses 1/0 in some cases)

### Versioning
- Schema version attribute defaults to "1.0"
- No version-specific behavior differences
- Forward-compatible: unknown attributes ignored

## Related

- [Spec: Export Pipeline](../specs/03-migration/02-export-pipeline.md)
- [Spec: Import Pipeline](../specs/03-migration/03-import-pipeline.md)
- [Microsoft CMT Documentation](https://docs.microsoft.com/power-platform/admin/manage-configuration-data)

## Source Files

| File | Purpose |
|------|---------|
| `src/PPDS.Migration/Formats/ICmtSchemaReader.cs` | Schema reader interface |
| `src/PPDS.Migration/Formats/CmtSchemaReader.cs` | Schema reader implementation |
| `src/PPDS.Migration/Formats/ICmtDataReader.cs` | Data reader interface |
| `src/PPDS.Migration/Formats/CmtDataReader.cs` | Data reader implementation |
| `src/PPDS.Migration/Formats/ICmtSchemaWriter.cs` | Schema writer interface |
| `src/PPDS.Migration/Formats/CmtSchemaWriter.cs` | Schema writer implementation |
| `src/PPDS.Migration/Formats/ICmtDataWriter.cs` | Data writer interface |
| `src/PPDS.Migration/Formats/CmtDataWriter.cs` | Data writer implementation |
| `src/PPDS.Migration/Formats/UserMappingReader.cs` | User mapping XML reader |
| `src/PPDS.Migration/Models/MigrationSchema.cs` | Schema model |
| `src/PPDS.Migration/Models/EntitySchema.cs` | Entity schema model |
| `src/PPDS.Migration/Models/FieldSchema.cs` | Field schema model |
| `src/PPDS.Migration/Models/RelationshipSchema.cs` | Relationship schema model |
| `src/PPDS.Migration/Models/MigrationData.cs` | Export data model |
| `src/PPDS.Migration/Models/ManyToManyRelationshipData.cs` | M2M data model |
| `tests/PPDS.Migration.Tests/Formats/CmtSchemaReaderTests.cs` | Schema reader tests |
| `tests/PPDS.Migration.Tests/Formats/CmtDataReaderTests.cs` | Data reader tests |
| `tests/PPDS.Migration.Tests/Formats/CmtDataWriterTests.cs` | Data writer tests |
