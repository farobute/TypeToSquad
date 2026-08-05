using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using VoiceInfo = WinRTSpeechSynthServer.Protocol.VoiceInfo;

namespace TypeToSquad.Wpf.Services;

/// <summary>
/// Synthesizes speech via Edge-TTS (community-maintained Python package).
/// Calls <c>edge-tts</c> CLI as a subprocess with <c>-t</c> (text) flag.
///
/// Requires: <c>pip install edge-tts</c> (one-time setup)
/// </summary>
public class EdgeTtsClient {

	readonly ILogger logger;

	public EdgeTtsClient(ILogger<EdgeTtsClient> logger) {
		this.logger = logger;
	}

	/// <summary>
	/// Synthesizes text via Edge-TTS and returns MP3 audio bytes.
	/// </summary>
	public async Task<byte[]> SynthesizeAsync(
		string text, string voiceShortName,
		double pitch, double rate, int volumePercent,
		string language = "zh-CN",
		CancellationToken cancel = default
	) {
		string tempDir = Path.GetTempPath();
		string outputFile = Path.Combine(tempDir, $"tts_e_{Guid.NewGuid():N}.mp3");

		try {
			// edge-tts CLI: -t for plain text, --voice for voice, --write-media for output
		// Escape double-quotes for command line
		// Escape double-quotes for command line
			string escaped = text.Replace("\"", "\"\"");
			// Base command: -t for text, --voice for voice selection, --write-media for output
			var psi = new ProcessStartInfo {
				FileName = "python",
				Arguments = $"-m edge_tts -t \"{escaped}\" --voice \"{voiceShortName}\" --write-media \"{outputFile}\"",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};

			using var proc = Process.Start(psi);
			if (proc is null)
				throw new InvalidOperationException("Failed to start edge-tts.");

			var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
			await proc.WaitForExitAsync(timeoutCts.Token);

			if (proc.ExitCode != 0) {
				string err = await proc.StandardError.ReadToEndAsync();
				// edge-tts writes status to stderr; exit code 0 = success
				if (proc.ExitCode != 0)
					throw new InvalidOperationException(
						$"edge-tts error (code {proc.ExitCode}): {err.Trim()}");
			}

			if (!File.Exists(outputFile) || new FileInfo(outputFile).Length == 0)
				throw new InvalidOperationException("edge-tts produced no audio.");

			byte[] audio = await File.ReadAllBytesAsync(outputFile, cancel);

			logger.LogInformation("Edge-TTS: {Bytes} bytes via {Voice}.",
				audio.Length, voiceShortName);

			return audio;
		} finally {
			try { File.Delete(outputFile); } catch { }
		}
	}

	public static string GetEdgeVoiceShortName(VoiceInfo voice) {
		// Preserve original language case (zh-CN, not zh-cn)
		// Edge-TTS voice names use BCP-47 format: zh-CN-XiaoxiaoNeural
		string lang = voice.Language ?? "zh-CN";
		string id = voice.Id?.ToLowerInvariant() ?? "";

		if (id.Contains("xiaoxiao")) return $"{lang}-XiaoxiaoNeural";
		if (id.Contains("yunxi")) return $"{lang}-YunxiNeural";
		if (id.Contains("yunyang")) return $"{lang}-YunyangNeural";
		if (id.Contains("xiaoyi")) return $"{lang}-XiaoyiNeural";
		if (id.Contains("xiaochen")) return $"{lang}-XiaochenNeural";
		if (id.Contains("xiaohan")) return $"{lang}-XiaohanNeural";
		if (id.Contains("xiaobei")) return $"{lang}-XiaobeiNeural";

		return $"{lang}-{voice.Name.Replace(" ", "")}";
	}
}
