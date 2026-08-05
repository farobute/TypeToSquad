using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
/// Dual-backend speech synthesizer:
/// - WinRT daemon for standard system voices
/// - <c>edge-tts</c> CLI (Python package) for AppX-installed neural voices
/// </summary>
public class SpeechSynthesizerService : ISpeechSynthesizer {

	readonly DaemonClient daemon;
	readonly EdgeTtsClient edgeTts;
	readonly ILogger logger;

	readonly Dictionary<string, VoiceInfo> voicesByKey = new();
	bool voicesLoaded = false;

	public event Action? VoicesLoaded;

	public SpeechSynthesizerService(
		DaemonClient daemon, EdgeTtsClient edgeTts,
		ILogger<SpeechSynthesizerService> logger
	) {
		this.daemon = daemon;
		this.edgeTts = edgeTts;
		this.logger = logger;
	}

	public static string VoiceToSelectionKey(VoiceInfo voice) =>
		$"{voice.Name} ({voice.Language})";

	public async Task LoadVoicesAsync() {
		if (voicesLoaded) return;

		try {
			var response = await daemon.DispatchRequestAsync(
				new GetVoicesRequest());

			if (response is AllVoicesResponse voicesResponse) {
				voicesByKey.Clear();
				foreach (var v in voicesResponse.Voices)
					voicesByKey[VoiceToSelectionKey(v)] = v;

				var appxVoices = AppXVoiceDiscoveryService.Discover(logger);
				var seenNames = new HashSet<string>(
					voicesResponse.Voices.Select(v => v.Name),
					StringComparer.OrdinalIgnoreCase);
				int appxAdded = 0;
				foreach (var v in appxVoices) {
					if (seenNames.Add(v.Name)) {
						voicesByKey[VoiceToSelectionKey(v)] = v;
						appxAdded++;
					}
				}

				voicesLoaded = true;
				logger.LogInformation(
					"Loaded {Total} voices ({WinRT} system + {AppX} neural/Edge-TTS).",
					voicesByKey.Count, voicesResponse.Voices.Length, appxAdded);
				VoicesLoaded?.Invoke();
			}
		} catch (Exception ex) {
			logger.LogError(ex, "Failed to load voices.");
		}
	}

	public string[] GetVoiceKeys() => voicesByKey.Keys.OrderBy(k => k).ToArray();

	public string GetDefaultVoiceKey() {
		if (voicesByKey.Count == 0) return "";
		return voicesByKey.Keys.OrderBy(k => k).First();
	}

	// === ISpeechSynthesizer ===

	public async Task<byte[]> SynthesizeAsync(
		RenderNode node, string defaultVoiceKey,
		double pitch, double rate, int volumePercent
	) {
		VoiceInfo voice = GetVoiceByKey(defaultVoiceKey)
			?? throw new InvalidOperationException(
				$"No voice under key \"{defaultVoiceKey}\"");

		// Extract text
		string text = node.Type == RenderNodeType.Text
			? node.Attributes.GetValueOrDefault(
				RenderNodeAttribute.TextContent, "")
			: MessageProcessor.StringifyNodeRecursive(node);

		// Route: AppX → Edge-TTS, standard → daemon
		bool isAppxVoice = voice.Id?.StartsWith(
			"appx:", StringComparison.OrdinalIgnoreCase) == true;

		if (isAppxVoice) {
			string lang = string.IsNullOrEmpty(voice.Language)
				? "zh-CN" : voice.Language;
			string shortName = EdgeTtsClient.GetEdgeVoiceShortName(voice);

			logger.LogInformation(
				"Edge-TTS: {Short} ({Chars} chars)", shortName, text.Length);

			return await edgeTts.SynthesizeAsync(
				text, shortName, pitch, rate, volumePercent, lang,
				CancellationToken.None);
		}

		// Standard daemon path
		var request = new SynthesizeRequest {
			InputString = text,
			IsSsml = node.Type != RenderNodeType.Text,
			VoiceName = voice.Name,
			Pitch = pitch,
			Rate = rate,
			Volume = volumePercent / 100.0,
		};

		var response = await daemon.DispatchRequestAsync(request);

		if (response is SynthesisResultResponse synthResponse) {
			if (!synthResponse.GivenVoiceExists)
				logger.LogWarning("Voice \"{Voice}\" not found.", voice.Name);
			return synthResponse.SynthesizedData;
		}

		throw new InvalidOperationException(
			$"Unexpected response type {response.Type}.");
	}

	public async Task<VoiceInfo[]> GetInstalledVoicesAsync() {
		await LoadVoicesAsync();
		return voicesByKey.Values.ToArray();
	}

	public VoiceInfo? GetVoiceByKey(string key) =>
		voicesByKey.GetValueOrDefault(key);

	public void StartDaemon() => daemon.StartDaemon();

	public async Task ShutdownAsync() {
		try {
			await daemon.DispatchRequestAsync(new TerminateRequest());
		} catch { /* best-effort */ }
		daemon.CloseAndDisposeDaemon();
	}
}
