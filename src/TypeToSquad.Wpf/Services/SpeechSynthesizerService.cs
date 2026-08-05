using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using TypeToSquad.Core.Domain;
using TypeToSquad.Core.Ports;
using TypeToSquad.Core.Services;
using TypeToSquad.Wpf.Infrastructure;

using VoiceInfo = WinRTSpeechSynthServer.Protocol.VoiceInfo;
using SynthesizeRequest = WinRTSpeechSynthServer.Protocol.Messages.SynthesizeRequest;
using GetVoicesRequest = WinRTSpeechSynthServer.Protocol.Messages.GetVoicesRequest;
using SynthesisResultResponse = WinRTSpeechSynthServer.Protocol.Messages.SynthesisResultResponse;
using AllVoicesResponse = WinRTSpeechSynthServer.Protocol.Messages.AllVoicesResponse;
using TerminateRequest = WinRTSpeechSynthServer.Protocol.Messages.TerminateRequest;

namespace TypeToSquad.Wpf.Services;

/// <summary>
/// Implements <see cref="ISpeechSynthesizer"/> over the daemon.
/// Converts RenderNode trees into WAV audio and caches the voice list.
/// </summary>
public class SpeechSynthesizerService : ISpeechSynthesizer {

	readonly DaemonClient daemon;
	readonly ILogger logger;

	readonly Dictionary<string, VoiceInfo> voicesByKey = new();
	bool voicesLoaded = false;

	/// <summary>Event raised when the voice list has been fetched.</summary>
	public event Action? VoicesLoaded;

	public SpeechSynthesizerService(DaemonClient daemon, ILogger<SpeechSynthesizerService> logger) {
		this.daemon = daemon;
		this.logger = logger;
	}

	public static string VoiceToSelectionKey(VoiceInfo voice) => $"{voice.Name} ({voice.Language})";

	/// <summary>Fetches the installed voice list from the daemon. Idempotent.</summary>
	public async Task LoadVoicesAsync() {
		if (voicesLoaded) return;

		try {
			var response = await daemon.DispatchRequestAsync(new GetVoicesRequest());

			if (response is AllVoicesResponse voicesResponse) {
				voicesByKey.Clear();
				foreach (var voice in voicesResponse.Voices) {
					voicesByKey[VoiceToSelectionKey(voice)] = voice;
				}
				voicesLoaded = true;
				logger.LogInformation("Loaded {Count} voices. Default: {Default}.",
					voicesResponse.Voices.Length, voicesResponse.DefaultVoice.Name);
				VoicesLoaded?.Invoke();
			} else {
				logger.LogError("Unexpected response type {Type} for GetVoices.", response.Type);
			}
		} catch (Exception ex) {
			logger.LogError(ex, "Failed to load voices from daemon.");
		}
	}

	/// <summary>Returns all voice selection keys, ordered by language.</summary>
	public string[] GetVoiceKeys() {
		return voicesByKey.Keys.OrderBy(k => k).ToArray();
	}

	/// <summary>Returns a sensible default voice key, or the first available one.</summary>
	public string GetDefaultVoiceKey() {
		if (voicesByKey.Count == 0) return "";
		return voicesByKey.Keys.OrderBy(k => k).First();
	}

	// === ISpeechSynthesizer ===

	/// <summary>
	/// Synthesizes a Text or SsmlRoot node into WAV bytes.
	/// The daemon request's VoiceName is the default voice; [voice] tags
	/// inside the SSML select other voices natively.
	/// </summary>
	public async Task<byte[]> SynthesizeAsync(RenderNode node, string defaultVoiceKey, double pitch, double rate, int volumePercent) {

		string input;
		bool isSsml;

		if (node.Type == RenderNodeType.Text) {
			input = node.Attributes.GetValueOrDefault(RenderNodeAttribute.TextContent, "");
			isSsml = false;
		} else if (node.Type == RenderNodeType.SsmlRoot) {
			input = MessageProcessor.StringifyNodeRecursive(node);
			isSsml = true;
		} else {
			throw new NotSupportedException($"Unsupported node type \"{node.Type}\" for direct synthesis.");
		}

		VoiceInfo voice = GetVoiceByKey(defaultVoiceKey)
			?? throw new InvalidOperationException($"No voice under key \"{defaultVoiceKey}\"");

		var request = new SynthesizeRequest {
			InputString = input,
			IsSsml = isSsml,
			VoiceName = voice.Name,
			Pitch = pitch,
			Rate = rate,
			Volume = volumePercent / 100.0,
		};

		var response = await daemon.DispatchRequestAsync(request);

		if (response is SynthesisResultResponse synthResponse) {
			if (!synthResponse.GivenVoiceExists) {
				logger.LogWarning("Selected voice does not exist. The default voice was used.");
			}
			return synthResponse.SynthesizedData;
		}

		throw new InvalidOperationException($"Unexpected response type {response.Type} for synthesize.");
	}

	public async Task<VoiceInfo[]> GetInstalledVoicesAsync() {
		await LoadVoicesAsync();
		return voicesByKey.Values.ToArray();
	}

	public VoiceInfo? GetVoiceByKey(string key) {
		return voicesByKey.GetValueOrDefault(key);
	}

	public void StartDaemon() {
		daemon.StartDaemon();
	}

	public async Task ShutdownAsync() {
		try {
			await daemon.DispatchRequestAsync(new TerminateRequest());
			logger.LogInformation("Daemon terminated gracefully.");
		} catch (Exception ex) {
			logger.LogError(ex, "Daemon did not terminate gracefully.");
		} finally {
			daemon.CloseAndDisposeDaemon();
		}
	}
}
