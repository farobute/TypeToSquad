using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Rephidock.GeneralUtilities.Randomness;

using WinRTSpeechSynthServer.Protocol;
using WinRTSpeechSynthServer.Protocol.Messages;

namespace TypeToSquad.Wpf.Infrastructure;

/// <summary>
/// Manages the daemon process and communication with it over named pipes.
/// Ported from the Godot version (SpeechDaemon) without Godot dependencies.
/// </summary>
public class DaemonClient : IDisposable {

	static readonly TimeSpan requestTimeout = TimeSpan.FromSeconds(5);
	static readonly TimeSpan daemonKillTimeout = TimeSpan.FromSeconds(1);

	const string RelativeExecutablePath = @"WinRTSpeechDaemon\WinRTSpeechSynthServer.exe";
	const string PipeNameFormat = @"TTSSpeechDaemon_{0:x8}";

	readonly ILogger logger;
	readonly ResponseReader responseReader = ResponseReader.CreateWithStandardRegistered();

	readonly ConcurrentQueue<Action> responseConsumptionCallbackQueue = new();

	Process? daemonProcess;
	string currentPipeName = "";

	public DaemonClient(ILogger<DaemonClient> logger) {
		this.logger = logger;
	}

	/// <summary>Checks if the daemon process is alive (no heartbeat involved).</summary>
	public bool IsDaemonAliveNoHeartbeat() {
		if (daemonProcess is null) return false;
		if (daemonProcess.HasExited) return false;
		return true;
	}

	/// <summary>Gets the absolute path to the daemon executable.</summary>
	public static string GetDaemonExecutablePath() {
		string projectRootPath = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
		return Path.Combine(projectRootPath, RelativeExecutablePath);
	}

	/// <summary>Starts (or restarts) the daemon process.</summary>
	public void StartDaemon() {

		ObjectDisposedException.ThrowIf(isDisposed, this);

		string pipeName = string.Format(PipeNameFormat, Random.Shared.NextUInt31());
		logger.LogInformation("Starting/restarting the daemon with pipe {PipeName}.", pipeName);

		// Kill existing
		CloseAndDisposeDaemon();

		// Try start new
		var daemonStartInfo = new ProcessStartInfo(GetDaemonExecutablePath(), [pipeName]) {
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		daemonProcess = Process.Start(daemonStartInfo);
		currentPipeName = pipeName;

		if (daemonProcess is null) {
			logger.LogError("Daemon could not be started.");
			currentPipeName = "";
			return;
		}

		if (daemonProcess.HasExited) {
			logger.LogError("Daemon process unexpectedly instantly exited.");
			CloseAndDisposeDaemon();
			return;
		}

		logger.LogInformation("Daemon started.");

		// Hook output and error
		daemonProcess.OutputDataReceived += (_, eventArgs) => {
			if (eventArgs.Data is not null) logger.LogInformation("[DAEMON] {Line}", eventArgs.Data);
		};
		daemonProcess.BeginOutputReadLine();

		_ = Task.Run(() => ReadProcessStandardErrorBatched(daemonProcess));
	}

	async Task ReadProcessStandardErrorBatched(Process daemon) {

		var errStringBuilder = new System.Text.StringBuilder();

		while (!daemon.HasExited) {
			string? firstLine = await daemon.StandardError.ReadLineAsync();
			if (firstLine is null) return; // stream ended; process terminated

			errStringBuilder.AppendLine(firstLine);
			while (!daemon.StandardError.EndOfStream) {
				errStringBuilder.AppendLine(await daemon.StandardError.ReadLineAsync());
			}

			logger.LogError("[DAEMON ERROR] {Message}", errStringBuilder);
			errStringBuilder.Clear();
		}
	}

	/// <summary>Safely closes and disposes of the daemon process.</summary>
	public void CloseAndDisposeDaemon() {
		if (daemonProcess is null) return;

		// Ask nicely first
		if (!daemonProcess.HasExited) {
			if (daemonProcess.CloseMainWindow()) {
				daemonProcess.WaitForExit(daemonKillTimeout);
			}
		}

		// Force exit
		if (!daemonProcess.HasExited) {
			daemonProcess.Kill();
			daemonProcess.WaitForExit(daemonKillTimeout);
		}

		if (!daemonProcess.HasExited) logger.LogError("Could not close daemon process.");

		daemonProcess.Dispose();
		daemonProcess = null;
		currentPipeName = "";
	}

	// --- Communication ---

	/// <summary>
	/// Sends a request and awaits the response. Runs the request on a background thread.
	/// </summary>
	public Task<Response> DispatchRequestAsync(Request request) {
		ObjectDisposedException.ThrowIf(isDisposed, this);

		if (!IsDaemonAliveNoHeartbeat()) {
			logger.LogError("Daemon is not alive. Starting new daemon.");
			StartDaemon();
		}

		return Task.Run(() => SendRequest(request));
	}

	/// <summary>Sends a single request over the named pipe and returns the response.</summary>
	Response SendRequest(Request req) {

		ObjectDisposedException.ThrowIf(isDisposed, this);

		if (!IsDaemonAliveNoHeartbeat()) {
			throw new InvalidOperationException("Daemon is not alive. Aborting request.");
		}

		using NamedPipeClientStream pipeClientStream = new(".", currentPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
		using BinaryReader reader = new(pipeClientStream);
		using BinaryWriter writer = new(pipeClientStream);

		try {
			logger.LogInformation("Connecting...");
			pipeClientStream.Connect(requestTimeout);
		} catch (Exception ex) when (ex is TimeoutException or IOException) {
			throw new IOException("Could not connect to the daemon", ex);
		}

		logger.LogInformation("Connected. Sending request of type {Type}.", req.Type);

		writer.Write(req.MessageType);
		req.WriteContents(writer);
		writer.Flush();

		logger.LogInformation("Waiting for response.");
		Response response = responseReader.ReadResponse(reader);
		logger.LogInformation("Got response of type {Type}.", response.Type);
		return response;
	}

	// --- Disposable ---

	bool isDisposed = false;

	public void Dispose() {
		if (isDisposed) return;

		CloseAndDisposeDaemon();
		daemonProcess?.Dispose();

		isDisposed = true;
		GC.SuppressFinalize(this);
	}
}
