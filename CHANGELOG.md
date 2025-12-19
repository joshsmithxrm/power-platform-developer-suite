# Changelog

All notable changes to PPDS.Plugins will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- Updated target frameworks: dropped `net6.0` (out of support), added `net10.0` (current LTS)
  - Now targets: `net462`, `net8.0`, `net10.0`

## [1.1.0] - 2025-12-16

### Added

- Added `SecureConfiguration` property to `PluginStepAttribute` for secure plugin settings

### Changed

- Updated GitHub Actions dependencies (checkout v6, setup-dotnet v5, upload-artifact v6)

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
- Multi-targeting: `net462`, `net6.0`, `net8.0`
- Strong name signing for Dataverse compatibility
- Full XML documentation
- GitHub Actions workflows for build and NuGet publishing
- Comprehensive unit test suite

[Unreleased]: https://github.com/joshsmithxrm/ppds-sdk/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/joshsmithxrm/ppds-sdk/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/joshsmithxrm/ppds-sdk/releases/tag/v1.0.0
