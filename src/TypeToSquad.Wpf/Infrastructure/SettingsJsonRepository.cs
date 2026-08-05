using System;
using System.IO;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using TypeToSquad.Core.Domain;
using TypeToSquad.Core.Ports;

namespace TypeToSquad.Wpf.Infrastructure;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON in the user's AppData directory.
/// </summary>
public class SettingsJsonRepository : ISettingsRepository {

	readonly ILogger logger;
	readonly string settingsDirectory;
	readonly string settingsPath;

	static readonly JsonSerializerOptions jsonOptions = new() {
		WriteIndented = true,
	};

	public SettingsJsonRepository(ILogger<SettingsJsonRepository> logger) {
		this.logger = logger;

		settingsDirectory = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
			"TypeToSquad"
		);
		settingsPath = Path.Combine(settingsDirectory, "config.json");
	}

	/// <summary>Absolute path to the config file (for display in settings UI).</summary>
	public string SettingsPath => settingsPath;

	public AppSettings Load() {

		// Create defaults if no file exists
		if (!File.Exists(settingsPath)) {
			var defaults = AppSettings.CreateDefault();
			Save(defaults);
			return defaults;
		}

		try {
			string json = File.ReadAllText(settingsPath);
			AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, jsonOptions);

			if (settings is null) {
				logger.LogError("Could not load settings: file parsed to null. Using defaults.");
				return AppSettings.CreateDefault();
			}

			settings.Clamp();
			return settings;
		} catch (Exception ex) {
			logger.LogError(ex, "Could not load settings: file is malformed. Using defaults.");
			return AppSettings.CreateDefault();
		}
	}

	public void Save(AppSettings settings) {
		try {
			Directory.CreateDirectory(settingsDirectory);
			string json = JsonSerializer.Serialize(settings, jsonOptions);
			File.WriteAllText(settingsPath, json);
			logger.LogInformation("Settings saved to {Path}.", settingsPath);
		} catch (Exception ex) {
			logger.LogError(ex, "Could not save settings.");
		}
	}
}
