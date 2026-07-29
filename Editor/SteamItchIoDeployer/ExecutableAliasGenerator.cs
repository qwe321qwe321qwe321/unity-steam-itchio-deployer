using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace SteamItchIoDeployer
{
    /// <summary>
    /// Generates a small forwarder executable so a Steamworks-configured launch executable name
    /// (e.g. "App.exe" / "App.app") can point at a differently-named Unity build output
    /// (e.g. "MyApp.exe" / "MyApp.app"). No prebuilt binaries ship with this package:
    ///   - Windows: a tiny C# forwarder is compiled on the fly via csc.exe.
    ///   - macOS: a forwarder ".app" bundle is generated whose CFBundleExecutable is a shell
    ///     script that execs the real app's binary.
    /// </summary>
    public static class ExecutableAliasGenerator
    {
        /// <summary>
        /// Generates a forwarder for the given build target. realBaseName/altBaseNameRaw are the
        /// product name without extension (e.g. "MyApp", "App"); a trailing ".exe"/".app" typed by
        /// the user is stripped automatically. No-op (returns true) when altBaseNameRaw is blank or
        /// resolves to the same name as realBaseName.
        /// </summary>
        public static bool TryGenerateAlias(BuildTarget target, string buildOutputPath, string realBaseName, string altBaseNameRaw, out string message)
        {
            message = null;

            if (string.IsNullOrWhiteSpace(altBaseNameRaw))
                return true;

            string altBaseName = StripKnownExtension(altBaseNameRaw.Trim());
            if (string.IsNullOrEmpty(altBaseName))
                return true;

            if (string.Equals(altBaseName, realBaseName, StringComparison.OrdinalIgnoreCase))
            {
                message = "Executable Alt Name matches the actual build executable name; skipping alias generation.";
                return true;
            }

            if (altBaseName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                message = $"Executable Alt Name '{altBaseName}' contains invalid file name characters.";
                return false;
            }

            switch (target)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                    return TryGenerateWindowsAlias(buildOutputPath, realBaseName + ".exe", altBaseName + ".exe", out message);

                case BuildTarget.StandaloneOSX:
                    return TryGenerateMacAlias(buildOutputPath, realBaseName, altBaseName, out message);

                default:
                    message = $"Executable alias generation is not supported for build target {target}.";
                    return false;
            }
        }

        private static string StripKnownExtension(string name)
        {
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - 4);
            return name;
        }

        // ─── Windows ──────────────────────────────────────────────────────────────

        private static bool TryGenerateWindowsAlias(string buildOutputPath, string realExeFileName, string altExeFileName, out string message)
        {
            message = null;

            string cscPath = FindCscPath();
            if (cscPath == null)
            {
                message = "Could not locate a usable csc.exe (checked the .NET Framework install and the Unity Editor's bundled Roslyn compiler); skipping alias generation.";
                return false;
            }

            string sourcePath = Path.Combine(buildOutputPath, "__alias_launcher_tmp.cs");
            string outputPath = Path.Combine(buildOutputPath, altExeFileName);

            try
            {
                File.WriteAllText(sourcePath, BuildWindowsSource(realExeFileName), Encoding.UTF8);

                var psi = new ProcessStartInfo
                {
                    FileName = cscPath,
                    Arguments = $"/nologo /target:winexe /platform:anycpu /reference:System.dll /out:\"{outputPath}\" \"{sourcePath}\"",
                    WorkingDirectory = buildOutputPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using (var process = Process.Start(psi))
                {
                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit(30000);

                    if (!process.HasExited || process.ExitCode != 0 || !File.Exists(outputPath))
                    {
                        message = $"csc.exe ({cscPath}) failed to generate '{altExeFileName}'. {stdout}\n{stderr}".Trim();
                        return false;
                    }
                }

                message = $"Generated alias executable '{altExeFileName}' -> launches '{realExeFileName}' (compiler: {cscPath}).";
                return true;
            }
            finally
            {
                try { if (File.Exists(sourcePath)) File.Delete(sourcePath); } catch { }
            }
        }

        /// <summary>
        /// Builds the C# source for the Windows forwarder executable. It resolves the real
        /// executable relative to its own location, launches it (forwarding command-line
        /// arguments), and exits immediately without waiting for the child process.
        /// </summary>
        private static string BuildWindowsSource(string realExeFileName)
        {
            string escapedTarget = realExeFileName.Replace("\\", "\\\\").Replace("\"", "\\\"");

            var sb = new StringBuilder();
            sb.AppendLine("using System.Diagnostics;");
            sb.AppendLine("using System.IO;");
            sb.AppendLine("using System.Text;");
            sb.AppendLine();
            sb.AppendLine("internal static class AliasLauncher");
            sb.AppendLine("{");
            sb.AppendLine("    private static void Main(string[] args)");
            sb.AppendLine("    {");
            sb.AppendLine("        try");
            sb.AppendLine("        {");
            sb.AppendLine("            string here = Path.GetDirectoryName(typeof(AliasLauncher).Assembly.Location);");
            sb.AppendLine("            string target = Path.Combine(here, \"" + escapedTarget + "\");");
            sb.AppendLine("            var argSb = new StringBuilder();");
            sb.AppendLine("            foreach (string a in args)");
            sb.AppendLine("            {");
            sb.AppendLine("                if (argSb.Length > 0) argSb.Append(' ');");
            sb.AppendLine("                argSb.Append('\"').Append(a).Append('\"');");
            sb.AppendLine("            }");
            sb.AppendLine("            Process.Start(target, argSb.ToString());");
            sb.AppendLine("        }");
            sb.AppendLine("        catch { }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string FindCscPath()
        {
            // Prefer the classic .NET Framework csc.exe (ships with Windows itself, self-contained,
            // no extra dependent assemblies to resolve). This is far more reliable than invoking
            // Unity's bundled Roslyn compiler standalone, which can fail to load its own dependent
            // assemblies (e.g. System.Text.Encoding.CodePages) outside of Unity's own compilation
            // pipeline/environment.
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(windir))
            {
                string[] frameworkCandidates =
                {
                    Path.Combine(windir, "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe"),
                    Path.Combine(windir, "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe"),
                };

                foreach (string frameworkCandidate in frameworkCandidates)
                {
                    if (File.Exists(frameworkCandidate))
                        return frameworkCandidate;
                }
            }

            // Fallback: Unity Editor's bundled Roslyn compiler (needed when the host OS has no
            // .NET Framework install, e.g. building the Windows target from a macOS/Linux Editor).
            string contents = EditorApplication.applicationContentsPath;
            if (string.IsNullOrEmpty(contents) || !Directory.Exists(contents))
                return null;

            string roslynCandidate = Path.Combine(contents, "DotNetSdkRoslyn", "csc.exe");
            if (File.Exists(roslynCandidate))
                return roslynCandidate;

            try
            {
                string[] found = Directory.GetFiles(contents, "csc.exe", SearchOption.AllDirectories);
                if (found.Length > 0)
                    return found[0];
            }
            catch
            {
                // Fall through to "not found".
            }

            return null;
        }

        // ─── macOS ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Generates {buildOutputPath}/{altAppBaseName}.app, a bundle whose CFBundleExecutable is a
        /// shell script that execs the real app's binary inside {realAppBaseName}.app and exits.
        /// The real app's inner binary is expected at Contents/MacOS/{CFBundleExecutable}, resolved
        /// from its Info.plist when possible (falls back to realAppBaseName, Unity's default).
        /// </summary>
        private static bool TryGenerateMacAlias(string buildOutputPath, string realAppBaseName, string altAppBaseName, out string message)
        {
            message = null;

            string realAppDir = Path.Combine(buildOutputPath, realAppBaseName + ".app");
            string realExecName = ResolveMacBundleExecutableName(realAppDir) ?? realAppBaseName;

            string altAppDir = Path.Combine(buildOutputPath, altAppBaseName + ".app");
            string contentsDir = Path.Combine(altAppDir, "Contents");
            string macOsDir = Path.Combine(contentsDir, "MacOS");

            try
            {
                if (Directory.Exists(altAppDir))
                    Directory.Delete(altAppDir, true);
                Directory.CreateDirectory(macOsDir);

                File.WriteAllText(Path.Combine(contentsDir, "Info.plist"), BuildMacInfoPlist(altAppBaseName), new UTF8Encoding(false));

                string scriptPath = Path.Combine(macOsDir, altAppBaseName);
                File.WriteAllText(scriptPath, BuildMacLauncherScript(realAppBaseName, realExecName), new UTF8Encoding(false));

#if UNITY_EDITOR_OSX
                Chmod(scriptPath);
                // Defensively re-mark the real binary executable too — a build produced or
                // transferred via a non-Mac filesystem can lose this bit before upload.
                string realExecPath = Path.Combine(realAppDir, "Contents", "MacOS", realExecName);
                if (File.Exists(realExecPath))
                    Chmod(realExecPath);

                message = $"Generated alias app '{altAppBaseName}.app' -> launches '{realAppBaseName}.app/Contents/MacOS/{realExecName}'.";
#else
                // Built from a non-macOS Editor (e.g. Windows), so the launcher script's Unix
                // executable bit cannot be set here — NTFS has no such concept. Two things make
                // this still work: (1) SteamPipe automatically marks the file registered as the
                // Steamworks "Launch Executable" as executable on the player's Mac at install
                // time, and (2) the script itself re-chmods the real binary at launch (see
                // BuildMacLauncherScript). Make sure the Steamworks partner site's macOS launch
                // executable is set to exactly this alias app's inner executable path.
                message = $"Generated alias app '{altAppBaseName}.app' -> launches '{realAppBaseName}.app/Contents/MacOS/{realExecName}'. " +
                           "Built from a non-macOS Editor, so the executable bit relies on Steam auto-flagging the configured Launch Executable on install — verify this on a real Mac before shipping.";
#endif
                return true;
            }
            catch (Exception ex)
            {
                message = $"Failed to generate macOS alias app '{altAppBaseName}.app': {ex.Message}";
                return false;
            }
        }

        private static string BuildMacInfoPlist(string executableName)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
            sb.Append("<plist version=\"1.0\">\n");
            sb.Append("<dict>\n");
            sb.Append("\t<key>CFBundleExecutable</key>\n");
            sb.Append("\t<string>" + executableName + "</string>\n");
            sb.Append("\t<key>CFBundleIdentifier</key>\n");
            sb.Append("\t<string>com.steamitchiodeployer.alias." + executableName.ToLowerInvariant() + "</string>\n");
            sb.Append("\t<key>CFBundleName</key>\n");
            sb.Append("\t<string>" + executableName + "</string>\n");
            sb.Append("\t<key>CFBundlePackageType</key>\n");
            sb.Append("\t<string>APPL</string>\n");
            sb.Append("\t<key>CFBundleShortVersionString</key>\n");
            sb.Append("\t<string>1.0</string>\n");
            sb.Append("\t<key>CFBundleInfoDictionaryVersion</key>\n");
            sb.Append("\t<string>6.0</string>\n");
            sb.Append("\t<key>LSMinimumSystemVersion</key>\n");
            sb.Append("\t<string>10.13</string>\n");
            sb.Append("</dict>\n");
            sb.Append("</plist>\n");
            return sb.ToString();
        }

        /// <summary>
        /// Builds the shell script used as the alias app's CFBundleExecutable. It resolves the real
        /// app bundle relative to its own location (three levels up: MacOS -> Contents -> Alt.app ->
        /// build output root), execs the real binary in its place (forwarding argv), and exits.
        /// </summary>
        private static string BuildMacLauncherScript(string realAppBaseName, string realExecName)
        {
            string escapedRealApp = realAppBaseName.Replace("\"", "\\\"");
            string escapedRealExec = realExecName.Replace("\"", "\\\"");

            var sb = new StringBuilder();
            sb.Append("#!/bin/bash\n");
            sb.Append("DIR=\"$( cd \"$( dirname \"${BASH_SOURCE[0]}\" )\" && pwd )\"\n");
            sb.Append("TARGET=\"$DIR/../../../" + escapedRealApp + ".app/Contents/MacOS/" + escapedRealExec + "\"\n");
            // If this bundle was built/uploaded from a Windows host, NTFS never stored a Unix
            // executable bit for the real binary, so re-assert it here at launch time (runs on
            // the player's Mac, where chmod is meaningful). This script itself is expected to be
            // executable already because it's the file registered as the Steamworks Launch
            // Executable — SteamPipe automatically marks that specific file executable on install.
            sb.Append("chmod +x \"$TARGET\" 2>/dev/null\n");
            sb.Append("exec \"$TARGET\" \"$@\"\n");
            return sb.ToString();
        }

        /// <summary>
        /// Best-effort extraction of CFBundleExecutable from a .app's Info.plist. Returns null if the
        /// bundle or key cannot be found (caller falls back to assuming the executable name matches
        /// the bundle's base name, which is Unity's default for Standalone macOS builds).
        /// </summary>
        private static string ResolveMacBundleExecutableName(string appDir)
        {
            try
            {
                string plistPath = Path.Combine(appDir, "Contents", "Info.plist");
                if (!File.Exists(plistPath))
                    return null;

                string content = File.ReadAllText(plistPath);
                Match match = Regex.Match(content, @"<key>\s*CFBundleExecutable\s*</key>\s*<string>(.*?)</string>", RegexOptions.Singleline);
                return match.Success ? match.Groups[1].Value.Trim() : null;
            }
            catch
            {
                return null;
            }
        }

#if UNITY_EDITOR_OSX
        private static void Chmod(string path)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/chmod",
                    Arguments = $"+x \"{path}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var process = Process.Start(psi))
                    process?.WaitForExit();
            }
            catch
            {
                // Best-effort; Steam also auto-marks the configured launch executable as
                // executable on the client at install time.
            }
        }
#endif
    }
}
