using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

using Forms = System.Windows.Forms;

using TypeToSquad.Core.Domain;
using TypeToSquad.Wpf.Infrastructure;

namespace TypeToSquad.Wpf.Views;

/// <summary>
/// The borderless floating input popup, shown by the summon hotkey.
/// Enter submits (hide + clear), Esc hides (preserve content),
/// Shift+Enter inserts a newline, Ctrl+Up/Down navigates history.
/// </summary>
public partial class InputPopup : Window {

	readonly HistoryTracker historyTracker;

	/// <summary>Raised when the user submits a message. Text is the raw message.</summary>
	public event Action<string>? MessageSubmitted;

	public InputPopup(HistoryTracker historyTracker) {
		this.historyTracker = historyTracker;
		InitializeComponent();

		SourceInitialized += (_, _) => {
			var hwnd = new WindowInteropHelper(this).Handle;
			NativeMethods.HideFromAltTab(hwnd);
		};

		// Position at bottom-center of the work area (subtitle position)
		PositionBottomCenter();
	}

	void PositionBottomCenter() {
		// SystemParameters.WorkArea is in WPF logical units (DIPs) —
		// the same coordinate space as Window.Left/Top. Using Forms.Screen
		// here would mix physical and logical coordinates under DPI scaling
		// and place the window off-screen.
		var workArea = SystemParameters.WorkArea;

		Left = workArea.Left + (workArea.Width - Width) / 2;
		Top = workArea.Top + workArea.Height - Height - 80;
	}

	/// <summary>Shows the popup, focused and ready. Restores preserved text if Esc was used last time.</summary>
	public void ShowPopup() {

		PositionBottomCenter();
		Show();
		Activate();

		// Windows may block focus stealing for a hotkey-summoned window.
		// The Topmost toggle forces the window to the top of the Z-order,
		// and SetForegroundWindow grants input focus.
		var hwnd = new WindowInteropHelper(this).Handle;
		Topmost = false;
		Topmost = true;
		NativeMethods.BringWindowToTop(hwnd);
		NativeMethods.SetForegroundWindow(hwnd);

		MessageTextBox.Focus();
		Keyboard.Focus(MessageTextBox);
		MessageTextBox.CaretIndex = MessageTextBox.Text.Length;
	}

	/// <summary>Called from the HwndSource hook when the window receives WM_COPYDATA "SHOW".</summary>
	public void ShowFromOtherInstance() {
		Dispatcher.Invoke(ShowPopup);
	}

	/// <summary>Test hook: sets text and submits, exercising the full submit path in-process.</summary>
	public void SimulateSubmitForTest(string text) {
		MessageTextBox.Text = text;
		SubmitMessage();
	}

	void SubmitMessage() {

		string text = MessageTextBox.Text;

		if (string.IsNullOrWhiteSpace(text)) return;

		MessageSubmitted?.Invoke(text);

		// After submit: hide + clear (Esc's preserved text is kept by the TextBox itself)
		Hide();
		MessageTextBox.Clear();
	}

	void Cancel() {
		// Esc: hide but keep the text so the next summon restores it
		Hide();
	}

	// PreviewKeyDown is used (not KeyDown) because TextBox's class handler
	// consumes Enter for newline insertion before instance KeyDown handlers
	// run — submit would never fire. Preview tunnels first, so we can
	// intercept Enter before the TextBox sees it.
	void OnMessagePreviewKeyDown(object sender, KeyEventArgs e) {

		switch (e.Key) {

			case Key.Enter:
				if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) {
					// Shift+Enter: newline — handled natively by TextBox
					return;
				}
				e.Handled = true;
				SubmitMessage();
				break;

			case Key.Escape:
				e.Handled = true;
				Cancel();
				break;

			case Key.Up:
				if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) {
					e.Handled = true;
					if (historyTracker.TryNavigatePrevious(MessageTextBox.Text, out string prev)) {
						MessageTextBox.Text = prev;
						MessageTextBox.CaretIndex = prev.Length;
					}
				}
				break;

			case Key.Down:
				if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) {
					e.Handled = true;
					if (historyTracker.TryNavigateNext(MessageTextBox.Text, out string next)) {
						MessageTextBox.Text = next;
						MessageTextBox.CaretIndex = next.Length;
					}
				}
				break;
		}
	}
}
