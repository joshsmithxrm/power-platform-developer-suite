# Power Platform Developer Suite

A comprehensive VS Code extension for Power Platform development and administration — your complete toolkit for Dynamics 365, Dataverse, and Power Platform solutions.

> **v1.0.0 — Stable.** This is the stable release of the rebuilt extension. Profile, environment, solutions, notebook, and viewer surfaces (plugin traces, connection references, environment variables, metadata browser, web resources, import jobs) are all included.

## Prerequisites

- [PPDS CLI](https://github.com/joshsmithxrm/power-platform-developer-suite) installed and on your PATH
- At least one authentication profile configured (`ppds auth create`)

## Features

### Profile Management

Create, select, rename, and delete authentication profiles directly from the VS Code sidebar. View profile details including identity, authentication method, cloud, and connected environment.

### Environment Discovery

Browse and select Dataverse environments associated with your profile. The active environment is shown in the VS Code status bar.

### Solutions Browser

Explore solutions in your connected environment with expandable component groups. Toggle visibility of managed solutions.

### Dataverse Notebooks (.ppdsnb)

Query Dataverse using an SSMS-like notebook experience:

- **SQL and FetchXML** — write queries in either language, toggle per cell
- **IntelliSense** — autocomplete for tables, columns, and FetchXML elements
- **Results** — inline display with virtual scrolling for large datasets
- **Export** — save results as CSV or JSON
- **History** — query history is saved automatically

### Data Explorer

Quick ad-hoc query panel for one-off Dataverse queries without creating a notebook.

## Quick Start

1. Install the extension from the VS Code Marketplace
2. Open the PPDS sidebar (activity bar icon)
3. Create or select an authentication profile
4. Select an environment
5. Create a new notebook (`Ctrl+Shift+P` → "PPDS: New Notebook")
6. Write a SQL query and execute the cell

## Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `ppds.queryDefaultTop` | 100 | Default TOP value for SQL queries (1-5000) |
| `ppds.autoStartDaemon` | true | Auto-start the ppds daemon on activation |
| `ppds.showEnvironmentInStatusBar` | true | Show active environment in status bar |

## Feedback

- [Report issues](https://github.com/joshsmithxrm/power-platform-developer-suite/issues)
- [Documentation](https://github.com/joshsmithxrm/power-platform-developer-suite)

## License

[MIT](LICENSE)
