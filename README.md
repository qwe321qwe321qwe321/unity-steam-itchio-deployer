# Unity Steam itch.io Deployer

> [繁體中文](README_ZH.md)

Unity Editor plugin for building once and deploying the same build to Steam and itch.io from a single EditorWindow.

*This project is a vibe-coded prototype for personal use.*

---

## Features

- Multi-target deployment: select **Steam**, **itch.io**, or both.
- Shared **Build & Upload** pipeline: one Unity build, then sequential uploads to each selected platform.
- Separate config assets for each platform: `SteamDeployConfig` and `ItchioDeployConfig`.
- Tabbed per-platform settings and auth UI while keeping one shared console log.
- Steam upload via generated VDF scripts and asynchronous `steamcmd` execution.
- itch.io upload via asynchronous `butler push` using `BUTLER_API_KEY`.
- Steam Guard retry flow for Steam uploads without rebuilding.
- AES-256 encrypted credentials stored in `EditorPrefs`, never in project assets.
- Configurable build output path stored per platform config as absolute or project-relative path.
- Unity 6+ Build Profile support.

---

## Requirements

| | Minimum |
|--|---------|
| Unity | 2021.3 LTS |
| OS | Windows 10 / macOS 12 / Ubuntu 20.04 |
| Steam account | Steamworks partner account with publish rights for the target AppID |
| itch.io account | Existing itch.io project page and a butler API key |

---

## Installation

### UPM (Git URL)

1. **Window → Package Manager → + → Add package from git URL**
2. Enter:

```text
https://github.com/qwe321qwe321qwe321/unity-steam-itchio-deployer.git
```

3. After import, **Tools → Steam itch.io Deployer → Open Window** appears in the menu.

### Manual

Copy the `Editor/SteamDeployer/` folder into any `Editor/` directory in the target project.

---

## Configuration

### 1. Create config assets

The window can create both platform configs:

- **Create Steam Config Asset**
- **Create itch.io Config Asset**

Both assets are non-sensitive and safe to commit.

### 2. Select deploy targets

At the top of the window, enable one or more targets:

- **Steam**
- **itch.io**

If both are enabled, the tool builds once and uploads to both sequentially.

### 3. Steam settings

`SteamDeployConfig` contains:

- `AppID`
- `DepotID`
- `SetLiveEnabled`
- `BuildBranch`
- `BuildDescription`
- `IgnoreFiles`
- `SteamCmdPath`
- `BuildOutputPath`

Steam auth is entered in the Steam tab:

- `Steam Username`
- `Password`
- optional encrypted save to `EditorPrefs`
- `Test Steam Login`

### 4. itch.io settings

`ItchioDeployConfig` contains:

- `ButlerPath`
- `Target` in `username/game` format
- `Channel`
- `UserVersion`
- `IgnoreFiles`
- `Hidden`
- `IfChanged`
- `BuildOutputPath`

itch.io auth is entered in the itch.io tab:

- `BUTLER_API_KEY`
- optional encrypted save to `EditorPrefs`

The API key is injected into the child process as the `BUTLER_API_KEY` environment variable.

Note: some butler versions do not support hidden-channel creation flags consistently. The current tool keeps the `Hidden` setting in the config/UI for future compatibility, but prioritizes successful uploads over forcing that flag.

### 5. Getting `BUTLER_API_KEY`

Use either of these approaches:

1. Open your itch.io account settings: `https://itch.io/user/settings/api-keys`
2. Create or find an API key intended for butler / wharf usage
3. Paste that value into the itch.io auth tab as `BUTLER_API_KEY`

Notes:

- If you have already logged in with `butler login` on the same machine, you can also inspect your local butler credentials file.
- On Windows, butler stores credentials at `%USERPROFILE%\.config\itch\butler_creds`
- On macOS, the path is `~/Library/Application Support/itch/butler_creds`
- On Linux, the path is `~/.config/itch/butler_creds`
- Treat the API key like a password. If it leaks, revoke it from the itch.io API keys page and create a new one.

### 6. Shared build output path

If both Steam and itch.io are selected together, they must point to the same `BuildOutputPath`. This keeps the workflow consistent: build once, upload the same build to both services.

---

## Deployment

The **Build & Upload** section exposes three buttons:

| Button | What it does |
|--------|-------------|
| **Build** | Runs `BuildPipeline.BuildPlayer` to the selected build output path |
| **Upload** | Uploads the existing build to each selected platform |
| **Build & Upload** | Runs one Unity build, then uploads to each selected platform in sequence |

If both platforms are selected, the sequence is:

1. Validate selected target settings
2. Run one Unity build
3. Upload to Steam, itch.io, or both
4. Stream all logs into the same console pane

---

## SteamCMD Setup

Install SteamCMD and point `SteamCmdPath` to the executable.

Windows download:

```text
https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip
```

The window also includes a **Download & Install** helper for SteamCMD.

> SteamCMD paths must be ASCII-only.

---

## Butler Setup

Install `butler` and point `ButlerPath` to the executable.

Docs:

```text
https://itch.io/docs/butler/installing.html
```

Create a butler API key from your itch.io account settings, then paste it into the itch.io auth tab.

---

## Architecture

```text
Editor/SteamDeployer/
├── SteamDeployConfig.cs        Steam-specific config asset
├── ItchioDeployConfig.cs       itch.io-specific config asset
├── CryptographyHelper.cs       AES-256 encrypt/decrypt, EditorPrefs ciphertext management
├── VDFGenerator.cs             Generates Steam app_build.vdf and depot_build.vdf
├── CliProcessHandler.cs        Generic async CLI process runner for steamcmd and butler
└── SteamDeployWindow.cs        Main EditorWindow UI and orchestration
```

---

## License

MIT License
