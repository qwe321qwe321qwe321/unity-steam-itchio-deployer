# Changelog

All notable changes to this project will be documented in this file.

The format is based on Keep a Changelog, and version numbers follow the existing `v0.x.y` tag style used in this repository.

## [v0.1.3] - 2026-05-03

### Fixed

- Fixed build configuration handling to better align the build pipeline with the selected settings.
- Fixed WebGL build output path handling and related build validation checks.

## [v0.1.2] - 2026-05-03

### Changed

- Replaced shared workflow settings stored in `EditorPrefs` with a dedicated `BuildDeployConfig` asset.
- Scoped saved editor preferences to the project and separated output logs per platform.
- Renamed parts of the internal hierarchy for a cleaner project structure.

## [v0.1.1] - 2026-05-03

### Added

- Added itch.io deployment support alongside the existing Steam workflow.
- Added multi-target deployment from one Editor window so the same build can be uploaded to Steam and itch.io.
- Added encrypted storage support for deployment credentials in `EditorPrefs`.

### Changed

- Refined the deployment window layout and overall workflow for multi-platform use.

## [v0.1.0] - 2026-04-11

### Added

- Initial Unity Editor deployment tool for building and uploading Steam releases.
- Added build output path selection and the ability to run build and upload as separate steps.
- Added Unity build profile selection support.
- Added Steam Guard flow improvements and a Steam login test action.
- Added `SetLive` toggle support and clearer error-code reporting.
- Added a one-click SteamCMD download and install helper.

### Changed

- Improved editor window layout and setup documentation.
