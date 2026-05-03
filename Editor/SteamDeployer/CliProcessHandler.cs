using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SteamDeployer
{
	/// <summary>
	/// Generic async child-process wrapper for CLI-based deploy tools such as steamcmd and butler.
	/// Output is collected on background threads and surfaced on the Unity main thread via PumpMainThread().
	/// </summary>
	public sealed class CliProcessHandler : IDisposable
	{
		public enum CliToolKind
		{
			Generic,
			SteamCmd,
			Butler,
		}

		private readonly struct LogEntry
		{
			public readonly string Message;
			public readonly LogLevel Level;

			public LogEntry(string message, LogLevel level)
			{
				Message = message;
				Level   = level;
			}
		}

		private enum LogLevel { Info, Error, SteamGuardRequired, AuthFailure }

		private readonly ConcurrentQueue<LogEntry> _logQueue = new ConcurrentQueue<LogEntry>();
		private readonly CliToolKind _toolKind;
		private volatile bool _hasExited;
		private int _exitCode = -1;

		private Process _process;
		private bool _disposed;

		public event Action<string> OnLogLine;
		public event Action<string> OnErrorLine;
		public event Action<string> OnSteamGuardRequired;
		public event Action<string> OnAuthenticationFailure;
		public event Action<int> OnProcessExited;

		private static readonly Regex SteamGuardRequiredPattern = new Regex(
			@"(not been authenticated for your account using Steam Guard|" +
			@"Steam Guard code:|" +
			@"Steam Guard code required|" +
			@"FAILED login with result code RequireTwoFactorCode|" +
			@"FAILED login with result code RequirePasswordEntry|" +
			@"Enter the current code from your Steam Guard)",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		private static readonly Regex SteamAuthFailurePattern = new Regex(
			@"(Invalid Password|Two-factor code mismatch|" +
			@"Login Failure|Logging in user.*Failed|FAILED login with result code InvalidPassword)",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		private static readonly Regex ButlerAuthFailurePattern = new Regex(
			@"(authentication not complete|api key|unauthorized|forbidden|invalid api key|not logged in)",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		private static readonly Regex GenericErrorPattern = new Regex(
			@"(ERROR!|error:|FAILED|Build Failed|Upload Failed|rate limit exceeded)",
			RegexOptions.Compiled | RegexOptions.IgnoreCase);

		public CliProcessHandler(CliToolKind toolKind)
		{
			_toolKind = toolKind;
		}

		public static string DescribeSteamExitCode(int exitCode)
		{
			switch (exitCode)
			{
				case 0:  return "Success.";
				case 1:  return "Unknown / general error.";
				case 2:  return "Steam session error — already logged in elsewhere, or generic login failure.";
				case 3:  return "No connection to the Steam network. Check your internet connection.";
				case 4:  return "Connection timeout or invalid command-line argument.";
				case 5:  return "Steam API / SDK initialisation failed.";
				case 6:  return "Build commit failed. Content was uploaded but could not be finalised. Common causes: SetLive branch not eligible yet, invalid branch name, or a transient Valve-side error.";
				case 7:  return "Too many failed login attempts. Wait before retrying.";
				case 8:  return "Rate limit exceeded — too many steamcmd operations in a short period. Wait and retry.";
				case 42: return "Rate limit exceeded (Valve-side throttle). Wait several minutes before retrying.";
				default: return $"Undocumented exit code {exitCode}. Check the steamcmd log in the logs/ folder for details.";
			}
		}

		public static string BuildSteamArguments(string username, string password, string steamGuardCode, string appVdfPath)
		{
			string quotedVdf = $"\"{appVdfPath}\"";

			if (!string.IsNullOrWhiteSpace(steamGuardCode))
			{
				return $"+set_steam_guard_code {steamGuardCode.Trim()} " +
				       $"+login {username} {password} " +
				       $"+run_app_build {quotedVdf} " +
				       $"+quit";
			}

			return $"+login {username} {password} " +
			       $"+run_app_build {quotedVdf} " +
			       $"+quit";
		}

		public static string BuildSteamTestLoginArguments(string username, string password, string steamGuardCode = "")
		{
			if (!string.IsNullOrWhiteSpace(steamGuardCode))
			{
				return $"+set_steam_guard_code {steamGuardCode.Trim()} " +
				       $"+login {username} {password} " +
				       $"+quit";
			}

			return $"+login {username} {password} +quit";
		}

		public static string BuildButlerPushArguments(
			string buildOutputPath,
			string target,
			string channel,
			string userVersion,
			bool hidden,
			bool ifChanged,
			string[] ignorePatterns)
		{
			string args = $"push \"{buildOutputPath}\" {target}:{channel}";

			if (!string.IsNullOrWhiteSpace(userVersion))
				args += $" --userversion \"{userVersion}\"";

			if (ifChanged)
				args += " --if-changed";

			if (ignorePatterns != null)
			{
				foreach (string pattern in ignorePatterns)
				{
					if (!string.IsNullOrWhiteSpace(pattern))
						args += $" --ignore \"{pattern.Trim()}\"";
				}
			}

			return args;
		}

		public bool Start(string executablePath, string arguments, IReadOnlyDictionary<string, string> environmentVariables = null)
		{
			var psi = new ProcessStartInfo
			{
				FileName               = executablePath,
				Arguments              = arguments,
				UseShellExecute        = false,
				CreateNoWindow         = true,
				RedirectStandardOutput = true,
				RedirectStandardError  = true,
				StandardOutputEncoding = System.Text.Encoding.UTF8,
				StandardErrorEncoding  = System.Text.Encoding.UTF8,
			};

			if (environmentVariables != null)
			{
				foreach (var pair in environmentVariables)
				{
					if (!string.IsNullOrWhiteSpace(pair.Key))
						psi.Environment[pair.Key] = pair.Value ?? string.Empty;
				}
			}

			_process = new Process
			{
				StartInfo           = psi,
				EnableRaisingEvents = true,
			};

			_process.OutputDataReceived += HandleOutputData;
			_process.ErrorDataReceived  += HandleErrorData;
			_process.Exited             += HandleProcessExited;

			try
			{
				bool started = _process.Start();
				if (!started)
				{
					Debug.LogError("[SteamDeployer] Process.Start() returned false.");
					return false;
				}

				_process.BeginOutputReadLine();
				_process.BeginErrorReadLine();

				return true;
			}
			catch (Exception ex)
			{
				Debug.LogError($"[SteamDeployer] Failed to start process: {ex.Message}");
				return false;
			}
		}

		public bool PumpMainThread()
		{
			while (_logQueue.TryDequeue(out LogEntry entry))
			{
				switch (entry.Level)
				{
					case LogLevel.SteamGuardRequired:
						OnSteamGuardRequired?.Invoke(entry.Message);
						DrainRemainingQueue();
						return true;
					case LogLevel.AuthFailure:
						OnAuthenticationFailure?.Invoke(entry.Message);
						DrainRemainingQueue();
						return true;
					case LogLevel.Error:
						OnErrorLine?.Invoke(entry.Message);
						break;
					default:
						OnLogLine?.Invoke(entry.Message);
						break;
				}
			}

			if (_hasExited)
			{
				OnProcessExited?.Invoke(_exitCode);
				return true;
			}

			return false;
		}

		public void Kill()
		{
			try
			{
				if (_process != null && !_process.HasExited)
				{
					_process.Kill();
					Debug.LogWarning("[SteamDeployer] Child process was forcefully terminated.");
				}
			}
			catch (Exception ex)
			{
				Debug.LogWarning($"[SteamDeployer] Exception during process kill: {ex.Message}");
			}
		}

		private void HandleOutputData(object sender, DataReceivedEventArgs e)
		{
			if (e.Data == null) return;
			_logQueue.Enqueue(new LogEntry(e.Data, ClassifyLogLine(e.Data, fromStdErr: false)));
		}

		private void HandleErrorData(object sender, DataReceivedEventArgs e)
		{
			if (e.Data == null) return;
			_logQueue.Enqueue(new LogEntry(e.Data, ClassifyLogLine(e.Data, fromStdErr: true)));
		}

		private void HandleProcessExited(object sender, EventArgs e)
		{
			_exitCode  = _process?.ExitCode ?? -1;
			_hasExited = true;
		}

		private LogLevel ClassifyLogLine(string line, bool fromStdErr)
		{
			if (_toolKind == CliToolKind.SteamCmd)
			{
				if (SteamGuardRequiredPattern.IsMatch(line))
					return LogLevel.SteamGuardRequired;

				if (SteamAuthFailurePattern.IsMatch(line))
					return LogLevel.AuthFailure;
			}

			if (_toolKind == CliToolKind.Butler && ButlerAuthFailurePattern.IsMatch(line))
				return LogLevel.AuthFailure;

			if (fromStdErr || GenericErrorPattern.IsMatch(line))
				return LogLevel.Error;

			return LogLevel.Info;
		}

		private void DrainRemainingQueue()
		{
			while (_logQueue.TryDequeue(out _)) { }
		}

		public void Dispose()
		{
			if (_disposed) return;
			_disposed = true;

			if (_process != null)
			{
				_process.OutputDataReceived -= HandleOutputData;
				_process.ErrorDataReceived  -= HandleErrorData;
				_process.Exited             -= HandleProcessExited;
				_process.Dispose();
				_process = null;
			}
		}
	}
}
