using UnityEngine;

namespace SteamItchIoDeployer
{
    /// <summary>
    /// ScriptableObject asset that stores all non-sensitive Steam deployment configuration.
    /// Designed to live inside an Editor folder and be committed to version control.
    /// SECURITY: Passwords and sensitive credentials are NEVER stored here.
    /// Create via: Assets > Create > Steam itch.io Deployer > Steam Deploy Config
    /// </summary>
    [CreateAssetMenu(fileName = "SteamDeployConfig", menuName = "Steam itch.io Deployer/Steam Deploy Config")]
    public class SteamDeployConfig : ScriptableObject
    {
        [Tooltip("Your Steam Application ID (found on the Steamworks partner portal).")]
        public string AppID = "";

        [Tooltip("Your Steam Depot ID (typically AppID + 1 for single-depot apps).")]
        public string DepotID = "";

        [Tooltip("When enabled, automatically sets the specified branch live after a successful upload. " +
                 "Disable this for new apps that have not yet passed Valve's review queue, " +
                 "or when you want to promote the build manually from the Steamworks partner portal.")]
        public bool SetLiveEnabled = false;

        [Tooltip("The Steam branch to set live after upload. Use 'default' for the main public branch, " +
                 "or a beta branch name like 'staging' or 'beta'. Only used when Set Live is enabled.")]
        public string BuildBranch = "default";

        [Tooltip("Path to the steamcmd executable, WITHOUT file extension. " +
                 "The correct extension (.exe on Windows, .sh on macOS) is appended automatically at runtime. " +
                 "WARNING: Path must contain only ASCII characters (no CJK, accents, etc.).")]
        public string SteamCmdPath = "";

        [Tooltip("Comma-separated glob patterns for files to exclude from the depot upload. " +
                 "Example: *.pdb, _BurstDebugInformation_DoNotShip, *.lib")]
        public string IgnoreFiles = "*.pdb, _BurstDebugInformation_DoNotShip";

        [Tooltip("Human-readable description for this build shown in the Steamworks build history. " +
                 "Supports {Version}, {Date}, {DateTime}, and {GitSHA} macro substitution. " +
                 "{GitSHA} is resolved from 'git rev-parse HEAD'; falls back to 'NO_SHA' if git is unavailable or the project is not a repository.")]
        public string BuildDescription = "v{Version} - {Date} - {GitSHA}";
    }
}
