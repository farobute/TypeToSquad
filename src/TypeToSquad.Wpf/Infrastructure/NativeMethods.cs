using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TypeToSquad.Wpf.Infrastructure;

/// <summary>Win32 P/Invoke interop for hotkeys and inter-process messages.</summary>
internal static class NativeMethods {

	// === Window messages ===

	public const int WM_HOTKEY = 0x0312;
	public const int WM_COPYDATA = 0x004A;

	// === Window style (hide from Alt+Tab) ===

	[DllImport("user32.dll")]
	private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

	[DllImport("user32.dll")]
	private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

	const int GWL_EXSTYLE = -20;
	const int WS_EX_TOOLWINDOW = 0x00000080;

	/// <summary>Marks the window as a tool window so it does not appear in Alt+Tab.</summary>
	public static void HideFromAltTab(IntPtr hwnd) {
		int style = GetWindowLong(hwnd, GWL_EXSTYLE);
		SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW);
	}

	// === RegisterHotKey ===

	public const uint MOD_ALT = 0x0001;
	public const uint MOD_CONTROL = 0x0002;
	public const uint MOD_SHIFT = 0x0004;
	public const uint MOD_WIN = 0x0008;
	public const uint MOD_NOREPEAT = 0x4000;

	[DllImport("user32.dll", SetLastError = true)]
	public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

	// === Window finding (for single-instance forwarding) ===

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static extern bool BringWindowToTop(IntPtr hWnd);

	[DllImport("user32.dll", SetLastError = true)]
	public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

	// === WM_COPYDATA ===

	[StructLayout(LayoutKind.Sequential)]
	public struct COPYDATASTRUCT {
		public IntPtr dwData;
		public int cbData;
		public IntPtr lpData;
	}

	/// <summary>Sends a string to another window via WM_COPYDATA. Returns the other window's response.</summary>
	public static IntPtr SendStringMessage(IntPtr hWnd, string message) {

		byte[] bytes = Encoding.Unicode.GetBytes(message);
		int size = bytes.Length;

		IntPtr ptr = Marshal.AllocHGlobal(size);
		try {
			Marshal.Copy(bytes, 0, ptr, size);

			var cds = new COPYDATASTRUCT {
				dwData = IntPtr.Zero,
				cbData = size,
				lpData = ptr,
			};

			return SendMessage(hWnd, WM_COPYDATA, IntPtr.Zero, ref cds);
		} finally {
			Marshal.FreeHGlobal(ptr);
		}
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, ref COPYDATASTRUCT lParam);
}
