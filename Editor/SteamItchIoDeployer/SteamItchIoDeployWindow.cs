using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace SteamItchIoDeployer
{
	public sealed class SteamItchIoDeployWindow : EditorWindow
	{
		private enum DeployState
		{
			Setup,
			TestingLogin,
			Building,
			Uploading,
			WaitingForSteamGuard,
			Success,
			Failed,
		}

		private enum PlatformTab
		{
			Steam,
			ItchIo,
		}

		private enum LogTab
		{
			General,
			Steam,
			ItchIo,
		}

		private const string SteamUsernamePrefsKey = "SteamDeployer_Username";
		private const string SteamPasswordCipherPrefsKey = "SteamDeployer_EncryptedPassword";
		private const string ItchIoSaveApiKeyPrefsKey = "SteamDeployer_ItchioSaveApiKey";
		private const string ItchIoApiKeyCipherPrefsKey = "SteamDeployer_EncryptedItchioApiKey";
		private const int MaxLogBufferChars = 60_000;

		private DeployState _state = DeployState.Setup;
		private string _taskLabel = "";
		private float _progressValue;

		private BuildDeployConfig _buildDeployConfig;
		private SteamDeployConfig _steamConfig;
		private ItchIoDeployConfig _itchIoConfig;

		private PlatformTab _selectedTab = PlatformTab.Steam;

		private string _steamUsername = "";
		private string _steamPassword = "";
		private bool _saveSteamCredentials;

		private string _itchIoApiKey = "";
		private bool _saveItchIoApiKey;

		private bool _authFoldout = true;
		private bool _platformSettingsFoldout = true;

		private bool _isDownloadingSteamCmd;
		private bool _isDownloadingButler;
		private bool _steamCmdFileExists;
		private bool _itchButlerFileExists;

		private string _steamGuardCodeInput = "";
		private bool _isTestLoginContext;
		private CliProcessHandler.CliToolKind _activeToolKind = CliProcessHandler.CliToolKind.Generic;
		private readonly Queue<DeployTargets> _pendingUploads = new Queue<DeployTargets>();

		private CliProcessHandler _processHandler;
		private bool _isProcessRunning;

		private string _generalLogBuffer = "";
		private string _steamLogBuffer = "";
		private string _itchIoLogBuffer = "";
		private Vector2 _generalLogScroll;
		private Vector2 _steamLogScroll;
		private Vector2 _itchIoLogScroll;
		private LogTab _selectedLogTab = LogTab.General;
		private Vector2 _mainScroll;

		private GUIStyle _boxStyle;
		private GUIStyle _bigButtonStyle;
		private GUIStyle _logStyle;
		private GUIStyle _successBoxStyle;
		private GUIStyle _failureBoxStyle;
		private GUIStyle _warningBoxStyle;
		private bool _stylesReady;

		[MenuItem("Tools/Steam itch.io Deployer/Open Window")]
		public static void OpenWindow()
		{
			var window = GetWindow<SteamItchIoDeployWindow>("Steam itch.io Deployer");
			window.minSize = new Vector2(560, 760);
			window.Show();
		}

		private void OnEnable()
		{
			TryLoadConfigs();
			RefreshExecutableExists();
			EnsureBuildDeployDefaults();

			_steamUsername = EditorPrefs.GetString(GetProjectScopedPrefsKey(SteamUsernamePrefsKey), "");
			if (CryptographyHelper.HasStoredValue(GetProjectScopedPrefsKey(SteamPasswordCipherPrefsKey)))
			{
				_steamPassword = CryptographyHelper.LoadDecryptedValue(GetProjectScopedPrefsKey(SteamPasswordCipherPrefsKey)) ?? "";
				_saveSteamCredentials = true;
			}

			_saveItchIoApiKey = EditorPrefs.GetBool(GetProjectScopedPrefsKey(ItchIoSaveApiKeyPrefsKey), false);
			if (CryptographyHelper.HasStoredValue(GetProjectScopedPrefsKey(ItchIoApiKeyCipherPrefsKey)))
				_itchIoApiKey = CryptographyHelper.LoadDecryptedValue(GetProjectScopedPrefsKey(ItchIoApiKeyCipherPrefsKey)) ?? "";

			bool steamAuthReady = !string.IsNullOrWhiteSpace(_steamUsername) && !string.IsNullOrWhiteSpace(_steamPassword);
			bool itchAuthReady = !string.IsNullOrWhiteSpace(_itchIoApiKey);
			_authFoldout = !(steamAuthReady && itchAuthReady);

			bool steamSettingsReady = _steamConfig != null
				&& !string.IsNullOrWhiteSpace(_steamConfig.AppID)
				&& !string.IsNullOrWhiteSpace(_steamConfig.DepotID)
				&& !string.IsNullOrWhiteSpace(_steamConfig.SteamCmdPath);
			bool itchSettingsReady = _itchIoConfig != null
				&& !string.IsNullOrWhiteSpace(_itchIoConfig.Target)
				&& !string.IsNullOrWhiteSpace(_itchIoConfig.Channel)
				&& !string.IsNullOrWhiteSpace(_itchIoConfig.ButlerPath);
			_platformSettingsFoldout = !(steamSettingsReady && itchSettingsReady);

			EditorApplication.update += OnEditorUpdate;
		}

		private void OnFocus()
		{
			RefreshExecutableExists();
		}

		private void OnDisable()
		{
			EditorApplication.update -= OnEditorUpdate;

			if (_isProcessRunning)
			{
				_processHandler?.Kill();
				_processHandler?.Dispose();
				_processHandler = null;
				_isProcessRunning = false;
			}
		}

		private void OnEditorUpdate()
		{
			if (!_isProcessRunning || _processHandler == null) return;

			CliProcessHandler activeHandler = _processHandler;
			bool done = _processHandler.PumpMainThread();
			if (done && ReferenceEquals(_processHandler, activeHandler))
				_isProcessRunning = false;

			Repaint();
		}

		private void OnGUI()
		{
			EnsureStyles();

			_mainScroll = EditorGUILayout.BeginScrollView(_mainScroll);
			EditorGUILayout.Space(6);
			EditorGUILayout.LabelField("  Steam itch.io Deployer", EditorStyles.largeLabel);
			EditorGUILayout.LabelField("  Build once, upload to Steam and/or itch.io", EditorStyles.miniLabel);
			EditorGUILayout.Space(4);

			bool locked = _state == DeployState.Building
				|| _state == DeployState.Uploading
				|| _state == DeployState.TestingLogin
				|| _state == DeployState.WaitingForSteamGuard;

			using (new EditorGUI.DisabledScope(locked))
			{
				DrawTargetSelectionSection();
			}

			DrawConfigSection(locked);
			DrawPlatformTabs(locked);
			DrawAuthSection(locked);
			DrawPlatformSettingsSection(locked);

			DrawBuildAndUploadSection(locked);
			DrawResultBanner();
			DrawLogSection();

			EditorGUILayout.EndScrollView();
		}

		private void DrawTargetSelectionSection()
		{
			using (new GUILayout.VerticalScope(_boxStyle))
			{
				EditorGUILayout.LabelField("Deploy Targets", EditorStyles.boldLabel);
				EditorGUILayout.HelpBox("Select one or more upload targets. Build runs once, then uploads to each selected platform in sequence.", MessageType.None);

				DeployTargets selectedTargets = GetSelectedTargets();
				bool steam = (selectedTargets & DeployTargets.Steam) != 0;
				bool itch = (selectedTargets & DeployTargets.ItchIo) != 0;

				using (var check = new EditorGUI.ChangeCheckScope())
				{
					steam = EditorGUILayout.ToggleLeft("Steam", steam);
					itch = EditorGUILayout.ToggleLeft("itch.io", itch);

					if (check.changed)
					{
						selectedTargets = DeployTargets.None;
						if (steam) selectedTargets |= DeployTargets.Steam;
						if (itch) selectedTargets |= DeployTargets.ItchIo;
						SetSelectedTargets(selectedTargets);

						if (_selectedTab == PlatformTab.Steam && !steam && itch)
							_selectedTab = PlatformTab.ItchIo;
						else if (_selectedTab == PlatformTab.ItchIo && !itch && steam)
							_selectedTab = PlatformTab.Steam;
					}
				}
			}
			EditorGUILayout.Space(3);
		}

		private void DrawConfigSection(bool locked)
		{
			using (new GUILayout.VerticalScope(_boxStyle))
			{
				EditorGUILayout.LabelField("Configuration Assets", EditorStyles.boldLabel);

				using (new EditorGUI.DisabledScope(locked))
				{
					_buildDeployConfig = (BuildDeployConfig)EditorGUILayout.ObjectField("Build/Deploy Config", _buildDeployConfig, typeof(BuildDeployConfig), false);
					if (_buildDeployConfig == null && GUILayout.Button("Create Build/Deploy Config Asset"))
						CreateBuildDeployConfigAsset();

					_steamConfig = (SteamDeployConfig)EditorGUILayout.ObjectField("Steam Config", _steamConfig, typeof(SteamDeployConfig), false);
					if (_steamConfig == null && GUILayout.Button("Create Steam Config Asset"))
						CreateSteamConfigAsset();

					_itchIoConfig = (ItchIoDeployConfig)EditorGUILayout.ObjectField("itch.io Config", _itchIoConfig, typeof(ItchIoDeployConfig), false);
					if (_itchIoConfig == null && GUILayout.Button("Create itch.io Config Asset"))
						CreateItchIoConfigAsset();
				}
			}
			EditorGUILayout.Space(3);
		}

		private void DrawPlatformTabs(bool locked)
		{
			DeployTargets selectedTargets = GetSelectedTargets();
			bool hasSteam = _steamConfig != null || (selectedTargets & DeployTargets.Steam) != 0;
			bool hasItch = _itchIoConfig != null || (selectedTargets & DeployTargets.ItchIo) != 0;
			if (!hasSteam && !hasItch)
			{
				hasSteam = true;
				hasItch = true;
			}

			using (new GUILayout.VerticalScope(_boxStyle))
			{
				EditorGUILayout.LabelField("Platform Settings", EditorStyles.boldLabel);
				EditorGUILayout.Space(4);

				using (new EditorGUI.DisabledScope(locked))
				{
					using (new GUILayout.HorizontalScope())
					{
						using (new EditorGUI.DisabledScope(!hasSteam))
						{
							if (GUILayout.Toggle(_selectedTab == PlatformTab.Steam, "Steam", EditorStyles.toolbarButton))
								_selectedTab = PlatformTab.Steam;
						}

						using (new EditorGUI.DisabledScope(!hasItch))
						{
							if (GUILayout.Toggle(_selectedTab == PlatformTab.ItchIo, "itch.io", EditorStyles.toolbarButton))
								_selectedTab = PlatformTab.ItchIo;
						}
					}
				}
			}
			EditorGUILayout.Space(3);
		}

		private void DrawAuthSection(bool locked)
		{
			using (new GUILayout.VerticalScope(_boxStyle))
			{
				_authFoldout = EditorGUILayout.Foldout(_authFoldout, $"Authentication - {GetSelectedTabLabel()}", true, EditorStyles.foldoutHeader);
				if (!_authFoldout)
				{
					if (_selectedTab == PlatformTab.Steam && !string.IsNullOrWhiteSpace(_steamUsername))
						EditorGUILayout.LabelField($"  Logged in as: {_steamUsername}", EditorStyles.miniLabel);
					else if (_selectedTab == PlatformTab.ItchIo && !string.IsNullOrWhiteSpace(_itchIoApiKey))
						EditorGUILayout.LabelField("  API key loaded", EditorStyles.miniLabel);
				}
				else
				{
					using (new EditorGUI.DisabledScope(locked))
					{
						if (_selectedTab == PlatformTab.Steam)
							DrawSteamAuthFields();
						else
							DrawItchIoAuthFields();
					}
				}
			}
			EditorGUILayout.Space(3);
		}

		private void DrawSteamAuthFields()
		{
			EditorGUILayout.Space(4);

			using (var check = new EditorGUI.ChangeCheckScope())
			{
				_steamUsername = EditorGUILayout.TextField("Steam Username", _steamUsername);
				if (check.changed)
					EditorPrefs.SetString(GetProjectScopedPrefsKey(SteamUsernamePrefsKey), _steamUsername);
			}

			_steamPassword = EditorGUILayout.PasswordField("Password", _steamPassword);
			EditorGUILayout.Space(4);

			bool prevSave = _saveSteamCredentials;
			_saveSteamCredentials = EditorGUILayout.Toggle(new GUIContent("Save credentials (AES-256)", "Encrypts the Steam password using your machine identity and stores it in EditorPrefs for this project only."), _saveSteamCredentials);

			if (prevSave && !_saveSteamCredentials)
				CryptographyHelper.ClearStoredValue(GetProjectScopedPrefsKey(SteamPasswordCipherPrefsKey));

			if (_saveSteamCredentials)
			{
				using (new GUILayout.HorizontalScope())
				{
					GUILayout.FlexibleSpace();
					if (GUILayout.Button("Save Now", GUILayout.Width(100)))
					{
						CryptographyHelper.SaveEncryptedValue(GetProjectScopedPrefsKey(SteamPasswordCipherPrefsKey), _steamPassword);
						EditorUtility.DisplayDialog("Saved", "Steam password encrypted and stored in EditorPrefs for this project.", "OK");
					}
					if (GUILayout.Button("Clear Saved", GUILayout.Width(100)))
						CryptographyHelper.ClearStoredValue(GetProjectScopedPrefsKey(SteamPasswordCipherPrefsKey));
				}

				if (CryptographyHelper.HasStoredValue(GetProjectScopedPrefsKey(SteamPasswordCipherPrefsKey)))
					EditorGUILayout.HelpBox("Encrypted Steam password stored for this project on this machine.", MessageType.Info);
			}

			EditorGUILayout.Space(6);
			bool canTestLogin = _steamConfig != null
				&& !string.IsNullOrWhiteSpace(_steamUsername)
				&& !string.IsNullOrWhiteSpace(_steamPassword)
				&& !string.IsNullOrWhiteSpace(_steamConfig.SteamCmdPath);

			using (new EditorGUI.DisabledScope(!canTestLogin))
			{
				if (GUILayout.Button(new GUIContent("Test Steam Login", "Runs steamcmd with +login only to verify credentials."), GUILayout.Height(28)))
					StartTestLogin();
			}

			if (!canTestLogin)
				EditorGUILayout.HelpBox("Fill in username, password, and SteamCMD path to enable Steam login testing.", MessageType.None);
		}

		private void DrawItchIoAuthFields()
		{
			EditorGUILayout.Space(4);
			EditorGUILayout.HelpBox("Use a butler API key. The upload process injects it as BUTLER_API_KEY for the child process.", MessageType.None);
			using (new GUILayout.HorizontalScope())
			{
				GUILayout.FlexibleSpace();
				if (GUILayout.Button("Open API Keys Page", GUILayout.Width(160)))
					Application.OpenURL("https://itch.io/user/settings/api-keys");
			}
			EditorGUILayout.Space(4);
			_itchIoApiKey = EditorGUILayout.PasswordField("BUTLER_API_KEY", _itchIoApiKey);
			EditorGUILayout.Space(4);

			bool prevSave = _saveItchIoApiKey;
			_saveItchIoApiKey = EditorGUILayout.Toggle(new GUIContent("Save API key (AES-256)", "Encrypts the itch.io API key and stores it in EditorPrefs for this project only."), _saveItchIoApiKey);
			EditorPrefs.SetBool(GetProjectScopedPrefsKey(ItchIoSaveApiKeyPrefsKey), _saveItchIoApiKey);

			if (prevSave && !_saveItchIoApiKey)
				CryptographyHelper.ClearStoredValue(GetProjectScopedPrefsKey(ItchIoApiKeyCipherPrefsKey));

			if (_saveItchIoApiKey)
			{
				using (new GUILayout.HorizontalScope())
				{
					GUILayout.FlexibleSpace();
					if (GUILayout.Button("Save Now", GUILayout.Width(100)))
					{
						CryptographyHelper.SaveEncryptedValue(GetProjectScopedPrefsKey(ItchIoApiKeyCipherPrefsKey), GetCurrentItchIoApiKeyInput());
						EditorUtility.DisplayDialog("Saved", "itch.io API key encrypted and stored in EditorPrefs for this project.", "OK");
					}
					if (GUILayout.Button("Clear Saved", GUILayout.Width(100)))
						CryptographyHelper.ClearStoredValue(GetProjectScopedPrefsKey(ItchIoApiKeyCipherPrefsKey));
				}

				if (CryptographyHelper.HasStoredValue(GetProjectScopedPrefsKey(ItchIoApiKeyCipherPrefsKey)))
					EditorGUILayout.HelpBox("Encrypted itch.io API key stored for this project on this machine.", MessageType.Info);
			}
		}

		private void DrawPlatformSettingsSection(bool locked)
		{
			using (new GUILayout.VerticalScope(_boxStyle))
			{
				_platformSettingsFoldout = EditorGUILayout.Foldout(_platformSettingsFoldout, $"Platform Settings - {GetSelectedTabLabel()}", true, EditorStyles.foldoutHeader);
				if (!_platformSettingsFoldout)
				{
					DrawPlatformSettingsSummary();
				}
				else
				{
					using (new EditorGUI.DisabledScope(locked))
					{
						if (_selectedTab == PlatformTab.Steam)
							DrawSteamSettingsFields();
						else
							DrawItchIoSettingsFields();
					}
				}
			}
			EditorGUILayout.Space(3);
		}

		private void DrawPlatformSettingsSummary()
		{
			if (_selectedTab == PlatformTab.Steam && _steamConfig != null)
				EditorGUILayout.LabelField($"  App ID: {_steamConfig.AppID}  |  Depot ID: {_steamConfig.DepotID}", EditorStyles.miniLabel);
			else if (_selectedTab == PlatformTab.ItchIo && _itchIoConfig != null)
				EditorGUILayout.LabelField($"  Target: {_itchIoConfig.Target}:{_itchIoConfig.Channel}", EditorStyles.miniLabel);
		}

		private void DrawSteamSettingsFields()
		{
			if (_steamConfig == null)
			{
				EditorGUILayout.HelpBox("Assign or create a Steam config asset first.", MessageType.Warning);
				return;
			}

			EditorGUILayout.Space(4);
			using (var check = new EditorGUI.ChangeCheckScope())
			{
				_steamConfig.AppID = EditorGUILayout.TextField("App ID", _steamConfig.AppID);
				_steamConfig.DepotID = EditorGUILayout.TextField("Depot ID", _steamConfig.DepotID);
				_steamConfig.SetLiveEnabled = EditorGUILayout.Toggle(new GUIContent("Set Live After Upload", "Promotes the uploaded build to the specified branch after upload."), _steamConfig.SetLiveEnabled);

				using (new EditorGUI.DisabledScope(!_steamConfig.SetLiveEnabled))
					_steamConfig.BuildBranch = EditorGUILayout.TextField("Branch", _steamConfig.BuildBranch);

				_steamConfig.BuildDescription = EditorGUILayout.TextField("Build Description", _steamConfig.BuildDescription);
				EditorGUILayout.HelpBox("Description supports {Version}, {Date}, {DateTime} macros.", MessageType.None);
				_steamConfig.IgnoreFiles = EditorGUILayout.TextField("Ignore Files", _steamConfig.IgnoreFiles);

				EditorGUILayout.Space(6);
				EditorGUILayout.LabelField("SteamCMD Executable", EditorStyles.boldLabel);

				using (new GUILayout.HorizontalScope())
				{
					_steamConfig.SteamCmdPath = EditorGUILayout.TextField(_steamConfig.SteamCmdPath);
					if (GUILayout.Button("Browse…", GUILayout.Width(72)))
					{
						string browsedPath = EditorUtility.OpenFilePanel("Locate steamcmd executable", "", IsWindowsEditor() ? "exe" : "");
						if (!string.IsNullOrEmpty(browsedPath))
						{
							_steamConfig.SteamCmdPath = NormalizeProjectRelativePath(browsedPath);
							RefreshExecutableExists();
							EditorUtility.SetDirty(_steamConfig);
							AssetDatabase.SaveAssets();
						}
					}
				}

				if (check.changed)
					SaveConfig(_steamConfig, refreshExecutables: true);
			}

			DrawSteamCmdDownloadTools();
		}

		private void DrawItchIoSettingsFields()
		{
			if (_itchIoConfig == null)
			{
				EditorGUILayout.HelpBox("Assign or create an itch.io config asset first.", MessageType.Warning);
				return;
			}

			EditorGUILayout.Space(4);
			using (var check = new EditorGUI.ChangeCheckScope())
			{
				_itchIoConfig.Target = EditorGUILayout.TextField(
					new GUIContent(
						"Target",
						"The itch.io project to upload to, in the form username/game. Example: leafo/celestial-roads. " +
						"This must match the exact URL slug of an already-created itch.io project page."),
					_itchIoConfig.Target);
				EditorGUILayout.HelpBox(
					"Target tells butler which itch.io project receives the build. Use the exact page address slug in the form username/game. " +
					"Example: if the page URL is https://myname.itch.io/my-cool-game, the target is myname/my-cool-game.",
					MessageType.None);

				_itchIoConfig.Channel = EditorGUILayout.TextField(
					new GUIContent(
						"Channel",
						"The slot name inside the itch.io project. Common examples: windows, windows-beta, linux, mac-stable. " +
						"Channel names influence initial platform tagging on itch.io."),
					_itchIoConfig.Channel);
				EditorGUILayout.HelpBox(
					"Channel is the destination slot inside that itch.io project. Re-uploading to the same channel updates that slot. " +
					"Names such as windows, linux, mac, windows-beta, or win-stable are typical. If the name contains win/windows, linux, or mac/osx, itch.io uses that to infer platform tags. " +
					"Use lower-case kebab-case when possible. For browser builds, you may use a name like html5, web, or browser, but note that browser-playable / HTML status is not controlled by the channel name alone. After the first upload, you still need to configure the itch.io project page as HTML / Playable in browser from the website.",
					MessageType.None);

				_itchIoConfig.UserVersion = EditorGUILayout.TextField(
					new GUIContent(
						"User Version",
						"Optional human-readable build version passed as butler --userversion. Useful for showing your own version label instead of only itch.io's internal build number."),
					_itchIoConfig.UserVersion);
				EditorGUILayout.HelpBox(
					"User Version is the build label visible to you and your players, for example 1.2.0, 2026.05.03, or demo-7. " +
					"It is passed to butler as --userversion. Supports {Version}, {Date}, and {DateTime} macros, so {Version} usually maps nicely to Application.version.",
					MessageType.None);
				_itchIoConfig.IgnoreFiles = EditorGUILayout.TextField("Ignore Files", _itchIoConfig.IgnoreFiles);
				_itchIoConfig.Hidden = EditorGUILayout.Toggle(
					new GUIContent(
						"Hidden First Push",
						"Reserved option for hiding the first upload on a newly created channel. Some butler versions do not support this flag."),
					_itchIoConfig.Hidden);
				EditorGUILayout.HelpBox(
					"Intended meaning: keep the first upload to a brand-new channel hidden so it does not become immediately visible to players. " +
					"However, some butler builds do not support the hidden-channel flag. For compatibility, the current tool keeps this setting as informational only and does not pass a hidden flag during upload.",
					MessageType.None);

				_itchIoConfig.IfChanged = EditorGUILayout.Toggle(
					new GUIContent(
						"Upload Only If Changed",
						"Passes --if-changed to butler so no new build is created when the local contents are identical to the latest upload on that channel."),
					_itchIoConfig.IfChanged);
				EditorGUILayout.HelpBox(
					"Upload Only If Changed skips the upload entirely when the selected build folder is identical to the latest build already on that channel. " +
					"Use it to reduce no-op uploads in repetitive release workflows. If you always want a fresh build entry even when files did not change, leave this off.",
					MessageType.None);

				EditorGUILayout.Space(6);
				EditorGUILayout.LabelField("Butler Executable", EditorStyles.boldLabel);

				using (new GUILayout.HorizontalScope())
				{
					_itchIoConfig.ButlerPath = EditorGUILayout.TextField(_itchIoConfig.ButlerPath);
					if (GUILayout.Button("Browse…", GUILayout.Width(72)))
					{
						string browsedPath = EditorUtility.OpenFilePanel("Locate butler executable", "", IsWindowsEditor() ? "exe" : "");
						if (!string.IsNullOrEmpty(browsedPath))
						{
							_itchIoConfig.ButlerPath = NormalizeProjectRelativePath(browsedPath);
							RefreshExecutableExists();
							EditorUtility.SetDirty(_itchIoConfig);
							AssetDatabase.SaveAssets();
						}
					}
				}

				if (check.changed)
					SaveConfig(_itchIoConfig, refreshExecutables: true);
			}

			if (!_itchButlerFileExists)
			{
				EditorGUILayout.HelpBox("butler executable not found at the configured path.", MessageType.Warning);
				DrawButlerDownloadTools();
			}
			else
			{
				DrawButlerDownloadTools();
			}
		}

		private void DrawBuildOutputEditor()
		{
			if (_buildDeployConfig == null)
			{
				EditorGUILayout.HelpBox("Assign or create a Build/Deploy config asset first.", MessageType.Warning);
				return;
			}

			EditorGUILayout.Space(6);
			EditorGUILayout.LabelField("Build Output Path", EditorStyles.boldLabel);

			using (new GUILayout.HorizontalScope())
			{
				_buildDeployConfig.BuildOutputPath = EditorGUILayout.TextField(_buildDeployConfig.BuildOutputPath ?? "");
				if (GUILayout.Button("Browse…", GUILayout.Width(72)))
				{
					string browsed = EditorUtility.OpenFolderPanel("Select Build Output Folder", _buildDeployConfig.BuildOutputPath ?? "", "");
					if (!string.IsNullOrEmpty(browsed))
					{
						_buildDeployConfig.BuildOutputPath = NormalizeProjectRelativePath(browsed);
						SaveConfig(_buildDeployConfig, refreshExecutables: false);
					}
				}
			}
		}

		private static string DescribeSelectedTargets(DeployTargets targets)
		{
			if (targets == DeployTargets.None) return "None";
			if (targets == (DeployTargets.Steam | DeployTargets.ItchIo)) return "Steam + itch.io";
			if (targets == DeployTargets.ItchIo) return "itch.io";
			return "Steam";
		}

		private void DrawSteamCmdDownloadTools()
		{
			if (_steamConfig == null) return;

			if (string.IsNullOrWhiteSpace(_steamConfig.SteamCmdPath) || !_steamCmdFileExists)
			{
				EditorGUILayout.Space(2);
				using (new EditorGUI.DisabledScope(_isDownloadingSteamCmd))
				{
					string downloadLabel = _isDownloadingSteamCmd ? "Downloading…" : "Download & Install";
					if (GUILayout.Button(new GUIContent(downloadLabel, "Downloads SteamCMD into a steamcmd/ folder at the project root and launches it once."), GUILayout.Height(26)))
						DownloadAndInstallSteamCmd();
				}

				EditorGUILayout.HelpBox(_isDownloadingSteamCmd
					? "Downloading SteamCMD from Valve — please wait…"
					: "No valid SteamCMD executable found. Use Browse or Download & Install.",
					_isDownloadingSteamCmd ? MessageType.Info : MessageType.Warning);
			}
			else
			{
				string resolvedSteamCmdDir = Path.GetDirectoryName(ResolveSteamCmdPath());
				if (!string.IsNullOrEmpty(resolvedSteamCmdDir) && IsPathInsideProject(resolvedSteamCmdDir))
				{
					string gitignorePath = Path.Combine(resolvedSteamCmdDir, ".gitignore");
					if (!File.Exists(gitignorePath))
					{
						EditorGUILayout.HelpBox("steamcmd is inside the project but has no .gitignore.", MessageType.Warning);
						if (GUILayout.Button("Add .gitignore", GUILayout.Height(24)))
							WriteGitignore(resolvedSteamCmdDir, "SteamCMD runtime data");
					}
				}
			}
		}

		private void DrawButlerDownloadTools()
		{
			if (_itchIoConfig == null) return;

			if (string.IsNullOrWhiteSpace(_itchIoConfig.ButlerPath) || !_itchButlerFileExists)
			{
				EditorGUILayout.Space(2);
				using (new EditorGUI.DisabledScope(_isDownloadingButler))
				{
					string downloadLabel = _isDownloadingButler ? "Downloading…" : "Download & Install";
					if (GUILayout.Button(new GUIContent(downloadLabel, "Downloads butler into a butler/ folder at the project root and sets ButlerPath automatically."), GUILayout.Height(26)))
						DownloadAndInstallButler();
				}

				EditorGUILayout.HelpBox(_isDownloadingButler
					? "Downloading butler from itch.io broth — please wait…"
					: "No valid butler executable found. Use Browse or Download & Install.",
					_isDownloadingButler ? MessageType.Info : MessageType.Warning);
			}
			else
			{
				string resolvedButlerDir = Path.GetDirectoryName(ResolveButlerPath());
				if (!string.IsNullOrEmpty(resolvedButlerDir) && IsPathInsideProject(resolvedButlerDir))
				{
					string gitignorePath = Path.Combine(resolvedButlerDir, ".gitignore");
					if (!File.Exists(gitignorePath))
					{
						EditorGUILayout.HelpBox("butler is inside the project but has no .gitignore.", MessageType.Warning);
						if (GUILayout.Button("Add .gitignore", GUILayout.Height(24)))
							WriteGitignore(resolvedButlerDir, "butler runtime data");
					}
				}
			}
		}

		private void DrawBuildAndUploadSection(bool locked)
		{
			if (_state == DeployState.WaitingForSteamGuard)
			{
				using (new GUILayout.VerticalScope(_warningBoxStyle))
				{
					EditorGUILayout.LabelField("Steam Guard Code Required", EditorStyles.boldLabel);
					EditorGUILayout.HelpBox("SteamCMD requires a Steam Guard code. Enter it below to continue the Steam upload.", MessageType.Warning);
					EditorGUILayout.Space(4);
					_steamGuardCodeInput = EditorGUILayout.TextField("Steam Guard Code", _steamGuardCodeInput);
					EditorGUILayout.Space(6);

					using (new GUILayout.HorizontalScope())
					{
						using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_steamGuardCodeInput)))
						{
							if (GUILayout.Button("Submit Code", GUILayout.Height(32)))
								SubmitSteamGuardCode();
						}

						if (GUILayout.Button("Cancel", GUILayout.Height(32)))
						{
							GUIUtility.keyboardControl = 0;
							_steamGuardCodeInput = "";
							_pendingUploads.Clear();
							_state = DeployState.Setup;
						}
					}
				}
				EditorGUILayout.Space(3);
				return;
			}

			using (new GUILayout.VerticalScope(_boxStyle))
			{
				EditorGUILayout.LabelField("Build & Upload", EditorStyles.boldLabel);

				if (locked)
				{
					EditorGUILayout.Space(6);
					EditorGUILayout.LabelField(_taskLabel, EditorStyles.centeredGreyMiniLabel);
					EditorGUILayout.Space(4);
					Rect bar = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(22));
					EditorGUI.ProgressBar(bar, _progressValue, _taskLabel);
					EditorGUILayout.Space(6);
					if (GUILayout.Button("Cancel", GUILayout.Height(28)))
						CancelOperation();
				}
				else
				{
					DrawSharedBuildSettingsFields(locked);

	#if UNITY_6000_0_OR_NEWER
	#endif

					DeployTargets selectedTargets = GetSelectedTargets();
					bool hasTargets = selectedTargets != DeployTargets.None;
					bool canBuild = CanBuildSelectedTargets();
					bool canUpload = hasTargets && ValidateSelectedTargetsForUpload(showDialogs: false) && CheckAnyBuildOutputExists();
					bool canBuildAndUpload = hasTargets && ValidateSelectedTargetsForUpload(showDialogs: false, requireCredentials: true, requireBuildOutput: true);

					using (new GUILayout.HorizontalScope())
					{
						using (new EditorGUI.DisabledScope(!canBuild))
						{
							if (GUILayout.Button(new GUIContent("Build", "Run the Unity build to the selected build output path."), GUILayout.Height(32)))
								EditorApplication.delayCall += StartBuildOnly;
						}

						using (new EditorGUI.DisabledScope(!canUpload))
						{
							if (GUILayout.Button(new GUIContent("Upload", "Upload the existing build to each selected target."), GUILayout.Height(32)))
								EditorApplication.delayCall += StartUploadOnly;
						}
					}

					EditorGUILayout.Space(6);
					using (new EditorGUI.DisabledScope(!canBuildAndUpload))
					{
						if (GUILayout.Button("Build & Upload", _bigButtonStyle, GUILayout.Height(32)))
							EditorApplication.delayCall += StartDeployment;
					}

					if (!hasTargets)
						EditorGUILayout.HelpBox("Select at least one target platform.", MessageType.Warning);
					else if (!HasAnyBuildOutputPath())
						EditorGUILayout.HelpBox("Set the shared build output path to enable Build.", MessageType.None);
					else if (!canUpload)
						EditorGUILayout.HelpBox("Upload needs valid platform settings and an existing build output in the selected build output path.", MessageType.None);
				}
			}
			EditorGUILayout.Space(3);
		}

		private void DrawSharedBuildSettingsFields(bool locked)
		{
			using (new GUILayout.VerticalScope(_boxStyle))
			{
				EditorGUILayout.LabelField("Shared Build Settings", EditorStyles.boldLabel);

				using (new EditorGUI.DisabledScope(locked || _buildDeployConfig == null))
				{
					if (_buildDeployConfig == null)
					{
						EditorGUILayout.HelpBox("Assign or create a Build/Deploy config asset first.", MessageType.Warning);
						return;
					}

	#if UNITY_6000_0_OR_NEWER
					using (var check = new EditorGUI.ChangeCheckScope())
					{
						_buildDeployConfig.BuildProfile = (UnityEditor.Build.Profile.BuildProfile)EditorGUILayout.ObjectField(
							new GUIContent(
								"Build Profile",
								"Optional: select a Unity 6+ Build Profile asset to activate before building. Leave empty to use the current active build settings."),
							_buildDeployConfig.BuildProfile,
							typeof(UnityEditor.Build.Profile.BuildProfile),
							allowSceneObjects: false);

						if (check.changed)
							SaveConfig(_buildDeployConfig, refreshExecutables: false);
					}
	#endif

					using (var check = new EditorGUI.ChangeCheckScope())
					{
						DrawBuildOutputEditor();

						if (check.changed)
							SaveConfig(_buildDeployConfig, refreshExecutables: false);
					}

					EditorGUILayout.Space(4);
					EditorGUILayout.LabelField($"Selected Targets: {DescribeSelectedTargets(GetSelectedTargets())}", EditorStyles.miniLabel);
				}
			}

			EditorGUILayout.Space(3);
		}

		private void DrawResultBanner()
		{
			if (_state == DeployState.Success)
			{
				string message = _isTestLoginContext ? "  Login Test Successful!" : "  Upload Successful!";
				using (new GUILayout.VerticalScope(_successBoxStyle))
					EditorGUILayout.LabelField(message, EditorStyles.boldLabel);
			}
			else if (_state == DeployState.Failed)
			{
				string message = _isTestLoginContext ? "  Login Test Failed — see Console for details." : "  Deployment Failed — see Console for details.";
				using (new GUILayout.VerticalScope(_failureBoxStyle))
					EditorGUILayout.LabelField(message, EditorStyles.boldLabel);
			}
		}

		private void DrawLogSection()
		{
			if (string.IsNullOrEmpty(_generalLogBuffer) && string.IsNullOrEmpty(_steamLogBuffer) && string.IsNullOrEmpty(_itchIoLogBuffer))
				return;

			using (new GUILayout.VerticalScope(_boxStyle))
			{
				using (new GUILayout.HorizontalScope())
				{
					EditorGUILayout.LabelField("Deployment Output", EditorStyles.boldLabel);
					GUILayout.Space(8);
					_selectedLogTab = (LogTab)GUILayout.Toolbar((int)_selectedLogTab, new[] { "General", "Steam", "itch.io" }, EditorStyles.toolbarButton, GUILayout.Width(220));
					GUILayout.FlexibleSpace();
					if (GUILayout.Button("Clear", GUILayout.Width(56)))
						ClearSelectedLogBuffer();
					if (GUILayout.Button("Open Editor.log", GUILayout.Width(110)))
						RevealEditorLog();
				}

				Vector2 scroll = GetSelectedLogScroll();
				scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(220));
				EditorGUILayout.TextArea(GetSelectedLogBuffer(), _logStyle, GUILayout.ExpandHeight(true));
				EditorGUILayout.EndScrollView();
				SetSelectedLogScroll(scroll);
			}
			EditorGUILayout.Space(3);
		}

		private void StartTestLogin()
		{
			if (!ValidateSteamLogin(showDialogs: true)) return;

			ClearAllLogBuffers();
			_selectedLogTab = LogTab.Steam;
			_isTestLoginContext = true;
			_steamGuardCodeInput = "";
			_pendingUploads.Clear();

			if (_saveSteamCredentials && !string.IsNullOrEmpty(_steamPassword))
				CryptographyHelper.SaveEncryptedValue(GetProjectScopedPrefsKey(SteamPasswordCipherPrefsKey), _steamPassword);

			LaunchSteamTestLogin("");
		}

		private void LaunchSteamTestLogin(string steamGuardCode)
		{
			string args = CliProcessHandler.BuildSteamTestLoginArguments(_steamUsername, GetEffectiveSteamPassword(), steamGuardCode);
			_processHandler = CreateAndWireProcessHandler(CliProcessHandler.CliToolKind.SteamCmd);
			_activeToolKind = CliProcessHandler.CliToolKind.SteamCmd;

			_state = DeployState.TestingLogin;
			_taskLabel = "Testing Steam login...";
			_progressValue = 0.5f;
			AppendPlatformLog(DeployTargets.Steam, "Testing login", false);
			_isProcessRunning = true;

			if (!_processHandler.Start(ResolveSteamCmdPath(), args))
			{
				_isProcessRunning = false;
				SetFailedState("Failed to start steamcmd.");
			}
		}

		private void StartBuildOnly()
		{
			if (!EnsureBuildOutputPathForBuild()) return;

			ClearAllLogBuffers();
			_selectedLogTab = LogTab.General;
			_progressValue = 0.05f;
			_taskLabel = "Preparing build...";
			_state = DeployState.Building;
			Repaint();

			string buildOutputPath = ResolveSelectedBuildOutputPath();
			if (!RunBuildIntoResolvedPath(buildOutputPath, out string failureReason))
			{
				SetFailedState(failureReason);
				return;
			}

			_state = DeployState.Success;
			_progressValue = 1f;
			_taskLabel = "Build complete!";
		}

		private void StartUploadOnly()
		{
			if (!ValidateSelectedTargetsForUpload(showDialogs: true, requireCredentials: true, requireBuildOutput: true)) return;
			if (!CheckAnyBuildOutputExists())
			{
				EditorUtility.DisplayDialog("No Build Found", "No build output was found in the selected build output path. Please run a build first.", "OK");
				return;
			}

			ClearAllLogBuffers();
			_selectedLogTab = LogTab.General;
			PrepareUploadSequence();
			_taskLabel = "Preparing uploads...";
			_progressValue = 0.6f;
			_state = DeployState.Uploading;
			Repaint();
			LaunchNextUploadTarget();
		}

		private void StartDeployment()
		{
			if (!ValidateSelectedTargetsForUpload(showDialogs: true, requireCredentials: true, requireBuildOutput: true)) return;

			ClearAllLogBuffers();
			_selectedLogTab = LogTab.General;
			_isTestLoginContext = false;
			_steamGuardCodeInput = "";
			_state = DeployState.Building;
			_progressValue = 0.05f;
			_taskLabel = "Preparing build...";
			Repaint();

			PersistSavedCredentials();

			string buildOutputPath = ResolveSelectedBuildOutputPath();
			if (!RunBuildIntoResolvedPath(buildOutputPath, out string failureReason))
			{
				SetFailedState(failureReason);
				return;
			}

			PrepareUploadSequence();
			_taskLabel = "Preparing uploads...";
			_progressValue = 0.6f;
			_state = DeployState.Uploading;
			LaunchNextUploadTarget();
		}

		private void PrepareUploadSequence()
		{
			_isTestLoginContext = false;
			_pendingUploads.Clear();
			_steamGuardCodeInput = "";

			PersistSavedCredentials();
			DeployTargets selectedTargets = GetSelectedTargets();

			if ((selectedTargets & DeployTargets.Steam) != 0)
				_pendingUploads.Enqueue(DeployTargets.Steam);
			if ((selectedTargets & DeployTargets.ItchIo) != 0)
				_pendingUploads.Enqueue(DeployTargets.ItchIo);
		}

		private void LaunchNextUploadTarget()
		{
			if (_pendingUploads.Count == 0)
			{
				_state = DeployState.Success;
				_progressValue = 1f;
				_taskLabel = "All uploads complete!";
				AppendGeneralLog("=== ALL SELECTED UPLOADS COMPLETED ===", false);
				Repaint();
				return;
			}

			DeployTargets nextTarget = _pendingUploads.Peek();
			if (nextTarget == DeployTargets.Steam)
			{
				_selectedLogTab = LogTab.Steam;
				LaunchSteamUpload("");
			}
			else
			{
				_selectedLogTab = LogTab.ItchIo;
				LaunchItchIoUpload();
			}
		}

		private void LaunchSteamUpload(string steamGuardCode)
		{
			string buildOutputPath = ResolveSelectedBuildOutputPath();
			string appVdfPath;

			try
			{
				string desc = ResolveMacros(_steamConfig.BuildDescription);
				appVdfPath = VDFGenerator.GenerateVdfScripts(_steamConfig, buildOutputPath, desc, ResolveSteamCmdPath());
				AppendPlatformLog(DeployTargets.Steam, $"VDF scripts written. App VDF: {appVdfPath}", false);
			}
			catch (Exception ex)
			{
				AppendPlatformLog(DeployTargets.Steam, $"VDF generation failed: {ex.Message}", true);
				SetFailedState("Steam VDF generation failed.");
				return;
			}

			string args = CliProcessHandler.BuildSteamArguments(_steamUsername, GetEffectiveSteamPassword(), steamGuardCode, appVdfPath);
			_processHandler = CreateAndWireProcessHandler(CliProcessHandler.CliToolKind.SteamCmd);
			_activeToolKind = CliProcessHandler.CliToolKind.SteamCmd;

			_taskLabel = "Uploading to Steam via SteamCMD...";
			_progressValue = 0.75f;
			_state = DeployState.Uploading;
			_isProcessRunning = true;

			AppendPlatformLog(DeployTargets.Steam, $"Launching: {ResolveSteamCmdPath()}", false);
			if (!_processHandler.Start(ResolveSteamCmdPath(), args))
			{
				_isProcessRunning = false;
				SetFailedState("Failed to start steamcmd.");
			}
		}

		private void LaunchItchIoUpload()
		{
			string buildOutputPath = ResolveSelectedBuildOutputPath();
			string[] ignorePatterns = SplitIgnorePatterns(_itchIoConfig.IgnoreFiles);
			string args = CliProcessHandler.BuildButlerPushArguments(
				buildOutputPath,
				_itchIoConfig.Target.Trim(),
				_itchIoConfig.Channel.Trim(),
				ResolveMacros(_itchIoConfig.UserVersion),
				_itchIoConfig.Hidden,
				_itchIoConfig.IfChanged,
				ignorePatterns);

			_processHandler = CreateAndWireProcessHandler(CliProcessHandler.CliToolKind.Butler);
			_activeToolKind = CliProcessHandler.CliToolKind.Butler;
			_taskLabel = "Uploading to itch.io via butler...";
			_progressValue = 0.75f;
			_state = DeployState.Uploading;
			_isProcessRunning = true;

			var env = new Dictionary<string, string>
			{
				["BUTLER_API_KEY"] = GetEffectiveItchIoApiKey(),
			};

			AppendPlatformLog(DeployTargets.ItchIo, $"Launching: {ResolveButlerPath()}", false);
			AppendPlatformLog(DeployTargets.ItchIo, $"Target: {_itchIoConfig.Target}:{_itchIoConfig.Channel}", false);
			if (!_processHandler.Start(ResolveButlerPath(), args, env))
			{
				_isProcessRunning = false;
				SetFailedState("Failed to start butler.");
			}
		}

		private bool RunBuildIntoResolvedPath(string buildOutputPath, out string failureReason)
		{
			failureReason = null;
			_taskLabel = "Building Unity project...";
			_progressValue = 0.15f;
			Repaint();

			string tempOutputPath = buildOutputPath + "_steamdeployer_tmp";
			if (Directory.Exists(tempOutputPath))
				Directory.Delete(tempOutputPath, true);
			Directory.CreateDirectory(tempOutputPath);

			BuildReport report = RunUnityBuild(tempOutputPath);
			if (report == null || report.summary.result != BuildResult.Succeeded)
			{
				try { Directory.Delete(tempOutputPath, true); } catch { }
				string detail = report != null ? $"Result={report.summary.result}, Errors={report.summary.totalErrors}" : "BuildReport was null (build may have been cancelled).";
				AppendGeneralLog($"BUILD FAILED: {detail}", true);
				Debug.LogError($"[SteamItchIoDeployer] Unity build FAILED — {detail}.");
				failureReason = "Build failed.";
				return false;
			}

			if (Directory.Exists(buildOutputPath))
				Directory.Delete(buildOutputPath, true);
			Directory.Move(tempOutputPath, buildOutputPath);

			AppendGeneralLog($"Build succeeded -> {buildOutputPath}", false);
			Debug.Log($"[SteamItchIoDeployer] Unity build succeeded. Output: {buildOutputPath}");
			return true;
		}

		private BuildReport RunUnityBuild(string outputPath)
		{
			try
			{
	#if UNITY_6000_0_OR_NEWER
				if (_buildDeployConfig != null && _buildDeployConfig.BuildProfile != null)
				{
					BuildTarget profileTarget = GetBuildTargetFromProfile(_buildDeployConfig.BuildProfile);
					var profileOptions = new BuildPlayerWithProfileOptions
					{
						buildProfile = _buildDeployConfig.BuildProfile,
						locationPathName = GetBuildLocationPath(outputPath, profileTarget),
						options = BuildOptions.None,
					};

					return BuildPipeline.BuildPlayer(profileOptions);
				}
	#endif

				BuildPlayerOptions opts = GetBuildPlayerOptionsWithoutDialog();
				opts.locationPathName = GetBuildLocationPath(outputPath, opts.target);
				return BuildPipeline.BuildPlayer(opts);
			}
			catch (TargetInvocationException ex)
			{
				Exception inner = ex.InnerException ?? ex;
				Debug.LogError($"[SteamItchIoDeployer] Failed to resolve build settings: {inner.GetType().Name}: {inner.Message}");
				return null;
			}
			catch (BuildPlayerWindow.BuildMethodException ex)
			{
				Debug.LogWarning($"[SteamItchIoDeployer] Build was cancelled or settings are invalid: {ex.Message}");
				return null;
			}
			catch (Exception ex)
			{
				Debug.LogError($"[SteamItchIoDeployer] Unexpected error during build: {ex.Message}");
				return null;
			}
		}

		public static BuildPlayerOptions GetBuildPlayerOptionsWithoutDialog()
		{
			var internalMethod = typeof(BuildPlayerWindow.DefaultBuildMethods)
				.GetMethod("GetBuildPlayerOptionsInternal", BindingFlags.NonPublic | BindingFlags.Static);

			if (internalMethod != null)
			{
				try
				{
					return (BuildPlayerOptions)internalMethod.Invoke(null, new object[] { false, new BuildPlayerOptions() });
				}
				catch (TargetInvocationException ex)
				{
					Exception inner = ex.InnerException ?? ex;
					Debug.LogWarning($"[SteamItchIoDeployer] Internal build option resolution failed, falling back to manual settings: {inner.GetType().Name}: {inner.Message}");
				}
			}

			string[] enabledScenes = EditorBuildSettings.scenes
				.Where(scene => scene.enabled)
				.Select(scene => scene.path)
				.Where(path => !string.IsNullOrWhiteSpace(path))
				.ToArray();

			if (enabledScenes.Length == 0)
				throw new InvalidOperationException("No enabled scenes found in Build Settings.");

			BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
			return new BuildPlayerOptions
			{
				scenes = enabledScenes,
				target = target,
				targetGroup = BuildPipeline.GetBuildTargetGroup(target),
				options = BuildOptions.None,
			};
		}

		private static string GetBuildLocationPath(string outputPath, BuildTarget target)
		{
			if (target == BuildTarget.WebGL)
				return outputPath;

			return Path.Combine(outputPath, Application.productName + GetExeExtension(target));
		}

	#if UNITY_6000_0_OR_NEWER
		private static BuildTarget GetBuildTargetFromProfile(UnityEditor.Build.Profile.BuildProfile buildProfile)
		{
			if (buildProfile == null)
				throw new ArgumentNullException(nameof(buildProfile));

			SerializedProperty buildTargetProperty = new SerializedObject(buildProfile).FindProperty("m_BuildTarget");
			if (buildTargetProperty == null)
				throw new InvalidOperationException("Unable to resolve Build Profile target platform.");

			return (BuildTarget)buildTargetProperty.intValue;
		}
	#endif

		private void SubmitSteamGuardCode()
		{
			GUIUtility.keyboardControl = 0;
			string code = _steamGuardCodeInput.Trim();
			_steamGuardCodeInput = "";

			if (_isTestLoginContext)
			{
				LaunchSteamTestLogin(code);
			}
			else
			{
				_state = DeployState.Uploading;
				_taskLabel = "Uploading to Steam via SteamCMD...";
				_progressValue = 0.75f;
				LaunchSteamUpload(code);
			}
		}

		private void HandleProcessExited(int exitCode)
		{
			_isProcessRunning = false;
			_processHandler?.Dispose();
			_processHandler = null;

			if (exitCode == 0)
			{
				if (_isTestLoginContext)
				{
					_state = DeployState.Success;
					_progressValue = 1f;
					_taskLabel = "Login successful!";
					AppendPlatformLog(DeployTargets.Steam, "=== LOGIN TEST SUCCESSFUL ===", false);
					return;
				}

				DeployTargets finishedTarget = _pendingUploads.Count > 0 ? _pendingUploads.Dequeue() : DeployTargets.None;
				AppendPlatformLog(finishedTarget, finishedTarget == DeployTargets.Steam ? "=== STEAM UPLOAD SUCCESSFUL ===" : "=== ITCH.IO UPLOAD SUCCESSFUL ===", false);
				LaunchNextUploadTarget();
				return;
			}

			if (_isTestLoginContext)
			{
				SetFailedState($"Login test failed (exit code {exitCode}).");
				AppendPlatformLog(DeployTargets.Steam, $"=== LOGIN TEST FAILED (exit code {exitCode}) ===", true);
				AppendPlatformLog(DeployTargets.Steam, $"Exit code {exitCode}: {CliProcessHandler.DescribeSteamExitCode(exitCode)}", true);
				return;
			}

			if (_activeToolKind == CliProcessHandler.CliToolKind.SteamCmd)
			{
				SetFailedState($"SteamCMD exited with code {exitCode}.");
				AppendPlatformLog(DeployTargets.Steam, $"=== STEAM UPLOAD FAILED (exit code {exitCode}) ===", true);
				AppendPlatformLog(DeployTargets.Steam, $"Exit code {exitCode}: {CliProcessHandler.DescribeSteamExitCode(exitCode)}", true);
			}
			else
			{
				SetFailedState($"butler exited with code {exitCode}.");
				AppendPlatformLog(DeployTargets.ItchIo, $"=== ITCH.IO UPLOAD FAILED (exit code {exitCode}) ===", true);
			}
		}

		private void HandleSteamGuardRequired(string message)
		{
			_processHandler?.Kill();
			_processHandler?.Dispose();
			_processHandler = null;
			_isProcessRunning = false;

			AppendPlatformLog(DeployTargets.Steam, $"Steam Guard code required: {message}", false);
			_state = DeployState.WaitingForSteamGuard;
			_steamGuardCodeInput = "";
			GUIUtility.keyboardControl = 0;
			Repaint();
		}

		private void HandleAuthFailure(string message)
		{
			_processHandler?.Kill();
			_processHandler?.Dispose();
			_processHandler = null;
			_isProcessRunning = false;

			AppendLogForTool(_activeToolKind, $"AUTH FAILURE: {message}", true);
			SetFailedState("Authentication failed.");
			Repaint();

			string title = _activeToolKind == CliProcessHandler.CliToolKind.Butler ? "itch.io Authentication Failed" : "Steam Authentication Failed";
			EditorApplication.delayCall += () => EditorUtility.DisplayDialog(title, message, "OK");
		}

		private void CancelOperation()
		{
			_processHandler?.Kill();
			_processHandler?.Dispose();
			_processHandler = null;
			_isProcessRunning = false;
			_pendingUploads.Clear();
			_state = DeployState.Setup;
			_taskLabel = "";
			AppendGeneralLog("Operation was manually cancelled.", true);
		}

		private CliProcessHandler CreateAndWireProcessHandler(CliProcessHandler.CliToolKind toolKind)
		{
			var handler = new CliProcessHandler(toolKind);
			handler.OnLogLine += line => AppendLogForTool(toolKind, line, false);
			handler.OnErrorLine += line => AppendLogForTool(toolKind, line, true);
			handler.OnSteamGuardRequired += HandleSteamGuardRequired;
			handler.OnAuthenticationFailure += HandleAuthFailure;
			handler.OnProcessExited += HandleProcessExited;
			return handler;
		}

		private bool ValidateSelectedTargetsForUpload(bool showDialogs, bool requireCredentials = false, bool requireBuildOutput = false)
		{
			DeployTargets selectedTargets = GetSelectedTargets();
			if (selectedTargets == DeployTargets.None)
				return ShowValidationError(showDialogs, "Error", "No target platform selected.");

			if ((selectedTargets & DeployTargets.Steam) != 0 && !ValidateSteamSelection(showDialogs, requireCredentials, requireBuildOutput))
				return false;

			if ((selectedTargets & DeployTargets.ItchIo) != 0 && !ValidateItchIoSelection(showDialogs, requireCredentials, requireBuildOutput))
				return false;

			return ValidateSharedBuildOutput(showDialogs, requireBuildOutput);
		}

		private bool ValidateSteamSelection(bool showDialogs, bool requireCredentials, bool requireBuildOutput)
		{
			if (_steamConfig == null)
				return ShowValidationError(showDialogs, "Error", "No Steam config asset is assigned.");

			if (string.IsNullOrWhiteSpace(_steamConfig.AppID) || string.IsNullOrWhiteSpace(_steamConfig.DepotID))
				return ShowValidationError(showDialogs, "Error", "Steam App ID and Depot ID are required.");

			if (string.IsNullOrWhiteSpace(_steamConfig.SteamCmdPath) || !File.Exists(ResolveSteamCmdPath()))
				return ShowValidationError(showDialogs, "SteamCMD Not Found", $"steamcmd not found at:\n{_steamConfig.SteamCmdPath}");

			if (requireCredentials && !ValidateSteamLogin(showDialogs))
				return false;

			if (requireBuildOutput && string.IsNullOrWhiteSpace(_buildDeployConfig?.BuildOutputPath))
				return ShowValidationError(showDialogs, "Error", "Shared Build Output Path is not set.");

			string steamCmdDir = Path.GetDirectoryName(ResolveSteamCmdPath());
			string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
			if (HasNonAscii(steamCmdDir))
				return ShowValidationError(showDialogs, "Non-ASCII Path Detected", $"The SteamCMD directory contains non-ASCII characters:\n\n{steamCmdDir}");
			if (HasNonAscii(projectRoot))
				return ShowValidationError(showDialogs, "Non-ASCII Project Path Detected", $"The Unity project path contains non-ASCII characters:\n\n{projectRoot}");

			return true;
		}

		private bool ValidateSteamLogin(bool showDialogs)
		{
			if (string.IsNullOrWhiteSpace(_steamUsername) || string.IsNullOrWhiteSpace(_steamPassword))
				return ShowValidationError(showDialogs, "Credentials Missing", "Please enter your Steam username and password.");
			return true;
		}

		private bool ValidateItchIoSelection(bool showDialogs, bool requireCredentials, bool requireBuildOutput)
		{
			if (_itchIoConfig == null)
				return ShowValidationError(showDialogs, "Error", "No itch.io config asset is assigned.");

			if (string.IsNullOrWhiteSpace(_itchIoConfig.Target) || !_itchIoConfig.Target.Contains("/"))
				return ShowValidationError(showDialogs, "Error", "itch.io Target must use the format username/game.");

			if (string.IsNullOrWhiteSpace(_itchIoConfig.Channel))
				return ShowValidationError(showDialogs, "Error", "itch.io Channel is required.");

			if (string.IsNullOrWhiteSpace(_itchIoConfig.ButlerPath) || !File.Exists(ResolveButlerPath()))
				return ShowValidationError(showDialogs, "Butler Not Found", $"butler not found at:\n{_itchIoConfig.ButlerPath}");

			if (requireCredentials && string.IsNullOrWhiteSpace(GetEffectiveItchIoApiKey()))
				return ShowValidationError(showDialogs, "Credentials Missing", "Please enter your itch.io BUTLER_API_KEY.");

			if (requireBuildOutput && string.IsNullOrWhiteSpace(_buildDeployConfig?.BuildOutputPath))
				return ShowValidationError(showDialogs, "Error", "Shared Build Output Path is not set.");

			return true;
		}

		private bool ValidateSharedBuildOutput(bool showDialogs, bool requireBuildOutput)
		{
			if (!requireBuildOutput) return true;

			if (_buildDeployConfig == null)
				return ShowValidationError(showDialogs, "Error", "No Build/Deploy config asset is assigned.");

			string resolvedPath = ResolveSelectedBuildOutputPath();
			if (string.IsNullOrWhiteSpace(resolvedPath))
				return ShowValidationError(showDialogs, "Error", "Build Output Path is not set for the selected targets.");

			return true;
		}

		private bool ShowValidationError(bool showDialogs, string title, string message)
		{
			if (showDialogs)
				EditorUtility.DisplayDialog(title, message, "OK");
			return false;
		}

		private bool EnsureBuildOutputPathForBuild()
		{
			if (GetSelectedTargets() == DeployTargets.None)
			{
				EditorUtility.DisplayDialog("Error", "No target platform selected.", "OK");
				return false;
			}

			if (HasAnyBuildOutputPath())
				return ValidateSelectedTargetsForUpload(showDialogs: true, requireCredentials: false, requireBuildOutput: true);

			string picked = EditorUtility.OpenFolderPanel("Select Build Output Folder", "", "");
			if (string.IsNullOrEmpty(picked)) return false;

			string normalized = NormalizeProjectRelativePath(picked);
			ApplySharedBuildOutputPath(normalized);
			return true;
		}

		private void ApplySharedBuildOutputPath(string path)
		{
			if (_buildDeployConfig == null) return;
			_buildDeployConfig.BuildOutputPath = path;
			SaveConfig(_buildDeployConfig, false);
		}

		private bool HasAnyBuildOutputPath()
		{
			return !string.IsNullOrWhiteSpace(ResolveSelectedBuildOutputPath());
		}

		private bool CanBuildSelectedTargets()
		{
			DeployTargets selectedTargets = GetSelectedTargets();
			if (selectedTargets == DeployTargets.None) return false;
			if (_buildDeployConfig == null) return false;
			if ((selectedTargets & DeployTargets.Steam) != 0 && _steamConfig == null) return false;
			if ((selectedTargets & DeployTargets.ItchIo) != 0 && _itchIoConfig == null) return false;
			return true;
		}

		private bool CheckAnyBuildOutputExists()
		{
			string path = ResolveSelectedBuildOutputPath();
			if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;

			BuildTarget target = GetSelectedBuildTarget();
			if (target == BuildTarget.WebGL)
				return HasWebGlBuildOutput(path);

			return Directory.GetFiles(path, "*.exe", SearchOption.TopDirectoryOnly).Length > 0
				|| Directory.GetFiles(path, "*.app", SearchOption.TopDirectoryOnly).Length > 0
				|| Directory.GetFiles(path, "*.x86_64", SearchOption.TopDirectoryOnly).Length > 0
				|| Directory.GetFiles(path, "*.x86", SearchOption.TopDirectoryOnly).Length > 0;
		}

		private BuildTarget GetSelectedBuildTarget()
		{
	#if UNITY_6000_0_OR_NEWER
			if (_buildDeployConfig != null && _buildDeployConfig.BuildProfile != null)
			{
				try
				{
					return GetBuildTargetFromProfile(_buildDeployConfig.BuildProfile);
				}
				catch
				{
				}
			}
	#endif

			return EditorUserBuildSettings.activeBuildTarget;
		}

		private static bool HasWebGlBuildOutput(string path)
		{
			return Directory.GetFiles(path, "index.html", SearchOption.AllDirectories).Length > 0
				&& Directory.GetDirectories(path, "Build", SearchOption.AllDirectories).Length > 0;
		}

		private string ResolveSelectedBuildOutputPath()
		{
			return ResolveConfigPath(_buildDeployConfig?.BuildOutputPath);
		}

		private void PersistSavedCredentials()
		{
			if (_saveSteamCredentials && !string.IsNullOrEmpty(_steamPassword))
				CryptographyHelper.SaveEncryptedValue(GetProjectScopedPrefsKey(SteamPasswordCipherPrefsKey), _steamPassword);

			string itchIoApiKey = GetCurrentItchIoApiKeyInput();
			if (_saveItchIoApiKey && !string.IsNullOrEmpty(itchIoApiKey))
				CryptographyHelper.SaveEncryptedValue(GetProjectScopedPrefsKey(ItchIoApiKeyCipherPrefsKey), itchIoApiKey);
		}

		private string GetEffectiveSteamPassword()
		{
			return _saveSteamCredentials ? (CryptographyHelper.LoadDecryptedValue(GetProjectScopedPrefsKey(SteamPasswordCipherPrefsKey)) ?? _steamPassword) : _steamPassword;
		}

		private string GetEffectiveItchIoApiKey()
		{
			string currentInput = GetCurrentItchIoApiKeyInput();
			if (!string.IsNullOrEmpty(currentInput))
				return currentInput;

			if (!_saveItchIoApiKey)
				return currentInput;

			return CryptographyHelper.LoadDecryptedValue(GetProjectScopedPrefsKey(ItchIoApiKeyCipherPrefsKey))?.Trim();
		}

		private string GetCurrentItchIoApiKeyInput()
		{
			return _itchIoApiKey?.Trim();
		}

		private static string GetProjectScopedPrefsKey(string baseKey)
		{
			if (string.IsNullOrWhiteSpace(baseKey))
				return baseKey;

			string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
			using (var sha256 = SHA256.Create())
			{
				byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(projectRoot));
				string projectHash = BitConverter.ToString(hashBytes).Replace("-", "").Substring(0, 16);
				return $"{baseKey}_{projectHash}";
			}
		}

		private void RefreshExecutableExists()
		{
			_steamCmdFileExists = !string.IsNullOrWhiteSpace(_steamConfig?.SteamCmdPath) && File.Exists(ResolveSteamCmdPath());
			_itchButlerFileExists = !string.IsNullOrWhiteSpace(_itchIoConfig?.ButlerPath) && File.Exists(ResolveButlerPath());
		}

		private string ResolveSteamCmdPath()
		{
			return ResolveConfigPath(_steamConfig?.SteamCmdPath);
		}

		private string ResolveButlerPath()
		{
			return ResolveConfigPath(_itchIoConfig?.ButlerPath);
		}

		private static string ResolveConfigPath(string path)
		{
			if (string.IsNullOrEmpty(path)) return "";
			if (Path.IsPathRooted(path)) return path;
			string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
			return Path.GetFullPath(Path.Combine(projectRoot, path));
		}

		private static string NormalizeProjectRelativePath(string absolutePath)
		{
			string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/');
			string normalized = absolutePath.Replace('\\', '/');

			if (normalized.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
				return normalized.Substring(projectRoot.Length).TrimStart('/');

			return absolutePath;
		}

		private static bool PathsEqual(string left, string right)
		{
			if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
			return string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
		}

		private static bool HasNonAscii(string path)
		{
			if (string.IsNullOrEmpty(path)) return false;
			foreach (char c in path)
				if (c > 127) return true;
			return false;
		}

		private static string ResolveMacros(string template)
		{
			if (string.IsNullOrEmpty(template)) return Application.version;
			return template
				.Replace("{Version}", Application.version)
				.Replace("{Date}", DateTime.Now.ToString("yyyy-MM-dd"))
				.Replace("{DateTime}", DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss"));
		}

		private static string[] SplitIgnorePatterns(string csv)
		{
			if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<string>();
			return csv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
		}

		private static string GetExeExtension(BuildTarget target)
		{
			switch (target)
			{
				case BuildTarget.StandaloneWindows:
				case BuildTarget.StandaloneWindows64: return ".exe";
				case BuildTarget.StandaloneOSX: return ".app";
				case BuildTarget.StandaloneLinux64: return ".x86_64";
				default: return "";
			}
		}

		private static string GetEditorLogPath()
		{
	#if UNITY_EDITOR_WIN
			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unity", "Editor", "Editor.log");
	#elif UNITY_EDITOR_OSX
			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Logs", "Unity", "Editor.log");
	#else
			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "unity3d", "Editor.log");
	#endif
		}

		private void RevealEditorLog()
		{
			string path = GetEditorLogPath();
			if (File.Exists(path))
				EditorUtility.RevealInFinder(path);
			else
				EditorUtility.DisplayDialog("Not Found", $"Editor log not found at:\n{path}", "OK");
		}

		private void AppendGeneralLog(string line, bool isError)
		{
			AppendLog(ref _generalLogBuffer, ref _generalLogScroll, line, isError);
		}

		private void AppendPlatformLog(DeployTargets target, string line, bool isError)
		{
			if (target == DeployTargets.Steam)
			{
				AppendLog(ref _steamLogBuffer, ref _steamLogScroll, line, isError);
				return;
			}

			if (target == DeployTargets.ItchIo)
			{
				AppendLog(ref _itchIoLogBuffer, ref _itchIoLogScroll, line, isError);
				return;
			}

			AppendGeneralLog(line, isError);
		}

		private void AppendLogForTool(CliProcessHandler.CliToolKind toolKind, string line, bool isError)
		{
			if (toolKind == CliProcessHandler.CliToolKind.SteamCmd)
			{
				AppendPlatformLog(DeployTargets.Steam, line, isError);
				return;
			}

			if (toolKind == CliProcessHandler.CliToolKind.Butler)
			{
				AppendPlatformLog(DeployTargets.ItchIo, line, isError);
				return;
			}

			AppendGeneralLog(line, isError);
		}

		private static void AppendLog(ref string buffer, ref Vector2 scroll, string line, bool isError)
		{
			string ts = DateTime.Now.ToString("HH:mm:ss");
			string tag = isError ? "ERR" : "LOG";
			string entry = $"[{ts}][{tag}] {line}\n";
			buffer += entry;

			if (buffer.Length > MaxLogBufferChars)
			{
				int cutAt = buffer.Length - MaxLogBufferChars;
				int nl = buffer.IndexOf('\n', cutAt);
				buffer = nl >= 0 ? buffer.Substring(nl + 1) : buffer.Substring(cutAt);
			}

			scroll = new Vector2(0, float.MaxValue);
		}

		private string GetSelectedLogBuffer()
		{
			if (_selectedLogTab == LogTab.Steam) return _steamLogBuffer;
			if (_selectedLogTab == LogTab.ItchIo) return _itchIoLogBuffer;
			return _generalLogBuffer;
		}

		private Vector2 GetSelectedLogScroll()
		{
			if (_selectedLogTab == LogTab.Steam) return _steamLogScroll;
			if (_selectedLogTab == LogTab.ItchIo) return _itchIoLogScroll;
			return _generalLogScroll;
		}

		private void SetSelectedLogScroll(Vector2 scroll)
		{
			if (_selectedLogTab == LogTab.Steam)
			{
				_steamLogScroll = scroll;
				return;
			}

			if (_selectedLogTab == LogTab.ItchIo)
			{
				_itchIoLogScroll = scroll;
				return;
			}

			_generalLogScroll = scroll;
		}

		private void ClearSelectedLogBuffer()
		{
			if (_selectedLogTab == LogTab.Steam)
			{
				_steamLogBuffer = "";
				_steamLogScroll = Vector2.zero;
				return;
			}

			if (_selectedLogTab == LogTab.ItchIo)
			{
				_itchIoLogBuffer = "";
				_itchIoLogScroll = Vector2.zero;
				return;
			}

			_generalLogBuffer = "";
			_generalLogScroll = Vector2.zero;
		}

		private void ClearAllLogBuffers()
		{
			_generalLogBuffer = "";
			_steamLogBuffer = "";
			_itchIoLogBuffer = "";
			_generalLogScroll = Vector2.zero;
			_steamLogScroll = Vector2.zero;
			_itchIoLogScroll = Vector2.zero;
		}

		private void SetFailedState(string reason)
		{
			_pendingUploads.Clear();
			_state = DeployState.Failed;
			_taskLabel = reason;
		}

		private void TryLoadConfigs()
		{
			string[] buildDeployGuids = AssetDatabase.FindAssets("t:BuildDeployConfig");
			if (buildDeployGuids.Length > 0)
				_buildDeployConfig = AssetDatabase.LoadAssetAtPath<BuildDeployConfig>(AssetDatabase.GUIDToAssetPath(buildDeployGuids[0]));

			string[] steamGuids = AssetDatabase.FindAssets("t:SteamDeployConfig");
			if (steamGuids.Length > 0)
				_steamConfig = AssetDatabase.LoadAssetAtPath<SteamDeployConfig>(AssetDatabase.GUIDToAssetPath(steamGuids[0]));

			string[] itchGuids = AssetDatabase.FindAssets("t:ItchIoDeployConfig");
			if (itchGuids.Length > 0)
				_itchIoConfig = AssetDatabase.LoadAssetAtPath<ItchIoDeployConfig>(AssetDatabase.GUIDToAssetPath(itchGuids[0]));
		}

		private void EnsureBuildDeployDefaults()
		{
			DeployTargets selectedTargets = GetSelectedTargets();
			if (selectedTargets == DeployTargets.ItchIo)
				_selectedTab = PlatformTab.ItchIo;
			else
				_selectedTab = PlatformTab.Steam;
		}

		private void CreateBuildDeployConfigAsset()
		{
			const string subFolder = "Assets/Editor/SteamItchIoDeployer";
			EnsureEditorFolders(subFolder);
			string path = $"{subFolder}/BuildDeployConfig.asset";
			_buildDeployConfig = CreateInstance<BuildDeployConfig>();
			AssetDatabase.CreateAsset(_buildDeployConfig, path);
			AssetDatabase.SaveAssets();
			EditorGUIUtility.PingObject(_buildDeployConfig);
			EnsureBuildDeployDefaults();
		}

		private DeployTargets GetSelectedTargets()
		{
			return _buildDeployConfig != null ? _buildDeployConfig.DeployTargets : DeployTargets.Steam;
		}

		private void SetSelectedTargets(DeployTargets targets)
		{
			if (_buildDeployConfig == null) return;
			_buildDeployConfig.DeployTargets = targets & (DeployTargets.Steam | DeployTargets.ItchIo);
			SaveConfig(_buildDeployConfig, refreshExecutables: false);
		}

		private void CreateSteamConfigAsset()
		{
			const string subFolder = "Assets/Editor/SteamItchIoDeployer";
			EnsureEditorFolders(subFolder);
			string path = $"{subFolder}/SteamDeployConfig.asset";
			_steamConfig = CreateInstance<SteamDeployConfig>();
			AssetDatabase.CreateAsset(_steamConfig, path);
			AssetDatabase.SaveAssets();
			EditorGUIUtility.PingObject(_steamConfig);
		}

		private void CreateItchIoConfigAsset()
		{
			const string subFolder = "Assets/Editor/SteamItchIoDeployer";
			EnsureEditorFolders(subFolder);
			string path = $"{subFolder}/ItchIoDeployConfig.asset";
			_itchIoConfig = CreateInstance<ItchIoDeployConfig>();
			AssetDatabase.CreateAsset(_itchIoConfig, path);
			AssetDatabase.SaveAssets();
			EditorGUIUtility.PingObject(_itchIoConfig);
		}

		private static void EnsureEditorFolders(string subFolder)
		{
			if (!AssetDatabase.IsValidFolder("Assets/Editor"))
				AssetDatabase.CreateFolder("Assets", "Editor");
			if (!AssetDatabase.IsValidFolder(subFolder))
				AssetDatabase.CreateFolder("Assets/Editor", "SteamItchIoDeployer");
		}

		private void SaveConfig(UnityEngine.Object config, bool refreshExecutables)
		{
			if (config == null) return;
			EditorUtility.SetDirty(config);
			AssetDatabase.SaveAssets();
			if (refreshExecutables)
				RefreshExecutableExists();
		}

		private void EnsureStyles()
		{
			if (_stylesReady) return;
			_stylesReady = true;

			_boxStyle = new GUIStyle(GUI.skin.box)
			{
				padding = new RectOffset(10, 10, 8, 8),
				margin = new RectOffset(4, 4, 2, 2),
			};

			_bigButtonStyle = new GUIStyle(GUI.skin.button)
			{
				fontSize = 15,
				fontStyle = FontStyle.Bold,
			};

			_logStyle = new GUIStyle(EditorStyles.textArea)
			{
				wordWrap = false,
				richText = false,
				fontSize = 10,
				font = EditorStyles.miniLabel.font,
			};

			if (EditorGUIUtility.isProSkin)
				_logStyle.normal.textColor = new Color(0.75f, 1f, 0.75f);

			_successBoxStyle = new GUIStyle(GUI.skin.box)
			{
				padding = new RectOffset(10, 10, 6, 6),
				normal = { background = MakeColorTex(new Color(0.1f, 0.55f, 0.1f, 0.6f)) },
			};

			_failureBoxStyle = new GUIStyle(GUI.skin.box)
			{
				padding = new RectOffset(10, 10, 6, 6),
				normal = { background = MakeColorTex(new Color(0.65f, 0.1f, 0.1f, 0.6f)) },
			};

			_warningBoxStyle = new GUIStyle(GUI.skin.box)
			{
				padding = new RectOffset(10, 10, 8, 8),
				margin = new RectOffset(4, 4, 2, 2),
				normal = { background = MakeColorTex(new Color(0.6f, 0.45f, 0f, 0.35f)) },
			};
		}

		private static Texture2D MakeColorTex(Color color)
		{
			var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
			tex.SetPixel(0, 0, color);
			tex.Apply();
			return tex;
		}

		private void DownloadAndInstallSteamCmd()
		{
			if (_steamConfig == null) return;

			string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
			string steamCmdDir = Path.Combine(projectRoot, "steamcmd");
			string zipPath = Path.Combine(steamCmdDir, "steamcmd_download.zip");
			string steamCmdExePath = Path.Combine(steamCmdDir, "steamcmd.exe");

			Directory.CreateDirectory(steamCmdDir);
			_isDownloadingSteamCmd = true;
			Repaint();

			Task.Run(() =>
			{
				using (var webClient = new WebClient())
					webClient.DownloadFile("https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip", zipPath);

				using (var archive = ZipFile.OpenRead(zipPath))
				{
					foreach (ZipArchiveEntry entry in archive.Entries)
					{
						string destinationPath = Path.GetFullPath(Path.Combine(steamCmdDir, entry.FullName));
						if (entry.Name == "")
						{
							Directory.CreateDirectory(destinationPath);
							continue;
						}
						Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
						entry.ExtractToFile(destinationPath, true);
					}
				}

				File.Delete(zipPath);
			}).ContinueWith(downloadTask =>
			{
				EditorApplication.delayCall += () =>
				{
					_isDownloadingSteamCmd = false;
					if (downloadTask.IsFaulted)
					{
						string errorMessage = downloadTask.Exception?.GetBaseException()?.Message ?? "Unknown error";
						EditorUtility.DisplayDialog("Download Failed", $"Failed to download SteamCMD:\n{errorMessage}", "OK");
						return;
					}

					_steamConfig.SteamCmdPath = NormalizeProjectRelativePath(steamCmdExePath.Replace('\\', '/'));
					SaveConfig(_steamConfig, true);
					AppendPlatformLog(DeployTargets.Steam, $"SteamCMD installed -> {steamCmdDir}", false);
					System.Diagnostics.Process.Start(steamCmdExePath);
				};
			});
		}

		private void DownloadAndInstallButler()
		{
			if (_itchIoConfig == null) return;

			string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
			string butlerDir = Path.Combine(projectRoot, "butler");
			string zipPath = Path.Combine(butlerDir, "butler_download.zip");
			string executableName = GetButlerExecutableName();
			string butlerExePath = Path.Combine(butlerDir, executableName);
			string downloadUrl = GetButlerDownloadUrl();

			if (string.IsNullOrEmpty(downloadUrl))
			{
				EditorUtility.DisplayDialog("Unsupported Platform", "Automatic butler download is only configured for Windows, macOS, and Linux editors.", "OK");
				return;
			}

			Directory.CreateDirectory(butlerDir);
			_isDownloadingButler = true;
			Repaint();

			Task.Run(() =>
			{
				using (var webClient = new WebClient())
					webClient.DownloadFile(downloadUrl, zipPath);

				using (var archive = ZipFile.OpenRead(zipPath))
				{
					foreach (ZipArchiveEntry entry in archive.Entries)
					{
						string destinationPath = Path.GetFullPath(Path.Combine(butlerDir, entry.FullName));
						if (entry.Name == "")
						{
							Directory.CreateDirectory(destinationPath);
							continue;
						}

						Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
						entry.ExtractToFile(destinationPath, true);
					}
				}

				File.Delete(zipPath);

	#if !UNITY_EDITOR_WIN
				try
				{
					var chmod = new System.Diagnostics.ProcessStartInfo
					{
						FileName = "/bin/chmod",
						Arguments = $"+x \"{butlerExePath}\"",
						UseShellExecute = false,
						CreateNoWindow = true,
					};
					using (var process = System.Diagnostics.Process.Start(chmod))
						process?.WaitForExit();
				}
				catch { }
	#endif
			}).ContinueWith(downloadTask =>
			{
				EditorApplication.delayCall += () =>
				{
					_isDownloadingButler = false;
					if (downloadTask.IsFaulted)
					{
						string errorMessage = downloadTask.Exception?.GetBaseException()?.Message ?? "Unknown error";
						EditorUtility.DisplayDialog("Download Failed", $"Failed to download butler:\n{errorMessage}", "OK");
						return;
					}

					_itchIoConfig.ButlerPath = NormalizeProjectRelativePath(butlerExePath.Replace('\\', '/'));
					SaveConfig(_itchIoConfig, true);
					AppendPlatformLog(DeployTargets.ItchIo, $"butler installed -> {butlerDir}", false);
				};
			});
		}

		private static string GetButlerExecutableName()
		{
	#if UNITY_EDITOR_WIN
			return "butler.exe";
	#else
			return "butler";
	#endif
		}

		private static string GetButlerDownloadUrl()
		{
	#if UNITY_EDITOR_WIN
			return "https://broth.itch.zone/butler/windows-amd64/LATEST/archive/default";
	#elif UNITY_EDITOR_OSX
			return "https://broth.itch.zone/butler/darwin-amd64/LATEST/archive/default";
	#elif UNITY_EDITOR_LINUX
			return "https://broth.itch.zone/butler/linux-amd64/LATEST/archive/default";
	#else
			return null;
	#endif
		}

		private static void WriteGitignore(string dir, string label)
		{
			string gitignorePath = Path.Combine(dir, ".gitignore");
			string content = $"# {label}\n*\n!.gitignore\n";
			File.WriteAllText(gitignorePath, content);
			EditorUtility.DisplayDialog("Done", $".gitignore created at:\n{gitignorePath}", "OK");
		}

		private static bool IsPathInsideProject(string absoluteDirPath)
		{
			if (string.IsNullOrEmpty(absoluteDirPath)) return false;
			string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
			return absoluteDirPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase);
		}

		private string GetSelectedTabLabel()
		{
			return _selectedTab == PlatformTab.Steam ? "Steam" : "itch.io";
		}

		private static bool IsWindowsEditor()
		{
	#if UNITY_EDITOR_WIN
			return true;
	#else
			return false;
	#endif
		}
	}
}
