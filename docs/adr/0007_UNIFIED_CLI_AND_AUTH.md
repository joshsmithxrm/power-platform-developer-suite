# ADR-0007: Unified CLI and Shared Authentication

**Status:** Accepted
**Date:** 2025-01-27
**Authors:** Josh, Claude

## Context

The PPDS SDK currently includes `PPDS.Migration.Cli` as a standalone CLI tool for data migration. As the ecosystem grows, we anticipate additional CLI functionality:

- Plugin registration management
- Solution operations
- Environment administration
- Additional data operations

Shipping separate CLIs per feature would create fragmentation:
- Multiple tools to install and update
- Inconsistent authentication across tools
- No shared infrastructure for common operations
- Poor discoverability of capabilities

Additionally, the current CLI's authentication model (per-command `--auth` flags) doesn't support:
- Stored credential profiles
- Environment discovery via Global Discovery Service
- Connection pooling with multiple credentials (our unique high-throughput feature)

## Decision

### 1. Unified CLI

Consolidate all CLI functionality into a single `ppds` tool with subcommand groups:

```
ppds
├── auth      Authentication profile management
├── env       Environment discovery and selection
├── data      Data operations (export, import, copy, analyze)
├── schema    Schema generation and entity listing
└── users     User mapping operations
```

**Rationale:**
- Industry standard pattern (PAC CLI, Azure CLI, AWS CLI, GitHub CLI)
- Single installation point for users
- Shared infrastructure (auth, output formatting, error handling)
- Better discoverability via `ppds --help`
- Consistent UX across all commands

### 2. PAC CLI-Compatible Authentication Model

Adopt PAC CLI's two-layer authentication model:

```
Auth Profile (WHO)          Environment Selection (WHERE)
├── Credentials             ├── Selected via `env select`
├── Cloud                   ├── Or `--environment` at creation
└── Tenant                  └── Stored in profile
```

Key behaviors (matching PAC):
- Profiles can be named or unnamed (referenced by index)
- First profile auto-selected as active
- Environment is optional ("universal" profiles)
- `env select` binds environment to current profile
- `env list` queries Global Discovery Service live
- No auto-select of environment (explicit required)

**Rationale:**
- PAC CLI users (our target audience) have muscle memory for this model
- Decoupling auth from environment allows credential reuse
- Explicit environment selection prevents accidental operations

### 3. Shared Authentication Package (PPDS.Auth)

Extract authentication into a shared package:

```
PPDS.Auth/
├── Credentials/      Credential providers for all auth methods
├── Discovery/        Global Discovery Service client
├── Profiles/         Profile storage and management
└── Cloud/            Cloud environment configuration
```

**Rationale:**
- Future CLIs can share authentication infrastructure
- Consistent credential handling across all tools
- Centralized profile storage
- Enables connection pooling with multiple profiles

### 4. Connection Pooling Support

Support multiple `--profile` flags for high-throughput operations:

```bash
ppds data import --profile app1,app2,app3 --environment "Prod" --data data.zip
```

When multiple profiles are specified:
- All must resolve to the same environment
- `--environment` overrides profile environments
- Each profile becomes an `IConnectionSource` for the pool
- Pool distributes load and handles throttle recovery

**Rationale:**
- Key differentiator for PPDS (PAC CLI doesn't support this)
- Multiplies API quota (6,000 requests/5min per Application User)
- Leverages existing `DataverseConnectionPool` architecture (ADR-0007)

## Consequences

### Positive

- **Unified experience** - One tool to learn, install, and update
- **PAC compatibility** - Familiar model for Power Platform developers
- **Reusable infrastructure** - Auth package usable by future tools
- **High throughput** - Connection pooling for large migrations
- **Better discoverability** - All capabilities visible via help

### Negative

- **Breaking change** - Existing `ppds-migrate` users must migrate
- **Larger binary** - All functionality in one tool (mitigated by trimming)
- **Development effort** - ~3 weeks to implement fully

### Neutral

- **Package structure change** - `PPDS.Migration.Cli` → `PPDS.Cli` + `PPDS.Auth`

## Alternatives Considered

### A. Separate CLIs per Feature

```bash
ppds-migrate export ...
ppds-plugin register ...
ppds-solution export ...
```

**Rejected because:**
- Poor discoverability
- Duplicated auth infrastructure
- Version compatibility issues
- Not industry standard

### B. Single CLI without PAC-Compatible Auth

Keep unified CLI but use simpler auth model (per-command flags).

**Rejected because:**
- No stored profiles (re-authenticate every time)
- No environment discovery
- Can't support pooling elegantly
- Poor CI/CD experience

### C. Adopt PAC CLI Directly

Tell users to use PAC CLI for auth, our tool for data.

**Rejected because:**
- Complex user experience
- Can't access PAC's stored credentials
- Doesn't support our pooling model
- Dependency on external tool

## Implementation

See [UNIFIED_CLI_SPEC.md](../specs/UNIFIED_CLI_SPEC.md) for full specification.

### Phase 1: Foundation
- PPDS.Auth package with profile storage
- Device Code and Client Secret credentials
- Basic auth commands (create, list, who)

### Phase 2: Environment Discovery
- Global Discovery Service client
- env commands (list, select, who)
- Environment resolution

### Phase 3: CLI Restructure
- Create PPDS.Cli package
- Port existing commands to new structure
- Add remaining auth commands

### Phase 4: Additional Auth Methods
- Certificate, Managed Identity, OIDC
- All remaining credential providers

### Phase 5: Pooling Integration
- Multiple `--profile` support
- Environment validation

## References

- [UNIFIED_CLI_SPEC.md](../specs/UNIFIED_CLI_SPEC.md) - Full specification
- [ADR-0007](0007_CONNECTION_SOURCE_ABSTRACTION.md) - Connection source abstraction
- [PAC CLI Documentation](https://learn.microsoft.com/en-us/power-platform/developer/cli/introduction)
- [Azure CLI Design Guidelines](https://github.com/Azure/azure-cli/blob/dev/doc/authoring_command_modules/authoring_commands.md)
