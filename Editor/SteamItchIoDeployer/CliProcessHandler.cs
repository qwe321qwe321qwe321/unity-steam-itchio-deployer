using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SteamItchIoDeployerCore;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace SteamItchIoDeployer
{
	/// <summary>
	/// Generic async child-process wrapper for CLI-based deploy tools such as steamcmd and butler.
	/// Output is collected on background threads and surfaced on the Unity main thread via PumpMainThread().
	/// An idle-output timeout is enforced: if no output is received for longer than <see cref="OutputIdleTimeoutSeconds"/>,
	/// <see cref="OnTimeoutDetected"/> is fired so the caller can kill the process and report a stall.
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
			public readonly CliLogLevel Level;

			public LogEntry(string message, CliLogLevel level)
			{
				Message = message;
				Level   = level;
			}
		}

		private readonly ConcurrentQueue<LogEntry> _logQueue = new ConcurrentQueue<LogEntry>();
		private readonly CliToolKind _toolKind;
		private volatile bool _hasExited;
		private int _exitCode = -1;
		private int _pendingReaderCount;
		private bool _exitNotified;

		// Idle-output timeout: if no output arrives within this window the process is considered stalled.
		// steamcmd normally produces output within a few seconds; 120 s is generous enough to survive slow
		// login / manifest-upload phases while still catching a true hang.
		public double OutputIdleTimeoutSeconds = 120.0;
		private long _lastOutputReceivedTicks; // Stopwatch.GetTimestamp() at last enqueue (written from bg thread)
		private bool _timeoutNotified;

		private Process _process;
		private Task _stdoutReaderTask;
		private Task _stderrReaderTask;
		private bool _disposed;

		public event Action<string> OnLogLine;
		public event Action<string> OnErrorLine;
		public event Action<string> OnSteamGuardRequired;
		public event Action<string> OnAuthenticationFailure;
		public event Action<int> OnProcessExited;
		public event Action OnTimeoutDetected;

		public CliProcessHandler(CliToolKind toolKind)
		{
			_toolKind = toolKind;
		}

		private static SteamItchIoDeployerCore.CliToolKind ToCoreToolKind(CliToolKind toolKind)
		{
			switch (toolKind)
			{
				case CliToolKind.SteamCmd: return SteamItchIoDeployerCore.CliToolKind.SteamCmd;
				case CliToolKind.Butler:   return SteamItchIoDeployerCore.CliToolKind.Butler;
				default:                   return SteamItchIoDeployerCore.CliToolKind.Generic;
			}
		}

		public static string DescribeSteamExitCode(int exitCode) => SteamExitCodeDescriptions.Describe(exitCode);

		public static string BuildSteamArguments(string username, string password, string steamGuardCode, string appVdfPath) =>
			CliArgumentQuoting.Join(SteamCommandBuilder.BuildLoginAndRunAppBuildArguments(username, password, steamGuardCode, appVdfPath));

		public static string BuildSteamTestLoginArguments(string username, string password, string steamGuardCode = "") =>
			CliArgumentQuoting.Join(SteamCommandBuilder.BuildTestLoginArguments(username, password, steamGuardCode));

		// The "hidden" parameter is accepted for source compatibility with existing call sites but,
		// same as before this was extracted into the shared core, has no effect: hiding an itch.io
		// channel is a dashboard-only setting with no butler push flag.
		public static string BuildButlerPushArguments(
			string buildOutputPath,
			string target,
			string channel,
			string userVersion,
			bool hidden,
			bool ifChanged,
			string[] ignorePatterns) =>
			CliArgumentQuoting.Join(ButlerCommandBuilder.BuildPushArguments(buildOutputPath, target, channel, userVersion, ifChanged, ignorePatterns));

		public bool Start(string executablePath, string arguments, IReadOnlyDictionary<string, string> environmentVariables = null)
		{
			var psi = new ProcessStartInfo
			{
				FileName               = executablePath,
				Arguments              = arguments,
				UseShellExecute        = false,
				CreateNoWindow         = false,
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

			_process.Exited             += HandleProcessExited;

			try
			{
				bool started = _process.Start();
				if (!started)
				{
					Debug.LogError("[SteamItchIoDeployer] Process.Start() returned false.");
					return false;
				}

				_pendingReaderCount = 2;
				_hasExited = false;
				_exitNotified = false;
				_timeoutNotified = false;
				_lastOutputReceivedTicks = Stopwatch.GetTimestamp();
				_stdoutReaderTask = Task.Run(() => PumpReader(_process.StandardOutput, fromStdErr: false));
				_stderrReaderTask = Task.Run(() => PumpReader(_process.StandardError, fromStdErr: true));

				return true;
			}
			catch (Exception ex)
			{
				Debug.LogError($"[SteamItchIoDeployer] Failed to start process: {ex.Message}");
				return false;
			}
		}

		public bool PumpMainThread()
		{
			while (_logQueue.TryDequeue(out LogEntry entry))
			{
				switch (entry.Level)
				{
					case CliLogLevel.SteamGuardRequired:
						OnSteamGuardRequired?.Invoke(entry.Message);
						DrainRemainingQueue();
						return true;
					case CliLogLevel.AuthFailure:
						OnAuthenticationFailure?.Invoke(entry.Message);
						DrainRemainingQueue();
						return true;
					case CliLogLevel.Error:
						OnErrorLine?.Invoke(entry.Message);
						break;
					default:
						OnLogLine?.Invoke(entry.Message);
						break;
				}
			}

			if (_hasExited && Volatile.Read(ref _pendingReaderCount) == 0 && !_exitNotified)
			{
				_exitNotified = true;
				OnProcessExited?.Invoke(_exitCode);
				return true;
			}

			if (!_hasExited && !_timeoutNotified && OutputIdleTimeoutSeconds > 0.0)
			{
				long idleTicks = Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastOutputReceivedTicks);
				double idleSeconds = (double)idleTicks / Stopwatch.Frequency;
				if (idleSeconds >= OutputIdleTimeoutSeconds)
				{
					_timeoutNotified = true;
					OnTimeoutDetected?.Invoke();
					return true;
				}
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
					Debug.LogWarning("[SteamItchIoDeployer] Child process was forcefully terminated.");
				}
			}
			catch (Exception ex)
			{
				Debug.LogWarning($"[SteamItchIoDeployer] Exception during process kill: {ex.Message}");
			}
		}

		private void PumpReader(StreamReader reader, bool fromStdErr)
		{
			var buffer = new StringBuilder();
			bool previousWasCarriageReturn = false;

			try
			{
				while (true)
				{
					int next = reader.Read();
					if (next < 0)
						break;

					char ch = (char)next;
					if (ch == '\r' || ch == '\n')
					{
						if (ch == '\n' && previousWasCarriageReturn)
						{
							previousWasCarriageReturn = false;
							continue;
						}

						EnqueueBufferedLine(buffer, fromStdErr);
						previousWasCarriageReturn = ch == '\r';
						continue;
					}

					buffer.Append(ch);
					previousWasCarriageReturn = false;
				}

				EnqueueBufferedLine(buffer, fromStdErr);
			}
			catch (ObjectDisposedException)
			{
				// Process shutdown can dispose the redirected stream while the reader is still active.
			}
			catch (InvalidOperationException)
			{
				// The process can close its streams during termination; ignore and let exit handling continue.
			}
			finally
			{
				Interlocked.Decrement(ref _pendingReaderCount);
			}
		}

		private void HandleProcessExited(object sender, EventArgs e)
		{
			_exitCode  = _process?.ExitCode ?? -1;
			_hasExited = true;
		}

		private void EnqueueBufferedLine(StringBuilder buffer, bool fromStdErr)
		{
			if (buffer.Length == 0)
				return;

			string line = buffer.ToString();
			buffer.Clear();
			Interlocked.Exchange(ref _lastOutputReceivedTicks, Stopwatch.GetTimestamp());
			_logQueue.Enqueue(new LogEntry(line, ClassifyLogLine(line, fromStdErr)));
		}

		private CliLogLevel ClassifyLogLine(string line, bool fromStdErr) =>
			CliOutputClassifier.Classify(ToCoreToolKind(_toolKind), line, fromStdErr);

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
				_process.Exited             -= HandleProcessExited;
				_process.Dispose();
				_process = null;
			}
		}
	}
}
