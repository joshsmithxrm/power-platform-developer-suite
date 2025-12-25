# PPDS Migration CLI v1 Enhancements Specification

**Status:** Implementation in Progress
**Branch:** `feature/v2-alpha`
**Date:** 2025-12-25

---

## Overview

This specification covers CLI enhancements for the v1 release, building on the System.CommandLine 2.0.1 migration.

### Goals

1. **Better validation** - Fail fast with clear errors before execution
2. **Improved UX** - Tab completions, response files, directives
3. **Flexible authentication** - Support multiple auth methods for different scenarios
4. **Security** - Never accept secrets as CLI arguments

---

## 1. Validators

### 1.1 File Validators

Use built-in `AcceptExistingOnly()` for input files and `AcceptLegalFileNamesOnly()` for output files.

| Option | Validator | Commands |
|--------|-----------|----------|
| `--schema` | `AcceptExistingOnly()` | export, migrate, analyze |
| `--data` | `AcceptExistingOnly()` | import |
| `--config` | `AcceptExistingOnly()` | all |
| `--user-mapping` | `AcceptExistingOnly()` | import |
| `--output` | `AcceptLegalFileNamesOnly()` | export, schema generate |

**Before (handler validation):**
```csharp
command.SetAction(async (parseResult, token) =>
{
    var schema = parseResult.GetValue(schemaOption)!;
    if (!schema.Exists)
    {
        ConsoleOutput.WriteError($"Schema file not found: {schema.FullName}", json);
        return ExitCodes.InvalidArguments;
    }
    // ...
});
```

**After (declarative validation):**
```csharp
var schemaOption = new Option<FileInfo>("--schema", "-s")
{
    Description = "Path to schema.xml file",
    Required = true
}.AcceptExistingOnly();

// Handler no longer needs existence check - validation happens before handler runs
```

### 1.2 Numeric Validators

Custom validators for options with constraints.

| Option | Constraint | Commands |
|--------|------------|----------|
| `--parallel` | Must be ≥ 1 | export |
| `--page-size` | Must be ≥ 1, ≤ 5000 | export |

**Implementation:**
```csharp
var parallelOption = new Option<int>("--parallel")
{
    Description = "Degree of parallelism for concurrent entity exports",
    DefaultValueFactory = _ => Environment.ProcessorCount * 2
};
parallelOption.Validators.Add(result =>
{
    var value = result.GetValue(parallelOption);
    if (value < 1)
        result.AddError("--parallel must be at least 1");
});
```

---

## 2. Tab Completions

### 2.1 Environment Name Completion

Dynamic completions for `--env` based on configuration.

```csharp
var envOption = new Option<string>("--env")
{
    Description = "Environment name from configuration",
    Required = true
};
envOption.CompletionSources.Add(ctx =>
{
    try
    {
        var config = ConfigurationHelper.Build(null, null);
        return ConfigurationHelper.GetEnvironmentNames(config)
            .Select(name => new CompletionItem(name));
    }
    catch
    {
        return Enumerable.Empty<CompletionItem>();
    }
});
```

**User experience:**
```bash
ppds-migrate export --env <TAB>
# Shows: Dev  QA  Prod
```

---

## 3. Authentication Architecture

### 3.1 Auth Modes

| Mode | Flag | Description | Use Case |
|------|------|-------------|----------|
| `config` | `--auth config` (default) | appsettings.json + User Secrets | Development |
| `env` | `--auth env` | Environment variables only | CI/CD, containers |
| `interactive` | `--auth interactive` | Device code flow (browser) | Humans, first-time setup |
| `managed` | `--auth managed` | Azure Managed Identity | Azure-hosted production |

### 3.2 Resolution Order

When `--auth` is not specified (auto-detect):

```
1. Check for DATAVERSE__URL environment variable
   └── If found: use environment variables (ClientCredentials)

2. Check for appsettings.json + User Secrets
   └── If found: use configuration (ClientCredentials)

3. Fail with helpful error message
```

### 3.3 Environment Variable Schema

| Variable | Description | Required |
|----------|-------------|----------|
| `DATAVERSE__URL` | Environment URL (e.g., https://org.crm.dynamics.com) | Yes |
| `DATAVERSE__CLIENTID` | Azure AD Application (client) ID | For ClientCredentials |
| `DATAVERSE__CLIENTSECRET` | Azure AD Client Secret | For ClientCredentials |
| `DATAVERSE__TENANTID` | Azure AD Tenant ID | Optional (auto-discovered) |

**Alternative prefix:** `PPDS__DATAVERSE__*` (for namespacing in complex environments)

### 3.4 Interactive Auth (Device Code Flow)

```bash
ppds-migrate export --auth interactive --env Dev --schema schema.xml --output data.zip
```

**Flow:**
1. CLI displays: "To sign in, use a web browser to open https://microsoft.com/devicelogin and enter the code XXXXXXXX"
2. User opens browser, enters code, authenticates
3. CLI receives token, proceeds with operation
4. Token cached for future use (until expiry)

**Implementation:**
```csharp
// Using MSAL or Dataverse Client's built-in support
var client = new ServiceClient(
    new Uri(url),
    authType: AuthenticationType.OAuth,
    promptBehavior: PromptBehavior.Auto);
```

### 3.5 Managed Identity

```bash
ppds-migrate export --auth managed --env Prod --schema schema.xml --output data.zip
```

**Implementation:**
```csharp
// Using Azure.Identity
var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    ExcludeInteractiveBrowserCredential = true,
    ExcludeVisualStudioCredential = true,
    // Only use managed identity
    ExcludeManagedIdentityCredential = false
});

var client = new ServiceClient(
    instanceUrl: new Uri(url),
    tokenProviderFunction: async (url) =>
    {
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(new[] { $"{url}/.default" }));
        return token.Token;
    });
```

**Required package:** `Azure.Identity`

### 3.6 Security Rules

| Rule | Enforcement |
|------|-------------|
| Never accept `--client-secret` as CLI argument | Not implemented as option |
| Never accept `--connection-string` as CLI argument | Not implemented as option |
| Clear sensitive env vars after use (PowerShell) | Documented best practice |
| Token caching for interactive auth | Use MSAL cache |

---

## 4. CLI Option Changes

### 4.1 New Global Option

```csharp
public static readonly Option<AuthMode> AuthOption = new("--auth")
{
    Description = "Authentication mode: config (default), env, interactive, managed"
};

public enum AuthMode
{
    Config,      // appsettings.json + User Secrets
    Env,         // Environment variables
    Interactive, // Device code flow
    Managed      // Azure Managed Identity
}
```

### 4.2 Updated Help Output

```
Description:
  PPDS Migration CLI - High-performance Dataverse data migration tool

Usage:
  ppds-migrate [command] [options]

Options:
  --auth <mode>          Authentication mode: config, env, interactive, managed [default: config]
  --secrets-id <id>      User Secrets ID for cross-process sharing (used with --auth config)
  -?, -h, --help         Show help and usage information
  --version              Show version information

Commands:
  export   Export data from Dataverse to a ZIP file
  import   Import data from a ZIP file into Dataverse
  analyze  Analyze schema and display dependency graph
  migrate  Migrate data between Dataverse environments
  schema   Generate and manage migration schemas
  config   Configuration management and diagnostics
```

---

## 5. Documentation Updates

### 5.1 Response Files

Document in README:
```markdown
## Response Files

Store frequently-used options in a file:

```
# export-dev.rsp
export
--env
Dev
--schema
schema.xml
--output
data.zip
```

Run with:
```bash
ppds-migrate @export-dev.rsp
```
```

### 5.2 Directives

Document in README:
```markdown
## Debugging

### Parse Diagram
See how your command is parsed:
```bash
ppds-migrate [diagram] export --env Dev --schema schema.xml
```

### Suggest Commands
Find commands by partial name:
```bash
ppds-migrate [suggest] exp
# Output: export
```
```

### 5.3 Authentication Matrix

Document in README:
```markdown
## Authentication

| Scenario | Recommended | Command |
|----------|-------------|---------|
| First-time / human | Interactive | `--auth interactive` |
| Development | Config + Secrets | `--secrets-id <id>` |
| CI/CD pipeline | Environment vars | Set `DATAVERSE__*` vars |
| Azure hosted | Managed Identity | `--auth managed` |
```

---

## 6. Dependencies

### New Packages

| Package | Version | Purpose |
|---------|---------|---------|
| `Azure.Identity` | Latest stable | Managed Identity, DefaultAzureCredential |

### Existing Packages (no changes)

- `Microsoft.PowerPlatform.Dataverse.Client` - already supports TokenCredential
- `System.CommandLine` - already updated to 2.0.1

---

## 7. Implementation Order

1. **Validators** - AcceptExistingOnly, custom numeric validators
2. **Tab Completions** - Environment name completion
3. **Environment Variable Auth** - DATAVERSE__* support
4. **Auth Infrastructure** - --auth option, AuthMode enum, resolution logic
5. **Interactive Auth** - Device code flow
6. **Managed Identity** - Azure.Identity integration
7. **Documentation** - README updates, help text

---

## 8. Testing Strategy

### Unit Tests

- Validator behavior (file exists, numeric ranges)
- Auth resolution logic (env vars → config → error)
- Environment variable parsing

### Integration Tests

- Interactive auth: Manual testing with real Azure AD
- Managed Identity: Test in Azure VM or use emulator
- Config auth: Existing tests cover this

### Manual Testing Checklist

- [ ] `ppds-migrate --help` shows --auth option
- [ ] `ppds-migrate export --auth interactive` opens browser
- [ ] `ppds-migrate export --auth managed` works in Azure VM
- [ ] Tab completion shows environment names
- [ ] Invalid file paths show clear error before handler runs
- [ ] `@response.rsp` file works
- [ ] `[diagram]` directive works

---

## 9. Breaking Changes

None. All changes are additive:
- New `--auth` option (default maintains current behavior)
- Validators provide earlier/clearer errors but same outcome
- Completions are UX enhancement only
