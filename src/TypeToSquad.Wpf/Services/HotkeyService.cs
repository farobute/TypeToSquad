using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

using Microsoft.Extensions.Logging;

using TypeToSquad.Wpf.Infrastructure;

namespace TypeToSquad.Wpf.Services;

/// <summary>
/// Registers global hotkeys via Win32 RegisterHotKey and raises events
/// when they are pressed, regardless of which app has focus.
/// </summary>
public class HotkeyService : IDisposable {

	readonly ILogger logger;
	readonly HwndSource hwndSource;

	HotkeyBinding? summonHotkey;
	HotkeyBinding? stopHotkey;

	/// <summary>Raised when the summon hotkey is pressed.</summary>
	public event Action? SummonPressed;

	/// <summary>Raised when the stop hotkey is pressed.</summary>
	public event Action? StopPressed;

	public HotkeyService(ILogger<HotkeyService> logger) {
		this.logger = logger;

		// Create a message-only window to receive WM_HOTKEY
		var parameters = new HwndSourceParameters("TypeToSquadHotkeyWindow") {
			WindowStyle = 0,
			Width = 0,
			Height = 0,
			PositionX = -32000,
			PositionY = -32000,
		};
		hwndSource = new HwndSource(parameters);
		hwndSource.AddHook(WndProc);
	}

	/// <summary>Parses a hotkey string like "Ctrl+Shift+T" into a binding. Returns null if invalid.</summary>
	static HotkeyBinding? ParseHotkey(string hotkeyString) {

		if (string.IsNullOrWhiteSpace(hotkeyString)) return null;

		uint modifiers = 0;
		uint keyCode = 0;

		string[] parts = hotkeyString.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length == 0) return null;

		// Last part is the key
		string keyPart = parts[^1];
		if (keyPart.Length == 1) {
			char c = char.ToUpperInvariant(keyPart[0]);
			if (c is >= 'A' and <= 'Z') {
				keyCode = c;
			} else {
				return null;
			}
		} else {
			keyCode = keyPart.ToUpperInvariant() switch {
				"F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
				"F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
				"F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
				"SPACE" => 0x20, "ENTER" => 0x0D, "TAB" => 0x09,
				_ => 0,
			};
			if (keyCode == 0) return null;
		}

		// Modifiers are the rest
		foreach (string mod in parts[..^1]) {
			modifiers |= mod.ToUpperInvariant() switch {
				"CTRL" or "CONTROL" => NativeMethods.MOD_CONTROL,
				"ALT" => NativeMethods.MOD_ALT,
				"SHIFT" => NativeMethods.MOD_SHIFT,
				"WIN" or "META" => NativeMethods.MOD_WIN,
				_ => 0,
			};
		}

		if (modifiers == 0) return null; // global hotkeys must have at least one modifier
		if (keyCode == 0) return null;

		return new HotkeyBinding { Modifiers = modifiers, VirtualKey = keyCode };
	}

	/// <summary>Registers the summon hotkey.</summary>
	public bool SetSummonHotkey(string hotkeyString) {
		return SetHotkey(ref summonHotkey, HotkeyId.Summon, hotkeyString);
	}

	/// <summary>Registers the stop hotkey.</summary>
	public bool SetStopHotkey(string hotkeyString) {
		return SetHotkey(ref stopHotkey, HotkeyId.Stop, hotkeyString);
	}

	bool SetHotkey(ref HotkeyBinding? binding, HotkeyId id, string hotkeyString) {

		// Unregister old
		if (binding is not null) {
			NativeMethods.UnregisterHotKey(hwndSource.Handle, (int)id);
			binding = null;
		}

		var parsed = ParseHotkey(hotkeyString);
		if (parsed is null) {
			logger.LogWarning("Invalid hotkey string \"{Hotkey}\". Hotkey disabled.", hotkeyString);
			return false;
		}

		bool ok = NativeMethods.RegisterHotKey(hwndSource.Handle, (int)id, parsed.Modifiers | NativeMethods.MOD_NOREPEAT, parsed.VirtualKey);

		if (!ok) {
			int error = Marshal.GetLastWin32Error();
			logger.LogWarning("Failed to register hotkey \"{Hotkey}\" (Win32 error {Error}).", hotkeyString, error);
			return false;
		}

		binding = parsed;
		logger.LogInformation("Hotkey registered: {Hotkey} (id {Id}).", hotkeyString, id);
		return true;
	}

	IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {

		if (msg == NativeMethods.WM_HOTKEY) {
			var id = (HotkeyId)wParam.ToInt32();

			switch (id) {
				case HotkeyId.Summon:
					handled = true;
					SummonPressed?.Invoke();
					break;

				case HotkeyId.Stop:
					handled = true;
					StopPressed?.Invoke();
					break;
			}
		}

		return IntPtr.Zero;
	}

	enum HotkeyId { Summon = 1, Stop = 2 }

	sealed record HotkeyBinding {
		public required uint Modifiers { get; init; }
		public required uint VirtualKey { get; init; }
	}

	public void Dispose() {
		if (summonHotkey is not null) {
			NativeMethods.UnregisterHotKey(hwndSource.Handle, (int)HotkeyId.Summon);
			summonHotkey = null;
		}
		if (stopHotkey is not null) {
			NativeMethods.UnregisterHotKey(hwndSource.Handle, (int)HotkeyId.Stop);
			stopHotkey = null;
		}
		hwndSource.Dispose();
	}
}
