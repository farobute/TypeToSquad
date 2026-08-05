using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

using Forms = System.Windows.Forms;

using Microsoft.Extensions.Logging;

using TypeToSquad.Core.Domain;
using TypeToSquad.Core.Ports;
using TypeToSquad.Wpf.Infrastructure;
using TypeToSquad.Wpf.Services;
using TypeToSquad.Wpf.Views;

namespace TypeToSquad.Wpf;

public partial class App : Application {

	const string MutexName = @"Local\TypeToSquad_SingleInstance";
	const string SignalWindowTitle = "TypeToSquadSignalWindow";
	const string SignalShowMessage = "SHOW";

	// --- Dependencies ---
	ILoggerFactory? loggerFactory;
	ISettingsRepository? settingsRepository;
	AppSettings? settings;
	DaemonClient? daemonClient;
	SpeechSynthesizerService? synthesizer;
	AudioPlaybackService? audioPlayer;
	PlaybackOrchestrator? orchestrator;
	HistoryTracker? historyTracker;
	HotkeyService? hotkeys;
	TrayIconService? tray;
	InputPopup? popup;
	SettingsWindow? settingsWindow;

	Mutex? singleInstanceMutex;
	HwndSource? signalWindow;

	/// <summary>Text to speak after startup completes (from --speak "..." CLI arg; used for testing).</summary>
	string? pendingSpeakText = null;

	/// <summary>Text to submit via the popup after startup (from --submit "..." CLI arg; used for testing).</summary>
	string? pendingSubmitText = null;

	/// <summary>Whether to open the settings window after startup (from --open-settings; used for testing).</summary>
	bool openSettingsAfterStartup = false;

	protected override void OnStartup(StartupEventArgs e) {
		base.OnStartup(e);

		// Hidden test/scripting args
		for (int i = 0; i < e.Args.Length - 1; i++) {
			if (e.Args[i] == "--speak") {
				pendingSpeakText = e.Args[i + 1];
			}
			if (e.Args[i] == "--submit") {
				pendingSubmitText = e.Args[i + 1];
			}
		}
		openSettingsAfterStartup = Array.IndexOf(e.Args, "--open-settings") >= 0;

		// --list-devices: log audio devices and exit
		if (Array.IndexOf(e.Args, "--list-devices") >= 0) {
			LogOutputDevices();
			Shutdown();
			return;
		}

		// --play-tone: play a known-good 440Hz test tone through the selected device (diagnostics)
		if (Array.IndexOf(e.Args, "--play-tone") >= 0) {
			PlayTestTone();
			Shutdown();
			return;
		}

		// --play-file <path>: play a WAV file through the selected device (diagnostics)
		for (int i = 0; i < e.Args.Length - 1; i++) {
			if (e.Args[i] == "--play-file") {
				PlayWavFileForDiagnostics(e.Args[i + 1]);
				Shutdown();
				return;
			}
		}

		// --dump-wav "text": synthesize text and save the WAV to temp (diagnostics)
		for (int i = 0; i < e.Args.Length - 1; i++) {
			if (e.Args[i] == "--dump-wav") {
				DumpWavForDiagnostics(e.Args[i + 1]);
				Shutdown();
				return;
			}
		}

		// --- Single instance check ---
		singleInstanceMutex = new Mutex(true, MutexName, out bool isFirstInstance);

		if (!isFirstInstance) {
			// Forward "SHOW" to the running instance, then exit
			ForwardShowToExistingInstance();
			Shutdown();
			return;
		}

		// --- Logging ---
		loggerFactory = LoggerFactory.Create(builder => {
			builder.SetMinimumLevel(LogLevel.Information);
			builder.AddProvider(new FileLoggerProvider());
		});
		var log = loggerFactory.CreateLogger<App>();

		try {

			// --- Services ---
			settingsRepository = new SettingsJsonRepository(loggerFactory.CreateLogger<SettingsJsonRepository>());
			settings = settingsRepository.Load();

			historyTracker = new HistoryTracker { HistorySlots = settings.HistorySlots };

			// Register bundled offline voices before starting the daemon.
			// WinRT SpeechSynthesizer scans the registry on first use, so
			// voices must be registered before the daemon process starts.
			int registeredVoices = Services.VoiceRegistrationService.RegisterBundledVoices(
				loggerFactory.CreateLogger<Services.VoiceRegistrationService>());
			if (registeredVoices > 0)
				log.LogInformation("Registered {Count} bundled voice(s).", registeredVoices);

			daemonClient = new DaemonClient(loggerFactory.CreateLogger<DaemonClient>());
			synthesizer = new SpeechSynthesizerService(daemonClient, loggerFactory.CreateLogger<SpeechSynthesizerService>());
			audioPlayer = new AudioPlaybackService(loggerFactory.CreateLogger<AudioPlaybackService>()) {
				MaxConcurrentStreams = settings.MaxConcurrentStreams,
			};
			audioPlayer.CurrentDevice = settings.OutputDevice;

			orchestrator = new PlaybackOrchestrator(
				synthesizer,
				audioPlayer,
				loggerFactory.CreateLogger<PlaybackOrchestrator>()
			);

			// --- Signal window (for single-instance forwarding) ---
			signalWindow = CreateSignalWindow();

			// --- Hotkeys ---
			hotkeys = new HotkeyService(loggerFactory.CreateLogger<HotkeyService>());
			hotkeys.SetSummonHotkey(settings.SummonHotkey);
			hotkeys.SetStopHotkey(settings.StopHotkey);

			// --- Popup ---
			popup = new InputPopup(historyTracker);

			// --- Tray ---
			tray = new TrayIconService(loggerFactory.CreateLogger<TrayIconService>());

			// --- Wire events ---
			WireEvents(log);

			// --- Start daemon & load voices ---
			synthesizer.StartDaemon();
			_ = synthesizer.LoadVoicesAsync();

			log.LogInformation("TypeToSquad started.");

		} catch (Exception ex) {
			log.LogCritical(ex, "Fatal error during startup.");
			tray?.ShowBalloonTip("TypeToSquad", $"启动失败: {ex.Message}", Forms.ToolTipIcon.Error);
			Shutdown();
		}
	}

	void WireEvents(ILogger log) {

		// Hotkeys
		hotkeys!.SummonPressed += () => {
			log.LogInformation("Summon hotkey pressed. Showing popup.");
			popup!.ShowPopup();
		};
		hotkeys.StopPressed += () => {
			log.LogInformation("Stop hotkey pressed.");
			orchestrator!.StopAll();
		};

		// Popup submit
		popup!.MessageSubmitted += async message => {
			try {
				log.LogInformation("Message submitted: {Message}", message);
				historyTracker!.AddHistoryEntry(message);

				bool spoken = await orchestrator!.SpeakAsync(message, settings!);
				if (!spoken) {
					log.LogWarning("Nothing was spoken for message.");
				}
			} catch (Exception ex) {
				log.LogError(ex, "Failed to speak message.");
				tray?.ShowBalloonTip("TypeToSquad", $"朗读失败: {ex.Message}", Forms.ToolTipIcon.Error);
			}
		};

		// Tray
		tray!.SummonRequested += () => {
			log.LogInformation("Tray left-click. Showing popup.");
			popup!.ShowPopup();
		};
		tray.SettingsRequested += OpenSettingsWindow;
		tray.ExitRequested += OnExitRequested;
		tray.StopRequested += () => orchestrator!.StopAll();
		tray.VoiceSelected += voiceKey => {
			settings!.VoiceKey = voiceKey;
			SaveSettings();
			UpdateTrayMenu();
		};
		tray.VolumeChanged += volume => {
			settings!.SynthesisVolumePercent = volume;
			SaveSettings();
			UpdateTrayMenu();
		};
		tray.OutputDeviceSelected += device => {
			settings!.OutputDevice = device;
			audioPlayer!.CurrentDevice = device;
			SaveSettings();
			UpdateTrayMenu();
		};

		// Voices loaded
		synthesizer!.VoicesLoaded += () => {
			// Assign a default voice key if the stored one is empty or no longer valid
			if (string.IsNullOrEmpty(settings!.VoiceKey)
				|| synthesizer.GetVoiceByKey(settings.VoiceKey) is null) {
				settings.VoiceKey = synthesizer.GetDefaultVoiceKey();
				SaveSettings();
			}
			UpdateTrayMenu();

			// Run pending --speak test
			if (pendingSpeakText is not null) {
				string text = pendingSpeakText;
				pendingSpeakText = null;
				_ = SpeakTestAsync(text);
			}

			// Run pending --submit test (exercises popup → submit → synthesis)
			if (pendingSubmitText is not null) {
				string text = pendingSubmitText;
				pendingSubmitText = null;
				loggerFactory?.CreateLogger<App>().LogInformation("--submit test: simulating popup submit.");
				popup!.SimulateSubmitForTest(text);
			}

			// Open settings window after startup (--open-settings test)
			if (openSettingsAfterStartup) {
				Dispatcher.BeginInvoke(OpenSettingsWindow);
			}
		};
	}

	async Task SpeakTestAsync(string text) {
		var log = loggerFactory?.CreateLogger<App>();
		try {
			bool spoken = await orchestrator!.SpeakAsync(text, settings!);
			log?.LogInformation("--speak test result: {Result}", spoken ? "spoken" : "not spoken");
		} catch (Exception ex) {
			log?.LogError(ex, "--speak test failed.");
		}
	}

	void OpenSettingsWindow() {

		if (settingsWindow is not null) {
			settingsWindow.Activate();
			return;
		}

		settingsWindow = new SettingsWindow(
			settings!,
			synthesizer!.GetVoiceKeys(),
			audioPlayer!.GetOutputDevices()
		);

		settingsWindow.SettingsSaved += OnSettingsSaved;
		settingsWindow.Closed += (_, _) => settingsWindow = null;

		settingsWindow.Show();
		settingsWindow.Activate();
	}

	void OnSettingsSaved() {
		SaveSettings();
		ApplySettingsToServices();
		UpdateTrayMenu();
	}

	void SaveSettings() {
		settings!.Clamp();
		settingsRepository!.Save(settings);
	}

	void ApplySettingsToServices() {
		historyTracker!.HistorySlots = settings!.HistorySlots;
		audioPlayer!.MaxConcurrentStreams = settings.MaxConcurrentStreams;
		audioPlayer.CurrentDevice = settings.OutputDevice;

		// Re-register hotkeys; surface conflicts (already used by another app)
		bool summonOk = hotkeys!.SetSummonHotkey(settings.SummonHotkey);
		bool stopOk = hotkeys.SetStopHotkey(settings.StopHotkey);

		if (!summonOk || !stopOk) {
			string failed = (!summonOk ? $"呼出 ({settings.SummonHotkey})" : "")
				+ (!summonOk && !stopOk ? " 和 " : "")
				+ (!stopOk ? $"停止 ({settings.StopHotkey})" : "");
			tray?.ShowBalloonTip("TypeToSquad", $"快捷键注册失败（可能被其他程序占用）: {failed}", Forms.ToolTipIcon.Warning);
		}
	}

	void UpdateTrayMenu() {
		if (tray is null || settings is null || synthesizer is null || audioPlayer is null) return;

		tray.UpdateContextMenu(
			voiceKeys: synthesizer.GetVoiceKeys(),
			currentVoiceKey: settings.VoiceKey,
			volumePercent: settings.SynthesisVolumePercent,
			currentOutputDevice: settings.OutputDevice,
			outputDevices: audioPlayer.GetOutputDevices(),
			isCurrentlySpeaking: false
		);
	}

	async void OnExitRequested() {
		var log = loggerFactory?.CreateLogger<App>();

		try {
			log?.LogInformation("Exiting...");

			// Save settings
			if (settings is not null && settingsRepository is not null) {
				SaveSettings();
			}

			// Gracefully terminate the daemon
			if (synthesizer is not null) {
				await synthesizer.ShutdownAsync();
			}

			// Cleanup
			hotkeys?.Dispose();
			tray?.Dispose();
			audioPlayer?.Dispose();
			daemonClient?.Dispose();
			signalWindow?.Dispose();
			loggerFactory?.Dispose();

		} catch (Exception ex) {
			log?.LogError(ex, "Error during exit.");
		} finally {
			Shutdown();
		}
	}

	/// <summary>Plays a 1-second 440Hz sine tone through the current output device.</summary>
	void PlayTestTone() {
		using var logFactory = LoggerFactory.Create(builder => {
			builder.SetMinimumLevel(LogLevel.Information);
			builder.AddProvider(new FileLoggerProvider());
		});
		var log = logFactory.CreateLogger<App>();
		try {
			using var player = new AudioPlaybackService(logFactory.CreateLogger<AudioPlaybackService>());

			// Generate 1s of 440Hz sine wave, 44.1kHz mono 16-bit PCM
			const int sampleRate = 44100;
			const int durationSeconds = 1;
			const float frequency = 440.0f;

			var samples = new short[sampleRate * durationSeconds];
			for (int i = 0; i < samples.Length; i++) {
				samples[i] = (short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * short.MaxValue * 0.5);
			}

			using var ms = new MemoryStream();
			using (var writer = new BinaryWriter(ms)) {
				// WAV header
				writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
				writer.Write(36 + samples.Length * 2);
				writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
				writer.Write(16);          // fmt chunk size
				writer.Write((short)1);    // PCM
				writer.Write((short)1);    // mono
				writer.Write(sampleRate);
				writer.Write(sampleRate * 2);
				writer.Write((short)2);    // block align
				writer.Write((short)16);   // bits per sample
				writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
				writer.Write(samples.Length * 2);
				foreach (var sample in samples) writer.Write(sample);
			}

			log.LogInformation("Playing 440Hz test tone (1s) via WASAPI...");
			player.Play(ms.ToArray(), 1.0f);
			Thread.Sleep(2500);
			log.LogInformation("Test tone done.");
		} catch (Exception ex) {
			log.LogError(ex, "Test tone failed.");
		}
	}

	/// <summary>Plays a WAV file through the current device (diagnostics).</summary>
	void PlayWavFileForDiagnostics(string path) {
		using var logFactory = LoggerFactory.Create(builder => {
			builder.SetMinimumLevel(LogLevel.Information);
			builder.AddProvider(new FileLoggerProvider());
		});
		var log = logFactory.CreateLogger<App>();
		try {
			byte[] wav = File.ReadAllBytes(path);
			using var player = new AudioPlaybackService(logFactory.CreateLogger<AudioPlaybackService>());
			log.LogInformation("Playing WAV file {Path} ({Length} bytes)...", path, wav.Length);
			player.Play(wav, 1.0f);
			Thread.Sleep(5000);
			log.LogInformation("WAV file playback done.");
		} catch (Exception ex) {
			log.LogError(ex, "WAV file playback failed.");
		}
	}

	/// <summary>Synthesizes text and saves the WAV to a temp file for inspection.</summary>
	void DumpWavForDiagnostics(string text) {
		// Runs on the thread pool so async continuations don't deadlock
		// against the blocked UI thread.
		Task.Run(async () => {
			try {
				using var logFactory = LoggerFactory.Create(builder => {
					builder.SetMinimumLevel(LogLevel.Information);
					builder.AddProvider(new FileLoggerProvider());
				});
				var log = logFactory.CreateLogger<App>();

				using var daemon = new DaemonClient(logFactory.CreateLogger<DaemonClient>());
				var synth = new SpeechSynthesizerService(daemon, logFactory.CreateLogger<SpeechSynthesizerService>());
				synth.StartDaemon();

				await synth.LoadVoicesAsync();
				string voiceKey = synth.GetDefaultVoiceKey();

				log.LogInformation("Synthesizing \"{Text}\"...", text);
				var node = new Core.Domain.RenderNode {
					Type = Core.Domain.RenderNodeType.Text,
					Attributes = { { Core.Domain.RenderNodeAttribute.TextContent, text } },
				};
				byte[] wav = await synth.SynthesizeAsync(node, voiceKey, 1.0, 1.0, 100);

				string path = Path.Combine(Path.GetTempPath(), "typesquad_dump.wav");
				File.WriteAllBytes(path, wav);
				log.LogInformation("WAV saved to {Path} ({Length} bytes).", path, wav.Length);

				daemon.CloseAndDisposeDaemon();
			} catch (Exception ex) {
				FileLoggerProvider.LogStatic($"DumpWavForDiagnostics failed: {ex}");
			}
		}).GetAwaiter().GetResult();
	}

	/// <summary>Logs the audio output devices NAudio sees (for diagnostics).</summary>
	void LogOutputDevices() {
		try {
			using var logFactory = LoggerFactory.Create(builder => {
				builder.SetMinimumLevel(LogLevel.Information);
				builder.AddProvider(new FileLoggerProvider());
			});
			var log = logFactory.CreateLogger<App>();

			using var probe = new AudioPlaybackService(logFactory.CreateLogger<AudioPlaybackService>());
			string[] devices = probe.GetOutputDevices();
			log.LogInformation("Available audio output devices ({Count}):", devices.Length);
			for (int i = 0; i < devices.Length; i++) {
				log.LogInformation("  [{i}] {Device}", i, devices[i]);
			}
		} catch (Exception ex) {
			FileLoggerProvider.LogStatic(ex.ToString());
		}
	}

	// ================================================================
	// Single instance
	// ================================================================

	HwndSource CreateSignalWindow() {

		var parameters = new HwndSourceParameters(SignalWindowTitle) {
			WindowStyle = 0,
			Width = 0,
			Height = 0,
			PositionX = -32000,
			PositionY = -32000,
		};

		var source = new HwndSource(parameters);

		source.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) => {
			if (msg == NativeMethods.WM_COPYDATA) {
				handled = HandleCopyData(lParam);
			}
			return IntPtr.Zero;
		});

		return source;
	}

	bool HandleCopyData(IntPtr lParam) {

		try {
			var copyData = Marshal.PtrToStructure<NativeMethods.COPYDATASTRUCT>(lParam);
			string message = copyData.lpData != IntPtr.Zero
				? Marshal.PtrToStringUni(copyData.lpData, copyData.cbData / 2) ?? ""
				: "";

			if (message == SignalShowMessage) {
				loggerFactory?.CreateLogger<App>().LogInformation("Received SHOW signal from another instance. Summoning popup.");
				Dispatcher.Invoke(popup!.ShowPopup);
				return true;
			}
		} catch (Exception ex) {
			loggerFactory?.CreateLogger<App>().LogWarning(ex, "Malformed WM_COPYDATA message.");
		}

		return false;
	}

	void ForwardShowToExistingInstance() {

		// Retry for a moment in case the first instance is still starting up
		for (int attempt = 0; attempt < 50; attempt++) {

			IntPtr hwnd = NativeMethods.FindWindow(null, SignalWindowTitle);
			if (hwnd != IntPtr.Zero) {
				NativeMethods.SendStringMessage(hwnd, SignalShowMessage);
				return;
			}

			Thread.Sleep(100);
		}
	}
}

/// <summary>Minimal file logger provider writing to %AppData%\TypeToSquad\log.txt.</summary>
sealed class FileLoggerProvider : ILoggerProvider {

	readonly string logPath;
	readonly object sync = new();

	public FileLoggerProvider() {
		string dir = System.IO.Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
			"TypeToSquad"
		);
		System.IO.Directory.CreateDirectory(dir);
		logPath = System.IO.Path.Combine(dir, "log.txt");
	}

	public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

	public void Log(string message) {
		lock (sync) {
			System.IO.File.AppendAllText(logPath, message + Environment.NewLine);
		}
	}

	/// <summary>Logs directly without an instance (used before the logger factory exists).</summary>
	public static void LogStatic(string message) {
		new FileLoggerProvider().Log(message);
	}

	public void Dispose() { }

	sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger {

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
			string message = formatter(state, exception);
			provider.Log($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{logLevel}] {category}: {message}"
				+ (exception is null ? "" : $"\n{exception}"));
		}
	}
}
