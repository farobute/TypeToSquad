using System.Threading.Tasks;

using TypeToSquad.Core.Domain;

using VoiceInfo = WinRTSpeechSynthServer.Protocol.VoiceInfo;

namespace TypeToSquad.Core.Ports;

/// <summary>Synthesizes speech from a RenderNode tree via the daemon.</summary>
public interface ISpeechSynthesizer {
	/// <summary>
	/// Synthesize a Text or SsmlRoot node into WAV audio bytes.
	/// </summary>
	/// <param name="node">The processed message tree.</param>
	/// <param name="defaultVoiceKey">Key of the default TTS voice for this request.</param>
	/// <param name="pitch">Voice pitch (0.0-2.0).</param>
	/// <param name="rate">Voice rate (0.5-6.0).</param>
	/// <param name="volumePercent">Volume (0-100).</param>
	/// <returns>PCM WAV audio data.</returns>
	Task<byte[]> SynthesizeAsync(RenderNode node, string defaultVoiceKey, double pitch, double rate, int volumePercent);

	/// <summary>Returns the list of installed TTS voices.</summary>
	Task<VoiceInfo[]> GetInstalledVoicesAsync();

	/// <summary>Look up a voice by its key.</summary>
	VoiceInfo? GetVoiceByKey(string key);

	/// <summary>Start the daemon process if not already running.</summary>
	void StartDaemon();

	/// <summary>Gracefully shut down the daemon.</summary>
	Task ShutdownAsync();
}
