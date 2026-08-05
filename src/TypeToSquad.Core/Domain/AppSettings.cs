using System.Collections.Generic;

namespace TypeToSquad.Core.Domain;

/// <summary>
/// Aggregate root for all user settings.
/// Serialized to/from config.json via System.Text.Json.
/// </summary>
public record AppSettings {

	// === Hotkeys ===

	/// <summary>Global hotkey to summon the input popup. Default "Ctrl+Shift+T".</summary>
	public string SummonHotkey { get; set; } = "Ctrl+Shift+T";

	/// <summary>Global hotkey to stop all playback. Default "Ctrl+Shift+X".</summary>
	public string StopHotkey { get; set; } = "Ctrl+Shift+X";

	// === Voice ===

	/// <summary>Key of the selected TTS voice (e.g. "Microsoft Zira (en-US)").</summary>
	public string VoiceKey { get; set; } = "";

	/// <summary>Synthesis volume percent (0-100). Default 100.</summary>
	public int SynthesisVolumePercent { get; set; } = 100;

	/// <summary>Voice pitch multiplier (0.0-2.0). Default 1.0.</summary>
	public double VoicePitch { get; set; } = 1.0;

	/// <summary>Voice rate / speed multiplier (0.5-6.0). Default 1.0.</summary>
	public double VoiceRate { get; set; } = 1.0;

	// === Output ===

	/// <summary>Name of the audio output device. Empty = system default.</summary>
	public string OutputDevice { get; set; } = "";

	/// <summary>Max number of concurrent audio streams (1-64). Default 6.</summary>
	public int MaxConcurrentStreams { get; set; } = 6;

	// === Messages ===

	/// <summary>Number of history entries kept in memory (>=0). Default 32.</summary>
	public int HistorySlots { get; set; } = 32;

	/// <summary>Max passes of text replacement (0-100). Default 20.</summary>
	public int MaxReplacementPasses { get; set; } = 20;

	// === Text Replacements ===

	/// <summary>Regex pattern→substitution rules.</summary>
	public List<TextReplacement> TextReplacements { get; set; } = new();

	/// <summary>Voice change hint→voiceKey mappings.</summary>
	public List<VoiceChangeMapping> VoiceChanges { get; set; } = new();

	/// <summary>Sound effect hint→filePath+volume mappings.</summary>
	public List<SoundEffectMapping> SoundEffects { get; set; } = new();

	/// <summary>Custom user tag definitions.</summary>
	public List<UserTagDefinition> UserTags { get; set; } = new();

	// === Notifications ===

	/// <summary>Show tray balloon on errors. Default true.</summary>
	public bool EnableErrorNotifications { get; set; } = true;

	/// <summary>Show tray balloon on warnings. Default false.</summary>
	public bool EnableWarningNotifications { get; set; } = false;

	// === Helpers for the processing pipeline ===

	/// <summary>
	/// Returns text replacements as a list of tuples for the processing pipeline.
	/// </summary>
	public IReadOnlyList<(string pattern, string replacement)> GetTextReplacements() {
		var result = new List<(string, string)>(TextReplacements.Count);
		foreach (var tr in TextReplacements) {
			result.Add((tr.Pattern, tr.Replacement));
		}
		return result;
	}

	/// <summary>
	/// Returns user tags as a list of tuples for the processing pipeline.
	/// </summary>
	public IReadOnlyList<(string type, string pattern, string replacement)> GetUserTags() {
		var result = new List<(string, string, string)>(UserTags.Count);
		foreach (var ut in UserTags) {
			result.Add((ut.TagType, ut.Pattern, ut.Replacement));
		}
		return result;
	}

	/// <summary>
	/// Returns voice changes as a list of tuples for the processing pipeline.
	/// </summary>
	public IReadOnlyList<(string hint, string voiceKey)> GetVoiceChanges() {
		var result = new List<(string, string)>(VoiceChanges.Count);
		foreach (var vc in VoiceChanges) {
			result.Add((vc.Hint, vc.VoiceKey));
		}
		return result;
	}

	/// <summary>
	/// Returns the distinct set of user-defined tag type names.
	/// </summary>
	public IEnumerable<string> GetUserTagTypeNames() {
		var seen = new HashSet<string>();
		foreach (var ut in UserTags) {
			if (seen.Add(ut.TagType)) yield return ut.TagType;
		}
	}

	/// <summary>Creates a settings instance with safe defaults.</summary>
	public static AppSettings CreateDefault() => new();

	/// <summary>Clamps numeric fields to their valid ranges.</summary>
	public void Clamp() {
		SynthesisVolumePercent = Math.Clamp(SynthesisVolumePercent, 0, 100);
		VoicePitch = Math.Clamp(VoicePitch, 0.0, 2.0);
		VoiceRate = Math.Clamp(VoiceRate, 0.5, 6.0);
		MaxConcurrentStreams = Math.Clamp(MaxConcurrentStreams, 1, 64);
		HistorySlots = Math.Max(0, HistorySlots);
		MaxReplacementPasses = Math.Clamp(MaxReplacementPasses, 0, 100);
	}

	// We use System.Math.Clamp without ambiguity since we removed Godot's Mathf
	private static class Math {
		public static int Clamp(int value, int min, int max) =>
			value < min ? min : value > max ? max : value;

		public static double Clamp(double value, double min, double max) =>
			value < min ? min : value > max ? max : value;

		public static int Max(int a, int b) => a > b ? a : b;
	}
}

// ================================================================
// Value Objects
// ================================================================

/// <summary>A regex pattern→substitution rule for text replacements.</summary>
public record TextReplacement {
	public string Pattern { get; set; } = "";
	public string Replacement { get; set; } = "";
}

/// <summary>A hint→voice mapping for [voice hint] tags.</summary>
public record VoiceChangeMapping {
	public string Hint { get; set; } = "";
	public string VoiceKey { get; set; } = "";
}

/// <summary>A hint→filePath+volume mapping for [sound hint] tags.</summary>
public record SoundEffectMapping {
	public string Hint { get; set; } = "";
	public string FilePath { get; set; } = "";
	public int VolumePercent { get; set; } = 100;
}

/// <summary>A custom user tag definition.</summary>
public record UserTagDefinition {
	public string TagType { get; set; } = "";
	public string Pattern { get; set; } = "";
	public string Replacement { get; set; } = "";
}
