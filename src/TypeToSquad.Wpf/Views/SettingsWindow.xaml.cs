using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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

		// Hotkeys
		SummonHotkeyBox.Text = settings.SummonHotkey;
		StopHotkeyBox.Text = settings.StopHotkey;

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
		workingSettings.SummonHotkey = SummonHotkeyBox.Text.Trim();
		workingSettings.StopHotkey = StopHotkeyBox.Text.Trim();
	}

	// === Hotkey capture ===
	//
	// The hotkey boxes are read-only TextBoxes. When focused, the next
	// modifier+key combination pressed is recorded and displayed.
	// Backspace/Delete clears (hotkey disabled), Esc restores the old value.

	void OnHotkeyPreviewKeyDown(object sender, KeyEventArgs e) {

		var box = (TextBox)sender;
		e.Handled = true; // never type into the box

		switch (e.Key) {

			case Key.Escape:
				// Restore the previous value
				box.Text = ReferenceEquals(box, SummonHotkeyBox)
					? workingSettings.SummonHotkey
					: workingSettings.StopHotkey;
				return;

			case Key.Back:
			case Key.Delete:
				// Clear = disable this hotkey
				box.Text = "";
				return;

			case Key.LeftCtrl:
			case Key.RightCtrl:
			case Key.LeftShift:
			case Key.RightShift:
			case Key.LeftAlt:
			case Key.RightAlt:
			case Key.LWin:
			case Key.RWin:
			case Key.System:
				// Pure modifier presses are ignored — wait for the actual key
				return;
		}

		string? keyName = KeyToHotkeyName(e.Key);
		if (keyName is null) return;

		var modifiers = Keyboard.Modifiers;

		// Global hotkeys must include at least one modifier
		if (modifiers == ModifierKeys.None) return;

		box.Text = BuildHotkeyString(modifiers, keyName);
	}

	/// <summary>Maps a WPF Key to the config vocabulary (A-Z, 0-9, F1-F12, SPACE, ENTER, TAB).</summary>
	static string? KeyToHotkeyName(Key key) {

		if (key is >= Key.A and <= Key.Z) return key.ToString();
		if (key is >= Key.D0 and <= Key.D9) return key.ToString()[1..]; // "D3" → "3"
		if (key is >= Key.NumPad0 and <= Key.NumPad9) return ((int)key - (int)Key.NumPad0).ToString();
		if (key is >= Key.F1 and <= Key.F12) return key.ToString();

		return key switch {
			Key.Space => "SPACE",
			Key.Enter => "ENTER",
			Key.Tab => "TAB",
			_ => null,
		};
	}

	static string BuildHotkeyString(ModifierKeys modifiers, string keyName) {

		var parts = new List<string>();

		if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("CTRL");
		if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("ALT");
		if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("SHIFT");
		if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("WIN");

		parts.Add(keyName);
		return string.Join("+", parts);
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
