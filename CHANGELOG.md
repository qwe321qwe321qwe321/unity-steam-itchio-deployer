# Changelog

All notable changes to this project will be documented in this file.

The format is based on Keep a Changelog, and version numbers follow the existing `v0.x.y` tag style used in this repository.

## [v0.1.17] - 2026-08-08

### Changed

- Extracted the engine-agnostic parts of the deploy logic (steamcmd/butler argument building, VDF script rendering, CLI output classification, macro resolution, git SHA lookup, steamcmd exit code descriptions, and credential key-derivation hashing) into a shared [`steam-itchio-deploy-core`](https://github.com/qwe321qwe321qwe321/steam-itchio-deploy-core) library, added here as a git submodule under `Editor/SteamItchIoDeployer/ThirdParty/`. The same code is now also used by the Godot version of this tool, so fixes to shared behavior (e.g. Steam Guard prompt detection) only need to be made once. No behavior change is intended; `steamcmd`/`butler` arguments are now properly quoted when they contain whitespace, fixing a latent bug where a Steam password containing a space would have been split into multiple arguments.

## [v0.1.16] - 2026-08-04

### Fixed

- Builds now save any dirty open scenes before calling `BuildPipeline.BuildPlayer`, so the "Scene(s) have been modified, save?" dialog no longer pops up and stalls an unattended batch build.

## [v0.1.14] - 2026-07-30

### Fixed

- Batch build-only and build-and-upload operations now confirm all non-empty or shared build output paths before the batch starts, instead of interrupting the operation with overwrite warnings between items.

## [v0.1.13] - 2026-07-29

### Fixed

- Unity 6 now activates the selected `BuildProfile` itself instead of relying only on the legacy `SwitchActiveBuildTarget` API, which can leave the previous profile's platform active. Target switching has a one-time recovery attempt and a 120-second timeout instead of waiting forever.
- The non-empty build output confirmation now appears before any platform/profile switch. Its accepted state survives the switch's domain reload, so the resumed build does not prompt twice.

## [v0.1.12] - 2026-07-29

### Fixed

- Unity 6 Build Profile builds now switch `EditorUserBuildSettings.activeBuildTarget` before running the player build, then resume only after platform reimport and script compilation finish. This prevents Addressables from generating a Windows player with a macOS catalog (or vice versa).
- Successful builds are now checked for mismatched Addressables `settings.json` and `catalog.bin` platform data before replacing the previous build or starting either a Steam or itch.io upload. The SteamCMD and itch.io butler launch paths repeat the check, so upload-only, batch upload-only, and retry flows cannot bypass it. Rejected output is retained in the `_steamdeployer_tmp` directory for diagnosis.

## [v0.1.9] - 2026-05-22

### Changed

- Replaced the fixed batch upload cooldown with a configurable `BuildDeployConfig.UploadCooldownSeconds` setting, defaulting to `120` seconds.
- Clarified in the setting tooltip and deploy window UI that the cooldown exists because Steam may temporarily reject a second depot upload submitted too soon after a successful upload.

### Fixed

- Fixed consecutive batch uploads to Steam failing with a manifest timeout error when configs were uploaded back-to-back. A cooldown is now enforced between uploads in batch mode to avoid Valve-side rate limiting.

### Added

- The deploy window now shows a live countdown during the inter-upload cooldown so the remaining wait time is always visible.

## [v0.1.8] - 2026-05-16

### Added

- Added batch build and deploy support: multiple `BuildDeployConfig` assets can be queued and processed sequentially with a single button press.
- Added batch build-only and batch upload-only modes in addition to the combined batch build-and-upload flow.

### Changed

- SteamCMD and butler executable paths no longer require the file extension to be specified; the correct extension is appended automatically at runtime per platform.

## [v0.1.7] - 2026-05-15

### Fixed

- Fixed macOS build output detection to recognise `.app` bundles (directories) in addition to `.app` files, so the deploy window correctly reports that a build exists after a macOS build.
- Fixed SteamCMD auto-download on macOS: the installer now downloads the correct `.tar.gz` archive, extracts it with `tar`, and sets the executable permission via `chmod`.

## [v0.1.6] - 2026-05-15

### Added

- Added `{GitSHA}` macro support in the Steam build description field. The SHA is resolved by running `git rev-parse HEAD`; falls back to `NO_SHA` when git is unavailable or the project is not a repository.

### Changed

- Updated the default `BuildDescription` value in `SteamDeployConfig` to `v{Version} - {Date} - {GitSHA}`.

## [v0.1.5] - 2026-05-11

### Added

- Added a custom Inspector for `BuildDeployConfig` with an "Open Deploy Window" button for quick access from the Project window.
- Added a "Show hints" toggle to hide or show all informational help boxes in the deploy window.
- Added persistent last-used config selection: the deploy window restores the previously selected `BuildDeployConfig` on reopen.
- Added `OpenWindowWithConfig` static entry point so the Inspector button can open the window pre-loaded with a specific config.

## [v0.1.4] - 2026-05-11

### Changed

- Moved `SteamDeployConfig` and `ItchIoDeployConfig` references into `BuildDeployConfig` as serialized fields, consolidating all per-platform settings into a single asset.
- The deploy window now shows platform config fields as indented sub-fields under the selected `BuildDeployConfig`, with change tracking to persist edits to the asset.

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
