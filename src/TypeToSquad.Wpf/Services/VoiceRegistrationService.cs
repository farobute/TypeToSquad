using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

using Microsoft.Extensions.Logging;
using Microsoft.Win32;

using TypeToSquad.Wpf.Infrastructure;

namespace TypeToSquad.Wpf.Services;

/// <summary>
/// Registers bundled offline neural TTS voices (AppX packages) as
/// system voices so the WinRT SpeechSynthesizer can discover them.
///
/// Voices live under <c>WinRTSpeechDaemon\Voices\</c>. Each is a
/// directory containing Tokens.xml, the model files, and a locale
/// .INI (e.g. 2052.INI for zh-CN). The service reads Tokens.xml
/// and writes the corresponding entries to the per-user speech
/// token registry keys. Those are visible to WinRT's speech APIs
/// without elevation.
/// </summary>
public class VoiceRegistrationService {

	const string VoicesSubDir = @"Voices";
	// HKLM is required — SpeechSynthesizer.AllVoices only reads from the
	// local-machine speech token store. HKCU tokens are not visible to the
	// WinRT speech API.
	const string RegistryBasePath = @"SOFTWARE\Microsoft\Speech_OneCore\Voices\Tokens";
	const string MachineRegistryBasePath = @"HKEY_LOCAL_MACHINE\" + RegistryBasePath;

	/// <summary>
	/// Scans the bundled Voices directory and registers every
	/// voice package found there. Already-registered voices are
	/// skipped (no-op).
	/// </summary>
	/// <returns>The number of voices registered.</returns>
	public static int RegisterBundledVoices(ILogger logger) {

		string daemonDir = DaemonClient.GetDaemonDirectory();
		string voicesRoot = Path.Combine(daemonDir, VoicesSubDir);

		if (!Directory.Exists(voicesRoot)) {
			logger.LogInformation("No bundled voices directory found at {Path}.", voicesRoot);
			return 0;
		}

		int registered = 0;

		foreach (string voiceDir in Directory.EnumerateDirectories(voicesRoot)) {
			try {
				if (TryRegisterVoicePackage(voiceDir, logger)) {
					registered++;
				}
			} catch (Exception ex) {
				logger.LogWarning(ex, "Failed to register voice package in {Dir}.", voiceDir);
			}
		}

		return registered;
	}

	/// <summary>
	/// Parses Tokens.xml in <paramref name="voiceDir"/> and writes
	/// the voice token to HKCU if not already present.
	/// </summary>
	static bool TryRegisterVoicePackage(string voiceDir, ILogger logger) {

		string tokensPath = Path.Combine(voiceDir, "Tokens.xml");
		if (!File.Exists(tokensPath)) {
			logger.LogWarning("No Tokens.xml in {Dir}. Skipping.", voiceDir);
			return false;
		}

		XDocument doc = XDocument.Load(tokensPath);

		// Expecting:
		// <Tokens><Category name="Voices" categoryBase="...">
		//   <Token name="TTS_MS_..." ...>...

		var category = doc.Root?.Element("Category");
		if (category is null) return false;

		foreach (var token in category.Elements("Token")) {

			string? tokenName = token.Attribute("name")?.Value;
			if (string.IsNullOrEmpty(tokenName)) continue;

			// Check both HKLM and HKCU for existing registration
			string subKey = $@"{RegistryBasePath}\{tokenName}";
			if (Registry.GetValue($@"{MachineRegistryBasePath}\{tokenName}", "", null) is not null
				|| Registry.CurrentUser.OpenSubKey(subKey) is not null) {
				logger.LogInformation(
					"Voice token {Token} already registered. Skipping.", tokenName);
				continue;
			}

			// Extract values from Tokens.xml
			string? displayName = null;
			string? clsid = null;
			string? langDataPath = null;
			string? voicePath = null;

			var attributes = new Dictionary<string, string>();

			foreach (var element in token.Elements()) {
				string name = element.Attribute("name")?.Value ?? "";
				string? value = element.Attribute("value")?.Value;

				if (value is null) continue;

				switch (element.Name.LocalName) {
					case "String" when string.IsNullOrEmpty(name):
						displayName = value;
						break;
					case "String" when name == "CLSID":
						clsid = value;
						break;
					case "String" when name == "LangDataPath":
						langDataPath = ResolvePath(value, voiceDir);
						break;
					case "String" when name == "VoicePath":
						voicePath = ResolveVoicePath(voiceDir);
						break;
					case "String":
						break;
					case "Attribute":
						attributes[name] = value;
						break;
				}
			}

			if (string.IsNullOrEmpty(clsid)) {
				logger.LogWarning("Token {Token} has no CLSID. Skipping.", tokenName);
				continue;
			}

			// Try HKLM first — required for SpeechSynthesizer.AllVoices.
			// If the process is not elevated, fall back to HKCU with a warning
			// (HKCU-only voices won't appear in the daemon's voice list).
			bool wroteToHKLM = TryWriteToken(
				Registry.LocalMachine, subKey,
				displayName, tokenName, clsid, langDataPath, voicePath, attributes);

			if (!wroteToHKLM) {
				TryWriteToken(
					Registry.CurrentUser, subKey,
					displayName, tokenName, clsid, langDataPath, voicePath, attributes);

				logger.LogWarning(
					"Voice {Token} registered in HKCU only (admin rights needed for system visibility). " +
					"Run TypeToSquad once as administrator to make the voice available.", tokenName);
			} else {
				logger.LogInformation(
					"Registered voice: {DisplayName} ({Token})", displayName, tokenName);
			}
		}

		return true;
	}

	/// <summary>Writes a voice token to the given registry hive. Returns false on access denied.</summary>
	static bool TryWriteToken(
		RegistryKey hive,
		string subKey,
		string? displayName,
		string tokenName,
		string clsid,
		string? langDataPath,
		string? voicePath,
		Dictionary<string, string> attributes
	) {
		try {
			using var tokenKey = hive.CreateSubKey(subKey);
			tokenKey.SetValue("", displayName ?? tokenName, RegistryValueKind.String);
			tokenKey.SetValue("CLSID", clsid, RegistryValueKind.String);

			// Language code at token root (required — matches system voice format)
			if (attributes.TryGetValue("Language", out string? langCode)) {
				tokenKey.SetValue(langCode, displayName ?? tokenName, RegistryValueKind.String);
			}

			// Paths use REG_EXPAND_SZ (matching system voices like Huihui)
			if (langDataPath is not null)
				tokenKey.SetValue("LangDataPath", langDataPath, RegistryValueKind.ExpandString);
			if (voicePath is not null)
				tokenKey.SetValue("VoicePath", voicePath, RegistryValueKind.ExpandString);

			if (attributes.Count > 0) {
				using var attrKey = tokenKey.CreateSubKey("Attributes");
				foreach (var kvp in attributes)
					attrKey.SetValue(kvp.Key, kvp.Value, RegistryValueKind.String);
			}
			return true;
		} catch (UnauthorizedAccessException) {
			return false;
		}
	}

	/// <summary>
	/// Resolves a path from Tokens.xml. <c>[INSTALLDIR]</c> is
	/// replaced with the absolute path of the voice package.
	/// The suffix (e.g. "2052") is the locale code, used to find
	/// the .INI file — <c>[INSTALLDIR]2052</c> means "the locale
	/// data at the package root named 2052.INI", so we strip the
	/// numeric suffix and point VoicePath at the package root.
	/// </summary>
	static string ResolvePath(string relativeOrTokenized, string voiceDir) {
		string resolved = relativeOrTokenized.Replace("[INSTALLDIR]", voiceDir + Path.DirectorySeparatorChar);
		return Path.GetFullPath(resolved);
	}

	/// <summary>
	/// Resolves VoicePath specifically. The Tokens.xml value
	/// <c>[INSTALLDIR]2052</c> points to the locale subdirectory
	/// whose .INI file lives at the root of the package.
	/// </summary>
	static string ResolveVoicePath(string voiceDir) {
		// Check if there's a locale .INI directly at the package root
		var iniFiles = Directory.GetFiles(voiceDir, "*.INI");
		if (iniFiles.Length > 0) {
			// The locale .INI is at the root — VoicePath = the voice dir itself
			return Path.GetFullPath(voiceDir);
		}
		// Fallback: look for a numbered subdirectory
		foreach (var subdir in Directory.EnumerateDirectories(voiceDir)) {
			string name = Path.GetFileName(subdir);
			if (name.Length == 4 && int.TryParse(name, out _)) {
				return Path.GetFullPath(subdir);
			}
		}
		// Last resort
		return Path.GetFullPath(voiceDir);
	}
}
