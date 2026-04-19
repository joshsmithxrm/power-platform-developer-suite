# Changelog - PPDS.Plugins

All notable changes to PPDS.Plugins will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [3.0.0] - 2026-04-18

Promotes the `2.1.0-beta.1` annotation surface to stable, aligned with the broader PPDS v1.0.0 launch. Source-compatible with 2.x — all attribute additions are additive. No framework target change — still `net462` (Dataverse plugin sandbox requirement).

### Changed

- **BREAKING:** Strong name signing key rotated. The assembly's public key token changed from `87a4b0dac59374c6` to `0b0809faff135778`, which changes the assembly identity. Consumers with strong-name references, binding redirects, or `InternalsVisibleTo` declarations targeting the old token must rebuild against the new identity. No source changes required.

### Added

- **`CustomApiAttribute`** — Code-first registration for Dataverse Custom APIs (name, binding, allowed customization, processing step type, plugin type).
- **`CustomApiParameterAttribute`** — Defines Custom API request/response parameters (multiple may be applied per class).
- **`PluginDeployment` enum** — `ServerOnly`, `Offline`, `Both` for `PluginStepAttribute.Deployment`.
- **`PluginInvocationSource` enum** — `Parent`, `Child` for distinguishing root vs cascaded invocation contexts.
- **`ApiBindingType` enum** — `Global`, `Entity`, `EntityCollection` for Custom API binding scope.
- **`ApiParameterType` enum** — Twelve types (`Boolean`, `DateTime`, `Decimal`, `Entity`, `EntityCollection`, `EntityReference`, `Float`, `Integer`, `Money`, `Picklist`, `String`, `StringArray`, `Guid`).
- **`ParameterDirection` enum** — `Input`, `Output`.
- **`ApiProcessingStepType` enum** — `None`, `AsyncOnly`, `SyncAndAsync`.
- **New `PluginStepAttribute` properties** — `Deployment` (default `ServerOnly`), `RunAsUser` (impersonation user), `CanBeBypassed` (default `true`), `CanUseReadOnlyConnection` (default `false`), `InvocationSource` (default `Parent`).
- **New `PluginImageAttribute` properties** — `Description` for documenting image purpose; `MessagePropertyName` to override the auto-inferred message property.

## [2.0.0] - 2025-12-31

### Added

- `Description` property to `PluginStepAttribute` for documenting step purpose
- `AsyncAutoDelete` property to `PluginStepAttribute` for auto-deleting async job records on success

### Removed

- **BREAKING:** `SecureConfiguration` property removed from `PluginStepAttribute`
  - Secure configuration contains secrets that should never be committed to source control
  - Use environment variables, Azure Key Vault, or Dataverse secure configuration via Plugin Registration Tool instead

## [1.1.1] - 2025-12-29

### Changed

- Added MinVer for automatic version management from git tags
- No functional changes

## [1.1.0] - 2025-12-16

### Added

- Added `SecureConfiguration` property to `PluginStepAttribute` for secure plugin settings

### Changed

- Updated GitHub Actions dependencies (checkout v6, setup-dotnet v5, upload-artifact v6)
- Updated target frameworks: simplified to `net462` only (Dataverse plugin sandbox requirement)
  - Removed net6.0 and net8.0 since plugins must target net462 for Dataverse compatibility

## [1.0.0] - 2025-12-15

### Added

- `PluginStepAttribute` for declarative plugin step registration
  - `Message`, `EntityLogicalName`, `Stage` (required)
  - `Mode`, `FilteringAttributes`, `ExecutionOrder` (optional)
  - `UnsecureConfiguration` for plugin settings
  - `StepId` for multi-step plugins
- `PluginImageAttribute` for defining pre/post images
  - `ImageType`, `Name` (required)
  - `Attributes`, `EntityAlias`, `StepId` (optional)
- `PluginStage` enum (`PreValidation`, `PreOperation`, `PostOperation`)
- `PluginMode` enum (`Synchronous`, `Asynchronous`)
- `PluginImageType` enum (`PreImage`, `PostImage`, `Both`)
- Target framework: `net462` (Dataverse plugin sandbox requirement)
- Strong name signing for Dataverse compatibility
- Full XML documentation
- Comprehensive unit test suite

[Unreleased]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Plugins-v3.0.0...HEAD
[3.0.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Plugins-v2.0.0...Plugins-v3.0.0
[2.0.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Plugins-v1.1.1...Plugins-v2.0.0
[1.1.1]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Plugins-v1.1.0...Plugins-v1.1.1
[1.1.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/compare/Plugins-v1.0.0...Plugins-v1.1.0
[1.0.0]: https://github.com/joshsmithxrm/power-platform-developer-suite/releases/tag/Plugins-v1.0.0
