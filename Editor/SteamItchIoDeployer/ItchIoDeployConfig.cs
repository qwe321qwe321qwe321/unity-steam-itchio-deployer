using UnityEngine;

namespace SteamItchIoDeployer
{
	/// <summary>
	/// ScriptableObject asset that stores all non-sensitive itch.io deployment configuration.
	/// Sensitive auth is stored separately in EditorPrefs.
	/// </summary>
	[CreateAssetMenu(fileName = "ItchIoDeployConfig", menuName = "Steam itch.io Deployer/itch.io Deploy Config")]
	public class ItchIoDeployConfig : ScriptableObject
	{
		[Tooltip("Absolute filesystem path to the butler executable on this machine.")]
		public string ButlerPath = "";

		[Tooltip("itch.io target in the form username/game.")]
		public string Target = "";

		[Tooltip("itch.io channel name, such as windows, win-beta, mac-stable, etc.")]
		public string Channel = "windows";

		[Tooltip("Optional user-facing version label. Supports {Version}, {Date}, and {DateTime} macros.")]
		public string UserVersion = "{Version}";

		[Tooltip("Comma-separated glob patterns passed to butler via repeated --ignore flags.")]
		public string IgnoreFiles = "*.pdb, _BurstDebugInformation_DoNotShip";

		[Tooltip("Hide the channel on its first push so it does not appear on the public page immediately.")]
		public bool Hidden = false;

		[Tooltip("Skip creating a new build if the local contents are identical to the latest channel build.")]
		public bool IfChanged = false;
	}
}
