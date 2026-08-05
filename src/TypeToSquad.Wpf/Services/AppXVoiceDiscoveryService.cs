using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;

using Microsoft.Extensions.Logging;

using VoiceInfo = WinRTSpeechSynthServer.Protocol.VoiceInfo;
using VoiceGender = WinRTSpeechSynthServer.Protocol.VoiceGender;

namespace TypeToSquad.Wpf.Services;

/// <summary>
/// Discovers offline neural TTS voices installed through Windows
/// AppX voice packages (Windows Settings → Speech → Add voices).
///
/// WinRT's <c>SpeechSynthesizer.AllVoices</c> does NOT return
/// AppX-registered voices for unpackaged desktop apps, but the
/// speech engine CAN use them through SSML &lt;voice name="..."&gt;.
///
/// This service queries <c>Get-AppxPackage</c> for voice packages,
/// parses their manifests to get display name and language, and
/// returns <see cref="VoiceInfo"/> entries.
/// </summary>
public static class AppXVoiceDiscoveryService {

	public static List<VoiceInfo> Discover(ILogger logger) {
		var result = new List<VoiceInfo>();

		try {
			var psi = new ProcessStartInfo {
				FileName = "powershell.exe",
				Arguments = "-NoProfile -Command \"Get-AppxPackage | Where-Object { $_.Name -like 'MicrosoftWindows.Voice.*' } | ForEach-Object { $_.InstallLocation + '|' + $_.Name } | ConvertTo-Json\"",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			};

			using var proc = Process.Start(psi);
			if (proc is null) return result;

			proc.WaitForExit(30000);
			string output = proc.StandardOutput.ReadToEnd();

			if (string.IsNullOrWhiteSpace(output)) return result;

			// Parse JSON array of "path|name" strings
			var entries = JsonSerializer.Deserialize<JsonElement>(output);

			foreach (var entry in entries.EnumerateArray()) {
				string? raw = entry.GetString();
				if (string.IsNullOrEmpty(raw)) continue;

				string[] parts = raw.Split('|', 2);
				string installPath = parts[0];
				string packageName = parts.Length > 1 ? parts[1] : "";

				var voiceInfo = ParseVoicePackage(installPath, packageName, logger);
				if (voiceInfo is not null)
					result.Add(voiceInfo);
			}
		} catch (Exception ex) {
			logger.LogWarning(ex, "Failed to discover AppX voice packages.");
		}

		return result;
	}

	static VoiceInfo? ParseVoicePackage(string installPath, string packageName, ILogger logger) {
		try {
			string manifestPath = Path.Combine(installPath, "AppxManifest.xml");
			if (!File.Exists(manifestPath)) return null;

			var doc = XDocument.Load(manifestPath);
			XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

			var properties = doc.Root?.Element(ns + "Properties");
			string? displayName = properties?.Element(ns + "DisplayName")?.Value;
			if (string.IsNullOrEmpty(displayName)) return null;

			var resources = doc.Root?.Element(ns + "Resources");
			string? language = resources?.Element(ns + "Resource")?.Attribute("Language")?.Value ?? "zh-CN";

			// Use a synthetic Id from the package name
			string id = $"appx:{packageName}";

			VoiceGender gender = GuessGender(packageName, displayName);

			return new VoiceInfo {
				Id = id,
				Name = displayName,
				Language = language,
				Gender = gender,
			};
		} catch (Exception ex) {
			logger.LogDebug(ex, "Failed to parse voice package at {Path}.", installPath);
			return null;
		}
	}

	static VoiceGender GuessGender(string packageName, string displayName) {
		string lower = (packageName + displayName).ToLowerInvariant();
		if (lower.Contains("xiaoxiao") || lower.Contains("xiaoyi") || lower.Contains("xiaobei"))
			return VoiceGender.Female;
		if (lower.Contains("yunxi") || lower.Contains("yunyang") || lower.Contains("yunjian"))
			return VoiceGender.Male;
		if (lower.Contains("xiaochen") || lower.Contains("xiaohan"))
			return VoiceGender.Female;
		return VoiceGender.Female;
	}
}
