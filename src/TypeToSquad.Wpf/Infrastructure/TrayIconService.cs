using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

using Microsoft.Extensions.Logging;

namespace TypeToSquad.Wpf.Infrastructure;

/// <summary>
/// Wraps the WinForms NotifyIcon used for the system tray.
/// Left-click summons the input popup; the context menu hosts quick settings.
/// </summary>
public class TrayIconService : IDisposable {

	readonly NotifyIcon notifyIcon;
	readonly ILogger logger;

	public event Action? SummonRequested;
	public event Action? SettingsRequested;
	public event Action? ExitRequested;

	/// <summary>Event raised with a menu item tag when a voice is selected from the tray menu.</summary>
	public event Action<string>? VoiceSelected;

	public TrayIconService(ILogger<TrayIconService> logger) {
		this.logger = logger;

		notifyIcon = new NotifyIcon {
			Icon = CreateAppIcon(),
			Text = "TypeToSquad",
			Visible = true,
		};

		notifyIcon.MouseClick += (_, e) => {
			if (e.Button == MouseButtons.Left) {
				SummonRequested?.Invoke();
			}
		};

		BuildContextMenu();
	}

	/// <summary>Rebuilds the context menu with the given voices and settings state.</summary>
	public void UpdateContextMenu(
		IReadOnlyList<string> voiceKeys,
		string currentVoiceKey,
		int volumePercent,
		string currentOutputDevice,
		IReadOnlyList<string> outputDevices,
		bool isCurrentlySpeaking
	) {
		BuildContextMenu(voiceKeys, currentVoiceKey, volumePercent, currentOutputDevice, outputDevices, isCurrentlySpeaking);
	}

	void BuildContextMenu(
		IReadOnlyList<string>? voiceKeys = null,
		string currentVoiceKey = "",
		int volumePercent = 100,
		string currentOutputDevice = "",
		IReadOnlyList<string>? outputDevices = null,
		bool isCurrentlySpeaking = false
	) {
		var menu = new ContextMenuStrip();
		menu.ShowImageMargin = false;

		// --- Voice submenu ---
		var voiceMenu = new ToolStripMenuItem("语音 (Voice)");
		if (voiceKeys is not null && voiceKeys.Count > 0) {
			foreach (string key in voiceKeys) {
				var item = new ToolStripMenuItem(key) {
					Checked = key == currentVoiceKey,
					Tag = key,
				};
				item.Click += (_, _) => {
					if (item.Tag is string tag) VoiceSelected?.Invoke(tag);
				};
				voiceMenu.DropDownItems.Add(item);
			}
		} else {
			voiceMenu.Enabled = false;
			voiceMenu.Text = "语音 (加载中...)";
		}
		menu.Items.Add(voiceMenu);

		// --- Volume submenu (TrackBar hosted in the dropdown) ---
		var volumeMenu = new ToolStripMenuItem($"音量 (Volume): {volumePercent}%");
		var volumeTrackBar = new TrackBar {
			Minimum = 0,
			Maximum = 100,
			Value = volumePercent,
			Width = 160,
			Height = 40,
			TickStyle = TickStyle.None,
			AutoSize = false,
		};
		volumeTrackBar.ValueChanged += (_, _) => {
			volumeMenu.Text = $"音量 (Volume): {volumeTrackBar.Value}%";
		};
		volumeTrackBar.MouseUp += (_, _) => {
			VolumeChanged?.Invoke(volumeTrackBar.Value);
		};
		volumeMenu.DropDownItems.Add(new ToolStripControlHost(volumeTrackBar));
		menu.Items.Add(volumeMenu);

		// --- Output device submenu ---
		var deviceMenu = new ToolStripMenuItem("输出设备 (Output Device)");
		if (outputDevices is not null && outputDevices.Count > 0) {

			// "System default" entry (empty string)
			var defaultItem = new ToolStripMenuItem("(系统默认)") {
				Checked = string.IsNullOrEmpty(currentOutputDevice),
				Tag = "",
			};
			defaultItem.Click += (_, _) => OutputDeviceSelected?.Invoke("");
			deviceMenu.DropDownItems.Add(defaultItem);
			deviceMenu.DropDownItems.Add(new ToolStripSeparator());

			foreach (string device in outputDevices) {
				var item = new ToolStripMenuItem(device) {
					Checked = device == currentOutputDevice,
					Tag = device,
				};
				item.Click += (_, _) => {
					if (item.Tag is string tag) OutputDeviceSelected?.Invoke(tag);
				};
				deviceMenu.DropDownItems.Add(item);
			}
		} else {
			deviceMenu.Enabled = false;
			deviceMenu.Text = "输出设备 (加载中...)";
		}
		menu.Items.Add(deviceMenu);

		// --- Settings ---
		var settingsItem = new ToolStripMenuItem("设置 (Settings...)");
		settingsItem.Click += (_, _) => SettingsRequested?.Invoke();
		menu.Items.Add(settingsItem);

		// --- Stop speaking ---
		var stopItem = new ToolStripMenuItem("停止朗读 (Stop)") {
			Enabled = isCurrentlySpeaking,
		};
		stopItem.Click += (_, _) => StopRequested?.Invoke();
		menu.Items.Add(stopItem);

		// --- Exit ---
		var exitItem = new ToolStripMenuItem("退出 (Exit)");
		exitItem.Click += (_, _) => ExitRequested?.Invoke();
		menu.Items.Add(exitItem);

		notifyIcon.ContextMenuStrip = menu;
	}

	/// <summary>Shows a tray balloon notification.</summary>
	public void ShowBalloonTip(string title, string message, ToolTipIcon icon = ToolTipIcon.Info) {
		notifyIcon.ShowBalloonTip(3000, title, message, icon);
	}

	/// <summary>Generates a simple speaker glyph icon at runtime (no .ico asset needed).</summary>
	static Icon CreateAppIcon() {

		using var bitmap = new Bitmap(32, 32);
		using (var g = Graphics.FromImage(bitmap)) {
			g.SmoothingMode = SmoothingMode.AntiAlias;

			// Background: rounded dark square
			using var backgroundBrush = new SolidBrush(Color.FromArgb(255, 27, 31, 38));
			g.FillRoundedRectangle(backgroundBrush, 1, 1, 30, 30, 8);

			// Speaker: white rounded rect + triangle
			using var whiteBrush = new SolidBrush(Color.White);
			g.FillRectangle(whiteBrush, 5, 13, 5, 6);          // speaker body
			using var speakerPath = new GraphicsPath();
			speakerPath.AddPolygon([new PointF(9, 13), new PointF(9, 19), new PointF(14, 23), new PointF(14, 9)]);
			g.FillPath(whiteBrush, speakerPath);                 // speaker cone

			// Sound waves
			using var wavePen = new Pen(Color.White, 1.5f);
			g.DrawArc(wavePen, 15, 11, 6, 10, -50, 100);
			g.DrawArc(wavePen, 18, 9, 6, 14, -50, 100);
		}

		IntPtr hIcon = bitmap.GetHicon();
		return Icon.FromHandle(hIcon);
	}

	/// <summary>Raised when the user adjusts volume from the tray menu.</summary>
	public event Action<int>? VolumeChanged;

	/// <summary>Raised when the user selects an output device from the tray menu.</summary>
	public event Action<string>? OutputDeviceSelected;

	/// <summary>Raised when the user selects "Stop" from the tray menu.</summary>
	public event Action? StopRequested;

	public void Dispose() {
		notifyIcon.Visible = false;
		notifyIcon.Dispose();
	}
}

/// <summary>Small extensions for rounded-rectangle drawing.</summary>
internal static class GraphicsExtensions {
	public static void FillRoundedRectangle(this Graphics g, Brush brush, int x, int y, int width, int height, int radius) {
		using var path = new GraphicsPath();
		path.AddArc(x, y, radius * 2, radius * 2, 180, 90);
		path.AddArc(x + width - radius * 2, y, radius * 2, radius * 2, 270, 90);
		path.AddArc(x + width - radius * 2, y + height - radius * 2, radius * 2, radius * 2, 0, 90);
		path.AddArc(x, y + height - radius * 2, radius * 2, radius * 2, 90, 90);
		path.CloseFigure();
		g.FillPath(brush, path);
	}
}
