# Unity Steam itch.io Deployer

> [English](README.md)

Unity Editor 外掛程式，可在同一個 EditorWindow 內完成一次建置，並將同一份內容部署到 Steam 與 itch.io。

*此專案為 Vibe Coding 之產物，僅為個人原型專案之速成應用。*

---

## 功能

- 多平台部署：可勾選 **Steam**、**itch.io**，或兩者同時。
- 共用 **Build & Upload** 流程：Unity 只建置一次，再依序上傳到各平台。
- 分離共用與平台設定資產：`BuildDeployConfig`、`SteamDeployConfig`、`ItchIoDeployConfig`。
- 中段 UI 以分頁切換各平台的設定與認證。
- 下方 log console 為共用，可連續顯示 Steam 與 itch.io 的輸出。
- Steam 透過 VDF + `steamcmd` 上傳。
- itch.io 透過 `butler push` 上傳。
- Steam Guard 驗證碼可在不中斷建置成果的情況下補輸入後續傳。
- 憑證可用 AES-256 加密後儲存在 `EditorPrefs`。
- 共用的 deploy target、`BuildOutputPath` 與 Unity 6+ `BuildProfile` 會存於 `BuildDeployConfig`。

---

## 系統需求

| 項目 | 版本 |
|------|------|
| Unity | 2021.3 LTS 以上 |
| 作業系統 | Windows 10 / macOS 12 / Ubuntu 20.04 |
| Steam 帳號 | 具有目標 AppID 發布權限的 Steamworks 合作夥伴帳號 |
| itch.io 帳號 | 已建立的專案頁面與 butler API key |

---

## 安裝

### UPM（Git URL）

1. **Window → Package Manager → + → Add package from git URL**
2. 輸入：

```text
https://github.com/qwe321qwe321qwe321/unity-steam-itchio-deployer.git
```

3. 匯入完成後，選單出現 **Tools → Steam itch.io Deployer → Open Window**。

### 手動安裝

將整個 `Editor/SteamItchIoDeployer/` 資料夾複製至目標專案的任意 `Editor/` 目錄下。

---

## 設定

### 1. 建立設定資產

視窗可直接建立所有設定資產：

- **Create Build/Deploy Config Asset**
- **Create Steam Config Asset**
- **Create itch.io Config Asset**

這三個 asset 都不含敏感資訊，可提交至版本控制。

### 2. 共用建置/部署設定

`BuildDeployConfig` 包含：

- `DeployTargets`
- `BuildOutputPath`
- Unity 6+ 的 `BuildProfile`

這個 asset 會保存非機密的共用流程設定，因此 deploy target 與 build profile 不再放在 `EditorPrefs`。

### 3. 選擇部署平台

在視窗最上方勾選要部署的平台：

- **Steam**
- **itch.io**

若同時勾選，工具會先建置一次，再依序上傳兩邊。

### 4. Steam 設定

`SteamDeployConfig` 包含：

- `AppID`
- `DepotID`
- `SetLiveEnabled`
- `BuildBranch`
- `BuildDescription`
- `IgnoreFiles`
- `SteamCmdPath`

Steam 分頁的認證欄位：

- `Steam Username`
- `Password`
- 可選擇加密儲存到 `EditorPrefs`
- `Test Steam Login`

### 5. itch.io 設定

`ItchIoDeployConfig` 包含：

- `ButlerPath`
- `Target`，格式為 `username/game`
- `Channel`
- `UserVersion`
- `IgnoreFiles`
- `Hidden`
- `IfChanged`

itch.io 分頁的認證欄位：

- `BUTLER_API_KEY`
- 可選擇加密儲存到 `EditorPrefs`

上傳時會把 API key 以環境變數 `BUTLER_API_KEY` 注入 `butler` 子程序。

補充：部分 butler 版本並不支援 hidden channel 相關旗標。為了優先確保上傳成功，目前工具會保留 `Hidden` 欄位，但不強制在 upload 時傳入對應參數。

### 6. `BUTLER_API_KEY` 去哪裡取得

可用下面方式取得：

1. 開啟 itch.io 帳號設定頁：`https://itch.io/user/settings/api-keys`
2. 建立新的 API key，或使用既有的 butler / wharf 用途 API key
3. 把該值貼到 itch.io 分頁中的 `BUTLER_API_KEY`

補充：

- 如果你已經在同一台機器跑過 `butler login`，也可以查看本機 butler 憑證檔
- Windows 路徑：`%USERPROFILE%\.config\itch\butler_creds`
- macOS 路徑：`~/Library/Application Support/itch/butler_creds`
- Linux 路徑：`~/.config/itch/butler_creds`
- API key 要當成密碼處理；若外流，請到 itch.io API keys 頁面撤銷後重建

### 7. 共用 Build Output Path

`BuildOutputPath` 現在放在 `BuildDeployConfig`，並由所有已選平台共用。這樣就能更直接符合「建一次、上傳 Steam 和/或 itch.io」的使用方式。

---

## 部署流程

**Build & Upload** 區段提供三個按鈕：

| 按鈕 | 說明 |
|------|------|
| **Build** | 將 Unity 建置到選定的平台輸出路徑 |
| **Upload** | 使用現有建置內容，上傳到所勾選的平台 |
| **Build & Upload** | 先建置一次，再依序上傳到所有勾選的平台 |

若兩平台都勾選，流程為：

1. 驗證所選平台設定
2. 執行一次 Unity 建置
3. 依序上傳到 Steam 與 itch.io
4. 所有輸出都匯入同一個 log console

---

## SteamCMD 安裝

安裝 SteamCMD 後，把 `SteamCmdPath` 指到執行檔。

Windows 下載：

```text
https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip
```

視窗內也提供 **Download & Install** 按鈕可協助下載 SteamCMD。

> SteamCMD 路徑必須為 ASCII-only。

---

## Butler 安裝

安裝 `butler` 後，把 `ButlerPath` 指到執行檔。

文件：

```text
https://itch.io/docs/butler/installing.html
```

再到 itch.io 帳號設定建立 butler API key，填入 itch.io 分頁即可。

---

## 架構

```text
Editor/SteamItchIoDeployer/
├── BuildDeployConfig.cs         共用建置/部署設定資產
├── SteamDeployConfig.cs        Steam 專用設定資產
├── ItchIoDeployConfig.cs       itch.io 專用設定資產
├── CryptographyHelper.cs       AES-256 加解密、EditorPrefs 密文管理
├── VDFGenerator.cs             生成 Steam 的 app_build.vdf 與 depot_build.vdf
├── CliProcessHandler.cs        通用 CLI 子程序執行器，供 steamcmd / butler 共用
└── SteamItchIoDeployWindow.cs  主視窗 UI 與部署流程協調
```

---

## 授權

MIT License
