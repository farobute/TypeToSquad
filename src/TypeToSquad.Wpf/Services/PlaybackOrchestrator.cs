using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using TypeToSquad.Core.Domain;
using TypeToSquad.Core.Services;
using TypeToSquad.Wpf.Infrastructure;

namespace TypeToSquad.Wpf.Services;

/// <summary>
/// Walks a RenderNode tree, synthesizes each segment via the daemon,
/// and plays the resulting audio through the audio player.
/// Equivalent to the Godot version's AudioProvider + AudioManager.
/// </summary>
public class PlaybackOrchestrator {

	readonly SpeechSynthesizerService synthesizer;
	readonly AudioPlaybackService audioPlayer;
	readonly ILogger logger;

	/// <summary>Raised (UI thread) when a message finishes playing.</summary>
	public event Action? PlaybackFinished;

	public PlaybackOrchestrator(
		SpeechSynthesizerService synthesizer,
		AudioPlaybackService audioPlayer,
		ILogger<PlaybackOrchestrator> logger
	) {
		this.synthesizer = synthesizer;
		this.audioPlayer = audioPlayer;
		this.logger = logger;
	}

	/// <summary>
	/// Processes a raw message and plays it. Returns true if something was spoken.
	/// </summary>
	public async Task<bool> SpeakAsync(string message, AppSettings settings) {

		if (string.IsNullOrWhiteSpace(message)) return false;

		logger.LogInformation("Processing...");
		var tree = MessageProcessor.ProcessMessage(
			message,
			settings.GetTextReplacements(),
			settings.GetUserTags(),
			settings.GetVoiceChanges(),
			settings.MaxReplacementPasses,
			settings.VoiceKey,
			key => synthesizer.GetVoiceByKey(key)
				?? throw new KeyNotFoundException($"No voice under key \"{key}\""),
			error => logger.LogError("{Error}", error)
		);

		logger.LogInformation("Synthesizing...");
		var clips = await BuildClipsAsync(tree, settings);

		if (clips.Count == 0) return false;

		logger.LogInformation("Playing {ClipCount} clip(s)...", clips.Count);
		audioPlayer.PlaySequence(clips);
		PlaybackFinished?.Invoke();

		return true;
	}

	/// <summary>Stops all currently playing audio.</summary>
	public void StopAll() {
		audioPlayer.StopAll();
	}

	/// <summary>
	/// Recursively converts a RenderNode tree into a list of playback clips.
	/// </summary>
	async Task<List<(byte[] data, float volume)>> BuildClipsAsync(RenderNode node, AppSettings settings) {

		var clips = new List<(byte[] data, float volume)>();

		if (node.Type == RenderNodeType.Text || node.Type == RenderNodeType.SsmlRoot) {

			byte[] wav = await synthesizer.SynthesizeAsync(
				node,
				settings.VoiceKey,
				settings.VoicePitch,
				settings.VoiceRate,
				settings.SynthesisVolumePercent
			);
			if (wav.Length > 0) clips.Add((wav, 1.0f));

		} else if (node.Type == RenderNodeType.Sound) {

			string hint = node.Attributes.GetValueOrDefault(RenderNodeAttribute.SoundHint, "");

			SoundEffectMapping? mapping = settings.SoundEffects.Find(s => s.Hint == hint);
			if (mapping is null) {
				logger.LogWarning("Unknown sound effect \"{Hint}\".", hint);
			} else if (!File.Exists(mapping.FilePath)) {
				logger.LogError("Sound effect file not found at \"{Path}\" (hint \"{Hint}\").",
					mapping.FilePath, hint);
			} else {
				byte[]? wav = AudioPlaybackService.LoadSoundFileToWav(mapping.FilePath);
				if (wav is null) {
					logger.LogWarning("Unsupported sound file format at \"{Path}\".", mapping.FilePath);
				} else {
					float volumeMult = Math.Clamp(mapping.VolumePercent / 100.0f, 0.0f, 1.0f);
					clips.Add((wav, volumeMult));
				}
			}

		} else if (node.Type == RenderNodeType.Serial) {

			foreach (var child in node.Children) {
				clips.AddRange(await BuildClipsAsync(child, settings));
			}

		} else {
			logger.LogWarning("Unsupported render node type \"{Type}\" during playback build.", node.Type);
		}

		return clips;
	}
}
