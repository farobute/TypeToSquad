using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging;

using NAudio.CoreAudioApi;
using NAudio.Vorbis;
using NAudio.Wave;

using TypeToSquad.Core.Ports;

namespace TypeToSquad.Wpf.Infrastructure;

/// <summary>
/// WASAPI-backed audio playback with concurrent stream management
/// and output device selection.
///
/// WASAPI (MMDeviceEnumerator) is used instead of WaveOut because
/// Windows enumerates ALL render endpoints through it — including
/// Bluetooth (A2DP) devices that WinMM's waveOut list often omits.
/// Device names match what the user sees in Windows sound settings.
/// </summary>
public class AudioPlaybackService : IAudioPlayer, IDisposable {

	readonly ILogger logger;

	/// <summary>A single playback clip and the player owning it.</summary>
	sealed class ActivePlayback {
		public required WasapiOut Player { get; init; }
		public required IDisposable StreamToDispose { get; init; }
	}

	readonly object syncLock = new();
	readonly List<ActivePlayback> activePlaybacks = new();
	readonly Queue<(byte[] data, float volume)> pendingQueue = new();

	bool isPlayingSequence = false;

	/// <summary>Max concurrent streams. Enforced when exceeded (oldest evicted).</summary>
	public int MaxConcurrentStreams { get; set; } = 6;

	public AudioPlaybackService(ILogger<AudioPlaybackService> logger) {
		this.logger = logger;
	}

	// === IAudioPlayer ===

	public string[] GetOutputDevices() {
		using var enumerator = new MMDeviceEnumerator();
		var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
		return devices.Select(d => d.FriendlyName).ToArray();
	}

	string currentDevice = "";

	public string CurrentDevice {
		get => currentDevice;
		set {
			if (value != currentDevice) {
				currentDevice = value;
				logger.LogInformation("Output device set to {Device}.", string.IsNullOrEmpty(value) ? "(default)" : value);
			}
		}
	}

	public void Play(byte[] wavData, float volumeMultiplier = 1.0f) {
		lock (syncLock) {
			PlayInternal(wavData, volumeMultiplier);
		}
	}

	/// <summary>Plays a list of clips sequentially (used for Serial render nodes).</summary>
	public void PlaySequence(IEnumerable<(byte[] data, float volume)> clips) {
		lock (syncLock) {
			var clipsList = clips.ToList();
			if (clipsList.Count == 0) return;

			// If we're mid-sequence, queue the rest
			if (isPlayingSequence) {
				foreach (var clip in clipsList) pendingQueue.Enqueue(clip);
				return;
			}

			isPlayingSequence = true;
			PlaySequenceInternal(clipsList, 0);
		}
	}

	/// <summary>Loads a sound effect file into WAV bytes. Returns null if unsupported or missing.</summary>
	public static byte[]? LoadSoundFileToWav(string path) {

		string extension = (Path.GetExtension(path) ?? "").ToLower();

		using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read);

		WaveStream? inputStream = extension switch {
			".wav" or ".wave" => new WaveFileReader(fileStream),
			".ogg" => new VorbisWaveReader(fileStream),
			".mp3" => new Mp3FileReader(fileStream),
			_ => null,
		};

		if (inputStream is null) return null;

		using (inputStream) {
			// Convert to PCM WAV in memory
			using var outputStream = new MemoryStream();
			WaveFileWriter.WriteWavFileToStream(outputStream, inputStream);
			return outputStream.ToArray();
		}
	}

	// === Playback internals ===

	void PlayInternal(byte[] wavData, float volumeMultiplier) {

		if (wavData.Length == 0) {
			logger.LogWarning("Tried to play empty audio data.");
			return;
		}

		var stream = new WaveFileReader(new MemoryStream(wavData));

		WasapiOut? player = null;
		MMDeviceEnumerator? enumerator = null;
		try {
			if (string.IsNullOrEmpty(currentDevice)) {
				// System default endpoint
				player = new WasapiOut(AudioClientShareMode.Shared, 100);
			} else {
				// NOTE: the enumerator must stay alive until the WasapiOut is
				// constructed. If it is disposed first, the MMDevice's COM
				// pointer becomes dangling (once the collection is GC'd) and
				// playback silently produces no sound.
				enumerator = new MMDeviceEnumerator();
				var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

				MMDevice? device = null;
				foreach (var d in devices) {
					if (d.FriendlyName == currentDevice) {
						device = d;
						break;
					}
				}

				if (device is null) {
					logger.LogWarning("Output device \"{Device}\" not found. Using default.", currentDevice);
					player = new WasapiOut(AudioClientShareMode.Shared, 100);
				} else {
					player = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: false, 100);
				}
			}

			player.Volume = Math.Clamp(volumeMultiplier, 0.0f, 1.0f);

			var format = stream.WaveFormat;
			logger.LogInformation("PlayInternal: device={Device}, format={Rate}Hz/{Channels}ch/{Bits}bit, volume={Volume}, duration={DurationMs}ms",
				string.IsNullOrEmpty(currentDevice) ? "(default)" : currentDevice,
				format.SampleRate, format.Channels, format.BitsPerSample,
				player.Volume,
				(int)(stream.TotalTime.TotalMilliseconds));

			var stopwatch = System.Diagnostics.Stopwatch.StartNew();
			ActivePlayback? playback = null;
			player.PlaybackStopped += (_, _) => {
				stopwatch.Stop();
				logger.LogInformation("PlaybackStopped after {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
				OnPlaybackFinished(playback);
			};

			player.Init(stream);
			playback = new ActivePlayback { Player = player, StreamToDispose = stream };
			lock (syncLock) activePlaybacks.Add(playback);
			player.Play();
		} catch (Exception ex) {
			logger.LogError(ex, "Failed to start playback.");
			player?.Dispose();
			stream.Dispose();
			return;
		} finally {
			enumerator?.Dispose();
		}

		EnsureConcurrentNodeMax();
	}

	void PlaySequenceInternal(List<(byte[] data, float volume)> clips, int index) {

		// All clips played — the sequence is over. Reset the flag and
		// drain anything queued while it was playing. (This must always
		// run for the LAST clip too, or isPlayingSequence stays true
		// forever and later messages are silently dropped.)
		if (index >= clips.Count) {
			isPlayingSequence = false;
			if (pendingQueue.Count > 0) {
				var next = pendingQueue.Dequeue();
				PlaySequenceInternal(new List<(byte[], float)> { next }, 0);
			}
			return;
		}

		PlayInternal(clips[index].data, clips[index].volume);

		var currentPlayback = GetLastActivePlayback();

		if (currentPlayback is null) {
			// Clip already finished (or failed to start) — continue immediately
			PlaySequenceInternal(clips, index + 1);
		} else {
			// Chain the next clip after the current one finishes
			currentPlayback.Player.PlaybackStopped += (_, _) => {
				lock (syncLock) PlaySequenceInternal(clips, index + 1);
			};
		}
	}

	ActivePlayback? GetLastActivePlayback() {
		lock (syncLock) {
			return activePlaybacks.LastOrDefault();
		}
	}

	void OnPlaybackFinished(ActivePlayback? playback) {
		if (playback is null) return;

		lock (syncLock) {
			activePlaybacks.Remove(playback);
		}

		playback.Player.Dispose();
		playback.StreamToDispose.Dispose();
	}

	public void StopAll() {
		lock (syncLock) {
			pendingQueue.Clear();
			isPlayingSequence = false;

			foreach (var playback in activePlaybacks.ToArray()) {
				playback.Player.Stop();
				OnPlaybackFinished(playback);
			}
		}
	}

	void EnsureConcurrentNodeMax() {
		lock (syncLock) {
			int max = Math.Max(1, MaxConcurrentStreams);
			while (activePlaybacks.Count > max) {
				var oldest = activePlaybacks[0];
				oldest.Player.Stop();
				OnPlaybackFinished(oldest);
			}
		}
	}

	public void Dispose() {
		StopAll();
		GC.SuppressFinalize(this);
	}
}
