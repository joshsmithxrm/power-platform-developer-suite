---
description: Show PPDS CLI quick reference
---

# PPDS CLI Commands

## Authentication

| Command | Description |
|---------|-------------|
| `ppds auth create` | Create authentication profile |
| `ppds auth list` | List saved profiles |
| `ppds auth select` | Set active profile |
| `ppds auth who` | Show current identity |
| `ppds auth delete` | Remove a profile |

## Environment

| Command | Description |
|---------|-------------|
| `ppds env list` | List available environments |
| `ppds env select` | Select active environment |
| `ppds env who` | Show environment details |

## Data Operations

| Command | Description |
|---------|-------------|
| `ppds data schema` | Generate schema from Dataverse |
| `ppds data export` | Export data to zip |
| `ppds data import` | Import data from zip |
| `ppds data load` | Load CSV data into entity |
| `ppds data copy` | Copy data between environments |
| `ppds data users` | Generate user mapping file |

## Plugins

| Command | Description |
|---------|-------------|
| `ppds plugins extract` | Extract registrations from assembly |
| `ppds plugins deploy` | Deploy to Dataverse |
| `ppds plugins diff` | Compare config vs environment |
| `ppds plugins list` | List registered plugins |
| `ppds plugins clean` | Remove orphaned registrations |

## Querying

| Command | Description |
|---------|-------------|
| `ppds query sql` | Execute SQL query |
| `ppds query fetch` | Execute FetchXML |

## Metadata

| Command | Description |
|---------|-------------|
| `ppds metadata entities` | List entities |
| `ppds metadata entity <name>` | Get entity details |
| `ppds metadata attributes <entity>` | List attributes |
| `ppds metadata relationships <entity>` | List relationships |
| `ppds metadata keys <entity>` | List alternate keys |
| `ppds metadata optionsets` | List global option sets |
| `ppds metadata optionset <name>` | Get option set values |

## Global Options

| Option | Description |
|--------|-------------|
| `-f json` | JSON output format |
| `-f csv` | CSV output format (query only) |
| `-v` / `--verbose` | Verbose output |
| `--debug` | Debug output |
| `-q` / `--quiet` | Suppress status messages |
| `--dry-run` | Preview without applying |
