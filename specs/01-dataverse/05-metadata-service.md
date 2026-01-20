# PPDS.Dataverse: Metadata Service

## Overview

The Metadata Service provides access to Dataverse schema metadata for entity discovery, attribute exploration, relationship mapping, and option set retrieval. It wraps SDK metadata requests and transforms the responses into strongly-typed DTOs for consumption by CLI, TUI, MCP, and migration components.

## Public API

### Interfaces

| Interface | Purpose |
|-----------|---------|
| `IMetadataService` | Provides metadata retrieval operations |

### Classes

| Class | Purpose |
|-------|---------|
| `DataverseMetadataService` | Implementation using SDK metadata APIs |

### DTOs/Models

| Type | Purpose |
|------|---------|
| `EntitySummary` | Basic entity info for list views |
| `EntityMetadataDto` | Full entity metadata with all components |
| `AttributeMetadataDto` | Attribute details (type, constraints, options) |
| `RelationshipMetadataDto` | One-to-many/many-to-one relationship info |
| `ManyToManyRelationshipDto` | Many-to-many relationship with intersect entity |
| `EntityRelationshipsDto` | Grouped relationships for an entity |
| `EntityKeyDto` | Alternate key definition |
| `PrivilegeDto` | Entity privilege (CRUD operations) |
| `OptionSetSummary` | Basic option set info for list views |
| `OptionSetMetadataDto` | Full option set with values |
| `OptionValueDto` | Single option value with label/color |

## Behaviors

### Entity Operations

| Method | Description | SDK Request |
|--------|-------------|-------------|
| `GetEntitiesAsync` | List all entities (summary) | `RetrieveAllEntitiesRequest` |
| `GetEntityAsync` | Get full entity metadata | `RetrieveEntityRequest` |
| `GetAttributesAsync` | Get entity attributes | `RetrieveEntityRequest` (Attributes filter) |
| `GetRelationshipsAsync` | Get entity relationships | `RetrieveEntityRequest` (Relationships filter) |
| `GetKeysAsync` | Get alternate keys | `RetrieveEntityRequest` (Entity filter) |

### Option Set Operations

| Method | Description | SDK Request |
|--------|-------------|-------------|
| `GetGlobalOptionSetsAsync` | List global option sets | `RetrieveAllOptionSetsRequest` |
| `GetOptionSetAsync` | Get option set with values | `RetrieveOptionSetRequest` |

### Filtering

Both entity and option set list operations support wildcard filtering:

- No wildcard: Contains search (`foo` matches `foobar`, `bazfoo`)
- With wildcard: Anchored pattern (`foo*` matches `foobar`, not `bazfoo`)

### Lifecycle

- **Initialization**: Service receives connection pool via DI
- **Operation**: Each method acquires/releases connection from pool
- **Cleanup**: No persistent state; pool manages connection lifecycle

## Edge Cases

| Scenario | Behavior | Notes |
|----------|----------|-------|
| Entity not found | SDK throws exception | Propagates to caller |
| Option set not found | SDK throws exception | Propagates to caller |
| Empty filter | Returns all items | No filtering applied |
| Custom entities only | Filters `IsCustomEntity == true` | Excludes system entities |
| Intersect entities | Excluded from list | Internal N:N tables |
| Null labels | Returns empty string | Graceful handling |

## Error Handling

| Exception | Condition | Recovery |
|-----------|-----------|----------|
| `FaultException<OrganizationServiceFault>` | Entity/option set not found | Check logical name |
| `ArgumentException` | Empty/null logical name | Validation error |
| `PoolExhaustedException` | No connection available | Retry with backoff |

## Dependencies

- **Internal**:
  - `PPDS.Dataverse.Pooling.IDataverseConnectionPool` - Connection management
- **External**:
  - `Microsoft.Xrm.Sdk` (OrganizationRequest)
  - `Microsoft.Xrm.Sdk.Messages` (Metadata requests)
  - `Microsoft.Xrm.Sdk.Metadata` (Metadata types)
  - `Microsoft.Extensions.Logging.Abstractions`

## Configuration

No configuration required. The service uses sensible defaults:

- `RetrieveAsIfPublished = false` - Returns only published metadata
- Intersect entities filtered out of entity lists

## Thread Safety

- **DataverseMetadataService**: Thread-safe (stateless; each method acquires own connection)
- **DTOs**: Immutable records; thread-safe

## DTO Details

### EntityMetadataDto

Includes all metadata components:

| Property Group | Contents |
|---------------|----------|
| Identity | LogicalName, SchemaName, MetadataId, ObjectTypeCode |
| Display | DisplayName, PluralName, Description |
| Type Info | IsCustomEntity, IsManaged, OwnershipType |
| Capabilities | IsActivity, HasNotes, HasActivities, ChangeTrackingEnabled |
| Bulk Support | CanCreateMultiple, CanUpdateMultiple |
| Components | Attributes, OneToManyRelationships, ManyToOneRelationships, ManyToManyRelationships, Keys, Privileges |

### AttributeMetadataDto

Type-specific properties populated based on attribute type:

| Property | Applicable Types |
|----------|-----------------|
| `MaxLength` | String, Memo |
| `MinValue`, `MaxValue` | Integer, Decimal, Double, Money |
| `Precision` | Decimal, Double, Money |
| `Targets` | Lookup (polymorphic lookup lists multiple targets) |
| `OptionSetName`, `Options` | Picklist, MultiSelect, State, Status, Boolean |
| `DateTimeBehavior`, `Format` | DateTime |
| `AutoNumberFormat` | String (auto-number columns) |
| `SourceType` | All (0=Simple, 1=Calculated, 2=Rollup) |
| `AttributeOf` | Virtual attributes (parent attribute name) |

### RelationshipMetadataDto

Properties for 1:N and N:1 relationships:

| Property | Description |
|----------|-------------|
| `ReferencedEntity` | The "one" side entity |
| `ReferencingEntity` | The "many" side entity |
| `ReferencedAttribute` | Primary key attribute |
| `ReferencingAttribute` | Foreign key (lookup) attribute |
| `CascadeAssign/Delete/Merge/Reparent/Share/Unshare` | Cascade behavior configuration |
| `IsHierarchical` | Self-referential hierarchy |

### ManyToManyRelationshipDto

Additional properties for N:N relationships:

| Property | Description |
|----------|-------------|
| `IntersectEntityName` | The hidden junction table |
| `Entity1LogicalName`, `Entity2LogicalName` | Participating entities |
| `Entity1IntersectAttribute`, `Entity2IntersectAttribute` | Junction table columns |
| `IsReflexive` | Entity1 == Entity2 (self-referential) |

## Integration Points

### CLI Commands

- `ppds entities` - Lists entities using `GetEntitiesAsync`
- `ppds entity <name>` - Shows entity details using `GetEntityAsync`
- `ppds attributes <entity>` - Lists attributes using `GetAttributesAsync`

### TUI Views

- Entity browser uses `GetEntitiesAsync` with filtering
- Attribute explorer uses `GetAttributesAsync` with type filtering
- Relationship viewer uses `GetRelationshipsAsync`

### MCP Tools

- `dataverse-describe-entity` uses `GetEntityAsync`
- `dataverse-list-entities` uses `GetEntitiesAsync`
- `dataverse-list-optionsets` uses `GetGlobalOptionSetsAsync`

### Migration

- Dependency analysis uses relationships to build dependency graph
- Import uses attribute metadata for type conversion
- Export uses attribute metadata for serialization

## Related

- [Connection Pooling spec](./01-connection-pooling.md) - Provides connections
- [SQL Transpiler spec](./04-sql-transpiler.md) - May use metadata for validation
- [Query Executor spec](./06-query-executor.md) - Uses metadata for type mapping

## Source Files

| File | Purpose |
|------|---------|
| `src/PPDS.Dataverse/Metadata/IMetadataService.cs` | Service interface |
| `src/PPDS.Dataverse/Metadata/DataverseMetadataService.cs` | Implementation |
| `src/PPDS.Dataverse/Metadata/Models/EntitySummary.cs` | Entity list DTO |
| `src/PPDS.Dataverse/Metadata/Models/EntityMetadataDto.cs` | Full entity DTO |
| `src/PPDS.Dataverse/Metadata/Models/AttributeMetadataDto.cs` | Attribute DTO |
| `src/PPDS.Dataverse/Metadata/Models/RelationshipMetadataDto.cs` | 1:N/N:1 relationship DTO |
| `src/PPDS.Dataverse/Metadata/Models/ManyToManyRelationshipDto.cs` | N:N relationship DTO |
| `src/PPDS.Dataverse/Metadata/Models/EntityRelationshipsDto.cs` | Grouped relationships DTO |
| `src/PPDS.Dataverse/Metadata/Models/EntityKeyDto.cs` | Alternate key DTO |
| `src/PPDS.Dataverse/Metadata/Models/PrivilegeDto.cs` | Privilege DTO |
| `src/PPDS.Dataverse/Metadata/Models/OptionSetSummary.cs` | Option set list DTO |
| `src/PPDS.Dataverse/Metadata/Models/OptionSetMetadataDto.cs` | Full option set DTO |
| `src/PPDS.Dataverse/Metadata/Models/OptionValueDto.cs` | Option value DTO |
