# PPDS.Migration: User Mapping

## Overview

The User Mapping subsystem handles the remapping of user, team, and owner references during cross-environment data migration. It supports explicit user-to-user mappings, automatic fallback to the current user, and owner field stripping for scenarios where source users don't exist in the target environment. The system can generate mappings automatically by matching users across environments via Azure AD Object ID or domain name.

## Public API

### Interfaces

| Interface | Purpose |
|-----------|---------|
| `IUserMappingReader` | Reads user mapping XML files |
| `IUserMappingGenerator` | Generates mappings between environments |

### Classes

| Class | Purpose |
|-------|---------|
| `UserMappingCollection` | Container for mappings with fallback strategies |
| `UserMapping` | Single source→target user mapping |
| `UserMappingReader` | Parses user mapping XML files |
| `UserMappingGenerator` | Generates mappings by matching users |

### DTOs/Models

| Type | Purpose |
|------|---------|
| `UserMappingCollection` | Collection with DefaultUserId and UseCurrentUserAsDefault |
| `UserMapping` | Source/target user IDs and names |
| `UserMappingResult` | Generation results (matched, unmatched counts) |
| `UserMappingMatch` | A matched source→target user pair with match method |
| `UserMappingOptions` | Options for generation (e.g., include disabled users) |
| `UserInfo` | User details from Dataverse query |

## Behaviors

### Mapping Resolution Order

When remapping a user reference during import:

1. **Explicit mapping**: Check `UserMappings.Mappings[sourceId]`
2. **Default user**: If not found, use `DefaultUserId` if set
3. **Current user fallback**: If `UseCurrentUserAsDefault = true` and `CurrentUserId` set
4. **Original reference**: Return unchanged if no resolution available

### User Reference Detection

References considered "user references" for mapping:
- `systemuser` entity references
- `team` entity references

### Owner Field Handling

Owner fields subject to `StripOwnerFields` option:

| Field | Description |
|-------|-------------|
| `ownerid` | Primary owner |
| `createdby` | Created by user (audit) |
| `modifiedby` | Modified by user (audit) |
| `createdonbehalfby` | Delegation field |
| `modifiedonbehalfby` | Delegation field |
| `owninguser` | Owning user relationship |
| `owningteam` | Owning team relationship |
| `owningbusinessunit` | Owning business unit |

**When StripOwnerFields = true:**
- Owner fields skipped during import
- Dataverse auto-assigns current user as owner
- Useful when source users don't exist in target

**When StripOwnerFields = false (default):**
- Owner fields included and remapped via UserMappings
- Falls back to current user if mapping not found
- Preserves original ID if no fallback (may fail in target)

### Mapping Generation

The `UserMappingGenerator` matches users between environments:

**Matching Priority:**
1. **Azure AD Object ID** (most reliable for AAD-joined tenants)
2. **Domain Name** (case-insensitive UPN matching)

**Query Fields:**
- SystemUserId
- FullName
- DomainName
- InternalEMailAddress
- AzureActiveDirectoryObjectId
- IsDisabled (excludes disabled users by default)
- AccessMode

### Lifecycle

- **Reading**: `UserMappingReader.ReadAsync()` parses XML into `UserMappingCollection`
- **Generation**: `UserMappingGenerator.GenerateAsync()` queries both environments
- **Import**: `TieredImporter` applies mappings during Phase 1 field preparation
- **Fallback**: Current user resolved via `WhoAmI` request at import start

## XML Format

### User Mapping File

```xml
<?xml version="1.0" encoding="utf-8"?>
<mappings useCurrentUserAsDefault="true" defaultUserId="optional-guid">
  <mapping sourceId="source-user-guid"
           sourceName="user1@domain.com"
           targetId="target-user-guid"
           targetName="user2@domain.com" />
  <!-- Additional mappings -->
</mappings>
```

**Root Attributes:**
| Attribute | Required | Default | Description |
|-----------|----------|---------|-------------|
| `useCurrentUserAsDefault` | No | `true` | Enable current user fallback |
| `defaultUserId` | No | - | Default target user GUID |

**Mapping Attributes:**
| Attribute | Required | Description |
|-----------|----------|-------------|
| `sourceId` | Yes | Source environment user GUID |
| `targetId` | Yes | Target environment user GUID |
| `sourceName` | No | Source user display name (informational) |
| `targetName` | No | Target user display name (informational) |

### Generated File Comments

```xml
<!-- Generated: 2025-01-19T20:48:00.0000000Z -->
<!-- Source users: 150, Target users: 145 -->
<!-- Matched: 142 (AAD: 6, Domain: 136), Unmatched: 8 -->
```

## Edge Cases

| Scenario | Behavior | Notes |
|----------|----------|-------|
| Mapping not found, fallback enabled | Uses CurrentUserId | Logged at DEBUG level |
| Mapping not found, no fallback | Returns original reference | May fail in target |
| Invalid mapping in XML | Silently skipped | Logged at INFO level |
| File not found | Throws FileNotFoundException | Validate path before |
| Team reference | Treated as user-like | Remapped via same collection |
| team.isdefault | Forced to false | Prevents default team conflicts |
| Null UserMappings | No user remapping | Standard ID mapping only |
| Empty mappings, UseCurrentUserAsDefault=true | All users → current user | Useful for test environments |

## Error Handling

| Exception | Condition | Recovery |
|-----------|-----------|----------|
| `FileNotFoundException` | Mapping file not found | Validate path before import |
| `XmlException` | Malformed XML | Fix mapping file |
| `FormatException` | Invalid GUID in mapping | Correct GUID format |

Invalid individual mappings are skipped with logging rather than failing the entire load.

## Dependencies

- **Internal**:
  - `PPDS.Migration.Models` - Mapping data structures
  - `PPDS.Dataverse` - Connection pool for generation
- **External**:
  - `System.Xml.Linq` - XML parsing

## Configuration

### ImportOptions Integration

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `UserMappings` | UserMappingCollection | null | User mapping collection |
| `CurrentUserId` | Guid? | null | Fallback user (from WhoAmI) |
| `StripOwnerFields` | bool | false | Remove owner fields |

### Recommended Combinations

| Scenario | UserMappings | CurrentUserId | StripOwnerFields |
|----------|--------------|---------------|------------------|
| Cross-tenant, keep ownership | Yes | Yes | No |
| Cross-tenant, reassign all | Yes | Yes | Yes |
| Same tenant, audit preservation | Yes | No | No |
| Test env, ignore ownership | No | N/A | Yes |

## Thread Safety

- **`UserMappingCollection`**: Not thread-safe (uses standard Dictionary internally). Safe for concurrent reads after initialization.
- **`UserMappingReader`**: Stateless, thread-safe
- **`UserMappingGenerator`**: Stateless, thread-safe

Mappings are read once before import and then shared (read-only) across parallel entity imports.

## CLI Integration

### Generate User Mappings

```bash
ppds data users --source-env <source-url> --target-env <target-url> --output <mapping.xml>
ppds data users --source-profile <name> --target-profile <name> --output <mapping.xml>
ppds data users --analyze  # Report only, no file output
```

### Import with User Mappings

```bash
ppds data import --data <data.zip> \
  --user-mapping <mapping.xml> \
  --strip-owner-fields \
  --continue-on-error
```

## Import Processing Pipeline

### Phase 1 - Entity Import

1. For each entity record:
   - Iterate all attributes
   - Skip deferred fields (Phase 2)
   - Skip owner fields if `StripOwnerFields = true`
   - For EntityReference attributes:
     - If user reference: apply UserMappings with fallback
     - If other reference: apply standard IdMappingCollection
   - Add prepared attribute to bulk operation

### Phase 2/3 - Deferred Fields and M2M

- Standard IdMappingCollection remapping only
- User mapping NOT reapplied (already processed in Phase 1)

## Related

- [Spec: Import Pipeline](03-import-pipeline.md)
- [Spec: CMT Compatibility](05-cmt-compatibility.md)

## Source Files

| File | Purpose |
|------|---------|
| `src/PPDS.Migration/Models/UserMapping.cs` | UserMapping and UserMappingCollection models |
| `src/PPDS.Migration/Formats/UserMappingReader.cs` | IUserMappingReader interface and XML parser |
| `src/PPDS.Migration/UserMapping/UserMappingGenerator.cs` | IUserMappingGenerator interface and cross-environment matching, plus UserMappingResult, UserMappingMatch, UserMappingOptions, UserInfo types |
| `src/PPDS.Migration/Import/ImportOptions.cs` | UserMappings and StripOwnerFields options |
| `src/PPDS.Migration/Import/TieredImporter.cs` | RemapEntityReference and IsUserReference methods |
| `src/PPDS.Cli/Commands/Data/UsersCommand.cs` | CLI user mapping generation |
| `src/PPDS.Cli/Commands/Data/ImportCommand.cs` | CLI import with mappings |
| `tests/PPDS.Migration.Tests/Models/UserMappingTests.cs` | Collection unit tests |
| `tests/PPDS.Migration.Tests/Formats/UserMappingReaderTests.cs` | Reader unit tests |
