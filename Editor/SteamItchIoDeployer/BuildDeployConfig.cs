using System;
using UnityEngine;

namespace SteamItchIoDeployer
{
	[Flags]
	public enum DeployTargets
	{
		None   = 0,
		Steam  = 1 << 0,
		ItchIo = 1 << 1,
	}

	/// <summary>
	/// ScriptableObject asset that stores shared, non-sensitive build and deployment settings.
	/// Credentials stay in project-scoped EditorPrefs on the local machine so this asset is safe to commit.
	/// </summary>
	[CreateAssetMenu(fileName = "BuildDeployConfig", menuName = "Steam itch.io Deployer/Build Deploy Config")]
	public class BuildDeployConfig : ScriptableObject
	{
		[Tooltip("Select one or more upload targets. Build runs once, then uploads to each selected platform in sequence.")]
		public DeployTargets DeployTargets = DeployTargets.Steam;

		[Tooltip("Absolute or project-relative path to the directory where Unity outputs the build. This same directory is uploaded to the selected services.")]
		public string BuildOutputPath = "";

		[Min(1)]
		[Tooltip("Minimum wait time in seconds after a successful Steam upload. Steam can temporarily reject a second depot upload submitted too soon after the previous one, so this cooldown helps avoid that rate limit. Default is 120 seconds.")]
		public int UploadCooldownSeconds = 120;

	#if UNITY_6000_0_OR_NEWER
		[Tooltip("Optional Unity 6+ Build Profile asset to activate before building. Leave empty to use the current active build settings.")]
		public UnityEditor.Build.Profile.BuildProfile BuildProfile;
	#endif

		[Tooltip("Steam deployment configuration asset.")]
		public SteamDeployConfig SteamConfig;

		[Tooltip("itch.io deployment configuration asset.")]
		public ItchIoDeployConfig ItchIoConfig;
	}
}
