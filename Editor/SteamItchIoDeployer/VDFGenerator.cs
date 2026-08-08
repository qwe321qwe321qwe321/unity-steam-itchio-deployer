using System.IO;
using System.Text;
using SteamItchIoDeployerCore;
using UnityEngine;

namespace SteamItchIoDeployer
{
    /// <summary>
    /// Writes SteamCMD's app_build/depot_build VDF script files to disk. The VDF text itself is
    /// rendered by <see cref="VdfContentBuilder"/> in the shared core; this type only owns the
    /// Unity-specific file layout (steamcmd_dir/scripts, steamcmd_dir/logs).
    ///
    /// OUTPUT FILES:
    ///   {steamcmd_dir}/scripts/app_build_{AppID}.vdf
    ///   {steamcmd_dir}/scripts/depot_build_{DepotID}.vdf
    /// </summary>
    public static class VDFGenerator
    {
        /// <summary>
        /// Generates both the app_build and depot_build VDF files, writing them to the
        /// /scripts/ subdirectory adjacent to steamcmd.exe.
        /// </summary>
        /// <param name="config">Deployment configuration (AppID, DepotID, branch, etc.).</param>
        /// <param name="buildOutputPath">Absolute path to the Unity build output folder.</param>
        /// <param name="resolvedDescription">Build description with macros already substituted.</param>
        /// <param name="steamCmdAbsolutePath">Absolute path to steamcmd.exe (must be pre-resolved; relative paths are NOT accepted).</param>
        /// <returns>Absolute path to the generated app_build VDF file.</returns>
        public static string GenerateVdfScripts(SteamDeployConfig config, string buildOutputPath, string resolvedDescription, string steamCmdAbsolutePath)
        {
            string steamCmdDir = Path.GetDirectoryName(steamCmdAbsolutePath);
            string scriptsDir  = Path.Combine(steamCmdDir, "scripts");
            string buildLogDir = Path.Combine(steamCmdDir, "logs");

            Directory.CreateDirectory(scriptsDir);
            Directory.CreateDirectory(buildLogDir);

            var options = new SteamVdfOptions
            {
                AppId          = config.AppID,
                DepotId        = config.DepotID,
                SetLiveEnabled = config.SetLiveEnabled,
                Branch         = config.BuildBranch,
                IgnoreFiles    = config.IgnoreFiles,
            };

            string depotVdfPath = Path.Combine(scriptsDir, $"depot_build_{config.DepotID}.vdf");
            File.WriteAllText(depotVdfPath, VdfContentBuilder.BuildDepotVdfContent(options), Encoding.UTF8);
            Debug.Log($"[SteamItchIoDeployer] Written depot VDF: {depotVdfPath}");

            string appVdfContent = VdfContentBuilder.BuildAppVdfContent(
                options,
                Path.GetFullPath(buildOutputPath),
                Path.GetFullPath(buildLogDir),
                resolvedDescription,
                Path.GetFullPath(depotVdfPath));

            string appVdfPath = Path.Combine(scriptsDir, $"app_build_{config.AppID}.vdf");
            File.WriteAllText(appVdfPath, appVdfContent, Encoding.UTF8);
            Debug.Log($"[SteamItchIoDeployer] Written app VDF: {appVdfPath}");

            return appVdfPath;
        }
    }
}
