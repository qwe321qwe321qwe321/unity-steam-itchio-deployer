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
	/// Credentials stay in EditorPrefs so this asset is safe to commit.
	/// </summary>
	[CreateAssetMenu(fileName = "BuildDeployConfig", menuName = "Steam itch.io Deployer/Build Deploy Config")]
	public class BuildDeployConfig : ScriptableObject
	{
		[Tooltip("Select one or more upload targets. Build runs once, then uploads to each selected platform in sequence.")]
		public DeployTargets DeployTargets = DeployTargets.Steam;

		[Tooltip("Absolute or project-relative path to the directory where Unity outputs the build. This same directory is uploaded to the selected services.")]
		public string BuildOutputPath = "";

	#if UNITY_6000_0_OR_NEWER
		[Tooltip("Optional Unity 6+ Build Profile asset to activate before building. Leave empty to use the current active build settings.")]
		public UnityEditor.Build.Profile.BuildProfile BuildProfile;
	#endif
	}
}
