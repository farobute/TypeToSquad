using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

using TypeToSquad.Core.Domain;

namespace TypeToSquad.Wpf.Views;

/// <summary>
/// Settings window with medium scope: voice, volume/pitch/rate,
/// output device, history, and the text replacement table.
/// Changes are saved on close.
/// </summary>
public partial class SettingsWindow : Window {

	public const string DefaultDeviceLabel = "(系统默认)";

	readonly AppSettings workingSettings;

	/// <summary>Raised after settings are saved, so the app can re-apply hotkeys etc.</summary>
	public event System.Action? SettingsSaved;

	public SettingsWindow(AppSettings settings, string[] voiceKeys, string[] outputDevices) {

		// Work on a copy; save on close
		workingSettings = settings;

		InitializeComponent();

		// Voice
		VoiceCombo.ItemsSource = voiceKeys;
		VoiceCombo.SelectedItem = voiceKeys.Contains(settings.VoiceKey) ? settings.VoiceKey : null;
		VoiceCombo.SelectionChanged += (_, _) => {
			if (VoiceCombo.SelectedItem is string key) workingSettings.VoiceKey = key;
		};

		// Volume / pitch / rate
		VolumeSlider.Value = settings.SynthesisVolumePercent;
		PitchSlider.Value = settings.VoicePitch;
		RateSlider.Value = settings.VoiceRate;

		// Output device — first entry is the system default (empty string)
		var deviceItems = new System.Collections.Generic.List<string> { DefaultDeviceLabel };
		deviceItems.AddRange(outputDevices);
		DeviceCombo.ItemsSource = deviceItems;
		DeviceCombo.SelectedItem = string.IsNullOrEmpty(settings.OutputDevice) ? DefaultDeviceLabel : settings.OutputDevice;
		DeviceCombo.SelectionChanged += (_, _) => {
			var selected = DeviceCombo.SelectedItem as string;
			workingSettings.OutputDevice = (selected is null || selected == DefaultDeviceLabel) ? "" : selected;
		};

		// History / passes
		HistorySlotsSlider.Value = settings.HistorySlots;
		ReplacementPassesSlider.Value = settings.MaxReplacementPasses;
		MaxStreamsSlider.Value = settings.MaxConcurrentStreams;

		// Hotkey info (read-only; edited in config.json)
		HotkeyInfo.Text = $"呼出: {settings.SummonHotkey}    停止: {settings.StopHotkey}";

		// Replacements
		var replacements = new ObservableCollection<TextReplacement>(settings.TextReplacements);
		ReplacementsGrid.ItemsSource = replacements;
	}

	protected override void OnClosing(System.ComponentModel.CancelEventArgs e) {
		base.OnClosing(e);

		// Commit changes to the live settings object
		ApplyToSettings();

		// Add/remove rows from the grid are auto-synced by the ObservableCollection
		workingSettings.TextReplacements = ReplacementsGrid.ItemsSource is ObservableCollection<TextReplacement> coll
			? coll.ToList()
			: workingSettings.TextReplacements;

		SettingsSaved?.Invoke();
	}

	void ApplyToSettings() {
		workingSettings.VoiceKey = VoiceCombo.SelectedItem as string ?? workingSettings.VoiceKey;
		workingSettings.SynthesisVolumePercent = (int)VolumeSlider.Value;
		workingSettings.VoicePitch = PitchSlider.Value;
		workingSettings.VoiceRate = RateSlider.Value;
		workingSettings.OutputDevice = DeviceCombo.SelectedItem as string ?? "";
		workingSettings.MaxConcurrentStreams = (int)MaxStreamsSlider.Value;
		workingSettings.HistorySlots = (int)HistorySlotsSlider.Value;
		workingSettings.MaxReplacementPasses = (int)ReplacementPassesSlider.Value;
	}

	// === Slider value labels ===

	void OnVolumeSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
		VolumeValue.Text = $"{(int)e.NewValue}%";

	void OnPitchSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
		PitchValue.Text = e.NewValue.ToString("0.00");

	void OnRateSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
		RateValue.Text = e.NewValue.ToString("0.00");

	void OnMaxStreamsSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
		MaxStreamsValue.Text = $"{(int)e.NewValue}";

	void OnHistorySlotsSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
		HistorySlotsValue.Text = $"{(int)e.NewValue}";

	void OnReplacementPassesSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
		ReplacementPassesValue.Text = $"{(int)e.NewValue}";

	// === Replacements buttons ===

	void OnAddReplacementClicked(object sender, RoutedEventArgs e) {
		if (ReplacementsGrid.ItemsSource is ObservableCollection<TextReplacement> coll) {
			coll.Add(new TextReplacement());
		}
	}

	void OnRemoveReplacementClicked(object sender, RoutedEventArgs e) {
		if (ReplacementsGrid.ItemsSource is ObservableCollection<TextReplacement> coll
			&& ReplacementsGrid.SelectedItem is TextReplacement selected) {
			coll.Remove(selected);
		}
	}
}
